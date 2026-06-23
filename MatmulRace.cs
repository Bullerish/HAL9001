using System.Reflection;

namespace HAL9001;

/// <summary>
/// Prime Directive race engine. One round = generate candidate implementations via the LLM →
/// compile (Roslyn) → CORRECTNESS GATE (must match the naive reference) → score → compare to the
/// hive's shared champion → on a new record, persist it and let the swarm broadcast a challenge.
///
/// TWO METRICS, chosen by matrix size (bite 15):
///   • <see cref="Metric.Muls"/> — for SMALL sizes, where wall-clock timing is pure noise. Candidates
///     are written over the cheat-proof <see cref="Scalar"/> type and ranked by how few scalar
///     MULTIPLICATIONS they use. This is the algorithm-novelty half (Strassen territory).
///   • <see cref="Metric.Time"/> — for LARGE sizes, where cache/SIMD behaviour dominates. Candidates
///     are plain double[,] kernels ranked by benchmarked median wall-clock. This is autotuning.
///
/// Either way correctness is the floor: a wrong candidate is disqualified no matter how good its score.
/// Each round also includes one "refine the champion" attempt (the LLM is shown the current best and
/// asked to improve it) so the loop doesn't just explore — it perfects.
/// </summary>
public static class MatmulRace
{
    public const int DefaultSize = 128;
    private const int Warmup = 3;
    private const int Timed = 10;
    private const double AbsTol = 1e-9;
    private const double RelTol = 1e-9;

    /// <summary>Which fitness function a round uses.</summary>
    public enum Metric { Time, Muls }

    public static string MetricName(Metric m) => m == Metric.Time ? "ms" : "muls";

    /// <summary>The hive's current record for one matrix size + metric.</summary>
    public sealed record Champion(
        string Node, string Strategy, Metric Metric,
        double Score, double Speedup, string Source = "");

    /// <summary>What one race round produced.</summary>
    public sealed record RoundResult(
        int Size, Metric Metric, double Score, double Speedup,
        string Strategy, bool NewRecord, string Summary);

    /// <summary>
    /// Run one full race round at <paramref name="size"/> under <paramref name="metric"/>. Generates
    /// <paramref name="randomCandidates"/> implementations from randomly-chosen strategies plus one
    /// "refine the champion" attempt when a champion exists, evaluates them, and updates the shared
    /// champion in Turso if the round beat it. Returns null when every candidate fails.
    /// </summary>
    public static async Task<RoundResult?> RunRoundAsync(
        AnthropicClient client, AgentCore core, int myPort,
        int size, Metric metric, int randomCandidates = 2,
        CancellationToken ct = default)
    {
        Champion? champ = await core.GetMatmulChampionAsync(size);

        // Fixed seeded inputs: EVERY node evaluates on identical data — only the implementation varies.
        var rng = new Random(20260621);
        double[,] a = MatrixOps.RandomMatrix(size, size, rng);
        double[,] b = MatrixOps.RandomMatrix(size, size, rng);
        double[,] reference = MatrixOps.MultiplyReference(a, b);

        double bestScore = double.MaxValue, baseline;
        string bestStrategy = "", bestSource = "";

        if (metric == Metric.Muls)
        {
            (bestScore, bestStrategy, bestSource) =
                await EvaluateMulsAsync(client, champ, size, a, b, reference, randomCandidates, ct);
            baseline = (double)size * size * size; // naive scalar-multiplication count
        }
        else
        {
            (bestScore, bestStrategy, bestSource, baseline) =
                await EvaluateTimeAsync(client, champ, size, a, b, reference, randomCandidates, ct);
        }

        if (bestScore == double.MaxValue) return null; // every candidate failed

        double speedup = baseline / bestScore;
        bool newRecord = champ is null || bestScore < champ.Score;

        if (newRecord)
            await core.SetMatmulChampionAsync(
                $"127.0.0.1:{myPort}", size, bestStrategy, metric, bestScore, speedup, bestSource);

        string unit = MetricName(metric);
        string mine = metric == Metric.Muls ? $"{bestScore:F0} {unit}" : $"{bestScore:F2}{unit}";
        string champLine = champ is null
            ? "No prior champion — I am first."
            : $"Previous: {champ.Node} at {(metric == Metric.Muls ? $"{champ.Score:F0} {unit}" : $"{champ.Score:F2}{unit}")}.";
        string summary = newRecord
            ? $"NEW RECORD {size}x{size} [{unit}]: {mine} ({speedup:F2}x vs naive) — '{Short(bestStrategy, 50)}'. {champLine}"
            : $"Round {size}x{size} [{unit}]: my best {mine} ({speedup:F2}x). {champLine} Still chasing.";

        return new RoundResult(size, metric, bestScore, speedup, bestStrategy, newRecord, summary);
    }

    // ── wall-clock track (large sizes) ────────────────────────────────────────────────────
    private static async Task<(double bestMs, string strategy, string source, double refMs)> EvaluateTimeAsync(
        AnthropicClient client, Champion? champ, int size,
        double[,] a, double[,] b, double[,] reference, int randomCandidates, CancellationToken ct)
    {
        var generator = new KernelGenerator(client);
        var picker = new Random();
        var genTasks = new List<Task<CandidateSource>>(
            KernelGenerator.Strategies.OrderBy(_ => picker.Next()).Take(randomCandidates)
                .Select(s => generator.GenerateForStrategyAsync(s, ct)));
        if (!string.IsNullOrWhiteSpace(champ?.Source))
            genTasks.Add(generator.RefineAsync(champ!.Source, champ.Score, champ.Strategy, ct));
        CandidateSource[] candidates = await Task.WhenAll(genTasks);

        double bestMs = double.MaxValue, refMs;
        string bestStrategy = "", bestSource = "";

        using (KernelBenchmark.QuietScope())
        {
            refMs = KernelBenchmark.Measure(MatrixOps.MultiplyReference, a, b, Warmup, Timed).MedianMs;
            foreach (CandidateSource cand in candidates)
            {
                if (string.IsNullOrWhiteSpace(cand.Source)) continue;
                if (!RuntimeCompiler.TryCompileAssembly(cand.Source, out Assembly? asm, out _)) continue;
                Func<double[,], double[,], double[,]>? fn = BindDouble(asm!);
                if (fn is null) continue;
                try { if (!MatrixOps.Compare(reference, fn(a, b), AbsTol, RelTol, out _, out _)) continue; }
                catch { continue; }
                double ms = KernelBenchmark.Measure(fn, a, b, Warmup, Timed).MedianMs;
                if (ms < bestMs) { bestMs = ms; bestStrategy = cand.Strategy; bestSource = cand.Source; }
            }
        }
        return (bestMs, bestStrategy, bestSource, refMs);
    }

    // ── multiplication-count track (small sizes) ──────────────────────────────────────────
    private static async Task<(double bestMuls, string strategy, string source)> EvaluateMulsAsync(
        AnthropicClient client, Champion? champ, int size,
        double[,] a, double[,] b, double[,] reference, int randomCandidates, CancellationToken ct)
    {
        var generator = new KernelGenerator(client);
        var picker = new Random();
        var genTasks = new List<Task<CandidateSource>>(
            KernelGenerator.CountingStrategies.OrderBy(_ => picker.Next()).Take(randomCandidates)
                .Select(s => generator.GenerateCountingAsync(s, size, ct)));
        if (!string.IsNullOrWhiteSpace(champ?.Source))
            genTasks.Add(generator.RefineCountingAsync(champ!.Source, (long)champ.Score, size, ct));
        CandidateSource[] candidates = await Task.WhenAll(genTasks);

        Scalar[,] sa = Scalar.From(a), sb = Scalar.From(b);
        double bestMuls = double.MaxValue;
        string bestStrategy = "", bestSource = "";

        foreach (CandidateSource cand in candidates)
        {
            if (string.IsNullOrWhiteSpace(cand.Source)) continue;
            if (!RuntimeCompiler.TryCompileAssembly(cand.Source, out Assembly? asm, out _)) continue;
            Func<Scalar[,], Scalar[,], Scalar[,]>? fn = BindScalar(asm!);
            if (fn is null) continue;

            // One counted run; correctness verified on its output (a fixed linear op — one dense
            // random pair is a strong check). A wrong candidate is disqualified before it can score.
            Scalar.ResetCounters();
            Scalar[,] got;
            try { got = fn(sa, sb); } catch { continue; }
            long muls = Scalar.Muls;
            if (!CompareScalar(reference, got)) continue;

            if (muls < bestMuls) { bestMuls = muls; bestStrategy = cand.Strategy; bestSource = cand.Source; }
        }
        return (bestMuls, bestStrategy, bestSource);
    }

    private static bool CompareScalar(double[,] want, Scalar[,] got)
    {
        if (got.GetLength(0) != want.GetLength(0) || got.GetLength(1) != want.GetLength(1)) return false;
        int n = want.GetLength(0), m = want.GetLength(1);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < m; j++)
            {
                double g = got[i, j].ToDouble(), w = want[i, j];
                if (double.IsNaN(g) || double.IsInfinity(g)) return false;
                if (Math.Abs(g - w) > AbsTol + RelTol * Math.Abs(w)) return false;
            }
        return true;
    }

    private static Func<double[,], double[,], double[,]>? BindDouble(Assembly asm)
        => BindMultiply<double>(asm);
    private static Func<Scalar[,], Scalar[,], Scalar[,]>? BindScalar(Assembly asm)
        => BindMultiply<Scalar>(asm);

    /// <summary>Reflect a public static <c>T[,] Multiply(T[,], T[,])</c> and bind it, for T = double or Scalar.</summary>
    private static Func<T[,], T[,], T[,]>? BindMultiply<T>(Assembly asm)
    {
        try
        {
            foreach (Type t in asm.GetTypes())
            {
                MethodInfo? mi = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m =>
                        m.Name == "Multiply" &&
                        m.ReturnType == typeof(T[,]) &&
                        m.GetParameters().Length == 2 &&
                        m.GetParameters()[0].ParameterType == typeof(T[,]) &&
                        m.GetParameters()[1].ParameterType == typeof(T[,]));
                if (mi is not null)
                    return mi.CreateDelegate<Func<T[,], T[,], T[,]>>();
            }
        }
        catch { }
        return null;
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "...";

    // ── self-test (no API key / no hive needed) ───────────────────────────────────────────
    /// <summary>
    /// Verify the multiplication-counting harness end-to-end through the REAL compile pipeline:
    /// compile a hand-written naive 2×2 and a Strassen 2×2 candidate over <see cref="Scalar"/>,
    /// run them, and confirm correctness + the exact multiplication counts (naive = 8, Strassen = 7).
    /// This proves the counter is wired correctly and that a separately-compiled candidate genuinely
    /// goes through the counted <c>*</c> operator. Invoked by <c>HAL9001 racetest</c>.
    /// </summary>
    public static void SelfTest()
    {
        Console.WriteLine("== matmul counting self-test (no API key / hive needed) ==");
        const int n = 2;
        var rng = new Random(20260621);
        double[,] a = MatrixOps.RandomMatrix(n, n, rng);
        double[,] b = MatrixOps.RandomMatrix(n, n, rng);
        double[,] reference = MatrixOps.MultiplyReference(a, b);
        Scalar[,] sa = Scalar.From(a), sb = Scalar.From(b);

        const string naive = """
            using HAL9001;
            public static class Kernel {
              public static Scalar[,] Multiply(Scalar[,] a, Scalar[,] b) {
                int n=a.GetLength(0), k=a.GetLength(1), m=b.GetLength(1);
                var c=new Scalar[n,m];
                for(int i=0;i<n;i++) for(int j=0;j<m;j++){ var s=new Scalar(0); for(int p=0;p<k;p++) s=s+a[i,p]*b[p,j]; c[i,j]=s; }
                return c;
              }
            }
            """;
        const string strassen = """
            using HAL9001;
            public static class Kernel {
              public static Scalar[,] Multiply(Scalar[,] a, Scalar[,] b) {
                var a11=a[0,0]; var a12=a[0,1]; var a21=a[1,0]; var a22=a[1,1];
                var b11=b[0,0]; var b12=b[0,1]; var b21=b[1,0]; var b22=b[1,1];
                var m1=(a11+a22)*(b11+b22);
                var m2=(a21+a22)*b11;
                var m3=a11*(b12-b22);
                var m4=a22*(b21-b11);
                var m5=(a11+a12)*b22;
                var m6=(a21-a11)*(b11+b12);
                var m7=(a12-a22)*(b21+b22);
                var c=new Scalar[2,2];
                c[0,0]=m1+m4-m5+m7; c[0,1]=m3+m5; c[1,0]=m2+m4; c[1,1]=m1-m2+m3+m6;
                return c;
              }
            }
            """;

        RunSelfTestOne("naive   (expect muls=8)", naive, sa, sb, reference);
        RunSelfTestOne("strassen(expect muls=7)", strassen, sa, sb, reference);
    }

    private static void RunSelfTestOne(string label, string src, Scalar[,] sa, Scalar[,] sb, double[,] reference)
    {
        if (!RuntimeCompiler.TryCompileAssembly(src, out Assembly? asm, out string? diag))
        { Console.WriteLine($"  {label}: COMPILE FAILED\n{diag}"); return; }
        Func<Scalar[,], Scalar[,], Scalar[,]>? fn = BindScalar(asm!);
        if (fn is null) { Console.WriteLine($"  {label}: no Scalar Multiply bound"); return; }
        Scalar.ResetCounters();
        Scalar[,] got = fn(sa, sb);
        long muls = Scalar.Muls, adds = Scalar.Adds;
        bool ok = CompareScalar(reference, got);
        Console.WriteLine($"  {label}: correct={ok}  muls={muls}  adds={adds}");
    }
}
