using System.Threading;
using System.Threading.Tasks;
using VSNAiRateTracker.Models;

namespace VSNAiRateTracker.Services;

public interface IAnthropicApiService
{
    Task<AnthropicUsageResult> GetUsageAsync(string? apiKey, OAuthTokens? tokens = null, CancellationToken cancellationToken = default);
    Task<bool> ValidateCredentialsAsync(string? apiKey, OAuthTokens? tokens = null, CancellationToken cancellationToken = default);

    // Convenience overloads
    Task<AnthropicUsageResult> GetUsageAsync(string apiKey, CancellationToken cancellationToken = default) =>
        GetUsageAsync(apiKey, null, cancellationToken);

    Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken = default) =>
        ValidateCredentialsAsync(apiKey, null, cancellationToken);
}
