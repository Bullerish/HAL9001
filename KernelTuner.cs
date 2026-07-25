using System.Text;

namespace HAL9001;

/// <summary>
/// LLM-FREE AUTOTUNING for the wall-clock half of the Prime Directive race.
///
/// The mult-count track has always had a free engine (tensor search, and now
/// <see cref="SchemeCompose"/>). The wall-clock track did not: every candidate kernel was
/// LLM-authored, so with no funded budget the big sizes (64/128/256) produced nothing at all —
/// rounds returned no candidate and the ladder just ticked its plateau counter. That is the gap
/// this closes. Autotuning does not need a language model; it needs a parameter space and a clock.
///
/// THE SPACE. Four families, each a template with knobs:
///   • ikj        — loop order that streams both B and C along rows (the single biggest cache win)
///   • transposed — copy B into row-major order first, then take dot products with unit stride
///   • blocked    — cache tiling with tile T, inner loops in ikj order
///   • strassen   — the recursive sub-cubic algorithm from <see cref="SchemeCompose"/> over
///                  doubles, with a cutoff C below which it falls back to a tuned base kernel
///
/// THE SEARCH. Each round starts from the CHAMPION's parameters (they are recorded in its strategy
/// string) and proposes NEIGHBOURS — one knob moved at a time — plus one fresh random point so the
/// climb can escape a local optimum. Correctness is still the floor: every candidate is compiled,
/// checked against the naive reference, and only then timed. This is hill-climbing on real measured
/// hardware behaviour, which is exactly what an autotuner is, and it costs zero tokens.
/// </summary>
public static class KernelTuner
{
    /// <summary>One tunable kernel: a strategy label that encodes its parameters (so the next round
    /// can climb from it) plus the source to compile.</summary>
    public sealed record Candidate(string Strategy, string Source);

    private static readonly int[] Tiles = { 16, 32, 48, 64, 96, 128 };
    private static readonly int[] Cutoffs = { 32, 64, 128 };

    /// <summary>Propose the round's free candidates: neighbours of the champion's parameters, plus a
    /// random point. <paramref name="champStrategy"/> is the champion's label — parameters are read
    /// back out of it, so the climb continues where the last round left off.</summary>
    public static IReadOnlyList<Candidate> Propose(int size, string? champStrategy, int count, Random rng)
    {
        var seen = new HashSet<string>();
        var outp = new List<Candidate>();

        void Add(Candidate? c)
        {
            if (c is null || outp.Count >= count) return;
            if (seen.Add(c.Strategy)) outp.Add(c);
        }

        (string family, int tile, int cut) = ParseStrategy(champStrategy);

        // 1. Neighbours of the current champion — one knob at a time.
        foreach (int t in Neighbours(Tiles, tile)) Add(Build(family, t, cut, size));
        foreach (int c in Neighbours(Cutoffs, cut)) Add(Build(family, tile, c, size));

        // 2. The other families at their default knobs — cheap way to notice a better shape exists.
        foreach (string f in new[] { "ikj", "transposed", "blocked", "strassen" })
            if (f != family) Add(Build(f, tile, cut, size));

        // 3. One random point, so a plateau in the neighbourhood is not the end of the search.
        Add(Build(
            new[] { "ikj", "transposed", "blocked", "strassen" }[rng.Next(4)],
            Tiles[rng.Next(Tiles.Length)], Cutoffs[rng.Next(Cutoffs.Length)], size));

        return outp;
    }

    /// <summary>Read the knobs back out of a strategy label written by <see cref="Build"/>. Unknown
    /// or LLM-authored labels fall back to sensible defaults — the climb just starts fresh.</summary>
    public static (string Family, int Tile, int Cut) ParseStrategy(string? s)
    {
        string family = "blocked";
        int tile = 64, cut = 64;
        if (!string.IsNullOrWhiteSpace(s) && s.StartsWith("tuner:", StringComparison.Ordinal))
        {
            string body = s["tuner:".Length..];
            string[] parts = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0) family = parts[0];
            foreach (string p in parts)
            {
                if (p.StartsWith("T=", StringComparison.Ordinal) && int.TryParse(p[2..], out int t)) tile = t;
                if (p.StartsWith("cut=", StringComparison.Ordinal) && int.TryParse(p[4..], out int c)) cut = c;
            }
        }
        return (family, tile, cut);
    }

    // The immediate neighbours of a value in a knob's ladder (one step either way).
    private static IEnumerable<int> Neighbours(int[] ladder, int value)
    {
        int idx = Array.IndexOf(ladder, value);
        if (idx < 0)
        {
            // Champion used a value off our ladder (or none): start from the closest rung.
            idx = 0;
            for (int i = 1; i < ladder.Length; i++)
                if (Math.Abs(ladder[i] - value) < Math.Abs(ladder[idx] - value)) idx = i;
            yield return ladder[idx];
        }
        if (idx > 0) yield return ladder[idx - 1];
        if (idx < ladder.Length - 1) yield return ladder[idx + 1];
    }

    /// <summary>Emit one kernel. Returns null when the parameters make no sense at this size (e.g. a
    /// tile bigger than the matrix, or a Strassen cutoff that leaves nothing to recurse on).</summary>
    public static Candidate? Build(string family, int tile, int cut, int size)
    {
        switch (family)
        {
            case "ikj":
                return new Candidate("tuner:ikj", Ikj());

            case "transposed":
                return new Candidate("tuner:transposed", Transposed());

            case "blocked":
                if (tile < 8 || tile > size) return null;
                return new Candidate($"tuner:blocked T={tile}", Blocked(tile));

            case "strassen":
            {
                // Only worth it when the size actually splits down to the cutoff a few times.
                if (size % 2 != 0 || cut >= size || cut < 8) return null;
                string src = SchemeCompose.SynthesizeDouble(TensorSearch.Strassen2, cut);
                return new Candidate($"tuner:strassen cut={cut}", src);
            }

            default:
                return null;
        }
    }

    // ── templates ─────────────────────────────────────────────────────────────────────────────
    // All of them expose `public static double[,] Multiply(double[,], double[,])`, which is what the
    // race binds by reflection.

    private static string Ikj() => """
        public static class Kernel {
          public static double[,] Multiply(double[,] A, double[,] B) {
            int n = A.GetLength(0), p = B.GetLength(1), m = B.GetLength(0);
            var C = new double[n, p];
            for (int i = 0; i < n; i++)
              for (int k = 0; k < m; k++) {
                double a = A[i, k];
                if (a == 0) continue;
                for (int j = 0; j < p; j++) C[i, j] += a * B[k, j];
              }
            return C;
          }
        }
        """;

    private static string Transposed() => """
        public static class Kernel {
          public static double[,] Multiply(double[,] A, double[,] B) {
            int n = A.GetLength(0), p = B.GetLength(1), m = B.GetLength(0);
            var Bt = new double[p, m];
            for (int k = 0; k < m; k++) for (int j = 0; j < p; j++) Bt[j, k] = B[k, j];
            var C = new double[n, p];
            for (int i = 0; i < n; i++)
              for (int j = 0; j < p; j++) {
                double s = 0;
                for (int k = 0; k < m; k++) s += A[i, k] * Bt[j, k];
                C[i, j] = s;
              }
            return C;
          }
        }
        """;

    private static string Blocked(int tile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("public static class Kernel {");
        sb.AppendLine($"  const int T = {tile};");
        sb.AppendLine("  public static double[,] Multiply(double[,] A, double[,] B) {");
        sb.AppendLine("    int n = A.GetLength(0), p = B.GetLength(1), m = B.GetLength(0);");
        sb.AppendLine("    var C = new double[n, p];");
        sb.AppendLine("    for (int ii = 0; ii < n; ii += T)");
        sb.AppendLine("      for (int kk = 0; kk < m; kk += T)");
        sb.AppendLine("        for (int jj = 0; jj < p; jj += T) {");
        sb.AppendLine("          int iMax = ii + T < n ? ii + T : n;");
        sb.AppendLine("          int kMax = kk + T < m ? kk + T : m;");
        sb.AppendLine("          int jMax = jj + T < p ? jj + T : p;");
        sb.AppendLine("          for (int i = ii; i < iMax; i++)");
        sb.AppendLine("            for (int k = kk; k < kMax; k++) {");
        sb.AppendLine("              double a = A[i, k];");
        sb.AppendLine("              if (a == 0) continue;");
        sb.AppendLine("              for (int j = jj; j < jMax; j++) C[i, j] += a * B[k, j];");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    return C;");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // ── demo / acceptance ─────────────────────────────────────────────────────────────────────
    /// <summary>`dotnet run -- tune [size]` — compile, correctness-check and BENCHMARK every free
    /// kernel family against the naive reference at this size. No API key, hive or network.</summary>
    public static void Demo(int size)
    {
        Console.WriteLine($"== LLM-free autotuning: {size}x{size} wall-clock candidates (no API key) ==");
        var rng = new Random(20260724);
        double[,] a = MatrixOps.RandomMatrix(size, size, rng);
        double[,] b = MatrixOps.RandomMatrix(size, size, rng);
        double[,] reference = MatrixOps.MultiplyReference(a, b);

        var cands = new List<Candidate>();
        foreach (string f in new[] { "ikj", "transposed", "blocked", "strassen" })
            foreach (int t in Tiles)
                foreach (int c in Cutoffs)
                {
                    Candidate? cand = Build(f, t, c, size);
                    if (cand is not null && cands.All(x => x.Strategy != cand.Strategy)) cands.Add(cand);
                }

        using (KernelBenchmark.QuietScope())
        {
            double refMs = KernelBenchmark.Measure(MatrixOps.MultiplyReference, a, b, 3, 10).MedianMs;
            Console.WriteLine($"   naive reference: {refMs:F2} ms\n");
            foreach (Candidate c in cands.OrderBy(x => x.Strategy))
            {
                var (ok, ms) = TimeIt(c, a, b, reference);
                Console.WriteLine(ok
                    ? $"   {c.Strategy,-28} {ms,8:F2} ms   {refMs / ms,5:F2}x vs naive"
                    : $"   {c.Strategy,-28}   rejected (compile or correctness)");
            }
        }
    }

    private static (bool Ok, double Ms) TimeIt(Candidate c, double[,] a, double[,] b, double[,] reference)
    {
        if (!RuntimeCompiler.TryCompileAssembly(c.Source, out System.Reflection.Assembly? asm, out _)) return (false, 0);
        Func<double[,], double[,], double[,]>? fn = MatmulRace.BindDoubleKernel(asm!);
        if (fn is null) return (false, 0);
        try { if (!MatrixOps.Compare(reference, fn(a, b), 1e-9, 1e-9, out _, out _)) return (false, 0); }
        catch { return (false, 0); }
        return (true, KernelBenchmark.Measure(fn, a, b, 3, 10).MedianMs);
    }
}
