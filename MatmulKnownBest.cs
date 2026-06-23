namespace HAL9001;

/// <summary>
/// The bar that separates "new to the hive" from "new to the world" (bite 16). The mult-count race
/// will constantly beat its OWN previous best — that is not a discovery, just the loop working. A
/// genuine finding is a CORRECT algorithm that uses fewer scalar multiplications than the best result
/// KNOWN TO HUMANITY for that size. This table holds those known-best counts (and, where proven, the
/// lower bound) so the race can tell the two apart.
///
/// Sources (small, famous, well-established cases — deliberately conservative):
///   • 2×2 = 7 (Strassen 1969), proven optimal (lower bound 7).
///   • 3×3 = 23 (Laderman 1976); best proven lower bound 19 (Bläser 2003) — the exact optimum is an
///     OPEN problem, known only to lie in [19, 23]. A correct 22 here would be a real advance.
///   • 4×4 = 49 (two-level Strassen) as the general-ring practical best; the exact proven lower bound
///     is left unset (0 = unknown) because it is field-dependent and we won't assert one we're unsure
///     of. (AlphaTensor's 47 and AlphaEvolve's 48 are field-specific — GF(2) / complex — not plain.)
///
/// A size with no entry returns <see cref="Verdict.NoTarget"/>: a correct result is still recorded,
/// but the hive makes no novelty CLAIM, because it has no trustworthy bar to compare against.
/// </summary>
public static class MatmulKnownBest
{
    // size → (best known mult count, proven lower bound). Lower = 0 means "lower bound unknown here".
    private static readonly Dictionary<int, (int Best, int Lower)> Table = new()
    {
        [2] = (7, 7),
        [3] = (23, 19),
        [4] = (49, 0),
    };

    public enum Verdict
    {
        NoTarget,        // no trustworthy known-best for this size — record, don't claim
        Rediscovery,     // matched or above known-best — the loop working, not news
        BeatsKnownBest,  // fewer muls than the best known to humanity — a genuine candidate discovery
        BelowLowerBound, // fewer muls than a PROVEN lower bound — impossible ⇒ our verification has a bug
    }

    /// <summary>Classify a verified multiplication count for a size against what humanity knows.</summary>
    public static (Verdict V, int Best, int Lower) Classify(int size, long muls)
    {
        if (!Table.TryGetValue(size, out var e)) return (Verdict.NoTarget, -1, -1);
        if (e.Lower > 0 && muls < e.Lower) return (Verdict.BelowLowerBound, e.Best, e.Lower);
        if (muls < e.Best) return (Verdict.BeatsKnownBest, e.Best, e.Lower);
        return (Verdict.Rediscovery, e.Best, e.Lower);
    }
}
