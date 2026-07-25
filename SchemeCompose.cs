using System.Text;
using System.Text.Json;

namespace HAL9001;

/// <summary>
/// LLM-FREE COMPOSITION — the engine that lets HAL keep getting better at matrix multiplication
/// without spending a single token.
///
/// THE MATH. A bilinear algorithm that multiplies m×m matrices in R multiplications composes with
/// ITSELF: apply it to matrices whose "entries" are h×h BLOCKS, and every block product becomes a
/// recursive call. m×m in R muls therefore gives (m·h)×(m·h) in R·(muls for h). Iterating k times
/// gives an exact algorithm for n = m^k using R^k multiplications — Strassen's 2×2-in-7 becomes
/// 8×8 in 343 (naive 512), 16×16 in 2401 (naive 4096), 32×32 in 16807 (naive 32768).
///
/// WHY THIS MATTERS HERE. The direct tensor search (<see cref="TensorSearch"/>) is the only other
/// free engine, and its reach is small n — the arrays it needs grow as rank·n⁴, so it declines
/// anything big. Before this, every rung above the search's reach could only improve by asking an
/// LLM, so an unfunded HAL did nothing at those sizes. Now every size that factors as m^k improves
/// for free, immediately.
///
/// IT COMPOUNDS. The base scheme is not hard-coded to Strassen: <see cref="BestBaseAsync"/> reads
/// the hive's champion schemes for small sizes and picks the base with the best exponent
/// ω = log(R)/log(m). So the day the search (or a volunteer) derives a better small scheme, EVERY
/// larger size inherits the improvement on the next round — HAL gets smarter at 32×32 by getting
/// smarter at 4×4. That is real, compounding, LLM-free self-improvement.
///
/// NOTHING IS TRUSTED. A base scheme is used only after its tensor residual is 0, and every
/// composed algorithm is compiled, multiplication-counted, and EXACT-verified (the bite-16
/// BigInteger verifier) before it can become a champion — same bar as any other candidate.
/// </summary>
public static class SchemeCompose
{
    /// <summary>A composition plan: build <see cref="Size"/> from <see cref="Base"/> by recursing
    /// <see cref="Levels"/> times, costing <see cref="Muls"/> multiplications.</summary>
    public sealed record Plan(int Size, TensorSearch.Decomposition Base, int Levels, long Muls, string Origin)
    {
        public string Recipe => $"{Base.N}x{Base.N} rank-{Base.Rank} ({Origin}) composed {Levels}x → {Size}x{Size} in {Muls} muls";
    }

    /// <summary>Largest composed rank we will turn into source + verify. Guards compile/verify time:
    /// the emitted source is small (O(R + m²) lines) but the RUN performs R^k multiplications, and
    /// the exact verifier runs it many times.</summary>
    public const long MaxComposedMuls = 200_000;

    /// <summary>How many exact-verification trials a composed scheme gets. Fewer for the big ones —
    /// each trial runs the whole algorithm plus a BigInteger reference multiply.</summary>
    private static int TrialsFor(long muls) => muls > 20_000 ? 8 : muls > 2_000 ? 24 : 64;

    // ── choosing a base ───────────────────────────────────────────────────────────────────────
    /// <summary>
    /// The best composition base the hive currently knows: its champion schemes for small sizes,
    /// ranked by exponent ω = log(rank)/log(m) (lower is better — that is what decides the cost of
    /// every larger size), falling back to built-in Strassen. Only schemes with residual 0 are used.
    /// </summary>
    public static async Task<(TensorSearch.Decomposition Base, string Origin)> BestBaseAsync(AgentCore core)
    {
        TensorSearch.Decomposition best = TensorSearch.Strassen2;
        string origin = "Strassen (built in)";
        double bestOmega = Omega(best);

        foreach (int m in new[] { 2, 3, 4 })
        {
            TensorSearch.Decomposition? cand = null;
            try
            {
                MatmulRace.Champion? champ = await core.GetMatmulChampionAsync(m);
                if (champ is not null && champ.Scheme.Length > 0) cand = Parse(champ.Scheme);
            }
            catch { }
            if (cand is null || cand.N != m) continue;
            double om = Omega(cand);
            if (om >= bestOmega) continue;
            // Never compose on top of an unverified scheme — one wrong base would corrupt every size.
            if (TensorSearch.Residual(cand) != 0) continue;
            best = cand; bestOmega = om; origin = $"hive champion {m}x{m}";
        }
        return (best, origin);
    }

    /// <summary>The exponent a base implies: n^ω operations, ω = log(rank)/log(m). Strassen = 2.807.</summary>
    public static double Omega(TensorSearch.Decomposition d) => Math.Log(d.Rank) / Math.Log(d.N);

    /// <summary>Plan a composition for <paramref name="size"/>, or null when the size is not an exact
    /// power of the base's dimension (e.g. base 2 can build 4/8/16/32, not 3 or 6) or the result would
    /// not beat the target we are trying to improve on.</summary>
    public static Plan? PlanFor(int size, TensorSearch.Decomposition b, string origin, long? mustBeat = null)
    {
        if (size < b.N || b.N < 2) return null;
        int levels = 0;
        long p = 1;
        while (p < size) { p *= b.N; levels++; if (levels > 24) return null; }
        if (p != size) return null;                       // not a power of the base dimension

        long muls = 1;
        for (int i = 0; i < levels; i++)
        {
            muls *= b.Rank;
            if (muls > MaxComposedMuls) return null;      // too big to verify honestly — decline
        }
        if (mustBeat is not null && muls >= mustBeat.Value) return null;
        return new Plan(size, b, levels, muls, origin);
    }

    // ── code generation ───────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Emit a RECURSIVE C# kernel for the plan. Recursive rather than flattened on purpose: a
    /// flattened rank-2401 scheme is megabytes of source that Roslyn takes an age to compile, while
    /// this is O(R + m²) lines regardless of the target size and compiles in milliseconds. The
    /// multiplication count is identical either way — it is the number of leaf Scalar products.
    /// </summary>
    public static string Synthesize(Plan plan) => Emit(plan.Base, "Scalar", counting: true);

    /// <summary>The same algorithm over plain doubles — a genuinely fast kernel for the wall-clock
    /// track (sub-cubic work, with a cutoff to naive where recursion stops paying).</summary>
    public static string SynthesizeDouble(TensorSearch.Decomposition b, int cutoff) =>
        Emit(b, "double", counting: false, cutoff: cutoff);

    private static string Emit(TensorSearch.Decomposition b, string t, bool counting, int cutoff = 1)
    {
        int m = b.N, m2 = m * m;
        var sb = new StringBuilder();
        if (counting) sb.AppendLine("using HAL9001;");
        sb.AppendLine("public static class Kernel {");
        sb.AppendLine($"  const int M = {m};");
        sb.AppendLine($"  const int CUT = {Math.Max(1, cutoff)};");
        sb.AppendLine($"  public static {t}[,] Multiply({t}[,] A, {t}[,] B) {{ return Mul(A, B, A.GetLength(0)); }}");
        sb.AppendLine($"  static {t}[,] Mul({t}[,] A, {t}[,] B, int n) {{");
        // Base case + the safety net: any size that is not divisible by M falls back to a naive
        // multiply, so the kernel is CORRECT at every size, not just the composed one.
        sb.AppendLine("    if (n <= CUT || n % M != 0) return Naive(A, B, n);");
        sb.AppendLine("    int h = n / M;");
        for (int a = 0; a < m2; a++) sb.AppendLine($"    var a{a} = Blk(A, {a / m} * h, {a % m} * h, h);");
        for (int bi = 0; bi < m2; bi++) sb.AppendLine($"    var b{bi} = Blk(B, {bi / m} * h, {bi % m} * h, h);");

        for (int r = 0; r < b.Rank; r++)
        {
            string? ac = Combo(b.U, r, m2, "a", byColumn: false);
            string? bc = Combo(b.V, r, m2, "b", byColumn: false);
            sb.AppendLine(ac is null || bc is null
                ? $"    var P{r} = Zero(h);"                      // an all-zero row contributes nothing
                : $"    var P{r} = Mul({ac}, {bc}, h);");
        }

        sb.AppendLine($"    var C = new {t}[n, n];");
        for (int g = 0; g < m2; g++)
        {
            string? cc = Combo(b.W, g, b.Rank, "P", byColumn: true);
            if (cc is not null) sb.AppendLine($"    Place(C, {cc}, {g / m} * h, {g % m} * h);");
        }
        sb.AppendLine("    return C;");
        sb.AppendLine("  }");

        // Helpers. Block add/sub cost only additions (free in the ranked metric); negation is free.
        sb.AppendLine($"  static {t}[,] Blk({t}[,] X, int r0, int c0, int h) {{ var o = new {t}[h, h];");
        sb.AppendLine("    for (int i = 0; i < h; i++) for (int j = 0; j < h; j++) o[i, j] = X[r0 + i, c0 + j]; return o; }");
        sb.AppendLine($"  static {t}[,] AddB({t}[,] X, {t}[,] Y) {{ int h = X.GetLength(0); var o = new {t}[h, h];");
        sb.AppendLine("    for (int i = 0; i < h; i++) for (int j = 0; j < h; j++) o[i, j] = X[i, j] + Y[i, j]; return o; }");
        sb.AppendLine($"  static {t}[,] SubB({t}[,] X, {t}[,] Y) {{ int h = X.GetLength(0); var o = new {t}[h, h];");
        sb.AppendLine("    for (int i = 0; i < h; i++) for (int j = 0; j < h; j++) o[i, j] = X[i, j] - Y[i, j]; return o; }");
        sb.AppendLine($"  static {t}[,] NegB({t}[,] X) {{ int h = X.GetLength(0); var o = new {t}[h, h];");
        sb.AppendLine("    for (int i = 0; i < h; i++) for (int j = 0; j < h; j++) o[i, j] = -X[i, j]; return o; }");
        sb.AppendLine($"  static {t}[,] Zero(int h) {{ return new {t}[h, h]; }}");
        sb.AppendLine($"  static void Place({t}[,] C, {t}[,] X, int r0, int c0) {{ int h = X.GetLength(0);");
        sb.AppendLine("    for (int i = 0; i < h; i++) for (int j = 0; j < h; j++) C[r0 + i, c0 + j] = X[i, j]; }");
        sb.AppendLine($"  static {t}[,] Naive({t}[,] A, {t}[,] B, int n) {{ var o = new {t}[n, n];");
        sb.AppendLine($"    for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) {{ {t} s = default; ");
        sb.AppendLine("      for (int k = 0; k < n; k++) s = s + A[i, k] * B[k, j]; o[i, j] = s; } return o; }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // A ±1 combination of named block variables, as nested AddB/SubB/NegB calls; null if all-zero.
    //   byColumn=false: row `line` of M over its second index  (U/V rows over the m² entries)
    //   byColumn=true:  column `line` of M over its first index (W columns over the R terms)
    private static string? Combo(int[,] mat, int line, int count, string prefix, bool byColumn)
    {
        string? expr = null;
        for (int idx = 0; idx < count; idx++)
        {
            int c = byColumn ? mat[idx, line] : mat[line, idx];
            if (c == 0) continue;
            string term = prefix + idx;
            expr = expr is null
                ? (c < 0 ? $"NegB({term})" : term)
                : (c < 0 ? $"SubB({expr}, {term})" : $"AddB({expr}, {term})");
        }
        return expr;
    }

    // ── evaluation ────────────────────────────────────────────────────────────────────────────
    /// <summary>Build, compile, count and EXACT-verify a composed algorithm. Returns the source and
    /// its verified multiplication count, or null if anything about it failed to check out.</summary>
    public static (string Source, long Muls)? Realize(Plan plan, Action<string>? log = null)
    {
        string src = Synthesize(plan);
        var (compiled, muls, exact) = MatmulRace.EvaluateCountingSource(src, plan.Size, TrialsFor(plan.Muls));
        if (!compiled) { log?.Invoke($"compose: {plan.Recipe} — FAILED to compile (rejected)"); return null; }
        if (!exact) { log?.Invoke($"compose: {plan.Recipe} — failed exact verification (rejected)"); return null; }
        if (muls != plan.Muls)
            log?.Invoke($"compose: counted {muls} muls (predicted {plan.Muls}) — using the counted value");
        return (src, muls);
    }

    /// <summary>The whole free track in one call: pick the best base, plan a composition for this
    /// size that beats <paramref name="mustBeat"/>, build it and verify it. Null when there is
    /// nothing better to offer (not a composable size, or the current champion already wins).</summary>
    public static async Task<(string Source, long Muls, string Recipe)?> TryImproveAsync(
        AgentCore core, int size, long? mustBeat, Action<string>? log = null)
    {
        var (b, origin) = await BestBaseAsync(core);
        Plan? plan = PlanFor(size, b, origin, mustBeat);
        if (plan is null) return null;
        log?.Invoke($"compose: {plan.Recipe} — building + verifying (no LLM)");
        var built = Realize(plan, log);
        if (built is null) return null;
        return (built.Value.Source, built.Value.Muls, plan.Recipe);
    }

    /// <summary>
    /// Push the hive's best small scheme UP into every composable size that a plain race round would
    /// never revisit — the step that actually makes composition compound.
    ///
    /// WHY THIS EXISTS. <see cref="TryImproveAsync"/> only runs while a size is being RACED, but the
    /// ladder climbs away from the small rungs and never returns to them, and the side round only
    /// re-attacks 2/3/4. So a better 4×4 had no path to 8/16/32: the hive sat on 16×16 = 2744 while
    /// 2401 was free and already provable. This walks the muls-scored rungs and adopts a composed
    /// champion wherever one genuinely beats the record.
    ///
    /// Only the multiplication-counted rungs are touched (below <see cref="MatmulLadder.MsThreshold"/>) —
    /// above that the ladder scores by wall-clock, where a mul count is not the record. Every adoption
    /// still goes through compile + BigInteger exact verification, and is gated on beating the current
    /// champion, so running this on several nodes at once converges instead of fighting.
    /// </summary>
    public static async Task<int> PropagateAsync(AgentCore core, int myPort, Action<string>? log = null)
    {
        var (b, origin) = await BestBaseAsync(core);
        int lifted = 0;

        foreach (int size in MatmulLadder.BaseRungs)
        {
            if (size >= MatmulLadder.MsThreshold) break;   // wall-clock territory — not ours to claim
            if (size <= b.N) continue;                     // the base itself is not a composition

            long? mustBeat = null;
            try
            {
                MatmulRace.Champion? champ = await core.GetMatmulChampionAsync(size);
                // Only compare against a mul-count record; a stale wall-clock row is not comparable.
                if (champ is not null && champ.Metric == MatmulRace.Metric.Muls) mustBeat = (long)champ.Score;
            }
            catch { }

            // Declines when the size is not a power of the base dimension, or the champion already wins.
            Plan? plan = PlanFor(size, b, origin, mustBeat);
            if (plan is null) continue;

            log?.Invoke($"compose: {size}x{size} — {plan.Recipe} (verifying)");
            var built = Realize(plan, log);
            if (built is null) continue;                   // compile or exact-verify rejected it

            double speedup = (double)size * size * size / built.Value.Muls;
            await core.SetMatmulChampionAsync($"127.0.0.1:{myPort}", size,
                "composition (LLM-free): " + plan.Recipe, MatmulRace.Metric.Muls,
                built.Value.Muls, speedup, built.Value.Source);
            log?.Invoke($"OK compose: NEW {size}x{size} champion — {built.Value.Muls} muls ({speedup:F2}x)");
            lifted++;
        }
        return lifted;
    }

    // ── scheme JSON (same {n,rank,u,v,w} shape the dashboard + volunteers use) ─────────────────
    private sealed record SchemeDto(int n, int rank, int[][]? u, int[][]? v, int[][]? w);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Parse a stored scheme; null if it is malformed or not square-consistent.</summary>
    public static TensorSearch.Decomposition? Parse(string json)
    {
        SchemeDto? d;
        try { d = JsonSerializer.Deserialize<SchemeDto>(json, JsonOpts); } catch { return null; }
        if (d is null || d.u is null || d.v is null || d.w is null) return null;
        if (d.n < 2 || d.rank < 1) return null;
        int n2 = d.n * d.n;
        if (d.u.Length != d.rank || d.v.Length != d.rank || d.w.Length != d.rank) return null;
        if (d.u.Any(r => r.Length != n2) || d.v.Any(r => r.Length != n2) || d.w.Any(r => r.Length != n2)) return null;
        return new TensorSearch.Decomposition(d.n, d.rank, To2D(d.u), To2D(d.v), To2D(d.w));
    }

    private static int[,] To2D(int[][] j)
    {
        var a = new int[j.Length, j[0].Length];
        for (int r = 0; r < j.Length; r++)
            for (int c = 0; c < j[0].Length; c++) a[r, c] = j[r][c];
        return a;
    }

    // ── demo / acceptance ─────────────────────────────────────────────────────────────────────
    /// <summary>`dotnet run -- compose [size]` — build every composable size from the built-in base
    /// and report verified multiplication counts against naive. No API key, no hive, no network.</summary>
    public static void Demo(int maxSize)
    {
        TensorSearch.Decomposition b = TensorSearch.Strassen2;
        Console.WriteLine($"== LLM-free composition: {b.N}x{b.N} rank-{b.Rank} (ω={Omega(b):F3}) composed upward ==");
        Console.WriteLine($"   residual of the base scheme = {TensorSearch.Residual(b)} (expect 0)\n");
        for (int size = b.N * b.N; size <= maxSize; size *= b.N)
        {
            Plan? plan = PlanFor(size, b, "built in");
            if (plan is null) { Console.WriteLine($"   {size}x{size}: declined (too large to verify honestly)"); continue; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var built = Realize(plan, s => Console.WriteLine("   " + s));
            sw.Stop();
            long naive = (long)size * size * size;
            Console.WriteLine(built is null
                ? $"   {size}x{size}: REJECTED"
                : $"   {size}x{size}: {built.Value.Muls} muls vs naive {naive} " +
                  $"({(double)naive / built.Value.Muls:F2}x fewer) — exact-verified in {sw.Elapsed.TotalSeconds:F1}s");
        }
    }
}
