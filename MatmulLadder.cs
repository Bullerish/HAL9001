namespace HAL9001;

/// <summary>
/// The Prime Directive size ladder (bite 15). The hive climbs a sequence of matrix sizes from 2×2
/// up to 256×256. For each size it races round after round; when it can no longer improve — a
/// PLATEAU of <see cref="PlateauRounds"/> consecutive rounds with no new record — it declares that
/// size converged and climbs to the next. When the top size plateaus, the ladder is done.
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
    /// <summary>The rungs, smallest first. Denser low (where the mult-count metric is interesting).</summary>
    public static readonly int[] Sizes = { 2, 3, 4, 8, 16, 32, 64, 128, 256 };

    /// <summary>Sizes &lt; this are scored by multiplication count; ≥ this by wall-clock time.</summary>
    public const int MsThreshold = 64;

    /// <summary>Consecutive no-improvement rounds at a size before it's declared converged.</summary>
    public const int PlateauRounds = 8;

    public static MatmulRace.Metric MetricFor(int size)
        => size >= MsThreshold ? MatmulRace.Metric.Time : MatmulRace.Metric.Muls;

    /// <summary>The outcome of one ladder step (for the swarm loop to report + react to).</summary>
    public sealed record LadderStep(
        int Size, MatmulRace.Metric Metric, MatmulRace.RoundResult? Round,
        bool Improved, bool Advanced, bool Done, int Stale, int NextSize);

    /// <summary>
    /// Run ONE step: read the shared cursor, race the current size once, update the plateau counter,
    /// and advance to the next size (or mark the ladder done) if the size has converged. Returns a
    /// done step immediately — with no LLM/race work — if the ladder is already complete.
    /// </summary>
    public static async Task<LadderStep?> StepAsync(
        AnthropicClient client, AgentCore core, int myPort, CancellationToken ct = default)
    {
        var (idx, stale, done) = await core.GetLadderAsync();
        if (done)
            return new LadderStep(Sizes[^1], MetricFor(Sizes[^1]), null, false, false, true, stale, Sizes[^1]);

        idx = Math.Clamp(idx, 0, Sizes.Length - 1);
        int size = Sizes[idx];
        MatmulRace.Metric metric = MetricFor(size);

        MatmulRace.RoundResult? round = await MatmulRace.RunRoundAsync(client, core, myPort, size, metric, ct: ct);
        bool improved = round?.NewRecord ?? false;

        // Plateau bookkeeping: a new record resets the counter; otherwise it ticks up.
        stale = improved ? 0 : stale + 1;

        bool advanced = false, nowDone = false;
        if (stale >= PlateauRounds)
        {
            if (idx + 1 < Sizes.Length) { idx++; stale = 0; advanced = true; }
            else { nowDone = true; }
        }
        await core.SetLadderAsync(idx, stale, nowDone);

        int nextSize = Sizes[Math.Clamp(idx, 0, Sizes.Length - 1)];
        return new LadderStep(size, metric, round, improved, advanced, nowDone, stale, nextSize);
    }
}
