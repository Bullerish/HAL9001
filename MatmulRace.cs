using System.Reflection;

namespace HAL9001;

/// <summary>
/// Prime Directive race engine. One round = randomly pick strategies from KernelGenerator.Strategies
/// → generate C# candidates concurrently via the LLM → compile → correctness gate (must match the
/// naive reference within 1e-9) → benchmark (warmup + median of 10 runs) → compare to hive champion.
///
/// When a round beats the champion the new record is written to Turso and a challenge broadcast goes
/// to every peer — who respond by firing their own round immediately. The result is a continuous
/// back-to-back competitive optimization loop across the whole swarm: nodes outwit each other toward
/// the Prime Directive.
///
/// One extra candidate per round is a "refine" attempt: the LLM is shown the current champion's
/// source and asked explicitly to improve it, so the loop doesn't just explore — it also perfects.
/// </summary>
public static class MatmulRace
{
    public const int DefaultSize = 128;
    private const int Warmup = 3;
    private const int Timed = 10;
    private const double AbsTol = 1e-9;
    private const double RelTol = 1e-9;

    /// <summary>The hive's current matmul speed record for one matrix size.</summary>
    public sealed record Champion(
        string Node, string Strategy,
        double MedianMs, double Speedup,
        string Source = "");

    /// <summary>What one race round produced.</summary>
    public sealed record RoundResult(
        double BestMs, double Speedup, string Strategy,
        bool NewRecord, string Summary);

    /// <summary>
    /// Run one full race round. Generates <paramref name="randomCandidates"/> implementations from
    /// randomly-chosen strategies, plus one "refine the champion" attempt when a champion exists.
    /// The fastest correct implementation is compared to the hive champion; the record is updated
    /// in Turso if we beat it. Returns null when every candidate fails compile or correctness.
    /// </summary>
    public static async Task<RoundResult?> RunRoundAsync(
        AnthropicClient client, AgentCore core, int myPort,
        int randomCandidates = 2, int size = DefaultSize,
        CancellationToken ct = default)
    {
        Champion? champ = await core.GetMatmulChampionAsync(size);

        // Fixed seeded inputs: EVERY node benchmarks on identical data — the only variable is the
        // implementation. Seeded at the project start date so the baseline never drifts.
        var rng = new Random(20260621);
        double[,] a = MatrixOps.RandomMatrix(size, size, rng);
        double[,] b = MatrixOps.RandomMatrix(size, size, rng);
        double[,] reference = MatrixOps.MultiplyReference(a, b);

        // Candidates: random strategies + one "refine the champion" attempt when source is known.
        var generator = new KernelGenerator(client);
        var picker = new Random();
        string[] chosen = KernelGenerator.Strategies
            .OrderBy(_ => picker.Next())
            .Take(randomCandidates)
            .ToArray();

        var genTasks = new List<Task<CandidateSource>>(
            chosen.Select(s => generator.GenerateForStrategyAsync(s, ct)));
        if (!string.IsNullOrWhiteSpace(champ?.Source))
            genTasks.Add(generator.RefineAsync(champ!.Source, champ.MedianMs, champ.Strategy, ct));

        CandidateSource[] candidates = await Task.WhenAll(genTasks);

        double refMs = 0, bestMs = double.MaxValue;
        string bestStrategy = "", bestSource = "";

        // Everything in one QuietScope: priority elevation + core affinity pinned for the whole
        // evaluation so baseline and all candidates are measured under identical OS conditions.
        using (KernelBenchmark.QuietScope())
        {
            refMs = KernelBenchmark.Measure(MatrixOps.MultiplyReference, a, b, Warmup, Timed).MedianMs;

            foreach (CandidateSource cand in candidates)
            {
                if (string.IsNullOrWhiteSpace(cand.Source)) continue;
                if (!RuntimeCompiler.TryCompileAssembly(cand.Source, out Assembly? asm, out _)) continue;
                Func<double[,], double[,], double[,]>? fn = BindMultiply(asm!);
                if (fn is null) continue;

                // Correctness gate: wrong answer = disqualified regardless of speed.
                try { if (!MatrixOps.Compare(reference, fn(a, b), AbsTol, RelTol, out _, out _)) continue; }
                catch { continue; }

                TimingStat t = KernelBenchmark.Measure(fn, a, b, Warmup, Timed);
                if (t.MedianMs < bestMs)
                {
                    bestMs = t.MedianMs;
                    bestStrategy = cand.Strategy;
                    bestSource = cand.Source;
                }
            }
        }

        if (bestMs == double.MaxValue) return null; // every candidate failed

        double speedup = refMs / bestMs;
        bool newRecord = champ is null || bestMs < champ.MedianMs;

        if (newRecord)
            await core.SetMatmulChampionAsync(
                $"127.0.0.1:{myPort}", size, bestStrategy, bestMs, speedup, bestSource);

        string champLine = champ is null
            ? "No prior champion — I am first."
            : $"Previous champion: {champ.Node} at {champ.MedianMs:F2}ms ({champ.Speedup:F2}x).";
        string summary = newRecord
            ? $"NEW RECORD {size}x{size}: {bestMs:F2}ms ({speedup:F2}x) — '{Short(bestStrategy, 55)}'. {champLine}"
            : $"Round {size}x{size}: my best {bestMs:F2}ms ({speedup:F2}x). {champLine} Still chasing.";

        return new RoundResult(bestMs, speedup, bestStrategy, newRecord, summary);
    }

    private static Func<double[,], double[,], double[,]>? BindMultiply(Assembly asm)
    {
        try
        {
            foreach (Type t in asm.GetTypes())
            {
                MethodInfo? mi = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
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
        catch { }
        return null;
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "...";
}
