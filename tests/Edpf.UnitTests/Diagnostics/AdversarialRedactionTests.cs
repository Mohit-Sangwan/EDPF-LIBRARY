using System.Globalization;
using System.Text.Json;
using Edpf.Abstractions.Configuration;
using Edpf.Abstractions.Diagnostics;
using Edpf.Abstractions.Primitives;
using Edpf.Diagnostics.Redaction;

namespace Edpf.UnitTests.Diagnostics;

/// <summary>
/// Phase 05 §⑤: attempt to log a PHI-bearing object by ten different routes
/// and assert zero PHI escapes in all ten. This is the suite that decides
/// whether "no PHI in logs" is a property of the build or a hope.
/// </summary>
public sealed class AdversarialRedactionTests
{
    private const string Mrn = "MRN-SECRET-0001";
    private const string FamilyName = "Rutherford";

    private readonly SensitiveDataRedactor _redactor = new();

    /// <summary>A PHI-bearing entity, tagged exactly as a real one would be.</summary>
    private sealed class Patient
    {
        public Guid Id { get; init; } = Guid.NewGuid();

        [DataClassification(DataClassificationLevel.Pii)]
        public string FamilyName { get; init; } = AdversarialRedactionTests.FamilyName;

        [DataClassification(DataClassificationLevel.Phi)]
        public string MedicalRecordNumber { get; init; } = Mrn;

        // The classic leak: a helpful ToString that defeats every other control.
        public override string ToString() => $"Patient {FamilyName} ({MedicalRecordNumber})";
    }

    private sealed class Encounter
    {
        public string Location { get; init; } = "Ward 4";
        public Patient Subject { get; init; } = new();
    }

    private sealed class PatientException(Patient patient)
        : Exception($"Failed processing {patient.MedicalRecordNumber}");

    private static string Render(object? redacted) => JsonSerializer.Serialize(redacted);

    private static void AssertNoPhi(string rendered)
    {
        Assert.DoesNotContain(Mrn, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FamilyName, rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // Route 1 — direct
    public void Redact_DirectObject_LeaksNothing()
    {
        AssertNoPhi(Render(_redactor.Redact(new Patient())));
    }

    [Fact] // Route 2 — nested inside another object
    public void Redact_NestedObject_LeaksNothing()
    {
        AssertNoPhi(Render(_redactor.Redact(new Encounter())));
    }

    [Fact] // Route 3 — via an exception message
    public void Redact_ExceptionMessage_LeaksNothing()
    {
        string rendered = Render(_redactor.Redact(new PatientException(new Patient())));

        AssertNoPhi(rendered);

        // The type survives — that is what makes the entry actionable — but
        // the free-text message does not.
        Assert.Contains(nameof(PatientException), rendered, StringComparison.Ordinal);
        Assert.Contains(SensitiveDataRedactor.RedactionMarker, rendered, StringComparison.Ordinal);
    }

    [Fact] // Route 3b — a nested inner exception cannot smuggle it out either
    public void Redact_InnerExceptionMessage_LeaksNothing()
    {
        var outer = new InvalidOperationException(
            "operation failed", new PatientException(new Patient()));

        AssertNoPhi(Render(_redactor.Redact(outer)));
    }

    [Fact]
    public void Redact_RegisteredSafeExceptionType_KeepsItsMessage()
    {
        // Opt-in for types whose messages are contractually code-only — the
        // Phase 18 taxonomy registers itself this way.
        var redactor = new SensitiveDataRedactor([typeof(InvalidOperationException)]);

        string rendered = Render(redactor.Redact(
            new InvalidOperationException("EDPF-DATA-3002: entity absent")));

        Assert.Contains("EDPF-DATA-3002", rendered, StringComparison.Ordinal);
    }

    [Fact] // Route 4 — via exception Data payload
    public void Redact_ExceptionDataPayload_LeaksNothing()
    {
        var ex = new InvalidOperationException("processing failed");
        ex.Data["patient"] = new Patient();

        AssertNoPhi(Render(_redactor.Redact(ex)));
    }

    [Fact] // Route 5 — via ToString()
    public void Redact_ToStringOutput_LeaksNothing()
    {
        // The redactor never calls ToString() on a complex type; it projects
        // members instead, so a helpful ToString cannot smuggle PHI out.
        AssertNoPhi(Render(_redactor.Redact(new Patient())));

        // And when someone has already stringified it, RedactText is not a
        // rescue — this asserts the documented boundary rather than a false
        // guarantee: the analyzer (EDPF0005) is what stops that call site.
        string preStringified = new Patient().ToString();
        Assert.Contains(Mrn, preStringified, StringComparison.Ordinal);
    }

    [Fact] // Route 6 — as a structured log property value
    public void Redact_StructuredProperty_LeaksNothing()
    {
        var properties = new Dictionary<string, object?>
        {
            ["subject"] = new Patient(),
            ["operation"] = "patient.read",
        };

        string rendered = Render(_redactor.Redact(properties));

        AssertNoPhi(rendered);
        Assert.Contains("patient.read", rendered, StringComparison.Ordinal);
    }

    [Fact] // Route 7 — inside a logging scope dictionary
    public void Redact_ScopeDictionary_LeaksNothing()
    {
        var scope = new Dictionary<string, object?>
        {
            ["encounter"] = new Encounter(),
        };

        AssertNoPhi(Render(_redactor.Redact(scope)));
    }

    [Fact] // Route 8 — inside a collection
    public void Redact_Collection_LeaksNothing()
    {
        AssertNoPhi(Render(_redactor.Redact(new List<Patient> { new(), new() })));
    }

    [Fact] // Route 9 — projected into an anonymous type
    public void Redact_AnonymousProjection_LeaksNothing()
    {
        var projection = new { Count = 1, Subject = new Patient() };

        AssertNoPhi(Render(_redactor.Redact(projection)));
    }

    [Fact] // Route 10 — via a SecretValue, at any depth
    public void Redact_SecretValue_LeaksNothing()
    {
        using var secret = new SecretValue("hunter2-database-password");
        var payload = new Dictionary<string, object?>
        {
            ["connection"] = secret,
            ["nested"] = new Dictionary<string, object?> { ["inner"] = secret },
        };

        string rendered = Render(_redactor.Redact(payload));

        Assert.DoesNotContain("hunter2", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SecretValue.Redacted, rendered, StringComparison.Ordinal);
    }

    // ── supporting properties of the redactor ──────────────────────────────

    [Fact]
    public void Redact_SafeMembers_ArePreserved()
    {
        // Redaction must not be so blunt that logs become useless: an
        // unclassified operational field still comes through.
        var encounter = new Encounter { Location = "Ward 4" };

        string rendered = Render(_redactor.Redact(encounter));

        Assert.Contains("Ward 4", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void CarriesClassifiedData_PhiBearingType_IsTrue()
    {
        Assert.True(_redactor.CarriesClassifiedData(typeof(Patient)));
        Assert.True(_redactor.CarriesClassifiedData(typeof(Encounter))); // via nesting
    }

    [Fact]
    public void CarriesClassifiedData_PlainType_IsFalse()
    {
        Assert.False(_redactor.CarriesClassifiedData(typeof(PageRequest)));
    }

    [Theory]
    [InlineData("line1\nline2", "line1\\nline2")]
    [InlineData("carriage\rreturn", "carriage\\rreturn")]
    [InlineData("tab\there", "tab\\there")]
    public void RedactText_ControlCharacters_AreNeutralised(string input, string expected)
    {
        // Log injection: a value must not be able to forge additional entries.
        Assert.Equal(expected, _redactor.RedactText(input));
    }

    [Fact]
    public void RedactText_ForgedLogEntry_CannotBreakOutOfItsField()
    {
        string forged = "ok\n2026-08-01 ERROR Fake entry injected by attacker";

        string sanitised = _redactor.RedactText(forged);

        Assert.DoesNotContain("\n", sanitised, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_SelfReferencingGraph_TerminatesWithoutStackOverflow()
    {
        var node = new Node { Name = "root" };
        node.Self = node;

        string rendered = Render(_redactor.Redact(node));

        Assert.Contains("cycle", rendered, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Node
    {
        public string Name { get; set; } = string.Empty;
        public Node? Self { get; set; }
    }

    [Fact]
    public void Redact_ThrowingPropertyGetter_DoesNotBringDownLogging()
    {
        string rendered = Render(_redactor.Redact(new Hostile()));

        Assert.Contains("unreadable", rendered, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Hostile
    {
        public string Safe { get; } = "fine";

        public string Explodes =>
            throw new InvalidOperationException("getter failure " + Mrn + Safe);
    }

    [Fact]
    public void Redact_LargeCollection_IsTruncated()
    {
        List<Patient> many = Enumerable.Range(0, 500).Select(_ => new Patient()).ToList();

        string rendered = Render(_redactor.Redact(many));

        AssertNoPhi(rendered);
        Assert.Contains("truncated", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Redact_Null_ReturnsNull()
    {
        Assert.Null(_redactor.Redact(null));
    }

    [Fact]
    public void Redact_SafeScalar_PassesThrough()
    {
        Assert.Equal(42, _redactor.Redact(42));
        Assert.Equal("plain", _redactor.Redact("plain"));
        Assert.Equal(
            1.5m.ToString(CultureInfo.InvariantCulture),
            _redactor.Redact(1.5m)!.ToString());
    }
}
