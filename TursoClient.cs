using System.Text;
using System.Text.Json;

namespace HAL9001;

/// <summary>
/// A thin client for the hive's shared knowledge store (Turso / libSQL), spoken over Turso's
/// HTTP API (the <c>/v2/pipeline</c> endpoint). We talk raw HTTP with <see cref="HttpClient"/>
/// for the same reasons <see cref="AnthropicClient"/> does: no native bindings to fight on
/// Windows/.NET, and the credentials stay outside the code — read from the environment
/// (TURSO_DATABASE_URL + TURSO_AUTH_TOKEN), never hardcoded, never committed.
///
/// Every node points at the SAME Turso database, so a row written by one node is visible to all —
/// that shared, persistent table is what makes the hive's knowledge (facts) a hive property rather
/// than per-node memory.
/// </summary>
public sealed class TursoClient
{
    private readonly HttpClient _http;
    private readonly string _pipelineUrl;

    private TursoClient(string pipelineUrl, string token)
    {
        _pipelineUrl = pipelineUrl;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new("Bearer", token);
    }

    /// <summary>Build a client from the env credentials, or null if they're not set (no hive).</summary>
    public static TursoClient? FromEnvironment()
    {
        string? url = Environment.GetEnvironmentVariable("TURSO_DATABASE_URL");
        string? token = Environment.GetEnvironmentVariable("TURSO_AUTH_TOKEN");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token)) return null;

        // Turso DB URLs are libsql://<db>-<org>.turso.io ; the HTTP API is the same host over https.
        string https = url.Trim();
        if (https.StartsWith("libsql://")) https = "https://" + https["libsql://".Length..];
        else if (https.StartsWith("wss://")) https = "https://" + https["wss://".Length..];
        else if (https.StartsWith("ws://")) https = "https://" + https["ws://".Length..];
        https = https.TrimEnd('/') + "/v2/pipeline";
        return new TursoClient(https, token.Trim());
    }

    /// <summary>
    /// Run one SQL statement (with optional text args bound to "?" placeholders) and return the
    /// result rows — each a list of nullable string cells. Non-query statements return no rows.
    /// Throws on an HTTP or SQL error so callers can fail loudly rather than silently lose data.
    /// </summary>
    public async Task<List<List<string?>>> ExecuteAsync(string sql, params string?[] args)
    {
        // All args bound as text — SQLite is dynamically typed, so text into TEXT columns is exact.
        object[] argArr = args.Select(a => a is null
            ? (object)new { type = "null" }
            : new { type = "text", value = a }).ToArray();

        var body = new
        {
            requests = new object[]
            {
                new { type = "execute", stmt = new { sql, args = argArr } },
                new { type = "close" },
            }
        };

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using HttpResponseMessage resp = await _http.PostAsync(_pipelineUrl, content);
        string respText = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Turso HTTP {(int)resp.StatusCode}: {Trunc(respText)}");

        using JsonDocument doc = JsonDocument.Parse(respText);
        JsonElement results = doc.RootElement.GetProperty("results");
        if (results.GetArrayLength() == 0) return new List<List<string?>>();

        JsonElement first = results[0];
        if (first.GetProperty("type").GetString() == "error")
            throw new InvalidOperationException($"Turso SQL error: {first.GetProperty("error").GetProperty("message").GetString()}");

        var rows = new List<List<string?>>();
        if (first.GetProperty("response").GetProperty("result").TryGetProperty("rows", out JsonElement rowsEl))
            foreach (JsonElement row in rowsEl.EnumerateArray())
            {
                var cells = new List<string?>();
                foreach (JsonElement cell in row.EnumerateArray())
                {
                    if (cell.GetProperty("type").GetString() == "null" || !cell.TryGetProperty("value", out var v))
                    { cells.Add(null); continue; }
                    // libSQL encodes integers (and text) as JSON STRINGS but FLOATs as JSON NUMBERS.
                    // GetString() throws on a number, so read non-string cells via their raw token.
                    cells.Add(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText());
                }
                rows.Add(cells);
            }
        return rows;
    }

    private static string Trunc(string s) => s.Length <= 300 ? s : s[..300] + "...";
}
