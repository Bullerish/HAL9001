namespace HAL9001;

/// <summary>
/// The injectable ambient clock — the seam that makes LIVE capabilities testable.
///
/// A LIVE capability (one whose answer depends on the current date/time, e.g. "days until
/// Christmas") must read "today"/"now" THROUGH this seam — <c>HAL9001.Clock.Today</c>,
/// <c>Clock.Now</c>, <c>Clock.UtcNow</c> — instead of calling <c>DateTime.*</c> directly. Then:
///   • in PRODUCTION nothing is injected, so the seam returns the REAL clock (the capability is
///     non-deterministic — it changes day to day, which is exactly why its value is never cached);
///   • under VALIDATION the harness injects a FIXED date, so the SAME capability becomes
///     deterministic and its date-math can be checked against per-date expected outputs.
///
/// The override is an <see cref="AsyncLocal{T}"/> so a per-call injected date flows into the
/// handler's <c>Task.Run</c> (which captures the execution context) without leaking across other,
/// concurrent calls. Scope is date/time ONLY this bite — no network/file/other ambient state.
/// </summary>
public static class Clock
{
    private static readonly AsyncLocal<DateTime?> _injected = new();

    /// <summary>Null in production (real clock); the validation harness sets a fixed instant.</summary>
    public static DateTime? Injected
    {
        get => _injected.Value;
        set => _injected.Value = value;
    }

    public static DateTime Now => _injected.Value ?? DateTime.Now;
    public static DateTime UtcNow => _injected.Value ?? DateTime.UtcNow;
    public static DateTime Today => (_injected.Value ?? DateTime.Now).Date;
}
