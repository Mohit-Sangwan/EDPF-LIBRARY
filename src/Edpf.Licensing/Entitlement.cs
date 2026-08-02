using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Edpf.Core.Guards;

namespace Edpf.Licensing;

/// <summary>
/// What a deployment is entitled to run (Phase 34b).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately short-lived. **An offline system cannot ask whether a licence
/// was revoked**, so expiry is the only revocation mechanism that exists, and
/// a three-year entitlement is a three-year window in which withdrawal is
/// impossible. Re-issuing quarterly is the trade: more operational friction,
/// and a revocation that actually takes effect.
/// </para>
/// <para>
/// The canonical form is what gets signed. It is built here rather than by
/// serialising the object, because a serializer's field ordering, culture
/// handling and version behaviour are all free to change underneath a
/// signature — and a signature that stops verifying after a library upgrade is
/// indistinguishable from one that was tampered with.
/// </para>
/// </remarks>
public sealed class Entitlement
{
    /// <summary>Initializes an entitlement.</summary>
    /// <param name="deploymentId">Which deployment it was issued to.</param>
    /// <param name="modules">The modules enabled, by name.</param>
    /// <param name="issuedUtc">When it was issued.</param>
    /// <param name="expiresUtc">When it lapses.</param>
    /// <param name="issuer">Who issued it.</param>
    /// <exception cref="ArgumentException">The validity window is inverted or empty.</exception>
    public Entitlement(
        string deploymentId,
        IReadOnlyList<string> modules,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        string issuer)
    {
        DeploymentId = Guard.NotNullOrWhiteSpace(deploymentId, nameof(deploymentId));
        Modules = Guard.NotNull(modules, nameof(modules));
        IssuedUtc = issuedUtc;
        ExpiresUtc = expiresUtc;
        Issuer = Guard.NotNullOrWhiteSpace(issuer, nameof(issuer));

        if (expiresUtc <= issuedUtc)
        {
            throw new ArgumentException(
                "An entitlement must expire after it was issued.", nameof(expiresUtc));
        }
    }

    /// <summary>Which deployment it was issued to.</summary>
    public string DeploymentId { get; }

    /// <summary>The modules enabled, by name.</summary>
    public IReadOnlyList<string> Modules { get; }

    /// <summary>When it was issued.</summary>
    public DateTimeOffset IssuedUtc { get; }

    /// <summary>When it lapses.</summary>
    public DateTimeOffset ExpiresUtc { get; }

    /// <summary>Who issued it.</summary>
    public string Issuer { get; }

    /// <summary>
    /// The exact bytes a signature covers.
    /// </summary>
    /// <returns>The canonical encoding.</returns>
    /// <remarks>
    /// <para>
    /// Field-tagged, length-prefixed and sorted, all for the same reason: two
    /// different entitlements must never produce the same bytes. Without
    /// length prefixes, a deployment id ending in a module name and a module
    /// list beginning with one would collide.
    /// </para>
    /// <para>
    /// Timestamps are round-trip UTC and the culture is invariant, so the same
    /// entitlement canonicalises identically on a server in any region — a
    /// signature that verified in London and failed in Istanbul would be
    /// indistinguishable from tampering (Phase 27).
    /// </para>
    /// </remarks>
    public byte[] CanonicalBytes()
    {
        var canonical = new StringBuilder();

        Append(canonical, "deployment", DeploymentId);
        Append(canonical, "issuer", Issuer);
        Append(canonical, "issued", IssuedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Append(canonical, "expires", ExpiresUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        // Sorted, so the same entitlement signs identically whatever order the
        // issuer happened to list the modules in.
        var modules = new List<string>(Modules);
        modules.Sort(StringComparer.Ordinal);

        foreach (string module in modules)
        {
            Append(canonical, "module", module);
        }

        return Encoding.UTF8.GetBytes(canonical.ToString());
    }

    private static void Append(StringBuilder builder, string tag, string value)
        => builder.Append(tag).Append(':')
            .Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':')
            .Append(value).Append(';');
}

/// <summary>Why an entitlement was not accepted (Phase 34b).</summary>
public enum EntitlementStatus
{
    /// <summary>Valid and in date.</summary>
    Valid = 0,

    /// <summary>The signature does not verify.</summary>
    SignatureInvalid = 1,

    /// <summary>Issued to a different deployment.</summary>
    WrongDeployment = 2,

    /// <summary>Not yet in force.</summary>
    NotYetValid = 3,

    /// <summary>Past its expiry.</summary>
    Expired = 4,

    /// <summary>
    /// The system clock has moved backwards past a previously observed time.
    /// </summary>
    /// <remarks>
    /// The offline attack: an air-gapped machine has no authority to check its
    /// clock against, so winding it back revives an expired entitlement
    /// indefinitely.
    /// </remarks>
    ClockRolledBack = 5,
}

/// <summary>The outcome of checking an entitlement (Phase 34b).</summary>
public sealed class EntitlementCheck
{
    /// <summary>Initializes an outcome.</summary>
    /// <param name="status">Whether it was accepted, and why not.</param>
    /// <param name="entitlement">The entitlement, when the signature verified.</param>
    /// <param name="highWaterMark">The latest time ever observed, to persist for the next check.</param>
    /// <param name="reason">A human-readable explanation.</param>
    public EntitlementCheck(
        EntitlementStatus status,
        Entitlement? entitlement,
        DateTimeOffset highWaterMark,
        string reason)
    {
        Status = status;
        Entitlement = entitlement;
        HighWaterMark = highWaterMark;
        Reason = Guard.NotNullOrWhiteSpace(reason, nameof(reason));
    }

    /// <summary>Whether it was accepted, and why not.</summary>
    public EntitlementStatus Status { get; }

    /// <summary>The entitlement, when the signature verified.</summary>
    public Entitlement? Entitlement { get; }

    /// <summary>
    /// The latest time ever observed, to be persisted and supplied to the next
    /// check.
    /// </summary>
    /// <remarks>
    /// The clock-rollback defence in one value. It only works if the caller
    /// stores it somewhere the operator cannot trivially reset — which is a
    /// deployment concern this type cannot enforce and the phase report states
    /// plainly.
    /// </remarks>
    public DateTimeOffset HighWaterMark { get; }

    /// <summary>A human-readable explanation.</summary>
    public string Reason { get; }

    /// <summary>Whether the entitlement may be relied on.</summary>
    public bool IsValid => Status == EntitlementStatus.Valid;
}
