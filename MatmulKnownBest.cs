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

    /// <summary>The PROVEN lower bound for a size, or 0 when none is known here. Used to avoid hunting
    /// a rank that mathematics has already ruled out: searching 2×2 for rank 6 can never succeed
    /// (7 is proven optimal), and burning the mesh's CPU on it forever is not "thinking", it is a
    /// treadmill. A size whose champion already equals its proven bound is SOLVED — move on.</summary>
    public static int ProvenLower(int size) => Table.TryGetValue(size, out var e) ? e.Lower : 0;

    /// <summary>Every size where humanity's best is on record here. Exposed so the dashboard can label a
    /// champion "closed / behind / matches" from the SAME table the race judges against, instead of
    /// hardcoding the mathematics in client JavaScript where it would silently drift out of date.</summary>
    public static IEnumerable<(int Size, int Best, int Lower, bool Closed)> All =>
        Table.OrderBy(e => e.Key)
             .Select(e => (e.Key, e.Value.Best, e.Value.Lower, IsClosed(e.Key)));

    /// <summary>True when humanity's best result for this size EQUALS a proven lower bound — the size is
    /// mathematically closed and no better algorithm can exist (2×2 at 7). Searching a closed size can
    /// never succeed, so the free search should spend its rounds somewhere a result is still possible.
    /// Sizes with no entry are NOT closed: unknown is not the same as finished.</summary>
    public static bool IsClosed(int size) =>
        Table.TryGetValue(size, out var e) && e.Lower > 0 && e.Best <= e.Lower;

    /// <summary>True if a search for exactly this rank is worth starting at all.</summary>
    public static bool WorthAttempting(int size, long rank)
    {
        int lower = ProvenLower(size);
        return lower <= 0 || rank >= lower;
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
