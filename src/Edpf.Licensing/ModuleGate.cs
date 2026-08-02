using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;
using Edpf.Core.Guards;

namespace Edpf.Licensing;

/// <summary>
/// Validates entitlements without network access (Phase 34b).
/// </summary>
/// <remarks>
/// Signature verification arrives through
/// <see cref="IDetachedSignatureVerifier"/>, whose only implementation lives
/// in <c>Edpf.Security</c>. Z.10 confines cryptography to one reviewed
/// assembly, and that rule is worth more than the convenience of calling
/// <c>RSA</c> directly from here.
/// </remarks>
public sealed class EntitlementVerifier
{
    private readonly IDetachedSignatureVerifier _signatures;

    /// <summary>Initializes a verifier.</summary>
    /// <param name="signatures">The signature verifier holding the issuer's public key.</param>
    public EntitlementVerifier(IDetachedSignatureVerifier signatures)
        => _signatures = Guard.NotNull(signatures, nameof(signatures));

    /// <summary>
    /// Checks an entitlement.
    /// </summary>
    /// <param name="entitlement">The entitlement.</param>
    /// <param name="signature">Its detached signature.</param>
    /// <param name="deploymentId">This deployment's identifier.</param>
    /// <param name="now">The current time, from the local clock.</param>
    /// <param name="lastObservedUtc">
    /// The latest time ever observed by a previous check, persisted by the
    /// caller. <see cref="DateTimeOffset.MinValue"/> on first run.
    /// </param>
    /// <returns>The outcome, including the high-water mark to persist.</returns>
    /// <remarks>
    /// <para>
    /// Checks run **signature first**. A tampered entitlement's dates and
    /// deployment id mean nothing, so reporting "expired" for a forged licence
    /// would tell an attacker which field to edit next.
    /// </para>
    /// <para>
    /// The clock-rollback check compares against the highest time ever seen.
    /// An air-gapped machine has no authority to check its clock against, so
    /// winding it back would otherwise revive an expired entitlement
    /// indefinitely.
    /// </para>
    /// </remarks>
    public EntitlementCheck Check(
        Entitlement entitlement,
        byte[] signature,
        string deploymentId,
        DateTimeOffset now,
        DateTimeOffset lastObservedUtc)
    {
        Guard.NotNull(entitlement, nameof(entitlement));
        Guard.NotNull(signature, nameof(signature));
        Guard.NotNullOrWhiteSpace(deploymentId, nameof(deploymentId));

        if (!_signatures.Verify(entitlement.CanonicalBytes(), signature))
        {
            // Nothing else in the entitlement is trustworthy, so nothing else
            // is reported. "Expired" for a forgery would say which field to
            // edit next.
            return new EntitlementCheck(
                EntitlementStatus.SignatureInvalid,
                null,
                lastObservedUtc,
                "The entitlement signature does not verify.");
        }

        // Past this point the content is authentic, so the high-water mark can
        // be advanced from a trusted reading of the clock.
        DateTimeOffset highWater = now > lastObservedUtc ? now : lastObservedUtc;

        if (now < lastObservedUtc)
        {
            return new EntitlementCheck(
                EntitlementStatus.ClockRolledBack,
                entitlement,
                lastObservedUtc,
                $"The system clock reads {now:O}, earlier than the {lastObservedUtc:O} already observed. "
                + "An offline deployment cannot check its clock against an authority, so a backward jump "
                + "is treated as tampering rather than as drift.");
        }

        if (!string.Equals(entitlement.DeploymentId, deploymentId, StringComparison.Ordinal))
        {
            return new EntitlementCheck(
                EntitlementStatus.WrongDeployment,
                entitlement,
                highWater,
                "The entitlement was issued to a different deployment.");
        }

        if (now < entitlement.IssuedUtc)
        {
            return new EntitlementCheck(
                EntitlementStatus.NotYetValid, entitlement, highWater,
                "The entitlement is not yet in force.");
        }

        if (now >= entitlement.ExpiresUtc)
        {
            return new EntitlementCheck(
                EntitlementStatus.Expired, entitlement, highWater,
                $"The entitlement expired at {entitlement.ExpiresUtc:O}.");
        }

        return new EntitlementCheck(
            EntitlementStatus.Valid, entitlement, highWater, "The entitlement is valid.");
    }
}

/// <summary>
/// Decides which modules a deployment may use (Phase 34b).
/// </summary>
/// <remarks>
/// <para>
/// **A licence check must never become a patient-safety hazard.** This is the
/// load-bearing decision in the whole phase, and it is enforced structurally
/// rather than documented as guidance.
/// </para>
/// <para>
/// Entitlement gates *features*. It does not gate reading data that already
/// exists, writing audit, or break-glass. A commercial control able to lock
/// clinicians out of the record — because a licence lapsed over a bank
/// holiday, or a clock drifted, or a renewal email went to someone who left —
/// is a hazard the vendor introduced into a hospital. <see cref="Register"/>
/// refuses to express it.
/// </para>
/// <para>
/// And a disabled module is **invisible, not error-producing**. Surfacing
/// "this feature requires a licence" inside a clinical workflow trains people
/// to click past warnings, which is the behaviour you least want when a real
/// one appears.
/// </para>
/// </remarks>
public sealed class ModuleGate
{
    private readonly HashSet<string> _enabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Capabilities that entitlement may never disable.
    /// </summary>
    /// <remarks>
    /// Reading an existing record, writing audit, and break-glass. Each is
    /// something a clinician or an investigator may need at a moment when the
    /// commercial relationship is the least important fact in the room.
    /// </remarks>
    public static IReadOnlyCollection<string> NeverGateable => NeverGateableSet;

    private static readonly HashSet<string> NeverGateableSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "core.read",
        "core.audit.write",
        "core.breakglass",
        "core.export.subjectaccess",
    };

    /// <summary>
    /// Registers a module as gateable.
    /// </summary>
    /// <param name="moduleName">The module.</param>
    /// <returns>This gate, for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// The module is one entitlement may never disable.
    /// </exception>
    public ModuleGate Register(string moduleName)
    {
        Guard.NotNullOrWhiteSpace(moduleName, nameof(moduleName));

        if (NeverGateableSet.Contains(moduleName))
        {
            throw new ArgumentException(
                $"'{moduleName}' cannot be placed behind an entitlement. A licence check that can stop a "
                + "clinician reading an existing record, stop an audit being written, or stop "
                + "break-glass is a patient-safety hazard introduced by a commercial control.",
                nameof(moduleName));
        }

        _known.Add(moduleName);
        return this;
    }

    /// <summary>
    /// Applies an entitlement check's outcome.
    /// </summary>
    /// <param name="check">The outcome.</param>
    /// <returns>Success, or a failure when the entitlement was not valid.</returns>
    /// <remarks>
    /// An invalid entitlement disables every gateable module and leaves the
    /// rest running. **The system degrades; it does not stop.**
    /// </remarks>
    public Result Apply(EntitlementCheck check)
    {
        Guard.NotNull(check, nameof(check));

        _enabled.Clear();

        if (!check.IsValid)
        {
            return Result.Failure(new Error(
                ErrorCodes.ConfigurationInvalid, check.Reason, ErrorCategory.Validation));
        }

        foreach (string module in check.Entitlement!.Modules)
        {
            // An entitlement naming a module this build does not have is not
            // an error: entitlements outlive releases, and a licence issued
            // for next year's module list must not stop this year's binary
            // starting.
            if (_known.Contains(module))
            {
                _enabled.Add(module);
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// Whether a capability is available.
    /// </summary>
    /// <param name="capability">The module or capability name.</param>
    /// <returns>Whether it may be used.</returns>
    /// <remarks>
    /// A never-gateable capability answers true whatever the entitlement says
    /// — including when no entitlement has been applied at all, which is the
    /// state a system is in while it is starting up or after a licence file
    /// has been deleted.
    /// </remarks>
    public bool IsAvailable(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability))
        {
            return false;
        }

        return NeverGateableSet.Contains(capability) || _enabled.Contains(capability);
    }

    /// <summary>The gateable modules currently enabled.</summary>
    public IReadOnlyCollection<string> EnabledModules => _enabled;
}
