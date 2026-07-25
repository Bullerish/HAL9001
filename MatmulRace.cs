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

    // Benchmark cost scales as n³, so a fixed 3+10 reps that takes a second at 128 takes many minutes
    // at 2048. Above 512 we cut the reps: the timing noise that matters at 64×64 (microseconds) is
    // irrelevant at 2048×2048 (seconds), so fewer, longer runs are both cheaper AND more reliable.
    private static (int Warm, int Reps) RepsFor(int size) =>
        size >= 2048 ? (1, 3) :
        size >= 1024 ? (1, 5) :
        size >= 512  ? (2, 7) : (Warmup, Timed);
    private const double AbsTol = 1e-9;
    private const double RelTol = 1e-9;

    /// <summary>Which fitness function a round uses.</summary>
    public enum Metric { Time, Muls }

    public static string MetricName(Metric m) => m == Metric.Time ? "ms" : "muls";

    /// <summary>The hive's current record for one matrix size + metric.</summary>
    public sealed record Champion(
        string Node, string Strategy, Metric Metric,
        double Score, double Speedup, string Source = "",
        // The winning bilinear scheme's U/V/W as JSON, when one won (muls track). This is what the
        // free composition engine reuses as a base for larger sizes — see SchemeCompose.
        string Scheme = "");

    /// <summary>What one race round produced.</summary>
    public sealed record RoundResult(
        int Size, Metric Metric, double Score, double Speedup,
        string Strategy, bool NewRecord, string Summary, bool Discovery = false);

    /// <summary>A round's result plus whether the round actually got to DO anything. The two are
    /// different: "raced and nothing beat the champion" is evidence a size has converged; "there was
    /// nothing to race" is not, and must not be counted as convergence by the ladder.</summary>
    public sealed record RoundOutcome(RoundResult? Round, bool Worked);

    /// <summary>
    /// Run one full race round at <paramref name="size"/> under <paramref name="metric"/>.
    ///
    /// TWO TIERS, and the free one is not a fallback — it is the floor the hive always stands on:
    ///   • FREE (always): composition of the best known scheme, direct tensor search, and parametric
    ///     autotuning. Costs no tokens, so it runs with no API key, no budget, and no visitor.
    ///   • LLM (only when a client is present AND today's budget is funded): candidate generation and
    ///     champion refinement — the amplifier a paid visitor's tokens switch on.
    /// Returns the round's result plus whether anything could be evaluated at all.
    /// </summary>
    public static async Task<RoundOutcome> RunRoundAsync(
        AnthropicClient? client, AgentCore core, int myPort,
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

        // Daily budget (bite 21): when the LLM budget is spent — or there is no key at all — the FREE
        // engines still run. LLM candidate generation is the only thing that pauses.
        bool llmAllowed = client is not null;
        if (llmAllowed) { try { llmAllowed = await core.HasBudgetAsync(); } catch { } }

        bool worked;
        if (metric == Metric.Muls)
        {
            (bestScore, bestStrategy, bestSource, bestScheme, worked) =
                await EvaluateMulsAsync(client, core, champ, size, a, b, reference, randomCandidates, llmAllowed, ct, log);
            baseline = (double)size * size * size; // naive scalar-multiplication count
        }
        else
        {
            (bestScore, bestStrategy, bestSource, baseline, worked) =
                await EvaluateTimeAsync(client, champ, size, a, b, reference, randomCandidates, llmAllowed, ct, log);
        }

        if (bestScore == double.MaxValue) return new RoundOutcome(null, worked); // nothing survived

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

        return new RoundOutcome(new RoundResult(size, metric, bestScore, speedup, bestStrategy, newRecord, summary, discovery), true);
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
    internal static (bool Compiled, long Muls, bool Exact) EvaluateCountingSource(string source, int size, int trials = ExactTrials)
    {
        if (!RuntimeCompiler.TryCompileAssembly(source, out Assembly? asm, out _)) return (false, 0, false);
        Func<Scalar[,], Scalar[,], Scalar[,]>? fn = BindScalar(asm!);
        if (fn is null) return (false, 0, false);
        var rng = new Random(20260621);
        double[,] a = MatrixOps.RandomMatrix(size, size, rng), b = MatrixOps.RandomMatrix(size, size, rng);
        Scalar.ResetCounters();
        try { _ = fn(Scalar.From(a), Scalar.From(b)); } catch { return (true, 0, false); }
        long muls = Scalar.Muls;
        return (true, muls, VerifyExact(fn, size, trials));
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
    internal static bool VerifyExact(Func<Scalar[,], Scalar[,], Scalar[,]> fn, int size, int trials = ExactTrials)
    {
        var rng = new Random(0x5CA1AB1E);
        for (int trial = 0; trial < Math.Max(1, trials); trial++)
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
    private static async Task<(double bestMs, string strategy, string source, double refMs, bool worked)> EvaluateTimeAsync(
        AnthropicClient? client, Champion? champ, int size,
        double[,] a, double[,] b, double[,] reference, int randomCandidates, bool llmAllowed, CancellationToken ct,
        Action<string>? log = null)
    {
        // FREE ENGINE — AUTOTUNING. This track used to be entirely LLM-authored, which meant an
        // unfunded hive did NOTHING at 64/128/256. Now every round proposes parametric kernels
        // (loop order / tiling / transpose / recursive Strassen) climbed from the champion's own
        // parameters. No tokens, real measured speedups.
        var picker = new Random();
        var candidates = new List<CandidateSource>();
        foreach (KernelTuner.Candidate tc in KernelTuner.Propose(size, champ?.Strategy, 4, picker))
            candidates.Add(new CandidateSource(tc.Strategy, tc.Source));
        log?.Invoke($"autotune: {candidates.Count} free kernel(s) proposed (no LLM)");

        // LLM candidates layer on top when today's budget is funded — the paid amplifier.
        if (llmAllowed)
        {
            var generator = new KernelGenerator(client!);
            var genTasks = new List<Task<CandidateSource>>(
                KernelGenerator.Strategies.OrderBy(_ => picker.Next()).Take(randomCandidates)
                    .Select(s => generator.GenerateForStrategyAsync(s, ct)));
            if (!string.IsNullOrWhiteSpace(champ?.Source))
                genTasks.Add(generator.RefineAsync(champ!.Source, champ.Score, champ.Strategy, ct));
            candidates.AddRange(await Task.WhenAll(genTasks));
        }

        double bestMs = double.MaxValue, refMs;
        string bestStrategy = "", bestSource = "";
        bool worked = false;

        var (warm, reps) = RepsFor(size);
        using (KernelBenchmark.QuietScope())
        {
            refMs = KernelBenchmark.Measure(MatrixOps.MultiplyReference, a, b, warm, reps).MedianMs;
            foreach (CandidateSource cand in candidates)
            {
                if (string.IsNullOrWhiteSpace(cand.Source)) continue;
                if (!RuntimeCompiler.TryCompileAssembly(cand.Source, out Assembly? asm, out _)) continue;
                Func<double[,], double[,], double[,]>? fn = BindDouble(asm!);
                if (fn is null) continue;
                try { if (!MatrixOps.Compare(reference, fn(a, b), AbsTol, RelTol, out _, out _)) continue; }
                catch { continue; }
                worked = true;   // a candidate compiled AND computed the right answer — the round did work
                double ms = KernelBenchmark.Measure(fn, a, b, warm, reps).MedianMs;
                log?.Invoke($"  {ms:F2}ms [{Short(cand.Strategy, 34)}]" + (ms < bestMs ? " ← best" : ""));
                if (ms < bestMs) { bestMs = ms; bestStrategy = cand.Strategy; bestSource = cand.Source; }
            }
        }
        return (bestMs, bestStrategy, bestSource, refMs, worked);
    }

    // ── multiplication-count track (small sizes) ──────────────────────────────────────────
    private static async Task<(double bestMuls, string strategy, string source, string? scheme, bool worked)> EvaluateMulsAsync(
        AnthropicClient? client, AgentCore core, Champion? champ, int size,
        double[,] a, double[,] b, double[,] reference, int randomCandidates, bool llmAllowed, CancellationToken ct,
        Action<string>? log = null)
    {
        double bestMuls = double.MaxValue;
        string bestStrategy = "", bestSource = "";
        string? bestScheme = null; // the winning bilinear triple (U/V/W) as JSON, when a tensor-search scheme wins
        bool worked = false;       // did ANY engine actually get to evaluate something this round?

        long currentBest = champ is not null ? (long)champ.Score : (long)size * size * size;

        // FREE ENGINE 1 — COMPOSITION. Build this size out of the best small scheme the hive knows
        // (Strassen at minimum) and verify it exactly. This is what lets sizes far beyond the direct
        // search's reach keep improving with no tokens: 8x8 in 343, 16x16 in 2401, 32x32 in 16807.
        // It also COMPOUNDS — the day a smaller scheme improves, every larger size inherits it.
        try
        {
            var composed = await SchemeCompose.TryImproveAsync(core, size, currentBest, log);
            if (composed is not null)
            {
                worked = true;
                if (composed.Value.Muls < bestMuls)
                {
                    bestMuls = composed.Value.Muls;
                    bestStrategy = "composition (LLM-free): " + composed.Value.Recipe;
                    bestSource = composed.Value.Source;
                    bestScheme = null; // the scheme is recursive, not a flat U/V/W triple
                }
            }
        }
        catch (Exception ex) { log?.Invoke($"compose: skipped ({ex.Message})"); }

        // FREE ENGINE 2 (bite 17) — DERIVE a better algorithm by searching the matmul tensor directly,
        // targeting one multiplication below the best we know RIGHT NOW — including anything composition
        // just produced this round, so the two engines ratchet each other instead of duplicating work.
        // Costs nothing, so it runs regardless of budget; it declines by itself at sizes whose arrays
        // would not fit the memory budget.
        long floor = bestMuls < double.MaxValue ? Math.Min(currentBest, (long)bestMuls) : currentBest;
        int target = (int)Math.Min(int.MaxValue, floor) - 1;
        // Don't hunt a rank that is already PROVEN impossible (2×2 below 7). A size sitting on its
        // proven lower bound is solved; grinding it forever would look like work and be none.
        if (target >= 1 && !MatmulKnownBest.WorthAttempting(size, target))
        {
            log?.Invoke($"tensor-search: {size}x{size} rank-{target} is below the PROVEN lower bound " +
                        $"({MatmulKnownBest.ProvenLower(size)}) — this size is solved, nothing to search");
            target = 0;
        }
        if (target >= 1)
        {
            log?.Invoke($"tensor-search: targeting rank-{target} for {size}x{size}...");
            // Stream the matrices being worked to the live "matrices" panel — but only for sizes small
            // enough that the U/V/W grids actually render (n ≤ 4); larger schemes are unreadable and the
            // JSON would be huge. The dashboard shows these grids mutating as the search hunts.
            Action<TensorSearch.Decomposition, int>? onSnap = size <= 4
                ? (dec, err) => LiveMatrix.Publish(SchemeJson(dec), err)
                : null;
            TensorSearch.Decomposition? d = TensorSearch.Search(size, target, out int bestErr, maxSeconds: 8,
                onProgress: p => log?.Invoke($"  {p}"), onSnapshot: onSnap);
            if (bestErr != TensorSearch.NotAttempted) worked = true;   // it ran, even if it found nothing
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
                log?.Invoke($"tensor-search: no exact rank-{target} in 8s (best err {TensorSearch.ErrText(bestErr)})");
            }
        }

        // LLM candidate track — the AMPLIFIER on top of the free engines, for what they can't crack.
        // Off unless someone funded today's budget (bite 21 + the cost guard), and off entirely on a
        // node with no key. This is the part a visitor's token purchase switches on.
        if (llmAllowed)
        {
            var generator = new KernelGenerator(client!);
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
                worked = true;
                if (!CompareScalar(reference, got)) { log?.Invoke($"  correctness FAIL [{Short(cand.Strategy, 30)}] {muls} muls"); continue; }

                // The float check above runs ONE fixed input pair (seed 20260621) with a 1e-9 tolerance.
                // That is enough to rank, but not enough to ADOPT: the seed never changes, and
                // `refine-champion` iterates on the champion round after round, so a candidate that is
                // wrong in general but happens to agree on that one pair could be refined straight onto
                // the board. The free engines (tensor search, composition) have always been held to
                // many-random-integer BigInteger exact verification — hold the LLM's work to the same bar.
                // Fewer trials for the bigger rungs (each trial also runs a BigInteger reference multiply);
                // by Schwartz–Zippel a wrong scheme surviving even 16 independent random integer inputs is
                // vanishingly unlikely.
                if (!VerifyExact(fn, size, size >= 16 ? 16 : ExactTrials))
                { log?.Invoke($"  exact-verify FAIL [{Short(cand.Strategy, 30)}] {muls} muls — passed the float check but is not exact"); continue; }

                log?.Invoke($"  OK {muls} muls [{Short(cand.Strategy, 30)}]" + (muls < bestMuls ? " ← new best" : ""));
                if (muls < bestMuls) { bestMuls = muls; bestStrategy = cand.Strategy; bestSource = cand.Source; bestScheme = null; }
            }
        }
        return (bestMuls, bestStrategy, bestSource, bestScheme, worked);
    }

    // Serialize a derived bilinear decomposition's factor triple as compact JSON {n,rank,u,v,w} for the
    // dashboard CRT (bite 2) — the matrices the hive is actually working. Same shape the volunteer path
    // uses, and the shape peers send each other in a free cross-node search round.
    internal static string SchemeJson(TensorSearch.Decomposition d)
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

    /// <summary>
    /// Re-prove a scheme a PEER sent us, from scratch and offline: it must parse, its tensor residual
    /// must be exactly 0, the synthesized algorithm must compile, and it must pass the BigInteger
    /// exact verifier. A peer is never trusted — this is the whole trust boundary of a cross-node
    /// round, and it touches no network so it can be tested on its own.
    /// </summary>
    internal static (bool Ok, TensorSearch.Decomposition? D, string Source, long Muls, string Note) VerifyPeerScheme(string schemeJson)
    {
        TensorSearch.Decomposition? d = SchemeCompose.Parse(schemeJson);
        if (d is null) return (false, null, "", 0, "unparseable scheme");
        if (TensorSearch.Residual(d) != 0) return (false, null, "", 0, "scheme is not an exact decomposition");

        string src = TensorSearch.Synthesize(d);
        var (compiled, muls, exact) = EvaluateCountingSource(src, d.N);
        if (!compiled) return (false, null, "", 0, "scheme did not compile");
        if (!exact) return (false, null, src, muls, "failed exact verification");
        return (true, d, src, muls, $"verified: exact {d.N}x{d.N} scheme in {muls} muls");
    }

    /// <summary>
    /// Adopt a peer-derived scheme: verify it locally (see <see cref="VerifyPeerScheme"/>), and record
    /// it only if it really does use fewer multiplications than the hive's current champion.
    /// </summary>
    internal static async Task<(bool Adopted, long Muls, string Note)> TryAdoptSchemeAsync(
        AgentCore core, string schemeJson, string fromNode, string strategy)
    {
        var (ok, d, src, muls, note) = VerifyPeerScheme(schemeJson);
        if (!ok || d is null) return (false, muls, note);

        Champion? champ = await core.GetMatmulChampionAsync(d.N);
        if (champ is not null && muls >= champ.Score)
            return (false, muls, $"verified {muls} muls but not better than {champ.Score:F0}");

        double speedup = (double)d.N * d.N * d.N / muls;
        await core.SetMatmulChampionAsync(fromNode, d.N, strategy, Metric.Muls, muls, speedup, src, schemeJson);
        return (true, muls, $"NEW {d.N}x{d.N} champion: {muls} muls ({speedup:F2}x) from {fromNode}");
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
    /// <summary>Same binding the race uses, exposed for the free autotuner's own bench harness.</summary>
    internal static Func<double[,], double[,], double[,]>? BindDoubleKernel(Assembly asm)
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

        // THE TRUST BOUNDARY for free cross-node search rounds: a peer sends a scheme, and this node
        // re-proves it from scratch. A genuine Strassen must survive; the same scheme with ONE
        // coefficient flipped must be rejected before it can ever reach the champion table.
        Console.WriteLine("-- peer-scheme verification: a peer's claim is re-proven locally, never trusted --");
        string good = SchemeJson(TensorSearch.Strassen2);
        TensorSearch.Decomposition tampered = TensorSearch.Strassen2;
        tampered.U[0, 0] = -tampered.U[0, 0];             // one sign flip ⇒ no longer computes matmul
        string bad = SchemeJson(tampered);

        var g = VerifyPeerScheme(good);
        var t = VerifyPeerScheme(bad);
        Console.WriteLine($"  valid Strassen  : ok={g.Ok} muls={g.Muls}  (expect True/7)");
        Console.WriteLine($"  tampered scheme : ok={t.Ok} — {t.Note}  (expect False)");
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
