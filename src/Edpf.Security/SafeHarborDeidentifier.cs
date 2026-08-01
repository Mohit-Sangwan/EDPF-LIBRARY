using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Edpf.Abstractions.Security;
using Edpf.Core.Guards;

namespace Edpf.Security;

/// <summary>
/// HIPAA Safe Harbor de-identification (45 CFR §164.514(b)(2)) with
/// per-subject consistent date shifting.
/// </summary>
/// <remarks>
/// <para>
/// Two rules carry the safety property, and both are the fail-closed
/// direction:
/// </para>
/// <list type="number">
/// <item>An **unmapped field is removed, not passed through** (unless the
/// policy explicitly opts out). Safe Harbor requires the absence of all
/// eighteen categories, and a field nobody classified is a field nobody
/// checked.</item>
/// <item>**Ages over 89 are aggregated to "90+"** and dates are reduced to
/// their year, because §164.514(b)(2)(i)(C) treats finer granularity as
/// identifying — a birth date plus a ZIP code re-identifies most people.</item>
/// </list>
/// </remarks>
public sealed class SafeHarborDeidentifier : IDeidentifier
{
    /// <summary>What a removed value is replaced with.</summary>
    public const string RemovedMarker = "[removed]";

    /// <summary>How ages above the threshold are reported.</summary>
    public const string AggregatedAge = "90+";

    /// <summary>Ages at or above this are aggregated (§164.514(b)(2)(i)(C)).</summary>
    public const int AgeAggregationThreshold = 90;

    /// <summary>Maximum date-shift magnitude in days, either direction.</summary>
    public const int MaxDateShiftDays = 364;

    private readonly byte[] _shiftSalt;

    /// <summary>
    /// Initializes the de-identifier.
    /// </summary>
    /// <param name="dateShiftSalt">
    /// Secret salt for date-shift derivation. Held under separate control
    /// from the de-identified data: whoever holds both can undo the shift,
    /// which is the whole point of keeping them apart.
    /// </param>
    /// <exception cref="ArgumentException">The salt is null or empty.</exception>
    public SafeHarborDeidentifier(byte[] dateShiftSalt)
    {
        if (dateShiftSalt is null || dateShiftSalt.Length == 0)
        {
            throw new ArgumentException(
                "A date-shift salt is required; an unsalted shift is reversible by anyone who can guess the scheme.",
                nameof(dateShiftSalt));
        }

        _shiftSalt = (byte[])dateShiftSalt.Clone();
    }

    /// <inheritdoc />
    public DeidentificationResult ApplySafeHarbor(
        IReadOnlyDictionary<string, object?> values, SafeHarborPolicy policy)
    {
        Guard.NotNull(values, nameof(values));
        Guard.NotNull(policy, nameof(policy));

        var output = new Dictionary<string, object?>(StringComparer.Ordinal);
        var removed = new HashSet<SafeHarborIdentifier>();
        var unmapped = new List<string>();

        string? subjectToken = policy.SubjectTokenField is not null
            && values.TryGetValue(policy.SubjectTokenField, out object? token)
                ? Convert.ToString(token, CultureInfo.InvariantCulture)
                : null;

        foreach (KeyValuePair<string, object?> field in values)
        {
            if (string.Equals(field.Key, policy.SubjectTokenField, StringComparison.Ordinal))
            {
                // The pseudonymous token is not an identifier and is retained
                // so the de-identified record remains linkable to itself.
                output[field.Key] = field.Value;
                continue;
            }

            if (!policy.FieldIdentifiers.TryGetValue(field.Key, out SafeHarborIdentifier identifier))
            {
                unmapped.Add(field.Key);

                if (policy.RejectUnmappedFields)
                {
                    output[field.Key] = RemovedMarker;
                }
                else
                {
                    output[field.Key] = field.Value;
                }

                continue;
            }

            removed.Add(identifier);
            output[field.Key] = Transform(identifier, field.Value, subjectToken);
        }

        return new DeidentificationResult(output, removed, unmapped);
    }

    /// <inheritdoc />
    public DateTime ShiftDate(DateTime date, string subjectToken)
    {
        Guard.NotNullOrWhiteSpace(subjectToken, nameof(subjectToken));

        // Derived from the subject, so every date for that subject moves by
        // the same amount and intervals survive; salted, so the offset cannot
        // be recomputed without the salt.
        //
        // Instance API rather than the static HashData: the static overload
        // does not exist on Tier 3 TFMs (ADR-002).
        using var hmac = new HMACSHA256(_shiftSalt);
        byte[] mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(subjectToken));
        int magnitude = BitConverter.ToInt32(mac, 0) & int.MaxValue;
        int offset = (magnitude % ((MaxDateShiftDays * 2) + 1)) - MaxDateShiftDays;

        return date.Date.AddDays(offset);
    }

    private object? Transform(SafeHarborIdentifier identifier, object? value, string? subjectToken)
    {
        if (value is null)
        {
            return null;
        }

        return identifier switch
        {
            // Explicitly classified as carrying no identifier: passes through.
            SafeHarborIdentifier.None => value,

            // Dates reduce to their year unless a subject token allows a
            // consistent shift, which preserves clinical intervals.
            SafeHarborIdentifier.DateElement => TransformDate(value, subjectToken),

            // ZIP codes keep at most their first three digits, and only where
            // the population rule permits — the caller supplies already-
            // truncated values; anything longer is removed.
            SafeHarborIdentifier.GeographicSubdivision => TruncateGeography(value),

            _ => RemovedMarker,
        };
    }

    private object TransformDate(object value, string? subjectToken)
    {
        if (value is int age)
        {
            return age >= AgeAggregationThreshold ? AggregatedAge : age.ToString(CultureInfo.InvariantCulture);
        }

        DateTime? date = value switch
        {
            DateTime dt => dt,
            DateTimeOffset dto => dto.UtcDateTime,
            _ => null,
        };

        if (date is null)
        {
            return RemovedMarker;
        }

        return subjectToken is null
            ? date.Value.Year.ToString(CultureInfo.InvariantCulture)
            : ShiftDate(date.Value, subjectToken);
    }

    private static string TruncateGeography(object value)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

        // Only the first three digits of a ZIP may remain, and only for
        // sufficiently populous areas. Anything that is not a plain numeric
        // code is removed rather than guessed at.
        if (text.Length < 3)
        {
            return RemovedMarker;
        }

        for (int i = 0; i < 3; i++)
        {
            if (!char.IsDigit(text[i]))
            {
                return RemovedMarker;
            }
        }

        return text.Substring(0, 3);
    }
}
