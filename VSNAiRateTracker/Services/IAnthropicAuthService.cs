using System.Threading;
using System.Threading.Tasks;
using VSNAiRateTracker.Models;

namespace VSNAiRateTracker.Services;

public interface IAnthropicAuthService
{
    Task<OAuthTokens> StartInteractiveLoginAsync(CancellationToken cancellationToken = default);
    Task<OAuthTokens?> RefreshTokensAsync(OAuthTokens currentTokens, CancellationToken cancellationToken = default);
    Task<OAuthTokens?> TryImportFromClaudeCliAsync(CancellationToken cancellationToken = default);
}

