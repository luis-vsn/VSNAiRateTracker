using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VSNAiRateTracker.Models;

namespace VSNAiRateTracker.Services;

public class AnthropicAuthService : IAnthropicAuthService
{
    private const string DefaultClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string DefaultAuthEndpoint = "https://claude.ai/oauth/authorize";
    private const string DefaultTokenEndpoint = "https://platform.claude.com/v1/oauth/token";
    private const string DefaultScope = "user:file_upload user:inference user:mcp_servers user:plugins user:profile user:sessions:claude_code";

    private readonly HttpClient _httpClient;
    private readonly string _clientId;
    private readonly string _authEndpoint;
    private readonly string _tokenEndpoint;
    private readonly string _scope;

    // Pluggable delegates for testability
    private readonly Func<string, Task>? _browserLauncher;
    private readonly Func<int, string, CancellationToken, Task<(string code, string state)>>? _codeReceiver;

    public AnthropicAuthService(
        HttpClient httpClient,
        string? clientId = null,
        string? authEndpoint = null,
        string? tokenEndpoint = null,
        string? scope = null,
        Func<string, Task>? browserLauncher = null,
        Func<int, string, CancellationToken, Task<(string code, string state)>>? codeReceiver = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _clientId = clientId ?? DefaultClientId;
        _authEndpoint = authEndpoint ?? DefaultAuthEndpoint;
        _tokenEndpoint = tokenEndpoint ?? DefaultTokenEndpoint;
        _scope = scope ?? DefaultScope;
        _browserLauncher = browserLauncher;
        _codeReceiver = codeReceiver;
    }

    public async Task<OAuthTokens> StartInteractiveLoginAsync(CancellationToken cancellationToken = default)
    {
        // 1. Generate PKCE verifier and challenge
        var codeVerifier = GenerateRandomString(64);
        var codeChallenge = GenerateCodeChallenge(codeVerifier);
        var state = GenerateRandomString(32);
        int localPort = 54321;
        var redirectUri = $"http://localhost:{localPort}/callback";

        var authUrl = $"{_authEndpoint}?response_type=code&code=true&client_id={Uri.EscapeDataString(_clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope={Uri.EscapeDataString(_scope)}&code_challenge={Uri.EscapeDataString(codeChallenge)}&code_challenge_method=S256&state={Uri.EscapeDataString(state)}";

        // 2. Launch browser & receive redirect
        Task<(string code, string state)> receiveTask = _codeReceiver != null
            ? _codeReceiver(localPort, state, cancellationToken)
            : ReceiveCodeViaHttpListenerAsync(localPort, state, cancellationToken);

        if (_browserLauncher != null)
        {
            await _browserLauncher(authUrl);
        }
        else
        {
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        }

        var (code, returnedState) = await receiveTask;

        if (returnedState != state)
        {
            throw new InvalidOperationException("OAuth state mismatch: potential CSRF attack detected.");
        }

        // 3. Exchange code for tokens
        var tokenRequest = new
        {
            grant_type = "authorization_code",
            client_id = _clientId,
            code = code,
            code_verifier = codeVerifier,
            redirect_uri = redirectUri
        };

        return await PostTokenExchangeAsync(tokenRequest, cancellationToken);
    }

    public async Task<OAuthTokens?> RefreshTokensAsync(OAuthTokens currentTokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentTokens);

        if (string.IsNullOrWhiteSpace(currentTokens.RefreshToken))
        {
            return null;
        }

        var refreshRequest = new
        {
            grant_type = "refresh_token",
            client_id = _clientId,
            refresh_token = currentTokens.RefreshToken
        };

        try
        {
            var content = new StringContent(JsonSerializer.Serialize(refreshRequest), Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(_tokenEndpoint, content, cancellationToken);

            if (response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Refresh token expired or revoked
                return null;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var accessToken = root.GetProperty("access_token").GetString() ?? string.Empty;
            var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? currentTokens.RefreshToken : currentTokens.RefreshToken;
            var expiresIn = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 28800;

            return new OAuthTokens
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
                TokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() ?? "Bearer" : "Bearer"
            };
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public async Task<OAuthTokens?> TryImportFromClaudeCliAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var credentialsPath = Path.Combine(userProfile, ".claude", ".credentials.json");

            if (!File.Exists(credentialsPath))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(credentialsPath, cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("claudeAiOauth", out var oauthSection))
            {
                return null;
            }

            var accessToken = oauthSection.TryGetProperty("accessToken", out var at) ? at.GetString() ?? string.Empty : string.Empty;
            var refreshToken = oauthSection.TryGetProperty("refreshToken", out var rt) ? rt.GetString() ?? string.Empty : string.Empty;

            DateTimeOffset expiresAt = DateTimeOffset.UtcNow;
            if (oauthSection.TryGetProperty("expiresAt", out var expVal) && expVal.TryGetInt64(out var expMs))
            {
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expMs);
            }

            if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(refreshToken))
            {
                return null;
            }

            var tokens = new OAuthTokens
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt = expiresAt,
                TokenType = "Bearer"
            };

            // If the token is already expired, refresh it automatically using the refreshToken
            if (tokens.IsExpired() && !string.IsNullOrWhiteSpace(tokens.RefreshToken))
            {
                var refreshed = await RefreshTokensAsync(tokens, cancellationToken);
                if (refreshed != null)
                {
                    return refreshed;
                }
            }

            return tokens;
        }
        catch
        {
            return null;
        }
    }

    private async Task<OAuthTokens> PostTokenExchangeAsync(object requestPayload, CancellationToken cancellationToken)
    {
        var content = new StringContent(JsonSerializer.Serialize(requestPayload), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(_tokenEndpoint, content, cancellationToken);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var accessToken = root.GetProperty("access_token").GetString() ?? string.Empty;
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? string.Empty : string.Empty;
        var expiresIn = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 28800;

        return new OAuthTokens
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            TokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() ?? "Bearer" : "Bearer"
        };
    }

    private static async Task<(string code, string state)> ReceiveCodeViaHttpListenerAsync(int port, string expectedState, CancellationToken cancellationToken)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/callback/");
        listener.Start();

        using (cancellationToken.Register(() => listener.Stop()))
        {
            try
            {
                var context = await listener.GetContextAsync();
                var req = context.Request;
                var code = req.QueryString["code"] ?? string.Empty;
                var state = req.QueryString["state"] ?? string.Empty;

                var responseString = "<html><head><meta charset='utf-8'/><title>Autenticado</title></head><body style='font-family:Segoe UI,sans-serif;text-align:center;padding:50px;'><h2 style='color:#2ECC71;'>¡Autenticación con Anthropic completada!</h2><p>Ya puedes cerrar esta pestaña y volver a VSNAiRateTracker.</p><script>window.close();</script></body></html>";
                var buffer = Encoding.UTF8.GetBytes(responseString);
                context.Response.ContentLength64 = buffer.Length;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length, cancellationToken);
                context.Response.OutputStream.Close();

                return (code, state);
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    private static string GenerateRandomString(int length)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=')[..length];
    }

    private static string GenerateCodeChallenge(string codeVerifier)
    {
        var sha256 = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Convert.ToBase64String(sha256).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }
}
