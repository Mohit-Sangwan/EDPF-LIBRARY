using System.Collections.Generic;
using Edpf.Devices;

namespace Edpf.Healthcare.Domain;

/// <summary>
/// Plausibility bands for the vital signs bedside devices report
/// (Phase 24f, in the vertical per ADR-024).
/// </summary>
/// <remarks>
/// <para>
/// **These values are the reason ADR-024 exists.** A device registry, a
/// calibration lifecycle and a framing protocol serve laboratory analyzers,
/// industrial scales and payment terminals alike — so they live in the core.
/// The knowledge that a heart rate of 320 is an artefact and a heart rate of
/// 190 is a real emergency is clinical, and it lives here.
/// </para>
/// <para>
/// Two bands per quantity, and the distinction between them is the whole
/// point. The **plausible** band bounds what a real measurement looks like;
/// outside it the reading is an artefact — a lead off the chest, a probe in
/// open air. The **expected** band bounds normality; outside it the reading
/// may be entirely real and the most important thing anyone will see that
/// shift. Collapsing them into one range forces a choice between discarding
/// true emergencies and accepting impossible numbers.
/// </para>
/// <para>
/// **These are adult bands.** A neonatal heart rate of 160 is unremarkable and
/// would be flagged by the expected band here; a paediatric deployment needs
/// its own set, selected by the population being monitored. Shipping one set
/// and calling it universal would be the more dangerous choice, so the bands
/// are exposed as data rather than applied automatically.
/// </para>
/// </remarks>
public static class ClinicalPlausibility
{
    /// <summary>
    /// Adult vital-sign bands, as UCUM-coded quantities.
    /// </summary>
    /// <returns>The ranges, for registration with a reading validator.</returns>
    public static IReadOnlyList<PlausibilityRange> AdultVitalSigns() =>
    [
        // A rate above ~300 exceeds what cardiac tissue can sustain; below 20
        // is an asystolic artefact rather than a perfusing rhythm. 60-100 is
        // the conventional adult resting band.
        new PlausibilityRange("HeartRate", "/min", 20m, 300m, 60m, 100m),

        // A saturation reading is a percentage, so the plausible band is the
        // measurable range of the instrument. Below 90 is clinically urgent
        // and entirely real, which is exactly why it must be flagged rather
        // than discarded.
        new PlausibilityRange("OxygenSaturation", "%", 50m, 100m, 94m, 100m),

        // Core temperature outside 25-45 degrees is incompatible with life and
        // therefore an artefact; 36.1-37.8 is the conventional normal band.
        new PlausibilityRange("BodyTemperature", "Cel", 25m, 45m, 36.1m, 37.8m),

        new PlausibilityRange("RespiratoryRate", "/min", 4m, 60m, 12m, 20m),

        new PlausibilityRange("SystolicBloodPressure", "mm[Hg]", 40m, 300m, 90m, 130m),

        new PlausibilityRange("DiastolicBloodPressure", "mm[Hg]", 20m, 200m, 60m, 85m),
    ];
}
