using System.Diagnostics;

namespace HAL9001;

/// <summary>
/// The single most-recent U/V/W decomposition the tensor-search is actively working — published by
/// the swarm as it hunts, read by the dashboard for the "matrices being worked" panel. Unlike the
/// append-only <see cref="LiveLog"/>, this is a SNAPSHOT: each publish overwrites the last, so the
/// panel always shows the current grids (not a scrolling history).
///
/// Same shared, non-namespaced path discipline as LiveLog (next to the executable) — the units run
/// with PrivateTmp=true, so /tmp would NOT be visible across the swarm and dashboard services.
/// </summary>
static class LiveMatrix
{
    private static readonly string Path =
        System.IO.Path.Combine(AppContext.BaseDirectory, "hal-matrix.json");
    private static readonly Stopwatch _sw = Stopwatch.StartNew();
    private static readonly object _lk = new();
    private static long _lastMs = -10000;

    /// <summary>Publish the current best scheme (compact {n,rank,u,v,w} JSON) and its residual error.
    /// Throttled to ~4 writes/sec — the search calls this far more often than the panel needs.</summary>
    public static void Publish(string schemeJson, int error)
    {
        if (string.IsNullOrEmpty(schemeJson)) return;
        lock (_lk)
        {
            long ms = _sw.ElapsedMilliseconds;
            if (ms - _lastMs < 250) return;
            _lastMs = ms;
            try
            {
                string wrapped = $"{{\"ts\":\"{DateTime.UtcNow:o}\",\"error\":{error},\"scheme\":{schemeJson}}}";
                File.WriteAllText(Path, wrapped);
            }
            catch { }
        }
    }

    /// <summary>Publish a compact "currently working" status for sizes too large to render as U/V/W grids
    /// (n &gt; 4). Keeps the panel FRESH and following the hive up the ladder instead of frozen on the last
    /// small scheme. Carries no grids — just n, the current best (muls), and a human note.</summary>
    public static void PublishStatus(int n, int rank, int error, string note)
    {
        string safe = (note ?? "").Replace("\\", " ").Replace("\"", "'").Replace("\n", " ").Replace("\r", " ");
        Publish($"{{\"n\":{n},\"rank\":{rank},\"note\":\"{safe}\"}}", error);
    }

    /// <summary>The latest published snapshot wrapper, or "" if none. The dashboard parses ts/error/scheme.</summary>
    public static string Read()
    {
        try { return File.ReadAllText(Path); }
        catch { return ""; }
    }
}
