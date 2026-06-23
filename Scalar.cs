namespace HAL9001;

/// <summary>
/// A multiplication-COUNTING scalar — the fitness instrument for the algorithm-novelty half of the
/// Prime Directive race (bite 15). At small matrix sizes wall-clock timing is pure noise (a 3×3
/// multiply finishes in nanoseconds), so we don't rank by speed there — we rank by the number of
/// scalar MULTIPLICATIONS the algorithm performs, which is exactly where Strassen-style novelty
/// lives (2×2 in 7 mults instead of 8 → O(n^2.807)).
///
/// CHEAT-PROOF BY CONSTRUCTION. A candidate is compiled into a SEPARATE assembly. Because the raw
/// value (<see cref="_v"/>), the counters, and <see cref="ToDouble"/> are all <c>internal</c>, the
/// candidate assembly cannot see any of them — it can ONLY combine Scalars through the public
/// <c>+ - *</c> operators. Therefore:
///   • every multiplication of two Scalars MUST go through <see cref="op_Multiply"/>, so the count
///     is exact and unfakeable (the candidate can't extract the doubles and multiply them raw, and
///     can't reset or read the counter to lie about it);
///   • the race harness (same assembly as Scalar) CAN read the value via <see cref="ToDouble"/> to
///     verify correctness, and read/reset the counters around a run.
///
/// Multiplications are the ranked metric; additions/subtractions are tracked for information only
/// (Strassen trades muls for adds, so adds aren't penalised). A candidate that wants to scale by a
/// small integer constant should do it as repeated addition (x+x, not 2*x) to avoid spending a mul.
///
/// Counting is done on a SINGLE, sequential run per candidate (the race never evaluates candidates
/// in parallel), so a plain static counter is sufficient — no locking or thread-local needed.
/// </summary>
public readonly struct Scalar
{
    private readonly double _v;   // internal value — invisible to the candidate assembly
    public Scalar(double v) => _v = v;   // public ctor: building inputs/constants is free (no op counted)

    // Counters live in HAL9001 and are internal: a candidate can neither read nor reset them.
    internal static long Muls;
    internal static long Adds;
    internal static void ResetCounters() { Muls = 0; Adds = 0; }

    // The ONLY way two Scalars can be multiplied — so this count is the true multiplication count.
    public static Scalar operator *(Scalar a, Scalar b) { Muls++; return new Scalar(a._v * b._v); }
    public static Scalar operator +(Scalar a, Scalar b) { Adds++; return new Scalar(a._v + b._v); }
    public static Scalar operator -(Scalar a, Scalar b) { Adds++; return new Scalar(a._v - b._v); }
    // Unary negation is FREE — scaling by -1 costs no multiplication. Synthesized decompositions
    // (bite 17) use this to render {-1} coefficients without spending a counted multiply.
    public static Scalar operator -(Scalar a) => new Scalar(-a._v);

    // Verification-only readout, internal so a candidate can't extract values to multiply raw doubles.
    internal double ToDouble() => _v;

    /// <summary>Wrap a double[,] as a Scalar[,] for feeding a counting candidate.</summary>
    internal static Scalar[,] From(double[,] m)
    {
        int n = m.GetLength(0), k = m.GetLength(1);
        var s = new Scalar[n, k];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < k; j++)
                s[i, j] = new Scalar(m[i, j]);
        return s;
    }
}
