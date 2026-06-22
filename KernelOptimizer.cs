using System.Reflection;

namespace HAL9001;

/// <summary>
/// KERNEL OPTIMIZATION SEARCH — bite 1 (single node, no swarm, no distribution, no push).
///
/// Proves the core loop of the whole direction on one machine:
///
///     generate N candidate implementations  (LLM, the toolsmith)
///        → compile each                      (RuntimeCompiler.TryCompileAssembly + Roslyn)
///        → CORRECTNESS GATE                  (must match the reference within tolerance, else DQ)
///        → benchmark the correct ones        (KernelBenchmark: warmup + median of N timed runs)
///        → rank by speed, name the winner    (fastest CORRECT candidate, with its speedup)
///
/// Correctness is the floor: a wrong candidate is disqualified no matter how fast — this is the
/// exact analog of HAL9001's competitive-deliberation quality floor, with SPEED added as the
/// ranking dimension on top of it. The operation is dense double matrix multiply at a fixed size.
/// </summary>
public static class KernelOptimizer
{
    // Correctness tolerance. A correct candidate differs from the reference only by floating-point
    // reordering (~k·eps ≈ 1e-13 here); a buggy one is off by O(1). 1e-9 sits safely in that gap.
    private const double AbsTol = 1e-9;
    private const double RelTol = 1e-9;

    /// <summary>One candidate's full result through the pipeline (for the final ranking table).</summary>
    private sealed record Result(
        int Index, string Strategy, string Source,
        bool Compiled, bool Correct,
        double MaxRelErr, TimingStat? Timing, string Note);

    public static async Task RunAsync(int size, int candidateCount, int warmup = 5, int iterations = 15)
    {
        PrintBanner(size, candidateCount, warmup, iterations);

        AnthropicClient? client = AnthropicClient.FromEnvironment();
        if (client is null)
        {
            Console.WriteLine("ANTHROPIC_API_KEY is not set — candidate generation needs it.");
            Console.WriteLine("Set it and re-run:  $env:ANTHROPIC_API_KEY = \"sk-ant-...\"");
            return;
        }
        using (client)
        {
            // ── Fixed, seeded inputs: the SAME A and B time every candidate (and the reference). ──
            var rng = new Random(20260621);
            double[,] a = MatrixOps.RandomMatrix(size, size, rng);
            double[,] b = MatrixOps.RandomMatrix(size, size, rng);

            // The reference output at the benchmark size — the oracle the timed call must match.
            double[,] referenceOutput = MatrixOps.MultiplyReference(a, b);

            // A battery of correctness tests of VARIED shapes. The benchmark pair (a,b) is included
            // so we know the very computation we time is correct; the others catch bugs a square
            // power-of-two would hide — non-square dims, tiny dims smaller than a block/unroll
            // factor, and the 1×1 edge. (Tiling/unrolling candidates must survive all of these.)
            var tests = BuildCorrectnessTests(a, b, referenceOutput, rng);

            // ── GENERATE (concurrently). ──────────────────────────────────────────────────────
            Console.WriteLine($"Generating {candidateCount} candidate(s) via {AnthropicClient.Model} ...\n");
            var generator = new KernelGenerator(client);
            IReadOnlyList<CandidateSource> candidates = await generator.GenerateAsync(candidateCount);

            var results = new List<Result>();

            // Quiet the machine (high priority + single-core pin, best-effort) for ALL timing,
            // including the reference baseline, so everything is measured under the same conditions.
            using (KernelBenchmark.QuietScope())
            {
                // Baseline: time the reference itself, identically to candidates.
                Console.WriteLine("Benchmarking reference (naive triple loop) as the baseline ...");
                TimingStat refTiming = KernelBenchmark.Measure(
                    MatrixOps.MultiplyReference, a, b, warmup, iterations);
                Console.WriteLine($"  reference: median {refTiming.MedianMs:F2} ms  (min {refTiming.MinMs:F2}, max {refTiming.MaxMs:F2})\n");

                // ── Each candidate: compile → correctness gate → benchmark. ────────────────────
                for (int i = 0; i < candidates.Count; i++)
                {
                    CandidateSource cand = candidates[i];
                    int label = i + 1;
                    Console.WriteLine($"── Candidate {label}: {Shorten(cand.Strategy, 70)}");

                    results.Add(Evaluate(label, cand, tests, a, b, warmup, iterations));
                    Console.WriteLine();
                }

                // ── RANK + REPORT. ─────────────────────────────────────────────────────────────
                Report(results, refTiming);
            }

            Console.WriteLine($"\n(anti-dead-code-elimination sink = {KernelBenchmark.Sink:E3})");
        }
    }

    /// <summary>Compile one candidate, run it through the correctness gate, and (if correct) time it.</summary>
    private static Result Evaluate(
        int label, CandidateSource cand,
        IReadOnlyList<(double[,] A, double[,] B, double[,] Want)> tests,
        double[,] benchA, double[,] benchB, int warmup, int iterations)
    {
        if (string.IsNullOrWhiteSpace(cand.Source))
            return new Result(label, cand.Strategy, cand.Source, false, false, double.NaN, null, "no source generated");

        // COMPILE (reusing HAL's Roslyn pipeline; unsafe enabled for pointer candidates).
        if (!RuntimeCompiler.TryCompileAssembly(cand.Source, out Assembly? asm, out string? diag))
        {
            string firstError = (diag ?? "compile failed").Trim().Split('\n').FirstOrDefault()?.Trim() ?? "compile failed";
            Console.WriteLine($"   [compile] FAILED — discarded ({firstError})");
            return new Result(label, cand.Strategy, cand.Source, false, false, double.NaN, null, "did not compile");
        }

        // REFLECT the required method and bind it to a typed delegate (no string marshalling).
        Func<double[,], double[,], double[,]>? fn = BindMultiply(asm!);
        if (fn is null)
        {
            Console.WriteLine("   [load] compiled, but no matching 'double[,] Multiply(double[,], double[,])' found — discarded");
            return new Result(label, cand.Strategy, cand.Source, true, false, double.NaN, null, "no Multiply method");
        }
        Console.WriteLine("   [compile] ok");

        // CORRECTNESS GATE — must pass EVERY shape, within tolerance, without throwing. DQ otherwise.
        double worstRel = 0.0;
        for (int t = 0; t < tests.Count; t++)
        {
            (double[,] A, double[,] B, double[,] Want) = tests[t];
            double[,] got;
            try
            {
                got = fn(A, B);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   [correct] THREW on test {t + 1} ({A.GetLength(0)}x{A.GetLength(1)} · {B.GetLength(0)}x{B.GetLength(1)}): {ex.GetType().Name} — DISQUALIFIED");
                return new Result(label, cand.Strategy, cand.Source, true, false, double.NaN, null, "threw during correctness");
            }

            bool ok = MatrixOps.Compare(Want, got, AbsTol, RelTol, out _, out double maxRel);
            if (maxRel > worstRel && !double.IsInfinity(maxRel)) worstRel = maxRel;
            if (!ok)
            {
                Console.WriteLine($"   [correct] WRONG output on test {t + 1} ({A.GetLength(0)}x{A.GetLength(1)} · {B.GetLength(0)}x{B.GetLength(1)}), maxRelErr={maxRel:E2} — DISQUALIFIED (speed irrelevant)");
                return new Result(label, cand.Strategy, cand.Source, true, false, maxRel, null, "incorrect output");
            }
        }
        Console.WriteLine($"   [correct] PASS all {tests.Count} tests (worst relative error {worstRel:E2})");

        // BENCHMARK — only correct candidates reach here.
        TimingStat timing = KernelBenchmark.Measure(fn, benchA, benchB, warmup, iterations);
        Console.WriteLine($"   [bench]   median {timing.MedianMs:F2} ms  (min {timing.MinMs:F2}, max {timing.MaxMs:F2})");
        return new Result(label, cand.Strategy, cand.Source, true, true, worstRel, timing, "ok");
    }

    /// <summary>Find a public static <c>double[,] Multiply(double[,], double[,])</c> and bind it.</summary>
    /// Any reflection failure (odd type-load, delegate bind) returns null — discarded, never fatal.
    private static Func<double[,], double[,], double[,]>? BindMultiply(Assembly asm)
    {
        try
        {
            foreach (Type type in asm.GetTypes())
            {
                MethodInfo? mi = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m =>
                        m.Name == "Multiply" &&
                        m.ReturnType == typeof(double[,]) &&
                        m.GetParameters().Length == 2 &&
                        m.GetParameters()[0].ParameterType == typeof(double[,]) &&
                        m.GetParameters()[1].ParameterType == typeof(double[,]));
                if (mi is not null)
                    return mi.CreateDelegate<Func<double[,], double[,], double[,]>>();
            }
        }
        catch { /* fall through → treated as "no Multiply method" */ }
        return null;
    }

    /// <summary>The varied-shape correctness battery (benchmark pair first, then edge shapes).</summary>
    private static List<(double[,] A, double[,] B, double[,] Want)> BuildCorrectnessTests(
        double[,] benchA, double[,] benchB, double[,] benchWant, Random rng)
    {
        var list = new List<(double[,], double[,], double[,])>
        {
            (benchA, benchB, benchWant), // the exact computation we will time
        };
        // (n,k,m): square, non-square, tiny-below-block, and the 1×1 edge.
        foreach (var (n, k, m) in new[] { (64, 64, 64), (40, 56, 24), (3, 5, 7), (1, 1, 1) })
        {
            double[,] a = MatrixOps.RandomMatrix(n, k, rng);
            double[,] b = MatrixOps.RandomMatrix(k, m, rng);
            list.Add((a, b, MatrixOps.MultiplyReference(a, b)));
        }
        return list;
    }

    /// <summary>Render the ranked table and announce the fastest correct candidate + its source.</summary>
    private static void Report(List<Result> results, TimingStat refTiming)
    {
        Console.WriteLine("══════════════════════════════════════════════════════════════════════════════");
        Console.WriteLine(" RESULTS — ranked by benchmark speed (correct candidates first, fastest on top)");
        Console.WriteLine("══════════════════════════════════════════════════════════════════════════════");
        Console.WriteLine($"{"#",-3}{"compiled",-10}{"correct",-9}{"median ms",-12}{"min ms",-10}{"speedup",-9}strategy");
        Console.WriteLine(new string('─', 78));

        // Baseline row for context (speedup 1.00× by definition).
        Console.WriteLine($"{"ref",-3}{"yes",-10}{"oracle",-9}{refTiming.MedianMs,-12:F2}{refTiming.MinMs,-10:F2}{"1.00x",-9}naive triple loop (baseline)");

        // Correct candidates fastest-first, then everything else (DQ/failed) after.
        var correct = results.Where(r => r.Correct && r.Timing is not null)
                             .OrderBy(r => r.Timing!.MedianMs).ToList();
        var rejected = results.Where(r => !(r.Correct && r.Timing is not null)).ToList();

        foreach (Result r in correct)
        {
            double speedup = refTiming.MedianMs / r.Timing!.MedianMs;
            Console.WriteLine($"{r.Index,-3}{"yes",-10}{"yes",-9}{r.Timing.MedianMs,-12:F2}{r.Timing.MinMs,-10:F2}{speedup,-8:F2}x {Shorten(r.Strategy, 38)}");
        }
        foreach (Result r in rejected)
        {
            string compiled = r.Compiled ? "yes" : "no";
            string correctness = r.Compiled ? "NO" : "-";
            Console.WriteLine($"{r.Index,-3}{compiled,-10}{correctness,-9}{"-",-12}{"-",-10}{"-",-9}{Shorten(r.Strategy, 38)}  [{r.Note}]");
        }

        Console.WriteLine(new string('─', 78));

        if (correct.Count == 0)
        {
            Console.WriteLine("No correct candidate this run — nothing to crown. (Re-run to draw new candidates.)");
            return;
        }

        Result winner = correct[0];
        double winSpeedup = refTiming.MedianMs / winner.Timing!.MedianMs;
        Console.WriteLine($"\n*** WINNER: Candidate {winner.Index} — {winner.Timing.MedianMs:F2} ms, {winSpeedup:F2}x faster than the naive reference. ***");
        Console.WriteLine($"    strategy: {winner.Strategy}");
        Console.WriteLine("\n──────── winning candidate source ────────");
        Console.WriteLine(winner.Source);
        Console.WriteLine("──────────────────────────────────────────");
    }

    private static void PrintBanner(int size, int count, int warmup, int iterations)
    {
        Console.WriteLine("==============================================================================");
        Console.WriteLine(" HAL9001 — Kernel Optimization Search (bite 1: single node)");
        Console.WriteLine("==============================================================================");
        Console.WriteLine($" operation : dense matrix multiply, {size}x{size} doubles (a*b)");
        Console.WriteLine($" candidates: {count}   |   benchmark: {warmup} warmup + {iterations} timed runs, ranked by MEDIAN");
        Console.WriteLine(" loop      : generate -> compile -> verify-correct (oracle: naive triple loop)");
        Console.WriteLine("             -> benchmark correct ones -> rank by speedup over the baseline");
        Console.WriteLine(" note      : correctness is the floor — a wrong candidate is disqualified");
        Console.WriteLine("             regardless of speed. Single-threaded comparison only.");
        Console.WriteLine("==============================================================================\n");
    }

    private static string Shorten(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";
}
