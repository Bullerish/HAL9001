namespace HAL9001;

/// <summary>
/// METACOGNITION LOOP helpers (sentience ladder, issue #8).
///
/// Close the prediction → outcome → updated self-model loop:
///   1. Before a significant action, record an explicit <c>expectation</c>
///      (what confidence / outcome the hive predicts).
///   2. After the outcome, record a <c>calibration</c> event comparing
///      predicted vs actual, with an absolute error signal.
///
/// Both kinds live in the shared episodic <see cref="EventLog"/> so they
/// survive restarts and are visible to SelfModel / mood / idle policy.
/// Writes are best-effort (EventLog already swallows failures).
///
/// This file is the logging + query surface only. Policy bias (prefer
/// strategies the hive is calibrated on; get curious where it is not) is a
/// later rung on the same issue.
/// </summary>
public static class Metacognition
{
    private static readonly System.Globalization.CultureInfo Inv =
        System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>Record an explicit expectation before a significant action.</summary>
    public static Task ExpectAsync(EventLog events, string domain, double? predictedConfidence, string? refId = null)
    {
        string pred = predictedConfidence is double p ? p.ToString("0.00", Inv) : "unknown";
        return events.AppendAsync(
            "expectation",
            $"expected confidence {pred} for {domain}",
            refId ?? domain);
    }

    /// <summary>Record predicted vs actual after the outcome, with absolute error.</summary>
    public static Task CalibrateAsync(EventLog events, string domain, double? predicted, double actual, string? refId = null)
    {
        string pred = predicted is double p ? p.ToString("0.00", Inv) : "none";
        string act = actual.ToString("0.00", Inv);
        string errText = predicted is double pp
            ? Math.Abs(pp - actual).ToString("0.00", Inv)
            : "n/a";
        return events.AppendAsync(
            "calibration",
            $"calibrated {domain}: predicted {pred} → actual {act} (error {errText})",
            refId ?? domain);
    }

    /// <summary>
    /// Summarize recent calibration accuracy from the event log.
    /// Returns (count of calibration events in the window, mean absolute error when parseable).
    /// </summary>
    public static async Task<(int Count, double? MeanAbsError)> RecentAccuracyAsync(EventLog events, int scan = 60)
    {
        IReadOnlyList<HiveEvent> recent = await events.RecentAsync(scan);
        var errors = new List<double>();
        int count = 0;
        foreach (HiveEvent e in recent)
        {
            if (e.Kind != "calibration") continue;
            count++;
            // Summary shape: "calibrated X: predicted P → actual A (error E)"
            int idx = e.Summary.LastIndexOf("(error ", StringComparison.Ordinal);
            if (idx < 0) continue;
            int start = idx + 7;
            int end = e.Summary.IndexOf(')', start);
            if (end <= start) continue;
            string token = e.Summary[start..end].Trim();
            if (token == "n/a") continue;
            if (double.TryParse(token, System.Globalization.NumberStyles.Any, Inv, out double err))
                errors.Add(err);
        }
        if (errors.Count == 0) return (count, null);
        return (count, errors.Average());
    }

    /// <summary>One-line human summary for mood / self-model surfaces.</summary>
    public static async Task<string> DescribeRecentAsync(EventLog events, int scan = 60)
    {
        var (count, mean) = await RecentAccuracyAsync(events, scan);
        if (count == 0) return "I have not yet closed any prediction→outcome loops.";
        if (mean is null) return $"I have {count} recent calibration event(s) but no parseable error signals yet.";
        string quality = mean < 0.15 ? "well calibrated"
            : mean < 0.35 ? "moderately calibrated"
            : "poorly calibrated";
        return $"Over {count} recent self-check(s) my mean prediction error is {mean.Value.ToString("0.00", Inv)} — I am {quality}.";
    }
}
