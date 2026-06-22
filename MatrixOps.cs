namespace HAL9001;

/// <summary>
/// Dense matrix-multiply REFERENCE + the numeric helpers the kernel-optimization search
/// (bite 1) is built around. Everything a candidate is judged against lives here:
///
///   • <see cref="MultiplyReference"/> — the known-correct naive triple loop. It is BOTH
///     the correctness oracle (every candidate's output must match it within a tolerance)
///     AND the speed baseline (every candidate's time is reported as a speedup over it).
///   • deterministic random matrices (seeded, so a run is reproducible),
///   • a tolerance-based comparison (floating-point reordering means candidates won't be
///     bit-identical, so exact equality is the wrong test — see <see cref="Compare"/>),
///   • a cheap checksum used to defeat dead-code elimination during benchmarking.
///
/// Matrices are <c>double[,]</c> with the convention A is n×k, B is k×m, C is n×m.
/// </summary>
public static class MatrixOps
{
    /// <summary>
    /// The reference: textbook naive i-j-k triple loop. Deliberately the SIMPLE, obviously
    /// correct version — its job is to be trustworthy, not fast. (It is also a perfectly
    /// valid "candidate" in spirit: the fastest candidate's speedup is measured against
    /// exactly this code's measured time.)
    /// </summary>
    public static double[,] MultiplyReference(double[,] a, double[,] b)
    {
        int n = a.GetLength(0);   // rows of A  → rows of C
        int k = a.GetLength(1);   // cols of A == rows of B (the contraction dimension)
        int m = b.GetLength(1);   // cols of B  → cols of C

        var c = new double[n, m];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < m; j++)
            {
                double sum = 0.0;
                for (int p = 0; p < k; p++)
                    sum += a[i, p] * b[p, j];
                c[i, j] = sum;
            }
        return c;
    }

    /// <summary>A deterministically-seeded random matrix with entries in [0, 1).</summary>
    /// We use [0, 1) on purpose: all-positive entries mean the dot-product sums never sit
    /// near zero, so RELATIVE error stays well-behaved and the tolerance check is meaningful
    /// (no catastrophic cancellation to muddy "is this candidate actually correct?").
    public static double[,] RandomMatrix(int rows, int cols, Random rng)
    {
        var x = new double[rows, cols];
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < cols; j++)
                x[i, j] = rng.NextDouble();
        return x;
    }

    /// <summary>
    /// Compare a candidate's output against the reference within a tolerance.
    ///
    /// WHY A TOLERANCE, NOT EQUALITY: floating-point addition is not associative, so summing
    /// the k products in a different order (which every interesting optimization does — tiling,
    /// reordered loops, transposing B) yields a slightly different rounding. A correct candidate
    /// therefore differs from the reference by a tiny amount (~k·machine-epsilon ≈ 1e-13 for our
    /// sizes), while a genuinely WRONG candidate is off by O(1). The gap between those is
    /// enormous, so any tolerance in between cleanly separates "correct" from "buggy".
    ///
    /// We test BOTH an absolute and a relative bound: a pair passes if
    /// <c>|got - want| ≤ atol + rtol·|want|</c> for every element. Dimensions must match too —
    /// a candidate that returns the wrong shape fails immediately. Returns the worst element
    /// errors seen so the report can show HOW correct (or how wrong) a candidate was.
    /// </summary>
    public static bool Compare(
        double[,] want, double[,] got,
        double atol, double rtol,
        out double maxAbsErr, out double maxRelErr)
    {
        maxAbsErr = double.PositiveInfinity;
        maxRelErr = double.PositiveInfinity;

        if (got is null) return false;
        if (got.GetLength(0) != want.GetLength(0) || got.GetLength(1) != want.GetLength(1))
            return false; // wrong shape ⇒ disqualified, no point measuring elements

        int rows = want.GetLength(0), cols = want.GetLength(1);
        double worstAbs = 0.0, worstRel = 0.0;
        bool ok = true;

        for (int i = 0; i < rows; i++)
            for (int j = 0; j < cols; j++)
            {
                double w = want[i, j], g = got[i, j];

                // NaN/Infinity in a candidate's output is an automatic fail.
                if (double.IsNaN(g) || double.IsInfinity(g)) { ok = false; g = w + 1e9; }

                double abs = Math.Abs(g - w);
                double rel = abs / (Math.Abs(w) + double.Epsilon);
                if (abs > worstAbs) worstAbs = abs;
                if (rel > worstRel) worstRel = rel;

                if (abs > atol + rtol * Math.Abs(w)) ok = false;
            }

        maxAbsErr = worstAbs;
        maxRelErr = worstRel;
        return ok;
    }

    /// <summary>
    /// A cheap O(rows+cols) checksum of a result matrix. Used ONLY to defeat dead-code
    /// elimination: the benchmark accumulates this into a value it later prints, so the JIT
    /// can't prove a candidate's output is unused and optimize the whole multiply away. We
    /// touch the four corners plus the diagonal — enough that the result must be materialised,
    /// negligible next to the O(n³) multiply itself.
    /// </summary>
    public static double Checksum(double[,] c)
    {
        int n = c.GetLength(0), m = c.GetLength(1);
        double s = c[0, 0] + c[n - 1, m - 1] + c[0, m - 1] + c[n - 1, 0];
        int d = Math.Min(n, m);
        for (int i = 0; i < d; i++) s += c[i, i];
        return s;
    }
}
