using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace HAL9001;

/// <summary>
/// A tiny client for the Anthropic Messages API (https://api.anthropic.com/v1/messages),
/// written as raw HTTP + JSON so every byte on the wire is visible. (An official C# SDK,
/// the "Anthropic" NuGet package, exists too — this hand-rolled version keeps the project
/// dependency-light and easy to follow.)
///
/// The API key is read from the ANTHROPIC_API_KEY environment variable — never hardcoded
/// and never committed.
/// </summary>
public sealed class AnthropicClient : IDisposable
{
    // ⇩ Change this ONE line to use a different model. (claude-opus-4-8 is the current
    //   most-capable model; claude-sonnet-4-6 / claude-haiku-4-5 are cheaper/faster.)
    public const string Model = "claude-haiku-4-5-20251001";

    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01"; // required API version header
    private const int MaxTokens = 4096;                    // plenty for one small handler class

    private readonly HttpClient _http = new();
    private readonly string _apiKey;

    private AnthropicClient(string apiKey) => _apiKey = apiKey;

    /// <summary>
    /// Build a client from ANTHROPIC_API_KEY. Returns null (not an exception) if the key
    /// isn't set, so the caller can print friendly setup instructions.
    /// </summary>
    public static AnthropicClient? FromEnvironment()
    {
        string? key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        return string.IsNullOrWhiteSpace(key) ? null : new AnthropicClient(key);
    }

    /// <summary>
    /// Send one user message (with a system prompt) and return Claude's text response.
    /// </summary>
    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        // The request body mirrors the Messages API shape exactly:
        //   { "model", "max_tokens", "system", "messages": [ { "role", "content" } ] }
        var requestBody = new
        {
            model = Model,
            max_tokens = MaxTokens,
            system = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = userPrompt },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Add("x-api-key", _apiKey);             // authentication
        request.Headers.Add("anthropic-version", AnthropicVersion);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        string responseJson = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // Surface the API's own error JSON (e.g. 401 bad key, 429 rate limit).
            throw new InvalidOperationException(
                $"Anthropic API returned {(int)response.StatusCode} {response.StatusCode}: {responseJson}");
        }

        return ExtractText(responseJson);
    }

    /// <summary>
    /// Pull the text out of a Messages API response. The shape is:
    ///   { "content": [ { "type": "text", "text": "..." }, ... ], "stop_reason": "..." }
    /// We concatenate every "text" block and ignore any other block types.
    /// </summary>
    private static string ExtractText(string responseJson)
    {
        using JsonDocument doc = JsonDocument.Parse(responseJson);
        JsonElement root = doc.RootElement;

        // A safety refusal comes back as HTTP 200 with stop_reason "refusal" and no usable
        // text — check it before trying to read content, or we'd return an empty string.
        if (root.TryGetProperty("stop_reason", out JsonElement stop) &&
            stop.GetString() == "refusal")
        {
            throw new InvalidOperationException("The model refused this request (stop_reason = refusal).");
        }

        var text = new StringBuilder();
        if (root.TryGetProperty("content", out JsonElement content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out JsonElement type) &&
                    type.GetString() == "text" &&
                    block.TryGetProperty("text", out JsonElement value))
                {
                    text.Append(value.GetString());
                }
            }
        }

        return text.ToString();
    }

    public void Dispose() => _http.Dispose();
}
