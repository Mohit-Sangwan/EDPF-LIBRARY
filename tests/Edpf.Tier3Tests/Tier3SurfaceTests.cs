using System;
using System.Globalization;
using Edpf.Abstractions.Primitives;
using Edpf.Compatibility;
using Edpf.Core.Guards;
using Edpf.Core.Time;
using Edpf.Devices;

namespace Edpf.Tier3Tests;

/// <summary>
/// The Tier 3 surface, executed rather than merely compiled.
/// </summary>
/// <remarks>
/// <para>
/// Runs on net48 and net10.0. Every assertion here is framework-independent by
/// intent, so a failure on one target and not the other is exactly the
/// divergence ADR-002's portable equivalents exist to prevent — and the only
/// way to see it is to run both.
/// </para>
/// <para>
/// The cases are chosen where "it compiles" and "it works" most often part
/// company: culture-sensitive formatting, <c>Substring</c> standing in for a
/// range expression, and instance hash APIs standing in for static ones.
/// </para>
/// </remarks>
public sealed class Tier3SurfaceTests
{
    // ── the framework this is actually running on ─────────────────────────

    [Fact]
    public void Suite_RunsOnTheFrameworkItClaimsTo()
    {
        // Guards against the whole project silently collapsing to one target.
        string description = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

#if NETFRAMEWORK
        Assert.StartsWith(".NET Framework", description, StringComparison.Ordinal);
#else
        Assert.StartsWith(".NET ", description, StringComparison.Ordinal);
        Assert.DoesNotContain("Framework", description, StringComparison.Ordinal);
#endif
    }

    // ── Edpf.Core: guards and results ─────────────────────────────────────

    [Fact]
    public void Guards_BehaveIdenticallyOnEveryTier()
    {
        Assert.Throws<ArgumentNullException>(() => Guard.NotNull<string>(null!, "p"));
        Assert.Throws<ArgumentException>(() => Guard.NotNullOrWhiteSpace("   ", "p"));
        Assert.Throws<ArgumentException>(() => Guard.NotDefault(Guid.Empty, "p"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Guard.Positive(0, "p"));

        Assert.Equal("value", Guard.NotNullOrWhiteSpace("value", "p"));
        Assert.Equal(7, Guard.Positive(7, "p"));
    }

    [Fact]
    public void Result_CarriesSuccessAndFailureOnEveryTier()
    {
        Result<int> ok = Result.Success(42);
        Result<int> bad = Result.Failure<int>(
            new Error(ErrorCodes.ValidationFailed, "no", ErrorCategory.Validation));

        Assert.True(ok.IsSuccess);
        Assert.Equal(42, ok.Value);
        Assert.True(bad.IsFailure);
        Assert.Equal(ErrorCodes.ValidationFailed, bad.Error!.Code);
    }

    // ── Edpf.Compatibility: the one assembly permitted #if ────────────────

    [Fact]
    public void EdpfTime_ProducesUtcOnEveryTier()
    {
        DateTimeOffset now = EdpfTime.UtcNow;

        Assert.Equal(TimeSpan.Zero, now.Offset);
        Assert.InRange(now.Year, 2020, 2100);
    }

    // ── StorableInstant: the cross-provider fix, on Tier 3 ────────────────

    [Fact]
    public void StorableInstant_RoundsIdenticallyOnEveryTier()
    {
        // Tick arithmetic is where a framework difference would be invisible
        // until an audit chain failed to verify (ADR-036).
        var instant = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);

        DateTimeOffset normalized = StorableInstant.Normalize(instant);

        Assert.True(StorableInstant.IsStorable(normalized));
        Assert.Equal(1_234_570, normalized.UtcTicks % TimeSpan.TicksPerSecond);
    }

    // ── Edpf.Devices: the assembly Tier 3 exists for ──────────────────────

    [Fact]
    public void AstmChecksum_IsIdenticalOnEveryTier()
    {
        // Hand-verifiable: '1' = 0x31, 'A' = 0x41, ETX = 0x03, sum 0x75.
        // Computed with Substring and instance APIs because Range and static
        // HashData do not exist here (ADR-002).
        Assert.Equal("75", AstmFrame.Checksum("1A" + AstmFrame.Etx));
    }

    [Fact]
    public void AstmFrame_RoundTripsOnEveryTier()
    {
        // The Substring-based parser is the portable equivalent of a Range
        // expression. If it were subtly wrong, this is where it shows.
        string frame = AstmFrame.Build(1, "R|1|^^^Glucose|5.4|mmol/L", isFinal: true).Value;

        AstmFrameContent parsed = AstmFrame.Parse(frame).Value;

        Assert.Equal(1, parsed.FrameNumber);
        Assert.Equal("R|1|^^^Glucose|5.4|mmol/L", parsed.Text);
        Assert.True(parsed.IsFinal);
    }

    [Fact]
    public void AstmFrame_DetectsCorruptionOnEveryTier()
    {
        string frame = AstmFrame.Build(1, "R|1|Glucose|5.4", isFinal: true).Value;
        char[] corrupted = frame.ToCharArray();
        corrupted[frame.IndexOf("5.4", StringComparison.Ordinal)] = '9';

        Assert.True(AstmFrame.Parse(new string(corrupted)).IsFailure);
    }

    [Fact]
    public void DeviceCalibration_GatesReadingsOnEveryTier()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var tenant = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

        var registry = new DeviceRegistry();
        registry.Register(new DeviceRegistration("ANALYZER-1", tenant, "Acme", DeviceTransport.Serial));

        var validator = new ReadingValidator(registry)
            .Declare(new PlausibilityRange("Mass", "kg", 0.1m, 500m, 40m, 150m));

        // Uncalibrated: rejected whatever the value.
        Assert.Equal(
            ReadingDisposition.Reject,
            validator.Evaluate(tenant, "ANALYZER-1", "Mass", "kg", 70m, now).Disposition);

        registry.RecordCalibration(tenant, "ANALYZER-1", now.AddDays(-1), now.AddYears(1));

        Assert.True(validator.Evaluate(tenant, "ANALYZER-1", "Mass", "kg", 70m, now).Accepted);
        Assert.Equal(
            ReadingDisposition.Reject,
            validator.Evaluate(tenant, "ANALYZER-1", "Mass", "kg", 900m, now).Disposition);
        Assert.Equal(
            ReadingDisposition.Flag,
            validator.Evaluate(tenant, "ANALYZER-1", "Mass", "kg", 200m, now).Disposition);
    }

    // ── culture: the difference most likely to diverge by framework ───────

    [Fact]
    public void InvariantFormatting_IsStableUnderAHostileCulture()
    {
        // .NET Framework and modern .NET ship different ICU/NLS behaviour.
        // Anything the framework formats for a checksum, a canonical form or a
        // wire protocol must not move with the thread's culture.
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            Assert.Equal("75", AstmFrame.Checksum("1A" + AstmFrame.Etx));
            Assert.Equal("1.5", 1.5m.ToString("0.#", CultureInfo.InvariantCulture));

            // The Turkish dotless-i trap, on the tier where the old NLS
            // implementation is most likely to differ. Under tr-TR the
            // CULTURE-sensitive lowering of "I" is "ı"; the invariant one must
            // still be "i", whichever framework is running.
            Assert.Equal("i", "I".ToLowerInvariant());
            Assert.Equal("ı", "I".ToLower(CultureInfo.CurrentCulture));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void OrdinalComparison_IsUnaffectedByCultureOnEveryTier()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            // Every identifier comparison in the framework is ordinal for this
            // reason; a culture-sensitive one would resolve differently here.
            Assert.NotEqual(0, string.CompareOrdinal("I", "i"));
            Assert.False(string.Equals("FILE", "file", StringComparison.Ordinal));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
