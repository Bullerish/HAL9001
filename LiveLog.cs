namespace HAL9001;

/// <summary>
/// Lightweight live-activity log: SwarmAgent appends here; Dashboard reads via /api/live.
/// File-based so the two separate systemd services can share it without Turso round-trips.
///
/// PATH MATTERS: the units run with <c>PrivateTmp=true</c>, which gives each service its OWN
/// private /tmp — so a file under /tmp would NOT be shared between hal-swarm and hal-dashboard.
/// We write next to the executable instead (<see cref="AppContext.BaseDirectory"/> = /opt/hal9001
/// for both services, owned by 'hal' and left writable by ProtectSystem=full). That path is NOT
/// namespaced, so both services see the same file. Locally it's just the build output dir.
/// </summary>
static class LiveLog
{
    private static readonly string Path =
        System.IO.Path.Combine(AppContext.BaseDirectory, "hal-live.log");
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
