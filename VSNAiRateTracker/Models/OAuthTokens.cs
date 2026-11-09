using System;

namespace VSNAiRateTracker.Models;

public class OAuthTokens
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public string TokenType { get; set; } = "Bearer";

    public bool IsExpired(TimeSpan? buffer = null)
    {
        var bufferSpan = buffer ?? TimeSpan.FromMinutes(5);

        if (ExpiresAt == default || ExpiresAt.UtcDateTime <= DateTime.MinValue + bufferSpan)
        {
            return true;
        }

        var threshold = ExpiresAt - bufferSpan;
        return DateTimeOffset.UtcNow >= threshold;
    }

    public bool HasValidRefreshToken => !string.IsNullOrWhiteSpace(RefreshToken);
}
