using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace HAL9001;

/// <summary>
/// VOLUNTEER COMPUTE worker (bite 19): donate your machine's CPU to the hive's matrix-algorithm
/// search — without ever being able to manipulate its code.
///
/// The deal is trustless by construction. This worker asks the coordinator what rank it currently
/// wants beaten (<c>GET /api/target</c>), runs the local <see cref="TensorSearch"/> for it, and — if it
/// finds a decomposition — POSTs back ONLY THE NUMBERS (the u/v/w coefficient arrays) to
/// <c>POST /api/contribute</c>. The coordinator re-synthesizes and EXACT-verifies those numbers itself
/// before accepting; nothing this worker sends is ever trusted or run as code. So a volunteer can add
/// raw search throughput (the genuinely expensive part) but can neither inject code nor corrupt the
/// hive — at worst a submission is rejected.
/// </summary>
public static class ContributeWorker
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record Target(int Size, int TargetRank, string? Metric);
    private sealed record Result(bool Accepted, string? Message);

    public static async Task RunAsync(string coordinatorUrl, double secondsPerRound)
    {
        string url = coordinatorUrl.TrimEnd('/');
        string me = Environment.MachineName;
        Console.WriteLine($"Contributing CPU to the hive at {url}  (as \"{me}\").");
        Console.WriteLine("Your machine SEARCHES for faster matrix algorithms; the hive verifies the numbers it gets back.");
        Console.WriteLine("No code is ever exchanged — you add compute, nothing else. Press Ctrl+C to stop.\n");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        while (!cts.IsCancellationRequested)
        {
            Target? t;
            try { t = await Http.GetFromJsonAsync<Target>($"{url}/api/target", J, cts.Token); }
            catch (Exception ex) { Console.WriteLine($"[worker] can't reach coordinator: {ex.Message} — retrying in 5s."); await Sleep(5, cts); continue; }
            if (t is null || t.Size < 2) { await Sleep(5, cts); continue; }

            Console.WriteLine($"[worker] target {t.Size}x{t.Size}, rank {t.TargetRank} — searching ~{secondsPerRound:0}s...");
            TensorSearch.Decomposition? d = TensorSearch.Search(t.Size, t.TargetRank, out int err, maxSeconds: secondsPerRound);
            if (d is null) { Console.WriteLine($"[worker] no decomposition this round (best residual {err}). Trying again."); continue; }

            var payload = new
            {
                size = d.N,
                rank = d.Rank,
                u = ToJagged(d.U),
                v = ToJagged(d.V),
                w = ToJagged(d.W),
                contributor = me,
            };
            try
            {
                HttpResponseMessage resp = await Http.PostAsJsonAsync($"{url}/api/contribute", payload, J, cts.Token);
                Result? r = await resp.Content.ReadFromJsonAsync<Result>(J, cts.Token);
                Console.WriteLine($"[worker] submitted rank {d.Rank}: {(r?.Accepted == true ? "ACCEPTED ✓" : "rejected")} — {r?.Message}");
            }
            catch (Exception ex) { Console.WriteLine($"[worker] submit failed: {ex.Message}"); }
        }
        Console.WriteLine("Stopped contributing. Thanks for the cycles.");
    }

    private static async Task Sleep(int seconds, CancellationTokenSource cts)
    { try { await Task.Delay(seconds * 1000, cts.Token); } catch { } }

    private static int[][] ToJagged(int[,] a)
    {
        int rows = a.GetLength(0), cols = a.GetLength(1);
        var j = new int[rows][];
        for (int r = 0; r < rows; r++) { j[r] = new int[cols]; for (int c = 0; c < cols; c++) j[r][c] = a[r, c]; }
        return j;
    }
}
