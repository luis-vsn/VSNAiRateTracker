using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VSNAiRateTracker.Models;
using VSNAiRateTracker.Services;
using Xunit;

namespace VSNAiRateTracker.Tests;

public class AnthropicAuthServiceTests
{
    [Fact]
    public async Task StartInteractiveLoginAsync_CompletesPkceExchange_ReturnsTokens()
    {
        // Arrange
        string capturedAuthUrl = string.Empty;
        var handler = new TestHttpMessageHandler(async (req, ct) =>
        {
            var body = await req.Content!.ReadAsStringAsync(ct);
            body.Should().Contain("grant_type");
            body.Should().Contain("code_verifier");

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\": \"at-12345\", \"refresh_token\": \"rt-67890\", \"expires_in\": 7200, \"token_type\": \"Bearer\"}")
            };
            return response;
        });

        var client = new HttpClient(handler);

        var sut = new AnthropicAuthService(
            client,
            clientId: "test-client",
            browserLauncher: url =>
            {
                capturedAuthUrl = url;
                return Task.CompletedTask;
            },
            codeReceiver: (port, state, ct) => Task.FromResult(("mock-auth-code", state)));

        // Act
        var result = await sut.StartInteractiveLoginAsync();

        // Assert
        capturedAuthUrl.Should().Contain("claude.ai/oauth/authorize");
        capturedAuthUrl.Should().Contain("code_challenge=");
        capturedAuthUrl.Should().Contain("scope=");
        capturedAuthUrl.Should().Contain("code=true");
        capturedAuthUrl.Should().Contain("client_id=test-client");
        result.Should().NotBeNull();
        result.AccessToken.Should().Be("at-12345");
        result.RefreshToken.Should().Be("rt-67890");
        result.TokenType.Should().Be("Bearer");
        result.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddHours(1));
    }

    [Fact]
    public async Task StartInteractiveLoginAsync_WhenStateMismatch_ThrowsInvalidOperationException()
    {
        // Arrange
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var client = new HttpClient(handler);

        var sut = new AnthropicAuthService(
            client,
            browserLauncher: _ => Task.CompletedTask,
            codeReceiver: (port, state, ct) => Task.FromResult(("mock-code", "tampered-state")));

        // Act
        Func<Task> act = async () => await sut.StartInteractiveLoginAsync();

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*state mismatch*");
    }

    [Fact]
    public async Task RefreshTokensAsync_WhenValidRefreshToken_ReturnsNewTokens()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(async (req, ct) =>
        {
            var body = await req.Content!.ReadAsStringAsync(ct);
            body.Should().Contain("grant_type");
            body.Should().Contain("refresh_token");

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\": \"at-new-999\", \"refresh_token\": \"rt-new-888\", \"expires_in\": 3600}")
            };
            return response;
        });

        var client = new HttpClient(handler);
        var sut = new AnthropicAuthService(client);

        var currentTokens = new OAuthTokens
        {
            AccessToken = "at-old",
            RefreshToken = "rt-old",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        // Act
        var result = await sut.RefreshTokensAsync(currentTokens);

        // Assert
        result.Should().NotBeNull();
        result!.AccessToken.Should().Be("at-new-999");
        result.RefreshToken.Should().Be("rt-new-888");
        result.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task RefreshTokensAsync_WhenRefreshTokenExpiredOrRevoked_ReturnsNull()
    {
        // Arrange
        var handler = new TestHttpMessageHandler((_, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\": \"invalid_grant\"}")
            });
        });

        var client = new HttpClient(handler);
        var sut = new AnthropicAuthService(client);

        var currentTokens = new OAuthTokens
        {
            AccessToken = "at-expired",
            RefreshToken = "rt-invalid",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1)
        };

        // Act
        var result = await sut.RefreshTokensAsync(currentTokens);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void OAuthTokens_IsExpired_IdentifiesBufferThreshold()
    {
        // Arrange
        var nearExpiry = new OAuthTokens
        {
            AccessToken = "token",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(3) // 3 mins remaining
        };

        var farExpiry = new OAuthTokens
        {
            AccessToken = "token",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30) // 30 mins remaining
        };

        // Act & Assert
        nearExpiry.IsExpired(TimeSpan.FromMinutes(5)).Should().BeTrue();
        farExpiry.IsExpired(TimeSpan.FromMinutes(5)).Should().BeFalse();
    }

    [Fact]
    public void OAuthTokens_IsWebSessionExpired_UsesRefreshTokenExpiresAt()
    {
        // Arrange
        var webActive = new OAuthTokens
        {
            AccessToken = "token",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-10), // Access token expired
            RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(25) // Web session valid
        };

        var webExpired = new OAuthTokens
        {
            AccessToken = "token",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        // Act & Assert
        webActive.IsExpired().Should().BeTrue();
        webActive.IsWebSessionExpired().Should().BeFalse();

        webExpired.IsExpired().Should().BeTrue();
        webExpired.IsWebSessionExpired().Should().BeTrue();
    }

    [Fact]
    public async Task RefreshTokensAsync_PreservesRefreshTokenExpiresAt_AndSendsUserAgent()
    {
        // Arrange
        string capturedUserAgent = string.Empty;
        var handler = new TestHttpMessageHandler(async (req, ct) =>
        {
            capturedUserAgent = req.Headers.UserAgent.ToString();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\": \"at-refreshed\", \"refresh_token\": \"rt-refreshed\", \"expires_in\": 7200}")
            };
            return response;
        });

        var client = new HttpClient(handler);
        var sut = new AnthropicAuthService(client);

        var webExp = DateTimeOffset.UtcNow.AddDays(28);
        var current = new OAuthTokens
        {
            AccessToken = "at-old",
            RefreshToken = "rt-old",
            RefreshTokenExpiresAt = webExp
        };

        // Act
        var result = await sut.RefreshTokensAsync(current);

        // Assert
        result.Should().NotBeNull();
        result!.AccessToken.Should().Be("at-refreshed");
        result.RefreshTokenExpiresAt.Should().Be(webExp);
        capturedUserAgent.Should().Contain("Claude-Code");
    }

    [Fact]
    public void LaunchCliLogin_WhenCustomLauncherConfigured_InvokesLauncher()
    {
        // Arrange
        bool launcherCalled = false;
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var client = new HttpClient(handler);
        var sut = new AnthropicAuthService(client, cliLauncher: () => launcherCalled = true);

        // Act
        sut.LaunchCliLogin();

        // Assert
        launcherCalled.Should().BeTrue();
    }
}
