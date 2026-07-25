namespace HAL9001;

/// <summary>
/// The Prime Directive size ladder (bite 15). The hive climbs matrix sizes: for each one it races
/// round after round, and when it can no longer improve — a PLATEAU of <see cref="PlateauRounds"/>
/// consecutive rounds with no new record — it declares that size converged and climbs to the next.
///
/// THE LADDER HAS NO END. Above the hand-written rungs it keeps DOUBLING (512, 1024, 2048, …), so
/// "perfected this size" always means "on to a bigger one". The only stop is a resource limit —
/// <see cref="MaxSize"/>, which you raise on a bigger machine — and even there the hive keeps racing
/// the top size rather than going quiet. There is no `done` state any more.
///
/// This is the honest version of "race until optimal". You cannot PROVE a matmul implementation is
/// optimal (for a plain 3×3 multiply the optimal multiplication count is literally an open problem —
/// known only to lie between 19 and 23), so the ladder stops on *empirical convergence*, not proof:
/// "we have stopped finding anything better here", not "nothing better can exist".
///
/// The metric switches with size (see <see cref="MetricFor"/>): small sizes are scored by scalar
/// MULTIPLICATION COUNT (where Strassen-style novelty lives and wall-clock is meaningless noise),
/// large sizes by benchmarked WALL-CLOCK (where cache/SIMD autotuning dominates).
///
/// The cursor (current size index, plateau counter, done flag) lives in one shared Turso row, so the
/// whole swarm collaborates on advancing ONE ladder rather than each node climbing its own.
/// </summary>
public static class MatmulLadder
{
    /// <summary>The hand-picked low rungs, smallest first — denser where the mult-count metric is
    /// interesting. ABOVE these the ladder keeps going by DOUBLING, without end: see <see cref="SizeAt"/>.
    /// 256×256 is not a ceiling, it is just the last rung anybody wrote down.</summary>
    public static readonly int[] BaseRungs = { 2, 3, 4, 8, 16, 32, 64, 128, 256 };

    /// <summary>
    /// The largest size this machine will attempt (env <c>HAL_MAX_SIZE</c>, default 2048). This is a
    /// RESOURCE limit, not a design one: a round at size n benchmarks n³ work, so each doubling costs
    /// ~8× the wall-clock per round (≈1.5 min/round at 2048, ≈10 min at 4096) and needs 3·n²·8 bytes of
    /// matrices. Raise it on a bigger box and the ladder simply keeps climbing. The ladder NEVER
    /// declares itself finished — at the top rung it keeps racing that size.
    /// </summary>
    public static int MaxSize
    {
        get
        {
            string? v = Environment.GetEnvironmentVariable("HAL_MAX_SIZE");
            int m = int.TryParse(v, out int mv) ? mv : 2048;
            return Math.Clamp(m, 256, 1 << 16);
        }
    }

    /// <summary>The size at a cursor index: the written-down rungs, then doubling forever (512, 1024,
    /// 2048, …), stopping at <see cref="MaxSize"/>.</summary>
    public static int SizeAt(int idx)
    {
        if (idx < BaseRungs.Length) return Math.Min(BaseRungs[idx], MaxSize);
        long s = BaseRungs[^1];
        for (int i = BaseRungs.Length; i <= idx; i++)
        {
            s *= 2;
            if (s >= MaxSize) return MaxSize;
        }
        return (int)s;
    }

    /// <summary>How many rungs exist up to (and including) <see cref="MaxSize"/> — grows if MaxSize does.</summary>
    public static int RungCount
    {
        get
        {
            int n = BaseRungs.Length;
            while (SizeAt(n - 1) < MaxSize && n < 64) n++;
            return n;
        }
    }

    /// <summary>Every rung this machine will climb, for display.</summary>
    public static int[] Rungs => Enumerable.Range(0, RungCount).Select(SizeAt).ToArray();

    /// <summary>Sizes &lt; this are scored by multiplication count; ≥ this by wall-clock time.</summary>
    public const int MsThreshold = 64;

    /// <summary>Consecutive no-improvement rounds at a size before it's declared converged.</summary>
    public const int PlateauRounds = 8;

    public static MatmulRace.Metric MetricFor(int size)
        => size >= MsThreshold ? MatmulRace.Metric.Time : MatmulRace.Metric.Muls;

    /// <summary>The outcome of one ladder step (for the swarm loop to report + react to).
    /// <paramref name="Worked"/> distinguishes "raced and found nothing better" from "could not race
    /// at all" — only the former is evidence of convergence. <paramref name="AtCeiling"/> means the
    /// cursor is on the biggest size this machine allows and is staying there — still racing, never
    /// "done"; raise <see cref="MaxSize"/> to let it climb further.</summary>
    public sealed record LadderStep(
        int Size, MatmulRace.Metric Metric, MatmulRace.RoundResult? Round,
        bool Improved, bool Advanced, bool Done, int Stale, int NextSize,
        bool Worked = true, bool AtCeiling = false);

    /// <summary>
    /// Run ONE step: read the shared cursor, race the current size once, update the plateau counter,
    /// and advance to the next size (or mark the ladder done) if the size has converged. Returns a
    /// done step immediately — with no LLM/race work — if the ladder is already complete.
    /// </summary>
    public static async Task<LadderStep?> StepAsync(
        AnthropicClient? client, AgentCore core, int myPort, CancellationToken ct = default,
        Action<string>? log = null)
    {
        var (idx, stale, done) = await core.GetLadderAsync();
        // A ladder left `done` by an older build is REOPENED rather than obeyed. There is no such thing
        // as a finished ladder any more: above the written-down rungs it keeps doubling, and the free
        // engines always have something to try. `done` on a live row means the old build ran out of
        // rungs — so pick up at the top rung and carry on climbing from there.
        if (done)
        {
            idx = Math.Max(idx, BaseRungs.Length - 1);
            stale = 0; done = false;
            await core.SetLadderAsync(idx, stale, false);
        }

        idx = Math.Clamp(idx, 0, RungCount - 1);
        int size = SizeAt(idx);
        MatmulRace.Metric metric = MetricFor(size);

        MatmulRace.RoundOutcome outcome = await MatmulRace.RunRoundAsync(client, core, myPort, size, metric, ct: ct, log: log);
        MatmulRace.RoundResult? round = outcome.Round;
        bool improved = round?.NewRecord ?? false;

        // Plateau bookkeeping: a new record resets the counter; a round that RACED and found nothing
        // better ticks it up. A round that could not race at all (nothing to evaluate — e.g. no budget
        // and no free track available at this size) is NOT evidence of convergence and must not tick
        // it, or the ladder would "converge" its way to the top having done no work whatsoever.
        if (improved) stale = 0;
        else if (outcome.Worked) stale++;

        bool advanced = false, atCeiling = false;
        // MOVE UP when either (a) the size has genuinely plateaued, or (b) there was nothing to race
        // here at all. Case (b) is a SKIP, not a convergence: it happens when every engine declines
        // this size right now (e.g. the search target no longer fits in memory and composition can't
        // beat the champion). Sitting on it would freeze the ladder, and counting it as convergence
        // would be a lie — so we step past it. The small rungs are not abandoned: they keep being
        // raced by the side rounds and by the mesh's free peer rounds, which is where a better base
        // scheme comes from, and a better base lifts every larger size through composition.
        if (stale >= PlateauRounds || !outcome.Worked)
        {
            if (idx + 1 < RungCount) { idx++; stale = 0; advanced = true; }
            else
            {
                // TOP RUNG for this machine. We do NOT stop and we do NOT declare the ladder finished —
                // we keep racing this size (autotuning always has another point to measure). Raising
                // HAL_MAX_SIZE adds rungs and the climb resumes.
                stale = 0; atCeiling = true;
            }
        }
        await core.SetLadderAsync(idx, stale, false);

        int nextSize = SizeAt(Math.Clamp(idx, 0, RungCount - 1));
        return new LadderStep(size, metric, round, improved, advanced, false, stale, nextSize,
                              Worked: outcome.Worked, AtCeiling: atCeiling);
    }
}
