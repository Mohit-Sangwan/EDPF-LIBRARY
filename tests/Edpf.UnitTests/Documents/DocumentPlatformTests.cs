using System.Text;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Tenancy;
using Edpf.Documents;
using Edpf.UnitTests.TestDoubles;

namespace Edpf.UnitTests.Documents;

/// <summary>
/// The document platform: generation, signing and printing over one artefact.
/// </summary>
public sealed class DocumentPlatformTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly TenantContextAccessor _tenants = new();
    private readonly FakeClock _clock = new();

    private static DocumentTemplate DischargeSummary() => new(
        "discharge-summary",
        [
            new DocumentBlock(DocumentBlockKind.Title, "Discharge Summary"),
            new DocumentBlock(DocumentBlockKind.Field, "Patient", "{{patientName}}"),
            new DocumentBlock(DocumentBlockKind.Field, "Allergies", "{{allergies}}"),
            new DocumentBlock(DocumentBlockKind.Heading, "Summary"),
            new DocumentBlock(DocumentBlockKind.Paragraph, "{{summary}}"),
        ],
        ["patientName", "allergies", "summary"]);

    private static Dictionary<string, DocumentValue> Values(string patientName = "Alex Smith") => new()
    {
        ["patientName"] = new DocumentValue(patientName, DataClassificationLevel.Phi),
        ["allergies"] = new DocumentValue("Penicillin", DataClassificationLevel.Phi),
        ["summary"] = new DocumentValue("Recovered well. Discharged home.", DataClassificationLevel.Phi),
    };

    private DocumentSigningService CreateService(IDocumentRenderer? renderer = null)
        => new(renderer ?? new PdfRenderer(), new TestHashingService(), _tenants, _clock);

    private IDisposable ActAs(Guid tenantId)
        => _tenants.Push(new TenantDescriptor(
            tenantId, "tenant", "eu-west", TenantIsolationMode.SharedSchema, Guid.NewGuid()));

    // ── the template is a closed grammar ──────────────────────────────────

    [Fact]
    public void Template_RefusesAnUndeclaredPlaceholder()
    {
        Assert.Throws<ArgumentException>(() => new DocumentTemplate(
            "t",
            [new DocumentBlock(DocumentBlockKind.Paragraph, "Hello {{name}} of {{clinic}}")],
            ["name"]));
    }

    [Theory]
    [InlineData("{{name.first}}")]
    [InlineData("{{1+1}}")]
    [InlineData("{{}}")]
    [InlineData("{{name")]
    public void Template_RefusesExpressionLikeOrMalformedPlaceholders(string body)
    {
        Assert.Throws<ArgumentException>(() => new DocumentTemplate(
            "t", [new DocumentBlock(DocumentBlockKind.Paragraph, body)], ["name"]));
    }

    [Fact]
    public void Template_RefusesATitleThatIsNotFirst()
    {
        // A document with a title in the middle is two documents.
        Assert.Throws<ArgumentException>(() => new DocumentTemplate(
            "t",
            [
                new DocumentBlock(DocumentBlockKind.Paragraph, "intro"),
                new DocumentBlock(DocumentBlockKind.Title, "Title"),
            ],
            []));
    }

    [Fact]
    public void Compose_WithAMissingValue_IsRefusedRatherThanRenderedBlank()
    {
        // "Allergies:" followed by nothing reads as "none known". That is a
        // clinical error, not a formatting one.
        Result<ComposedDocument> composed = DischargeSummary().Compose(
            new Dictionary<string, DocumentValue>
            {
                ["patientName"] = new DocumentValue("Alex", DataClassificationLevel.Phi),
                ["summary"] = new DocumentValue("ok", DataClassificationLevel.Phi),
            });

        Assert.True(composed.IsFailure);
        Assert.Equal(ErrorCodes.ValidationFailed, composed.Error!.Code);
    }

    [Fact]
    public void Compose_TakesTheHighestClassificationSubstituted()
    {
        ComposedDocument composed = DischargeSummary().Compose(Values()).Value;

        Assert.Equal(DataClassificationLevel.Phi, composed.Classification);
    }

    // ── the PDF is inert, and that is checkable ───────────────────────────

    [Theory]
    [InlineData("/JavaScript")]
    [InlineData("/JS")]
    [InlineData("/OpenAction")]
    [InlineData("/AA")]
    [InlineData("/Launch")]
    [InlineData("/EmbeddedFile")]
    [InlineData("/URI")]
    [InlineData("/RichMedia")]
    public void Pdf_ContainsNoActiveContentConstruct(string construct)
    {
        // The storage layer refuses to serve inbound PDFs inline because a PDF
        // is a program. This is the other side of that judgement: the ones EDPF
        // produces are inert, and it is asserted over the bytes rather than
        // promised in a comment.
        byte[] pdf = new PdfRenderer().Render(DischargeSummary().Compose(Values()).Value).Value;
        string text = Encoding.ASCII.GetString(pdf);

        Assert.DoesNotContain(construct, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_IsAWellFormedDocument()
    {
        byte[] pdf = new PdfRenderer().Render(DischargeSummary().Compose(Values()).Value).Value;
        string text = Encoding.ASCII.GetString(pdf);

        Assert.StartsWith("%PDF-1.4", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF", text, StringComparison.Ordinal);
        Assert.Contains("/Type /Catalog", text, StringComparison.Ordinal);
        Assert.Contains("trailer", text, StringComparison.Ordinal);
        Assert.Contains("startxref", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_IsDeterministic()
    {
        // No creation date, no producer string. Two renders of the same
        // document produce identical bytes — which is the only thing that makes
        // a signature over those bytes verifiable a year later.
        byte[] first = new PdfRenderer().Render(DischargeSummary().Compose(Values()).Value).Value;
        byte[] second = new PdfRenderer().Render(DischargeSummary().Compose(Values()).Value).Value;

        Assert.Equal(first, second);
    }

    [Fact]
    public void Pdf_EscapesAValueThatWouldCloseTheTextLiteral()
    {
        // The same family as SQL and CSV injection, in a format where the
        // payload would be a page description.
        var values = Values(patientName: "Alex) Tj /F1 40 Tf (INJECTED");
        byte[] pdf = new PdfRenderer().Render(DischargeSummary().Compose(values).Value).Value;
        string text = Encoding.ASCII.GetString(pdf);

        // Both parentheses in the payload are escaped, so neither closes the
        // real literal nor opens a new one.
        Assert.Contains("\\) Tj", text, StringComparison.Ordinal);
        Assert.Contains("\\(INJECTED", text, StringComparison.Ordinal);

        // The precise property: the injected font operator never becomes an
        // operator. Searching for "(INJECTED)" would not test this — the
        // *escaped* form contains that substring too, which is what made the
        // first version of this assertion fail against correct output.
        Assert.DoesNotContain("Tf (INJECTED", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_RefusesCharactersTheBuiltInFontCannotRepresent()
    {
        // Refused, not transliterated. A name silently rendered as question
        // marks on a legal document is worse than a document that failed.
        Result<byte[]> rendered = new PdfRenderer().Render(
            DischargeSummary().Compose(Values(patientName: "李明")).Value);

        Assert.True(rendered.IsFailure);
        Assert.Equal(ErrorCodes.ValidationFailed, rendered.Error!.Code);
    }

    [Fact]
    public void Pdf_StampsTheClassificationOnTheArtefact()
    {
        // Paper leaves every technical boundary the platform has. The handling
        // rule has to travel on the page, because nothing else follows it there.
        byte[] pdf = new PdfRenderer().Render(DischargeSummary().Compose(Values()).Value).Value;

        Assert.Contains("Classification: PHI", Encoding.ASCII.GetString(pdf), StringComparison.Ordinal);
    }

    // ── what-you-see-is-what-you-sign ─────────────────────────────────────

    [Fact]
    public async Task SignAsync_CoversTheExactBytesThatWereRendered()
    {
        DocumentSigningService service = CreateService();

        using (ActAs(TenantA))
        {
            RenderedDocument document = service.Render(DischargeSummary(), Values()).Value;

            Result<DocumentSignature> signed = await service.SignAsync(
                document, "dr-jones", "I confirm I have reviewed this summary.", default);

            Assert.True(signed.IsSuccess);
            Assert.Equal(document.ContentHash, signed.Value.DocumentHash);
            Assert.True(service.Verifies(signed.Value, document));
        }
    }

    [Fact]
    public async Task Signature_DoesNotVerifyAgainstADifferentDocument()
    {
        // The defect this closes: signing "the discharge summary for patient X"
        // and rendering it afterwards. The artefact can then differ from what
        // the signer read, and the signature says nothing about which one they
        // meant.
        DocumentSigningService service = CreateService();

        using (ActAs(TenantA))
        {
            RenderedDocument signedDocument = service.Render(DischargeSummary(), Values()).Value;
            RenderedDocument altered = service.Render(
                DischargeSummary(), Values(patientName: "Alex Smythe")).Value;

            DocumentSignature signature = (await service.SignAsync(
                signedDocument, "dr-jones", "reviewed", default)).Value;

            Assert.True(service.Verifies(signature, signedDocument));
            Assert.False(service.Verifies(signature, altered));
        }
    }

    [Fact]
    public async Task SignAsync_RequiresARecordedIntent()
    {
        // A signature with no intent proves a key was used, not that a person
        // agreed to anything.
        DocumentSigningService service = CreateService();

        using (ActAs(TenantA))
        {
            RenderedDocument document = service.Render(DischargeSummary(), Values()).Value;

            await Assert.ThrowsAsync<ArgumentException>(
                () => service.SignAsync(document, "dr-jones", "   ", default));
        }
    }

    [Fact]
    public async Task SignAsync_RefusesADocumentWhoseHashDoesNotMatchItsBytes()
    {
        // A caller who can hand over a hash can hand over one belonging to a
        // different document, and the signature would attest to bytes nobody saw.
        DocumentSigningService service = CreateService();

        using (ActAs(TenantA))
        {
            RenderedDocument real = service.Render(DischargeSummary(), Values()).Value;
            var tampered = new RenderedDocument(
                real.TemplateId, real.ContentType, Encoding.ASCII.GetBytes("different"),
                real.ContentHash, real.Classification);

            Result<DocumentSignature> signed = await service.SignAsync(
                tampered, "dr-jones", "reviewed", default);

            Assert.True(signed.IsFailure);
        }
    }

    [Fact]
    public async Task Signature_DoesNotVerifyForAnotherTenant()
    {
        DocumentSigningService service = CreateService();
        RenderedDocument document;
        DocumentSignature signature;

        using (ActAs(TenantA))
        {
            document = service.Render(DischargeSummary(), Values()).Value;
            signature = (await service.SignAsync(document, "dr-jones", "reviewed", default)).Value;
        }

        using (ActAs(TenantB))
        {
            Assert.False(service.Verifies(signature, document));
        }
    }

    [Fact]
    public void Render_WithNoResolvedTenant_IsRefused()
    {
        Result<RenderedDocument> rendered = CreateService().Render(DischargeSummary(), Values());

        Assert.True(rendered.IsFailure);
        Assert.Equal(ErrorCodes.TenantScopeViolation, rendered.Error!.Code);
    }

    // ── printing is the last enforceable decision ─────────────────────────

    [Fact]
    public async Task PrintAsync_ToADeviceBelowTheDocumentsClassification_IsRefused()
    {
        // A printer in a corridor is a different disclosure risk from one in a
        // locked records office, and the ceiling is declared by whoever
        // registered the device rather than asserted by the job.
        var transport = new RecordingPrintTransport();
        var service = new PrintService(
            [new PrintDestination(
                "corridor-1", "Ward 3 corridor", DataClassificationLevel.Internal, ["application/pdf"])],
            transport, _tenants, _clock);

        using (ActAs(TenantA))
        {
            RenderedDocument document = CreateService().Render(DischargeSummary(), Values()).Value;

            Result printed = await service.PrintAsync("corridor-1", document, "nurse-1", default);

            Assert.True(printed.IsFailure);
            Assert.Equal(ErrorCodes.ChannelClassificationExceeded, printed.Error!.Code);
        }

        Assert.Empty(transport.Submissions);
    }

    [Fact]
    public async Task PrintAsync_ToARegisteredDevice_SubmitsAndRecords()
    {
        var transport = new RecordingPrintTransport();
        var service = new PrintService(
            [new PrintDestination(
                "records-1", "Records office", DataClassificationLevel.Phi, ["application/pdf"])],
            transport, _tenants, _clock);

        using (ActAs(TenantA))
        {
            RenderedDocument document = CreateService().Render(DischargeSummary(), Values()).Value;

            Assert.True((await service.PrintAsync("records-1", document, "clerk-2", default)).IsSuccess);

            PrintRecord record = Assert.Single(service.Records);
            Assert.Equal("Records office", record.Location);
            Assert.Equal(document.ContentHash, record.DocumentHash);
            Assert.Equal("clerk-2", record.RequestedBy);
        }

        Assert.Single(transport.Submissions);
    }

    [Fact]
    public async Task PrintAsync_ToADeviceThatCannotRenderTheFormat_IsRefused()
    {
        var service = new PrintService(
            [new PrintDestination("label-1", "Phlebotomy", DataClassificationLevel.Phi, ["text/plain"])],
            new RecordingPrintTransport(), _tenants, _clock);

        using (ActAs(TenantA))
        {
            RenderedDocument pdf = CreateService().Render(DischargeSummary(), Values()).Value;

            Result printed = await service.PrintAsync("label-1", pdf, "phleb-1", default);

            Assert.True(printed.IsFailure);
            Assert.Equal(ErrorCodes.CapabilityNotSupported, printed.Error!.Code);
        }
    }

    [Fact]
    public void PrintDestination_RefusesToBeRegisteredWithoutALocation()
    {
        // Where the paper comes out is the control. An unlocated printer cannot
        // be risk-assessed, so it cannot be registered.
        Assert.Throws<ArgumentException>(() => new PrintDestination(
            "x", "  ", DataClassificationLevel.Public, ["text/plain"]));
    }

    [Fact]
    public async Task PrintAsync_ToAnUnregisteredDevice_IsRefused()
    {
        var service = new PrintService([], new RecordingPrintTransport(), _tenants, _clock);

        using (ActAs(TenantA))
        {
            RenderedDocument document = CreateService(new PlainTextRenderer())
                .Render(DischargeSummary(), Values()).Value;

            Assert.True((await service.PrintAsync("unknown", document, "user", default)).IsFailure);
        }
    }

    [Fact]
    public void PlainTextRenderer_AlsoStampsTheClassification()
    {
        byte[] rendered = new PlainTextRenderer()
            .Render(DischargeSummary().Compose(Values()).Value).Value;

        Assert.Contains("Classification: PHI", Encoding.UTF8.GetString(rendered), StringComparison.Ordinal);
    }
}
