using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Stripboard.Web.Services;

/// <summary>
/// Calls the Conflict Sentinel, which answers questions about the shoot from Grafana
/// (EV-29) and runs as a private Cloud Run service (EV-31).
///
/// Private matters: the sentinel spends Gemini tokens on every request, so an open
/// endpoint is a bill anyone could run up. Only this app's service account may invoke it,
/// which means each call carries an identity token minted from the Cloud Run metadata
/// server. Running locally there is no metadata server and no token, which is exactly
/// right for a local sentinel that is not exposed either.
/// </summary>
public sealed class SentinelClient
{
    private const string MetadataIdentityUrl =
        "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/identity";

    private readonly IHttpClientFactory _factory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SentinelClient> _logger;

    // Identity tokens last an hour; re-minting one per keystroke would be wasteful.
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public SentinelClient(IHttpClientFactory factory, IConfiguration configuration, ILogger<SentinelClient> logger)
    {
        _factory = factory;
        _configuration = configuration;
        _logger = logger;
    }

    public string? BaseUrl =>
        _configuration["Sentinel:BaseUrl"] ?? Environment.GetEnvironmentVariable("SENTINEL_URL");

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);

    public sealed class AskResponse
    {
        [JsonPropertyName("question")] public string Question { get; set; } = string.Empty;
        [JsonPropertyName("answer")] public string Answer { get; set; } = string.Empty;
        [JsonPropertyName("rounds")] public int Rounds { get; set; }
        [JsonPropertyName("total_tokens")] public int TotalTokens { get; set; }
        [JsonPropertyName("tool_calls")] public List<ToolCall> ToolCalls { get; set; } = new();
    }

    public sealed class ToolCall
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("arguments")] public Dictionary<string, object>? Arguments { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    private sealed class ErrorResponse
    {
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    public sealed class SentinelUnavailableException(string message) : Exception(message);

    public async Task<AskResponse> AskAsync(string question, CancellationToken ct = default)
    {
        var baseUrl = BaseUrl
            ?? throw new SentinelUnavailableException(
                "The Conflict Sentinel is not configured for this deployment, so there is nothing to ask.");

        var http = _factory.CreateClient();
        http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(120);

        var token = await GetIdentityTokenAsync(baseUrl, ct);
        if (token is not null)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await http.PostAsJsonAsync("api/ask", new { question }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var problem = await SafeReadErrorAsync(response, ct);
            throw new SentinelUnavailableException(problem
                ?? $"The sentinel answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        return await response.Content.ReadFromJsonAsync<AskResponse>(cancellationToken: ct)
               ?? throw new SentinelUnavailableException("The sentinel returned an empty response.");
    }

    private static async Task<string?> SafeReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return (await response.Content.ReadFromJsonAsync<ErrorResponse>(cancellationToken: ct))?.Error;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Mints an identity token for the sentinel's URL. Returns null off Cloud Run, where
    /// there is no metadata server — a local sentinel is not access-controlled either.
    /// </summary>
    private async Task<string?> GetIdentityTokenAsync(string audience, CancellationToken ct)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiry)
        {
            return _cachedToken;
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiry)
            {
                return _cachedToken;
            }

            var http = _factory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(5);

            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{MetadataIdentityUrl}?audience={Uri.EscapeDataString(audience)}");
            request.Headers.Add("Metadata-Flavor", "Google");

            var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("No identity token available ({Status}); calling the sentinel unauthenticated.",
                    response.StatusCode);
                return null;
            }

            _cachedToken = (await response.Content.ReadAsStringAsync(ct)).Trim();
            _tokenExpiry = DateTimeOffset.UtcNow.AddMinutes(45);
            return _cachedToken;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Not on Cloud Run. Expected during local development.
            _logger.LogDebug("Metadata server unreachable; calling the sentinel unauthenticated.");
            return null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}
