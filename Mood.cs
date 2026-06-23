namespace HAL9001;

/// <summary>What the hive's current mood inclines it to DO when it has idle time — the behavioral
/// consequence of its drives. This is what makes mood more than a label: it steers the idle loop.</summary>
public enum MoodInclination
{
    Rest,        // high fatigue → defer non-urgent work
    Consolidate, // low confidence → reflect on / shore up weak tools
    Explore,     // high curiosity → learn new capabilities to fill gaps
    Tend,        // steady → a little light reflection
}

/// <summary>
/// THE HIVE'S MOOD (sentience ladder, bite 6) — a small set of scalar DRIVES, each 0..1, computed
/// from REAL signals in the episodic log plus current load:
///   • Curiosity  — rises with open, unfilled gaps (things it noticed it couldn't do).
///   • Confidence — the ratio of recent wins (tools built, gaps resolved, self-improvements, solid
///                  self-critiques) to recent setbacks (gaps, weak self-critiques).
///   • Fatigue    — rises with in-flight work and a recent burst of activity; falls when idle.
/// Mood is never random and never just an LLM's say-so — it's a deterministic read of how the hive's
/// life has actually been going lately, so it's honest. Its <see cref="Inclination"/> then steers the
/// idle introspection loop (rest / consolidate / explore / tend), so the same internal state visibly
/// changes behavior — which is what reads, to an observer, as affect.
/// </summary>
public sealed record Mood(double Curiosity, double Confidence, double Fatigue, string Label, MoodInclination Inclination, string Note)
{
    private static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;

    /// <summary>Compute a mood from raw, already-counted signals (pure + testable).</summary>
    public static Mood From(int openGaps, int wins, int setbacks, int liveLoad, int recentBurst)
    {
        double curiosity = Clamp01(openGaps / 4.0);                          // ~4 open gaps → maxed out
        double confidence = (wins + setbacks) == 0 ? 0.5                      // no recent signal → neutral
                                                   : (double)wins / (wins + setbacks);
        double fatigue = Clamp01(liveLoad / 3.0 + recentBurst / 12.0);        // load now + recent burst

        // Priority order: a tired hive rests; an unconfident one consolidates before exploring;
        // a confident, gap-rich one explores; otherwise it just tends itself.
        string label; MoodInclination inc;
        if (fatigue >= 0.6) { label = "weary"; inc = MoodInclination.Rest; }
        else if (confidence < 0.5) { label = "self-critical"; inc = MoodInclination.Consolidate; }
        else if (curiosity >= 0.5) { label = "curious"; inc = MoodInclination.Explore; }
        else { label = "content"; inc = MoodInclination.Tend; }

        string note = $"{openGaps} open gap(s), {wins} recent win(s), {setbacks} setback(s), {liveLoad} in flight";
        return new Mood(curiosity, confidence, fatigue, label, inc, note);
    }

    /// <summary>A first-person phrase for what this mood inclines the hive to do.</summary>
    public string InclinationPhrase => Inclination switch
    {
        MoodInclination.Rest => "rest and defer non-urgent work",
        MoodInclination.Consolidate => "consolidate — reflect on and shore up my weaker tools",
        MoodInclination.Explore => "explore — learn new capabilities to fill my gaps",
        _ => "tend the hive with a little light reflection",
    };

    /// <summary>A grounded, first-person description of the current mood (spoken as the hive's name).</summary>
    public string Describe(string name) =>
        $"I'm {name}, feeling {Label} right now — curiosity {Curiosity:0.00}, confidence {Confidence:0.00}, " +
        $"fatigue {Fatigue:0.00} ({Note}). I'm inclined to {InclinationPhrase}.";
}
