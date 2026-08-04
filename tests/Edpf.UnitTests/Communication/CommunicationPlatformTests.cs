using Edpf.Abstractions.Communication;
using Edpf.Abstractions.Compliance;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Tenancy;
using Edpf.Communication;
using Edpf.Core.Tenancy;

namespace Edpf.UnitTests.Communication;

/// <summary>
/// The communication platform (ADR-037 v1.0 addition 3). The controls here are
/// the reason the platform exists — sending a string is not the hard part.
/// </summary>
public sealed class CommunicationPlatformTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private readonly TenantContextAccessor _tenants = new();
    private readonly RecordingChannel _sms = RecordingChannel.Sms();

    private static MessageTemplate ReminderTemplate() => new(
        "appointment-reminder",
        "Sms",
        "Reminder",
        "Hello {{givenName}}, you have an appointment on {{appointmentDate}}.",
        ["givenName", "appointmentDate"]);

    private CommunicationDispatcher CreateDispatcher(
        IConsentEvaluator? consent = null,
        MessageTemplate? template = null,
        ICommunicationChannel? channel = null)
        => new(
            [template ?? ReminderTemplate()],
            [channel ?? _sms],
            consent ?? new AlwaysPermits(),
            _tenants);

    private IDisposable ActAs(Guid tenantId)
        => _tenants.Push(new TenantDescriptor(
            tenantId, "tenant", "eu-west", TenantIsolationMode.SharedSchema, Guid.NewGuid()));

    private static SendRequest Reminder(
        DataClassificationLevel dateClassification = DataClassificationLevel.Public,
        string purpose = "appointment-reminder")
        => new(
            "appointment-reminder",
            MessageAddress.ForPhone("+441234567890"),
            new Dictionary<string, TemplateValue>
            {
                ["givenName"] = TemplateValue.Public("Alex"),
                ["appointmentDate"] = new TemplateValue("14 August", dateClassification),
            },
            purpose,
            "subject-token-abc");

    // ── addresses are validated, never repaired ───────────────────────────

    [Theory]
    [InlineData("alex@example.com")]
    [InlineData("alex.smith+tag@sub.example.co.uk")]
    public void ForEmail_AcceptsAWellFormedMailbox(string address)
        => Assert.Equal(address, MessageAddress.ForEmail(address).Value);

    [Theory]
    [InlineData("alex@example.com\r\nBcc: attacker@evil.test")]
    [InlineData("alex@example.com\nBcc: attacker@evil.test")]
    [InlineData("alex at example.com")]
    [InlineData("alex@@example.com")]
    [InlineData("alex@example")]
    [InlineData("alex@example.")]
    [InlineData("@example.com")]
    [InlineData("alex@")]
    [InlineData("")]
    public void ForEmail_RejectsAnythingElse(string address)
        => Assert.Throws<ArgumentException>(() => MessageAddress.ForEmail(address));

    [Theory]
    [InlineData("+441234567890")]
    [InlineData("+12025550143")]
    public void ForPhone_AcceptsE164(string number)
        => Assert.Equal(number, MessageAddress.ForPhone(number).Value);

    [Theory]
    [InlineData("01234567890")]
    [InlineData("+44 1234 567890")]
    [InlineData("+44123")]
    [InlineData("+4412345678901234567")]
    [InlineData("+44123456789a")]
    public void ForPhone_RejectsNationalAndMalformedNumbers(string number)
        => Assert.Throws<ArgumentException>(() => MessageAddress.ForPhone(number));

    [Fact]
    public void OutboundMessage_RejectsASubjectCarryingAHeaderBreak()
    {
        Assert.Throws<ArgumentException>(() => new OutboundMessage(
            MessageAddress.ForEmail("alex@example.com"),
            "Results\r\nBcc: attacker@evil.test",
            "body",
            DataClassificationLevel.Public));
    }

    // ── the template is a substitution grammar, not a language ────────────

    [Fact]
    public void Template_RejectsAPlaceholderTheDeclarationDoesNotMention()
    {
        Assert.Throws<ArgumentException>(() => new MessageTemplate(
            "t", "Sms", "s", "Hello {{givenName}} of {{clinicName}}", ["givenName"]));
    }

    [Fact]
    public void Template_RejectsADeclarationTheTextDoesNotUse()
    {
        // The two drifted apart and there is no way to know which is stale.
        Assert.Throws<ArgumentException>(() => new MessageTemplate(
            "t", "Sms", "s", "Hello {{givenName}}", ["givenName", "clinicName"]));
    }

    [Theory]
    [InlineData("Hello {{givenName}")]
    [InlineData("Hello {{}}")]
    [InlineData("Hello {{given.Name}}")]
    [InlineData("Hello {{1+1}}")]
    public void Template_RejectsMalformedOrExpressionLikePlaceholders(string body)
    {
        Assert.Throws<ArgumentException>(() => new MessageTemplate("t", "Sms", "s", body, ["givenName"]));
    }

    [Fact]
    public void Render_WithAMissingValue_IsRefusedRatherThanRenderedEmpty()
    {
        // "Dear ," is a visible incident. An empty substitution is never a
        // reasonable fallback for a message that reaches a human.
        Result<RenderedMessage> rendered = ReminderTemplate().Render(
            new Dictionary<string, TemplateValue> { ["givenName"] = TemplateValue.Public("Alex") });

        Assert.True(rendered.IsFailure);
        Assert.Equal(ErrorCodes.ValidationFailed, rendered.Error!.Code);
    }

    [Fact]
    public void Render_WithAnUnusedValue_IsRefused()
    {
        // Supplying `diagnosis` to a template that has no such placeholder
        // almost always means the wrong template was selected.
        Result<RenderedMessage> rendered = ReminderTemplate().Render(
            new Dictionary<string, TemplateValue>
            {
                ["givenName"] = TemplateValue.Public("Alex"),
                ["appointmentDate"] = TemplateValue.Public("14 August"),
                ["diagnosis"] = new TemplateValue("oncology", DataClassificationLevel.Phi),
            });

        Assert.True(rendered.IsFailure);
    }

    [Fact]
    public void Render_DoesNotRescanSubstitutedText()
    {
        // A value containing a placeholder marker is literal output. Rescanning
        // would let a caller-supplied value reach a placeholder they were never
        // given a value for.
        var template = new MessageTemplate("t", "Sms", "s", "A: {{a}} B: {{b}}", ["a", "b"]);

        RenderedMessage rendered = template.Render(new Dictionary<string, TemplateValue>
        {
            ["a"] = TemplateValue.Public("{{b}}"),
            ["b"] = TemplateValue.Public("secret"),
        }).Value;

        Assert.Equal("A: {{b}} B: secret", rendered.Body);
    }

    [Fact]
    public void Render_TakesTheHighestClassificationOfEverythingSubstituted()
    {
        // A harmless template carrying one PHI value is a PHI message. There is
        // no arithmetic under which it is not.
        RenderedMessage rendered = ReminderTemplate().Render(new Dictionary<string, TemplateValue>
        {
            ["givenName"] = TemplateValue.Public("Alex"),
            ["appointmentDate"] = new TemplateValue("14 August, Oncology", DataClassificationLevel.Phi),
        }).Value;

        Assert.Equal(DataClassificationLevel.Phi, rendered.Classification);
    }

    // ── the dispatcher's controls ─────────────────────────────────────────

    [Fact]
    public async Task SendAsync_WithNoResolvedTenant_IsRefused()
    {
        Result<OutboundMessage> sent = await CreateDispatcher().SendAsync(Reminder(), default);

        Assert.True(sent.IsFailure);
        Assert.Equal(ErrorCodes.TenantScopeViolation, sent.Error!.Code);
        Assert.Empty(_sms.Sent);
    }

    [Fact]
    public async Task SendAsync_WithoutALawfulBasis_IsRefusedAndNothingLeaves()
    {
        CommunicationDispatcher dispatcher = CreateDispatcher(consent: new AlwaysRefuses());

        using (ActAs(TenantA))
        {
            Result<OutboundMessage> sent = await dispatcher.SendAsync(Reminder(), default);

            Assert.True(sent.IsFailure);
            Assert.Equal(ErrorCodes.ConsentRequired, sent.Error!.Code);
        }

        Assert.Empty(_sms.Sent);
    }

    [Fact]
    public async Task SendAsync_ChecksConsentBeforeRendering()
    {
        // Order matters: rendering pulls the subject's data into memory, so a
        // consent refusal afterwards has already performed the processing it
        // was refusing.
        //
        // Observed rather than instrumented. This request would fail rendering
        // too — a placeholder has no value — so the code that reports the
        // *consent* failure is the code that ran first.
        var consent = new AlwaysRefuses();
        CommunicationDispatcher dispatcher = CreateDispatcher(consent);

        var unrenderable = new SendRequest(
            "appointment-reminder",
            MessageAddress.ForPhone("+441234567890"),
            new Dictionary<string, TemplateValue> { ["givenName"] = TemplateValue.Public("Alex") },
            "appointment-reminder",
            "subject-token-abc");

        using (ActAs(TenantA))
        {
            Result<OutboundMessage> sent = await dispatcher.SendAsync(unrenderable, default);

            Assert.True(consent.WasEvaluated);
            Assert.Equal(ErrorCodes.ConsentRequired, sent.Error!.Code);
        }
    }

    [Fact]
    public async Task SendAsync_OfContentAboveTheChannelCeiling_IsRefused()
    {
        // The reason appointment reminders say "an appointment" and not "your
        // oncology appointment". SMS traverses carrier infrastructure the
        // deployment does not control.
        CommunicationDispatcher dispatcher = CreateDispatcher();

        using (ActAs(TenantA))
        {
            Result<OutboundMessage> sent = await dispatcher.SendAsync(
                Reminder(dateClassification: DataClassificationLevel.Phi), default);

            Assert.True(sent.IsFailure);
            Assert.Equal(ErrorCodes.ChannelClassificationExceeded, sent.Error!.Code);
        }

        Assert.Empty(_sms.Sent);
    }

    [Fact]
    public async Task SendAsync_RefusalDoesNotDiscloseTheContentItRefused()
    {
        CommunicationDispatcher dispatcher = CreateDispatcher();

        using (ActAs(TenantA))
        {
            Result<OutboundMessage> sent = await dispatcher.SendAsync(
                Reminder(dateClassification: DataClassificationLevel.Phi), default);

            // The message names the channel and its ceiling. It does not name
            // the recipient, the subject, or a word of the body.
            Assert.DoesNotContain("Alex", sent.Error!.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("+44", sent.Error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("subject-token", sent.Error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SendAsync_WithinTheCeiling_Delivers()
    {
        CommunicationDispatcher dispatcher = CreateDispatcher();

        using (ActAs(TenantA))
        {
            Result<OutboundMessage> sent = await dispatcher.SendAsync(Reminder(), default);

            Assert.True(sent.IsSuccess);
        }

        OutboundMessage delivered = Assert.Single(_sms.Sent);
        Assert.Equal("Hello Alex, you have an appointment on 14 August.", delivered.Body);
    }

    [Fact]
    public async Task SendAsync_ToAnAddressOfTheWrongKind_IsRefused()
    {
        CommunicationDispatcher dispatcher = CreateDispatcher();
        var request = new SendRequest(
            "appointment-reminder",
            MessageAddress.ForEmail("alex@example.com"),
            new Dictionary<string, TemplateValue>
            {
                ["givenName"] = TemplateValue.Public("Alex"),
                ["appointmentDate"] = TemplateValue.Public("14 August"),
            },
            "appointment-reminder",
            "subject-token-abc");

        using (ActAs(TenantA))
        {
            Result<OutboundMessage> sent = await dispatcher.SendAsync(request, default);

            Assert.True(sent.IsFailure);
            Assert.Equal(ErrorCodes.ValidationFailed, sent.Error!.Code);
        }
    }

    [Fact]
    public void Dispatcher_RefusesATemplateWhoseChannelIsNotRegistered()
    {
        // A composition-time failure, per ADR-014. The alternative is finding
        // out at send time, which means finding out in production.
        var orphan = new MessageTemplate("t", "Fax", "s", "{{a}}", ["a"]);

        Assert.Throws<ArgumentException>(
            () => new CommunicationDispatcher([orphan], [_sms], new AlwaysPermits(), _tenants));
    }

    [Fact]
    public void SendRequest_RequiresADeclaredPurpose()
    {
        Assert.Throws<ArgumentException>(() => new SendRequest(
            "t",
            MessageAddress.ForPhone("+441234567890"),
            new Dictionary<string, TemplateValue>(),
            "   ",
            "subject"));
    }

    [Fact]
    public void SmsCeiling_IsBelowPhi()
    {
        // Asserted rather than assumed, because this single value is what
        // stands between a template author and a HIPAA incident.
        Assert.True(RecordingChannel.Sms().MaximumClassification < DataClassificationLevel.Phi);
    }

    // ── doubles ───────────────────────────────────────────────────────────

    private sealed class AlwaysPermits : IConsentEvaluator
    {
        public Task<Result<ConsentDecision>> EvaluateAsync(
            ProcessingRequest request, CancellationToken cancellationToken)
            => Task.FromResult(Result<ConsentDecision>.FromValue(
                ConsentDecision.Permit(LawfulBasis.Consent, "test", "v1")));
    }

    private sealed class AlwaysRefuses : IConsentEvaluator
    {
        public bool WasEvaluated { get; private set; }

        public Task<Result<ConsentDecision>> EvaluateAsync(
            ProcessingRequest request, CancellationToken cancellationToken)
        {
            WasEvaluated = true;
            return Task.FromResult(Result<ConsentDecision>.FromValue(ConsentDecision.Refuse("no basis")));
        }
    }
}
