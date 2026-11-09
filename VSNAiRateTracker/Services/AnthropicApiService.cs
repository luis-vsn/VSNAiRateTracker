using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VSNAiRateTracker.Models;

namespace VSNAiRateTracker.Services;

public class AnthropicApiService : IAnthropicApiService
{
    private const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    private const string DefaultOAuthUsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string AnthropicVersion = "2023-06-01";
    private const string AnthropicBetaOAuth = "oauth-2025-04-20";

    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _oauthUsageEndpoint;
    private readonly int _maxRetries;
    private readonly TimeSpan _initialBackoff;

    public AnthropicApiService(
        HttpClient httpClient,
        string? endpoint = null,
        int maxRetries = 3,
        TimeSpan? initialBackoff = null,
        string? oauthUsageEndpoint = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = endpoint ?? DefaultEndpoint;
        _oauthUsageEndpoint = oauthUsageEndpoint ?? DefaultOAuthUsageEndpoint;
        _maxRetries = maxRetries;
        _initialBackoff = initialBackoff ?? TimeSpan.FromMilliseconds(200);
    }

    public async Task<AnthropicUsageResult> GetUsageAsync(
        string? apiKey,
        OAuthTokens? tokens = null,
        CancellationToken cancellationToken = default)
    {
        bool hasToken = tokens != null && !string.IsNullOrWhiteSpace(tokens.AccessToken);
        bool hasApiKey = !string.IsNullOrWhiteSpace(apiKey);

        if (!hasToken && !hasApiKey)
        {
            return AnthropicUsageResult.Failure("API Key or OAuth Access Token is missing or empty.");
        }

        for (int attempt = 0; attempt <= _maxRetries; attempt++)
        {
            try
            {
                using var request = CreateRequest(apiKey, tokens);
                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    if (hasToken)
                    {
                        return await ParseOAuthUsageResponseAsync(response, cancellationToken);
                    }
                    return ParseSuccessfulResponse(response);
                }

                int statusCode = (int)response.StatusCode;

                // 401 Unauthorized or 403 Forbidden indicates authentication/session expiration
                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return AnthropicUsageResult.Failure("Authentication failed: session expired or invalid credentials.", statusCode);
                }

                // 429 Too Many Requests or 5xx server errors are retriable
                bool isTransient = response.StatusCode == HttpStatusCode.TooManyRequests || (statusCode >= 500 && statusCode < 600);
                if (isTransient && attempt < _maxRetries)
                {
                    var delay = GetBackoffDelay(response, attempt);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                var errorSuffix = string.IsNullOrWhiteSpace(errorBody) ? string.Empty : $" ({errorBody})";
                return AnthropicUsageResult.Failure($"API request failed with HTTP {statusCode}: {response.ReasonPhrase}{errorSuffix}", statusCode);
            }
            catch (HttpRequestException ex)
            {
                if (attempt == _maxRetries)
                {
                    return AnthropicUsageResult.Failure($"Network error: {ex.Message}");
                }
                var delay = _initialBackoff * Math.Pow(2, attempt);
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == _maxRetries)
                {
                    return AnthropicUsageResult.Failure("Network request timed out.");
                }
                var delay = _initialBackoff * Math.Pow(2, attempt);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                return AnthropicUsageResult.Failure($"Unexpected error querying Anthropic API: {ex.Message}");
            }
        }

        return AnthropicUsageResult.Failure("Exceeded maximum retries contacting Anthropic API.");
    }

    public async Task<bool> ValidateCredentialsAsync(
        string? apiKey,
        OAuthTokens? tokens = null,
        CancellationToken cancellationToken = default)
    {
        var result = await GetUsageAsync(apiKey, tokens, cancellationToken);
        return result.IsSuccess || result.StatusCode == 200;
    }

    public Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken = default) =>
        ValidateCredentialsAsync(apiKey, null, cancellationToken);

    private HttpRequestMessage CreateRequest(string? apiKey, OAuthTokens? tokens)
    {
        if (tokens != null && !string.IsNullOrWhiteSpace(tokens.AccessToken))
        {
            var oauthRequest = new HttpRequestMessage(HttpMethod.Get, _oauthUsageEndpoint);
            oauthRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            oauthRequest.Headers.Add("anthropic-version", AnthropicVersion);
            oauthRequest.Headers.Add("anthropic-beta", AnthropicBetaOAuth);
            oauthRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return oauthRequest;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Add("x-api-key", apiKey);
        }

        request.Headers.Add("anthropic-version", AnthropicVersion);

        // Minimal lightweight ping payload for token headers
        var payload = new
        {
            model = "claude-3-5-haiku-20241022",
            max_tokens = 1,
            messages = new[]
            {
                new { role = "user", content = "ping" }
            }
        };

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<AnthropicUsageResult> ParseOAuthUsageResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        double percentConsumed = 0.0;
        DateTimeOffset cycleEnd = DateTimeOffset.UtcNow.AddHours(5);
        DateTimeOffset cycleStart = DateTimeOffset.UtcNow;

        if (root.TryGetProperty("five_hour", out var fiveHour) &&
            fiveHour.ValueKind == JsonValueKind.Object &&
            fiveHour.TryGetProperty("utilization", out var utilProp) &&
            utilProp.ValueKind == JsonValueKind.Number)
        {
            percentConsumed = utilProp.GetDouble();
            if (fiveHour.TryGetProperty("resets_at", out var resetsAtProp) &&
                resetsAtProp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(resetsAtProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedEnd))
            {
                cycleEnd = parsedEnd;
                cycleStart = cycleEnd.AddHours(-5);
            }
        }
        else if (root.TryGetProperty("seven_day", out var sevenDay) &&
                 sevenDay.ValueKind == JsonValueKind.Object &&
                 sevenDay.TryGetProperty("utilization", out var sevenUtilProp) &&
                 sevenUtilProp.ValueKind == JsonValueKind.Number)
        {
            percentConsumed = sevenUtilProp.GetDouble();
            if (sevenDay.TryGetProperty("resets_at", out var resetsAtProp) &&
                resetsAtProp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(resetsAtProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedEnd))
            {
                cycleEnd = parsedEnd;
                cycleStart = cycleEnd.AddDays(-7);
            }
        }
        else if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in limits.EnumerateArray())
            {
                if (limit.TryGetProperty("percent", out var pctProp) && pctProp.ValueKind == JsonValueKind.Number)
                {
                    percentConsumed = pctProp.GetDouble();
                    if (limit.TryGetProperty("resets_at", out var resetsAtProp) &&
                        resetsAtProp.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(resetsAtProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedEnd))
                    {
                        cycleEnd = parsedEnd;
                        cycleStart = cycleEnd.AddHours(-5);
                    }
                    break;
                }
            }
        }

        percentConsumed = Math.Clamp(percentConsumed, 0.0, 100.0);
        return AnthropicUsageResult.Success(percentConsumed, cycleStart, cycleEnd);
    }

    private static AnthropicUsageResult ParseSuccessfulResponse(HttpResponseMessage response)
    {
        long? tokensLimit = null;
        long? tokensRemaining = null;
        DateTimeOffset cycleEnd = DateTimeOffset.UtcNow.AddHours(5);
        DateTimeOffset cycleStart = DateTimeOffset.UtcNow;

        if (response.Headers.TryGetValues("anthropic-ratelimit-tokens-limit", out var limitVals))
        {
            if (long.TryParse(limitVals.FirstOrDefault(), out var parsedLimit))
            {
                tokensLimit = parsedLimit;
            }
        }

        if (response.Headers.TryGetValues("anthropic-ratelimit-tokens-remaining", out var remVals))
        {
            if (long.TryParse(remVals.FirstOrDefault(), out var parsedRem))
            {
                tokensRemaining = parsedRem;
            }
        }

        if (response.Headers.TryGetValues("anthropic-ratelimit-tokens-reset", out var resetVals))
        {
            var rawReset = resetVals.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(rawReset))
            {
                if (DateTimeOffset.TryParse(rawReset, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedReset))
                {
                    cycleEnd = parsedReset;
                }
                else if (long.TryParse(rawReset, out var secondsOffset))
                {
                    cycleEnd = DateTimeOffset.UtcNow.AddSeconds(secondsOffset);
                }
            }
        }

        double percentConsumed = 0.0;
        if (tokensLimit.HasValue && tokensLimit.Value > 0 && tokensRemaining.HasValue)
        {
            var consumed = tokensLimit.Value - tokensRemaining.Value;
            percentConsumed = Math.Clamp((consumed / (double)tokensLimit.Value) * 100.0, 0.0, 100.0);
        }

        cycleStart = cycleEnd.AddHours(-5);

        return AnthropicUsageResult.Success(
            percentConsumed,
            cycleStart,
            cycleEnd,
            tokensLimit,
            tokensRemaining);
    }

    private TimeSpan GetBackoffDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter != null)
        {
            if (response.Headers.RetryAfter.Delta.HasValue)
            {
                return response.Headers.RetryAfter.Delta.Value;
            }
            if (response.Headers.RetryAfter.Date.HasValue)
            {
                var delay = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    return delay;
                }
            }
        }

        return _initialBackoff * Math.Pow(2, attempt);
    }
}
