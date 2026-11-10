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
    private readonly Action? _cliLauncher;

    public AnthropicAuthService(
        HttpClient httpClient,
        string? clientId = null,
        string? authEndpoint = null,
        string? tokenEndpoint = null,
        string? scope = null,
        Func<string, Task>? browserLauncher = null,
        Func<int, string, CancellationToken, Task<(string code, string state)>>? codeReceiver = null,
        Action? cliLauncher = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _clientId = clientId ?? DefaultClientId;
        _authEndpoint = authEndpoint ?? DefaultAuthEndpoint;
        _tokenEndpoint = tokenEndpoint ?? DefaultTokenEndpoint;
        _scope = scope ?? DefaultScope;
        _browserLauncher = browserLauncher;
        _codeReceiver = codeReceiver;
        _cliLauncher = cliLauncher;
    }

    public async Task<OAuthTokens> StartInteractiveLoginAsync(CancellationToken cancellationToken = default)
    {
        // 1. Generate PKCE verifier (32 random bytes, base64url encoded = 43 chars)
        var codeVerifier = GenerateRandomBytesBase64Url(32);
        var codeChallenge = GenerateCodeChallenge(codeVerifier);
        var state = GenerateRandomBytesBase64Url(32);
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

        // 3. Exchange code for tokens (matching Anthropic/Claude Code payload structure)
        var tokenRequest = new
        {
            grant_type = "authorization_code",
            code = code,
            redirect_uri = redirectUri,
            client_id = _clientId,
            code_verifier = codeVerifier,
            state = state
        };

        return await PostTokenExchangeAsync(tokenRequest, cancellationToken);
    }

    public void LaunchCliLogin()
    {
        if (_cliLauncher != null)
        {
            _cliLauncher();
            return;
        }

        var claudeCmd = FindClaudeCliCommand();

        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k \"\"{claudeCmd}\" auth login\"",
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                Arguments = $"-a Terminal -n --args {claudeCmd} auth login",
                UseShellExecute = true
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "x-terminal-emulator",
                Arguments = $"-e {claudeCmd} auth login",
                UseShellExecute = true
            });
        }
    }

    private static string FindClaudeCliCommand()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var localBin = Path.Combine(userProfile, ".local", "bin", "claude.exe");
            if (File.Exists(localBin))
            {
                return localBin;
            }

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var npmCmd = Path.Combine(appData, "npm", "claude.cmd");
            if (File.Exists(npmCmd))
            {
                return npmCmd;
            }
        }

        return "claude";
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
            var jsonPayload = JsonSerializer.Serialize(refreshRequest);
            using var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint)
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };
            request.Headers.UserAgent.ParseAdd("Claude-Code/2.1.319");

            using var response = await _httpClient.SendAsync(request, cancellationToken);

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
                RefreshTokenExpiresAt = currentTokens.RefreshTokenExpiresAt,
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

            DateTimeOffset? refreshTokenExpiresAt = null;
            if (oauthSection.TryGetProperty("refreshTokenExpiresAt", out var refExpVal) && refExpVal.TryGetInt64(out var refExpMs))
            {
                refreshTokenExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(refExpMs);
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
                RefreshTokenExpiresAt = refreshTokenExpiresAt,
                TokenType = "Bearer"
            };

            // If the access token is already expired, try to refresh it silently using the refreshToken
            if (tokens.IsExpired() && !string.IsNullOrWhiteSpace(tokens.RefreshToken))
            {
                var refreshed = await RefreshTokensAsync(tokens, cancellationToken);
                if (refreshed != null)
                {
                    refreshed.RefreshTokenExpiresAt = refreshTokenExpiresAt;
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
        var jsonPayload = JsonSerializer.Serialize(requestPayload);
        using var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint)
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };
        request.Headers.UserAgent.ParseAdd("Claude-Code/2.1.319");

        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Error durante el intercambio de token con Anthropic (HTTP {(int)response.StatusCode} {response.ReasonPhrase}): {errorBody}");
        }

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
        listener.Prefixes.Add($"http://localhost:{port}/");
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

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");
    }

    private static string GenerateRandomBytesBase64Url(int byteCount = 32)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Base64UrlEncode(bytes);
    }

    private static string GenerateCodeChallenge(string codeVerifier)
    {
        var sha256 = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(sha256);
    }
}
