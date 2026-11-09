using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VSNAiRateTracker.Models;

namespace VSNAiRateTracker.Services;

public class AnthropicApiService : IAnthropicApiService
{
    private const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly int _maxRetries;
    private readonly TimeSpan _initialBackoff;

    public AnthropicApiService(
        HttpClient httpClient,
        string? endpoint = null,
        int maxRetries = 3,
        TimeSpan? initialBackoff = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = endpoint ?? DefaultEndpoint;
        _maxRetries = maxRetries;
        _initialBackoff = initialBackoff ?? TimeSpan.FromMilliseconds(200);
    }

    public async Task<AnthropicUsageResult> GetUsageAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return AnthropicUsageResult.Failure("API Key is missing or empty.");
        }

        for (int attempt = 0; attempt <= _maxRetries; attempt++)
        {
            try
            {
                using var request = CreateRequest(apiKey);
                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return ParseSuccessfulResponse(response);
                }

                int statusCode = (int)response.StatusCode;

                // 401 Unauthorized or 403 Forbidden should never retry
                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return AnthropicUsageResult.Failure("Authentication failed: invalid or expired Anthropic API Key.", statusCode);
                }

                // 429 Too Many Requests or 5xx server errors are retriable
                bool isTransient = response.StatusCode == HttpStatusCode.TooManyRequests || (statusCode >= 500 && statusCode < 600);
                if (isTransient && attempt < _maxRetries)
                {
                    var delay = GetBackoffDelay(response, attempt);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                return AnthropicUsageResult.Failure($"API request failed with HTTP {statusCode}: {response.ReasonPhrase}", statusCode);
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

    public async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        var result = await GetUsageAsync(apiKey, cancellationToken);
        return result.IsSuccess || result.StatusCode == 200;
    }

    private HttpRequestMessage CreateRequest(string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);

        // Minimal lightweight ping payload for token headers
        var payload = new
        {
            model = "claude-3-haiku-20240307",
            max_tokens = 1,
            messages = new[]
            {
                new { role = "user", content = "ping" }
            }
        };

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return request;
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
