namespace HAL9001;

/// <summary>
/// Lightweight live-activity log: SwarmAgent appends here; Dashboard reads via /api/live.
/// File-based so the two separate systemd services can share it without Turso round-trips.
/// </summary>
static class LiveLog
{
    private const string Path = "/tmp/hal-live.log";
    private const int MaxLines = 400;
    private const int TrimTo = 300;
    private static readonly object _lk = new();
    private static int _since;

    public static void Append(string msg)
    {
        string line = $"[{DateTime.UtcNow:HH:mm:ss}] {msg}";
        try
        {
            lock (_lk)
            {
                File.AppendAllText(Path, line + "\n");
                if (++_since >= 50) { _since = 0; Trim(); }
            }
        }
        catch { }
    }

    public static string[] Tail(int n = 80)
    {
        try { return File.ReadAllLines(Path).TakeLast(n).ToArray(); }
        catch { return []; }
    }

    private static void Trim()
    {
        try
        {
            string[] all = File.ReadAllLines(Path);
            if (all.Length > MaxLines) File.WriteAllLines(Path, all[^TrimTo..]);
        }
        catch { }
    }
}
