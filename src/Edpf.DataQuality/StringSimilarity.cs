using System;
using Edpf.Core.Guards;

namespace Edpf.DataQuality;

/// <summary>
/// String distance measures for duplicate detection (Phase 23d).
/// </summary>
/// <remarks>
/// <para>
/// Feeds Phase 23's entity resolution. **These functions produce a similarity;
/// they do not decide that two records are the same person.** That decision
/// needs a threshold, and a threshold needs a labelled set built by people who
/// know the domain — the cost of a false merge is a chart carrying two
/// people's allergies, and the cost of a false non-merge is a duplicate
/// record. Those are not symmetric, so the threshold is not a technical
/// choice.
/// </para>
/// <para>
/// Everything here is ordinal and culture-independent. A similarity that
/// varied with the server's culture would make the same pair of names match in
/// one region and not another (Phase 27).
/// </para>
/// </remarks>
public static class StringSimilarity
{
    /// <summary>
    /// The Levenshtein edit distance between two strings.
    /// </summary>
    /// <param name="first">The first string.</param>
    /// <param name="second">The second string.</param>
    /// <returns>The number of single-character edits needed to turn one into the other.</returns>
    public static int Levenshtein(string first, string second)
    {
        Guard.NotNull(first, nameof(first));
        Guard.NotNull(second, nameof(second));

        if (first.Length == 0)
        {
            return second.Length;
        }

        if (second.Length == 0)
        {
            return first.Length;
        }

        // Two rows rather than the full matrix: the algorithm only ever reads
        // the previous row, and a full matrix over two long free-text fields
        // is a memory profile nobody budgeted for.
        int[] previous = new int[second.Length + 1];
        int[] current = new int[second.Length + 1];

        for (int j = 0; j <= second.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= first.Length; i++)
        {
            current[0] = i;

            for (int j = 1; j <= second.Length; j++)
            {
                int substitution = previous[j - 1] + (first[i - 1] == second[j - 1] ? 0 : 1);
                int deletion = previous[j] + 1;
                int insertion = current[j - 1] + 1;

                current[j] = Math.Min(substitution, Math.Min(deletion, insertion));
            }

            (previous, current) = (current, previous);
        }

        return previous[second.Length];
    }

    /// <summary>
    /// The Jaro similarity of two strings, from 0 to 1.
    /// </summary>
    /// <param name="first">The first string.</param>
    /// <param name="second">The second string.</param>
    /// <returns>1 for identical strings, 0 for no shared characters in range.</returns>
    public static decimal Jaro(string first, string second)
    {
        Guard.NotNull(first, nameof(first));
        Guard.NotNull(second, nameof(second));

        if (first.Length == 0 && second.Length == 0)
        {
            return 1m;
        }

        if (first.Length == 0 || second.Length == 0)
        {
            return 0m;
        }

        if (string.Equals(first, second, StringComparison.Ordinal))
        {
            return 1m;
        }

        // Characters count as matching only within this window, which is what
        // stops two long strings sharing a common alphabet from scoring high.
        int window = Math.Max(0, (Math.Max(first.Length, second.Length) / 2) - 1);

        bool[] firstMatched = new bool[first.Length];
        bool[] secondMatched = new bool[second.Length];
        int matches = 0;

        for (int i = 0; i < first.Length; i++)
        {
            int start = Math.Max(0, i - window);
            int end = Math.Min(i + window + 1, second.Length);

            for (int j = start; j < end; j++)
            {
                if (secondMatched[j] || first[i] != second[j])
                {
                    continue;
                }

                firstMatched[i] = true;
                secondMatched[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0)
        {
            return 0m;
        }

        int transpositions = 0;
        int k = 0;

        for (int i = 0; i < first.Length; i++)
        {
            if (!firstMatched[i])
            {
                continue;
            }

            while (!secondMatched[k])
            {
                k++;
            }

            if (first[i] != second[k])
            {
                transpositions++;
            }

            k++;
        }

        decimal m = matches;
        decimal t = transpositions / 2m;

        return ((m / first.Length) + (m / second.Length) + ((m - t) / m)) / 3m;
    }

    /// <summary>
    /// The Jaro-Winkler similarity of two strings, from 0 to 1.
    /// </summary>
    /// <param name="first">The first string.</param>
    /// <param name="second">The second string.</param>
    /// <param name="scalingFactor">The prefix weight; 0.1 is Winkler's value and the maximum permitted.</param>
    /// <returns>The similarity.</returns>
    /// <remarks>
    /// Boosts pairs sharing a prefix, which suits personal names: people
    /// mistype and abbreviate the ends of names far more often than the
    /// beginnings. The prefix considered is capped at four characters, above
    /// which the boost can push genuinely different names past a threshold.
    /// </remarks>
    public static decimal JaroWinkler(string first, string second, decimal scalingFactor = 0.1m)
    {
        if (scalingFactor < 0m || scalingFactor > 0.25m)
        {
            // Above 0.25 the boost can exceed the headroom and push the result
            // past 1, which turns a similarity into a nonsense number.
            throw new ArgumentOutOfRangeException(
                nameof(scalingFactor), scalingFactor, "The scaling factor must be between 0 and 0.25.");
        }

        decimal jaro = Jaro(first, second);

        int prefix = 0;
        int limit = Math.Min(4, Math.Min(first.Length, second.Length));

        while (prefix < limit && first[prefix] == second[prefix])
        {
            prefix++;
        }

        return jaro + (prefix * scalingFactor * (1m - jaro));
    }

    /// <summary>
    /// Normalized edit similarity, from 0 to 1.
    /// </summary>
    /// <param name="first">The first string.</param>
    /// <param name="second">The second string.</param>
    /// <returns>1 for identical strings.</returns>
    public static decimal EditSimilarity(string first, string second)
    {
        Guard.NotNull(first, nameof(first));
        Guard.NotNull(second, nameof(second));

        int longest = Math.Max(first.Length, second.Length);
        return longest == 0 ? 1m : 1m - ((decimal)Levenshtein(first, second) / longest);
    }
}
