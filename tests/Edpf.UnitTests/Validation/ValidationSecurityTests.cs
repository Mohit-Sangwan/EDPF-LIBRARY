using System.Diagnostics;
using Edpf.Abstractions.Validation;

namespace Edpf.UnitTests.Validation;

/// <summary>
/// Phase 17 §"Security dimension": validation failures must **never echo back
/// attacker-supplied content unescaped**, and processing must stay bounded on
/// maximum-size input.
/// </summary>
public sealed class ValidationSecurityTests
{
    public static TheoryData<string> HostileInput => new(HostilePayloads);

    private static readonly string[] HostilePayloads =
    [
        "<script>alert(1)</script>",
        "\"><img src=x onerror=alert(1)>",
        "'; DROP TABLE PATIENT;--",
        "javascript:alert(document.cookie)",
        "line1\nFAKE LOG ENTRY",
        "carriage\rreturn",
        "null\0byte",
        "<svg/onload=alert(1)>",
        "&lt;already&gt;encoded",
        "{{7*7}}",
        "${jndi:ldap://x/y}",
    ];

    [Theory]
    [MemberData(nameof(HostileInput))]
    public void ValidationFailure_MessageContainingHostileInput_IsNeutralised(string payload)
    {
        var failure = new ValidationFailure("GivenName", "pattern", payload);

        // Markup delimiters encoded, control characters removed — so the
        // message is safe in an HTML response, a JSON body, and a log line.
        Assert.DoesNotContain('<', failure.Message);
        Assert.DoesNotContain('>', failure.Message);
        Assert.DoesNotContain('"', failure.Message);
        Assert.DoesNotContain('\'', failure.Message);
        Assert.DoesNotContain('\n', failure.Message);
        Assert.DoesNotContain('\r', failure.Message);
        Assert.DoesNotContain('\0', failure.Message);
    }

    [Theory]
    [MemberData(nameof(HostileInput))]
    public void ValidationFailure_FieldAndRuleNames_AreAlsoNeutralised(string payload)
    {
        // A field name can be attacker-influenced too — it arrives in the
        // request body just as the value does.
        var failure = new ValidationFailure(payload, payload, "invalid");

        Assert.DoesNotContain('<', failure.FieldName);
        Assert.DoesNotContain('\n', failure.RuleName);
    }

    [Fact]
    public void ValidationFailure_OverlongMessage_IsTruncated()
    {
        // Unbounded messages are a DoS and log-flood vector.
        var failure = new ValidationFailure("Field", "rule", new string('a', 10_000));

        Assert.True(failure.Message.Length <= ValidationFailure.MaxMessageLength + 1);
    }

    [Fact]
    public void ValidationFailure_MaximumSizeInput_ProcessesInBoundedTime()
    {
        // Phase 17 §⑤: "assert bounded processing time on maximum-size input".
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            _ = new ValidationFailure("Field", "rule", new string('<', 100_000));
        }

        stopwatch.Stop();

        Assert.True(
            stopwatch.ElapsedMilliseconds < 2_000,
            $"100 maximum-size validations took {stopwatch.ElapsedMilliseconds}ms; sanitisation must stay linear "
            + "and must stop at the length bound.");
    }

    [Fact]
    public void ValidationFailure_BlankFieldOrRule_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new ValidationFailure("  ", "rule", "m"));
        Assert.Throws<ArgumentException>(() => new ValidationFailure("Field", "  ", "m"));
    }

    [Fact]
    public void ValidationOutcome_WarningsAndInfo_DoNotBlockTheOperation()
    {
        var outcome = new ValidationOutcome(
        [
            new ValidationFailure("A", "hint", "consider this", ValidationSeverity.Info),
            new ValidationFailure("B", "soft", "unusual", ValidationSeverity.Warning),
        ]);

        Assert.True(outcome.IsValid);
    }

    [Fact]
    public void ValidationOutcome_AnyError_BlocksTheOperation()
    {
        var outcome = new ValidationOutcome(
        [
            new ValidationFailure("A", "hint", "fine", ValidationSeverity.Info),
            new ValidationFailure("B", "required", "missing", ValidationSeverity.Error),
        ]);

        Assert.False(outcome.IsValid);
    }

    [Fact]
    public void ValidationOutcome_Valid_HasNoFailures()
    {
        Assert.True(ValidationOutcome.Valid.IsValid);
        Assert.Empty(ValidationOutcome.Valid.Failures);
    }

    [Fact]
    public void ValidationFailure_ToString_IsStructuralAndSafeToLog()
    {
        var failure = new ValidationFailure("GivenName", "required", "<script>");

        Assert.Equal("GivenName: required", failure.ToString());
    }
}
