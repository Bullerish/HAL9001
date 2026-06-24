using System.Numerics;
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
        string Strategy, bool NewRecord, string Summary, bool Discovery = false);

    /// <summary>
    /// Run one full race round at <paramref name="size"/> under <paramref name="metric"/>. Generates
    /// <paramref name="randomCandidates"/> implementations from randomly-chosen strategies plus one
    /// "refine the champion" attempt when a champion exists, evaluates them, and updates the shared
    /// champion in Turso if the round beat it. Returns null when every candidate fails.
    /// </summary>
    public static async Task<RoundResult?> RunRoundAsync(
        AnthropicClient client, AgentCore core, int myPort,
        int size, Metric metric, int randomCandidates = 2,
        CancellationToken ct = default, Action<string>? log = null)
    {
        Champion? champ = await core.GetMatmulChampionAsync(size);

        // Fixed seeded inputs: EVERY node evaluates on identical data — only the implementation varies.
        var rng = new Random(20260621);
        double[,] a = MatrixOps.RandomMatrix(size, size, rng);
        double[,] b = MatrixOps.RandomMatrix(size, size, rng);
        double[,] reference = MatrixOps.MultiplyReference(a, b);

        double bestScore = double.MaxValue, baseline;
        string bestStrategy = "", bestSource = "";
        string? bestScheme = null; // the winning U/V/W triple JSON (muls track only), persisted for the dashboard

        // Daily budget (bite 21): when the LLM budget is spent, the FREE tensor search still runs
        // (muls path), but LLM candidate generation pauses. The hive keeps grinding matrices for $0.
        bool llmAllowed = true;
        try { llmAllowed = await core.HasBudgetAsync(); } catch { }

        if (metric == Metric.Muls)
        {
            (bestScore, bestStrategy, bestSource, bestScheme) =
                await EvaluateMulsAsync(client, champ, size, a, b, reference, randomCandidates, llmAllowed, ct, log);
            baseline = (double)size * size * size; // naive scalar-multiplication count
        }
        else
        {
            (bestScore, bestStrategy, bestSource, baseline) =
                await EvaluateTimeAsync(client, champ, size, a, b, reference, randomCandidates, llmAllowed, ct);
        }

        if (bestScore == double.MaxValue) return null; // every candidate failed

        double speedup = baseline / bestScore;
        bool newRecord = champ is null || bestScore < champ.Score;

        if (newRecord)
            await core.SetMatmulChampionAsync(
                $"127.0.0.1:{myPort}", size, bestStrategy, metric, bestScore, speedup, bestSource, bestScheme);

        // ── NOVELTY GATE (bite 16) ──────────────────────────────────────────────────────────
        // A new mult-count record might be genuinely novel (beats the best known to humanity). Only
        // here do we check — and only a record that BEATS known-best AND passes EXACT verification is
        // claimed. The race's 1e-9 float check is fine for ranking but NOT for asserting a theorem.
        bool discovery = false;
        if (metric == Metric.Muls && newRecord)
            discovery = await ClaimIfNovelAsync(core, size, (long)bestScore, bestStrategy, bestSource, $"127.0.0.1:{myPort}", ct);

        string unit = MetricName(metric);
        string mine = metric == Metric.Muls ? $"{bestScore:F0} {unit}" : $"{bestScore:F2}{unit}";
        string champLine = champ is null
            ? "No prior champion — I am first."
            : $"Previous: {champ.Node} at {(metric == Metric.Muls ? $"{champ.Score:F0} {unit}" : $"{champ.Score:F2}{unit}")}.";
        string summary = newRecord
            ? $"NEW RECORD {size}x{size} [{unit}]: {mine} ({speedup:F2}x vs naive) — '{Short(bestStrategy, 50)}'. {champLine}"
            : $"Round {size}x{size} [{unit}]: my best {mine} ({speedup:F2}x). {champLine} Still chasing.";

        return new RoundResult(size, metric, bestScore, speedup, bestStrategy, newRecord, summary, discovery);
    }

    // ── novelty gate (bite 16) ────────────────────────────────────────────────────────────
    /// <summary>
    /// Decide whether a new mult-count record is a genuine discovery and, if so, record it. Compares
    /// against <see cref="MatmulKnownBest"/>; a result below a PROVEN lower bound is flagged as a bug
    /// (verification is wrong, not a breakthrough); a result that beats known-best is EXACT-verified
    /// (BigInteger, many random integer inputs) before any claim, then written as a discovery artifact.
    /// Returns true only when a real, exactly-verified discovery was recorded.
    /// </summary>
    private static async Task<bool> ClaimIfNovelAsync(
        AgentCore core, int size, long muls, string strategy, string source, string node, CancellationToken ct)
    {
        var (verdict, best, lower) = MatmulKnownBest.Classify(size, muls);
        switch (verdict)
        {
            case MatmulKnownBest.Verdict.BelowLowerBound:
                Console.WriteLine($"\n[novelty] {size}x{size} reported {muls} muls — below the PROVEN lower bound of {lower}. " +
                                  "That's impossible, so our verification has a bug. NOT claiming anything; rejecting.");
                await core.Events.AppendAsync("novelty-impossible",
                    $"{size}x{size} {muls} muls < proven lower bound {lower} — verification bug, rejected");
                return false;

            case MatmulKnownBest.Verdict.BeatsKnownBest:
                Console.WriteLine($"\n[novelty] {size}x{size} {muls} muls BEATS known-best ({best}) — running EXACT verification before any claim...");
                if (!RecompileAndVerifyExact(source, size))
                {
                    Console.WriteLine("[novelty] exact verification FAILED — the float check was fooled. Rejecting, not a discovery.");
                    await core.Events.AppendAsync("novelty-false-positive",
                        $"{size}x{size} {muls} muls beat known-best but failed exact verification — rejected");
                    return false;
                }
                Console.WriteLine("[novelty] exact verification PASSED. Recording a candidate discovery for human review.");
                await core.RecordDiscoveryAsync(size, muls, best, lower, strategy, source, node, ct);
                return true;

            default: // Rediscovery / NoTarget — no claim
                return false;
        }
    }

    /// <summary>Compile a counting-track source, count its scalar multiplications on one run, and
    /// exact-verify it. Used by the LLM-free derivation engine (bite 17) and its demo.</summary>
    internal static (bool Compiled, long Muls, bool Exact) EvaluateCountingSource(string source, int size)
    {
        if (!RuntimeCompiler.TryCompileAssembly(source, out Assembly? asm, out _)) return (false, 0, false);
        Func<Scalar[,], Scalar[,], Scalar[,]>? fn = BindScalar(asm!);
        if (fn is null) return (false, 0, false);
        var rng = new Random(20260621);
        double[,] a = MatrixOps.RandomMatrix(size, size, rng), b = MatrixOps.RandomMatrix(size, size, rng);
        Scalar.ResetCounters();
        try { _ = fn(Scalar.From(a), Scalar.From(b)); } catch { return (true, 0, false); }
        long muls = Scalar.Muls;
        return (true, muls, VerifyExact(fn, size));
    }

    /// <summary>EXACT verification: recompile the source and confirm it computes the true product on
    /// many random INTEGER matrices using BigInteger arithmetic — a bilinear scheme correct on enough
    /// random integer inputs is correct with overwhelming certainty (Schwartz–Zippel).</summary>
    private static bool RecompileAndVerifyExact(string source, int size)
    {
        if (!RuntimeCompiler.TryCompileAssembly(source, out Assembly? asm, out _)) return false;
        Func<Scalar[,], Scalar[,], Scalar[,]>? fn = BindScalar(asm!);
        return fn is not null && VerifyExact(fn, size);
    }

    private const int ExactTrials = 64;
    private const int ExactEntryBound = 6; // small entries keep every double exact (far under 2^53)

    /// <summary>Run the candidate on <see cref="ExactTrials"/> random integer matrices and require an
    /// EXACT match against a BigInteger reference each time. Small entries guarantee the candidate's
    /// double arithmetic is itself exact (integers below 2^53), so equality is a true exact check.</summary>
    internal static bool VerifyExact(Func<Scalar[,], Scalar[,], Scalar[,]> fn, int size)
    {
        var rng = new Random(0x5CA1AB1E);
        for (int trial = 0; trial < ExactTrials; trial++)
        {
            var ia = new long[size, size];
            var ib = new long[size, size];
            var da = new double[size, size];
            var db = new double[size, size];
            for (int i = 0; i < size; i++)
                for (int j = 0; j < size; j++)
                {
                    ia[i, j] = rng.Next(-ExactEntryBound, ExactEntryBound + 1);
                    ib[i, j] = rng.Next(-ExactEntryBound, ExactEntryBound + 1);
                    da[i, j] = ia[i, j];
                    db[i, j] = ib[i, j];
                }

            Scalar[,] got;
            try { got = fn(Scalar.From(da), Scalar.From(db)); }
            catch { return false; }
            if (got.GetLength(0) != size || got.GetLength(1) != size) return false;

            for (int i = 0; i < size; i++)
                for (int j = 0; j < size; j++)
                {
                    BigInteger want = 0;
                    for (int p = 0; p < size; p++) want += (BigInteger)ia[i, p] * ib[p, j];
                    double g = got[i, j].ToDouble();
                    if (g != Math.Floor(g)) return false;             // non-integer ⇒ wrong/rounded
                    if ((BigInteger)g != want) return false;           // exact mismatch ⇒ wrong scheme
                }
        }
        return true;
    }

    // ── wall-clock track (large sizes) ────────────────────────────────────────────────────
    private static async Task<(double bestMs, string strategy, string source, double refMs)> EvaluateTimeAsync(
        AnthropicClient client, Champion? champ, int size,
        double[,] a, double[,] b, double[,] reference, int randomCandidates, bool llmAllowed, CancellationToken ct)
    {
        // The wall-clock track is entirely LLM-authored; with no budget there's nothing free to do.
        if (!llmAllowed) return (double.MaxValue, "", "", 0);
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
    private static async Task<(double bestMuls, string strategy, string source, string? scheme)> EvaluateMulsAsync(
        AnthropicClient client, Champion? champ, int size,
        double[,] a, double[,] b, double[,] reference, int randomCandidates, bool llmAllowed, CancellationToken ct,
        Action<string>? log = null)
    {
        double bestMuls = double.MaxValue;
        string bestStrategy = "", bestSource = "";
        string? bestScheme = null; // the winning bilinear triple (U/V/W) as JSON, when a tensor-search scheme wins

        // LLM-FREE FIRST (bite 17): DERIVE a better algorithm by searching the matmul tensor directly —
        // target one multiplication below the current best. This runs even when the LLM budget is spent
        // (it costs nothing), so the hive keeps grinding matrices for free.
        int target = (champ is not null ? (int)champ.Score : size * size * size) - 1;
        if (target >= 1)
        {
            log?.Invoke($"tensor-search: targeting rank-{target} for {size}x{size}...");
            TensorSearch.Decomposition? d = TensorSearch.Search(size, target, out int bestErr, maxSeconds: 8,
                onProgress: p => log?.Invoke($"  {p}"));
            if (d is not null)
            {
                log?.Invoke($"tensor-search: FOUND rank-{target}! verifying...");
                string src = TensorSearch.Synthesize(d);
                var (ok, muls, exact) = EvaluateCountingSource(src, size);
                if (ok && exact && muls < bestMuls)
                { bestMuls = muls; bestStrategy = "tensor-search (LLM-free derivation)"; bestSource = src; bestScheme = SchemeJson(d); }
                if (ok && exact) log?.Invoke($"tensor-search: exact-verified {muls} muls");
                else log?.Invoke("tensor-search: verification failed — rejected");
            }
            else
            {
                log?.Invoke($"tensor-search: no exact rank-{target} in 8s (best err {bestErr})");
            }
        }

        // LLM candidate track — the fallback for what the free search can't crack. Paused when the
        // daily budget is spent (bite 21).
        if (llmAllowed)
        {
            var generator = new KernelGenerator(client);
            var picker = new Random();
            var strategies = KernelGenerator.CountingStrategies.OrderBy(_ => picker.Next()).Take(randomCandidates).ToList();
            bool refining = !string.IsNullOrWhiteSpace(champ?.Source);
            log?.Invoke($"LLM: generating {strategies.Count + (refining ? 1 : 0)} candidate(s)...");
            var genTasks = new List<Task<CandidateSource>>(
                strategies.Select(s => generator.GenerateCountingAsync(s, size, ct)));
            if (refining)
                genTasks.Add(generator.RefineCountingAsync(champ!.Source, (long)champ.Score, size, ct));
            CandidateSource[] candidates = await Task.WhenAll(genTasks);

            Scalar[,] sa = Scalar.From(a), sb = Scalar.From(b);
            foreach (CandidateSource cand in candidates)
            {
                if (string.IsNullOrWhiteSpace(cand.Source)) { log?.Invoke($"  LLM: empty response [{Short(cand.Strategy, 30)}]"); continue; }
                if (!RuntimeCompiler.TryCompileAssembly(cand.Source, out Assembly? asm, out _)) { log?.Invoke($"  compile FAIL [{Short(cand.Strategy, 30)}]"); continue; }
                Func<Scalar[,], Scalar[,], Scalar[,]>? fn = BindScalar(asm!);
                if (fn is null) continue;

                Scalar.ResetCounters();
                Scalar[,] got;
                try { got = fn(sa, sb); } catch { log?.Invoke($"  runtime crash [{Short(cand.Strategy, 30)}]"); continue; }
                long muls = Scalar.Muls;
                if (!CompareScalar(reference, got)) { log?.Invoke($"  correctness FAIL [{Short(cand.Strategy, 30)}] {muls} muls"); continue; }
                log?.Invoke($"  OK {muls} muls [{Short(cand.Strategy, 30)}]" + (muls < bestMuls ? " ← new best" : ""));
                if (muls < bestMuls) { bestMuls = muls; bestStrategy = cand.Strategy; bestSource = cand.Source; bestScheme = null; }
            }
        }
        return (bestMuls, bestStrategy, bestSource, bestScheme);
    }

    // Serialize a derived bilinear decomposition's factor triple as compact JSON {n,rank,u,v,w} for the
    // dashboard CRT (bite 2) — the matrices the hive is actually working. Same shape the volunteer path uses.
    private static string SchemeJson(TensorSearch.Decomposition d)
    {
        static string Mat(int[,] m)
        {
            int rows = m.GetLength(0), cols = m.GetLength(1);
            var rj = new string[rows];
            for (int r = 0; r < rows; r++)
            {
                var cells = new int[cols];
                for (int c = 0; c < cols; c++) cells[c] = m[r, c];
                rj[r] = "[" + string.Join(",", cells) + "]";
            }
            return "[" + string.Join(",", rj) + "]";
        }
        return $"{{\"n\":{d.N},\"rank\":{d.Rank},\"u\":{Mat(d.U)},\"v\":{Mat(d.V)},\"w\":{Mat(d.W)}}}";
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

        // A transpose-bug candidate: correct only for symmetric inputs, so it slips the single-pair
        // float check sometimes but the exact verifier (64 random integer matrices) must reject it.
        const string buggy = """
            using HAL9001;
            public static class Kernel {
              public static Scalar[,] Multiply(Scalar[,] a, Scalar[,] b) {
                int n=a.GetLength(0), k=a.GetLength(1), m=b.GetLength(1);
                var c=new Scalar[n,m];
                for(int i=0;i<n;i++) for(int j=0;j<m;j++){ var s=new Scalar(0); for(int p=0;p<k;p++) s=s+a[i,p]*b[j,p]; c[i,j]=s; }
                return c;
              }
            }
            """;

        Console.WriteLine("-- exact verifier (bite 16): correct schemes pass, wrong ones rejected --");
        Console.WriteLine($"  naive   : exact-verify={ExactVerifyOne(naive)}    (expect True)");
        Console.WriteLine($"  strassen: exact-verify={ExactVerifyOne(strassen)}    (expect True)");
        Console.WriteLine($"  buggy   : exact-verify={ExactVerifyOne(buggy)}    (expect False)");
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

    private static bool ExactVerifyOne(string src) => RecompileAndVerifyExact(src, 2);
}
