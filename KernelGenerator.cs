namespace HAL9001;

/// <summary>A generated candidate: the strategy we asked for, plus the raw C# source we got back.</summary>
public sealed record CandidateSource(string Strategy, string Source);

/// <summary>
/// Asks the LLM to WRITE varied C# matrix-multiply implementations — the "generate" step of the
/// kernel-optimization search. As everywhere in HAL9001 the model is a TOOLSMITH: it returns
/// CODE, never an answer. Here it returns a fast(er) matmul method that we then compile, verify,
/// and time ourselves.
///
/// To maximise DIVERSITY (and keep parsing trivial — one method per reply) we make one focused
/// call per strategy, each steering the model toward a different optimization technique. The
/// strategies are classic single-threaded matmul optimizations with real, measurable speed
/// differences.
/// </summary>
public sealed class KernelGenerator
{
    private readonly AnthropicClient _client;
    public KernelGenerator(AnthropicClient client) => _client = client;

    /// <summary>The distinct optimization angles we ask for, in order. Take as many as requested.</summary>
    public static readonly IReadOnlyList<string> Strategies = new[]
    {
        "Classic i-j-k triple loop, but written as cleanly and tightly as possible (cache locals, hoist invariants). A baseline-style implementation.",
        "Reorder the loops to i-k-j so the innermost loop strides CONTIGUOUSLY across a row of B and a row of C. This is cache-friendly and the inner loop auto-vectorises (SIMD). Accumulate into the C row in place.",
        "Transpose B into a temporary array first, then compute each C[i,j] as a dot product of a contiguous row of A and a contiguous row of the transposed B — so BOTH operands are walked with unit stride.",
        "Cache blocking / tiling: split the i, j, k loops into blocks (e.g. block size 32 or 64) so each block of the working set fits in L1/L2 cache and is reused before eviction. Must handle dimensions that are not a multiple of the block size.",
        "Flatten the matrices to 1D and use unsafe pointers (or Span<double>) to eliminate array bounds checks on the hot path, combined with a cache-friendly loop order. Keep it single-threaded.",
        "Register blocking / loop unrolling: an i-k-j order whose innermost loop is unrolled by 4 (handling any remainder) to expose instruction-level parallelism and reduce loop overhead.",
        "Pack B into a contiguous column-major 1D scratch buffer (B_col[k * m + j] = B[k, j]) before the main loops, then multiply each A row against its matching B column segment with unit stride on both sides of every dot product.",
        "Recursive divide-and-conquer: split A into top/bottom halves and B into left/right halves, recurse on the 4 sub-problems and accumulate into C, with a base case (n <= 32) that falls back to a tight i-k-j triple loop. Handle non-square and non-power-of-two sizes.",
    };

    private const string SystemPrompt = """
        You are an expert C# performance engineer specialising in numerical kernels. You write
        ONLY code — no prose, no explanation, no markdown fences.

        Write a single self-contained C# implementation of dense matrix multiplication with EXACTLY
        this shape (the benchmark calls it by reflection, so the signature must match precisely):

            public static class Kernel
            {
                public static double[,] Multiply(double[,] a, double[,] b)
                {
                    // a is n x k, b is k x m, return c (n x m) = a * b
                }
            }

        HARD REQUIREMENTS:
        - The method MUST be `public static double[,] Multiply(double[,] a, double[,] b)`.
        - It MUST be correct for GENERAL rectangular sizes: n = a.GetLength(0), k = a.GetLength(1) =
          b.GetLength(0), m = b.GetLength(1). Do NOT assume square, and do NOT assume sizes are a
          multiple of any block/unroll factor — handle remainders.
        - SINGLE-THREADED ONLY. Do NOT use Parallel, Task, Thread, threads, or any concurrency.
        - Only the System namespace / built-in types. No external packages.
        - You MAY use `unsafe`, pointers, and `Span<T>` if your strategy calls for it.
        - Return ONLY the C# source (you may include `using` directives and, if you use unsafe,
          assume the compiler allows unsafe blocks). No backticks, no commentary.
        """;

    /// <summary>Generate the first <paramref name="count"/> strategies CONCURRENTLY.</summary>
    public async Task<IReadOnlyList<CandidateSource>> GenerateAsync(int count, CancellationToken ct = default)
    {
        var picked = Strategies.Take(Math.Min(count, Strategies.Count)).ToList();
        var tasks = picked.Select(s => GenerateOneAsync(s, ct)).ToList();
        return await Task.WhenAll(tasks);
    }

    /// <summary>Generate a candidate for one specific <paramref name="strategy"/>.</summary>
    public Task<CandidateSource> GenerateForStrategyAsync(string strategy, CancellationToken ct = default)
        => GenerateOneAsync(strategy, ct);

    /// <summary>
    /// Ask the LLM to improve upon <paramref name="championSource"/> — the hive's current fastest
    /// implementation. Gives the model the existing code and timing so it can make targeted changes
    /// rather than generating from scratch. This is the "perfect the method" loop.
    /// </summary>
    public async Task<CandidateSource> RefineAsync(
        string championSource, double championMs, string championStrategy,
        CancellationToken ct = default)
    {
        string user = $"""
            The current fastest correct matrix-multiply implementation runs in {championMs:F2} ms
            (strategy: {championStrategy}).

            Source:
            {championSource}

            Improve it further. Try: better tiling parameters, SIMD-friendly inner loops, reducing
            memory traffic, or a fundamentally different approach. The signature must remain:
                public static double[,] Multiply(double[,] a, double[,] b)
            All hard requirements from the system prompt still apply. Return ONLY the C# source.
            """;
        string raw;
        try { raw = await _client.CompleteAsync(SystemPrompt, user, ct); }
        catch (Exception ex)
        {
            Console.WriteLine($"  [refine] failed: {ex.Message}");
            raw = "";
        }
        return new CandidateSource("refine-champion", CleanSource(raw));
    }

    private async Task<CandidateSource> GenerateOneAsync(string strategy, CancellationToken ct)
    {
        string user = $"Optimization strategy to use for this implementation:\n{strategy}\n\nReturn the C# code:";
        string raw;
        try
        {
            raw = await _client.CompleteAsync(SystemPrompt, user, ct);
        }
        catch (Exception ex)
        {
            // A failed generation isn't fatal — emit empty source so it shows as "did not compile".
            Console.WriteLine($"  [generate] strategy failed: {ex.Message}");
            raw = "";
        }
        return new CandidateSource(strategy, CleanSource(raw));
    }

    /// <summary>
    /// Strip markdown code fences if the model added them despite instructions. Mirrors the
    /// defensive fence-stripping used elsewhere in HAL9001 (the model usually complies, but we
    /// never trust it to).
    /// </summary>
    private static string CleanSource(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```"))
        {
            int firstNewline = s.IndexOf('\n');
            if (firstNewline >= 0) s = s[(firstNewline + 1)..];
            int lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0) s = s[..lastFence];
        }
        return s.Trim();
    }
}
