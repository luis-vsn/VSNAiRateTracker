using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VSNAiRateTracker.Services;
using Xunit;

namespace VSNAiRateTracker.Tests;

public class TestHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public int CallCount { get; private set; }
    public HttpRequestMessage? LastRequest { get; private set; }

    public TestHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;
        return await _handler(request, cancellationToken);
    }
}

public class AnthropicApiServiceTests
{
    [Fact]
    public async Task GetUsageAsync_WhenApiKeyIsEmpty_ReturnsFailureWithoutCallingHttp()
    {
        // Arrange
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var client = new HttpClient(handler);
        var sut = new AnthropicApiService(client);

        // Act
        var result = await sut.GetUsageAsync(string.Empty);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("API Key");
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetUsageAsync_WhenApiReturns200WithRateLimitHeaders_ParsesCorrectly()
    {
        // Arrange
        var resetDate = DateTimeOffset.UtcNow.AddHours(4);
        var handler = new TestHttpMessageHandler((req, _) =>
        {
            req.Headers.Contains("x-api-key").Should().BeTrue();
            req.Headers.Contains("anthropic-version").Should().BeTrue();

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"type\": \"message\", \"usage\": {\"input_tokens\": 10, \"output_tokens\": 20}}")
            };
            response.Headers.Add("anthropic-ratelimit-tokens-limit", "400000");
            response.Headers.Add("anthropic-ratelimit-tokens-remaining", "100000");
            response.Headers.Add("anthropic-ratelimit-tokens-reset", resetDate.ToString("o"));
            return Task.FromResult(response);
        });

        var client = new HttpClient(handler);
        var sut = new AnthropicApiService(client);

        // Act
        var result = await sut.GetUsageAsync("sk-ant-test-key-12345");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.TokensLimit.Should().Be(400000);
        result.TokensRemaining.Should().Be(100000);
        // (400,000 - 100,000) / 400,000 = 75.0%
        result.PercentConsumed.Should().BeApproximately(75.0, 0.01);
        result.CycleEnd.Should().BeCloseTo(resetDate, TimeSpan.FromSeconds(1));
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetUsageAsync_WhenApiReturns401_DoesNotRetry()
    {
        // Arrange
        var handler = new TestHttpMessageHandler((_, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\": {\"type\": \"authentication_error\"}}")
            });
        });

        var client = new HttpClient(handler);
        var sut = new AnthropicApiService(client, maxRetries: 3, initialBackoff: TimeSpan.FromMilliseconds(5));

        // Act
        var result = await sut.GetUsageAsync("sk-ant-invalid-key");

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(401);
        handler.CallCount.Should().Be(1); // No retries for 401
    }

    [Fact]
    public async Task GetUsageAsync_WhenApiReturns429Then200_RetriesWithBackoffAndSucceeds()
    {
        // Arrange
        int callCount = 0;
        var handler = new TestHttpMessageHandler((_, _) =>
        {
            callCount++;
            if (callCount == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
            response.Headers.Add("anthropic-ratelimit-tokens-limit", "100000");
            response.Headers.Add("anthropic-ratelimit-tokens-remaining", "50000");
            return Task.FromResult(response);
        });

        var client = new HttpClient(handler);
        var sut = new AnthropicApiService(client, maxRetries: 3, initialBackoff: TimeSpan.FromMilliseconds(5));

        // Act
        var result = await sut.GetUsageAsync("sk-ant-valid-key");

        // Assert
        result.IsSuccess.Should().BeTrue();
        handler.CallCount.Should().Be(2);
        result.PercentConsumed.Should().BeApproximately(50.0, 0.01);
    }

    [Fact]
    public async Task GetUsageAsync_WhenApiRepeatedlyReturns500_StopsAtMaxRetriesAndFails()
    {
        // Arrange
        var handler = new TestHttpMessageHandler((_, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        });

        var client = new HttpClient(handler);
        var sut = new AnthropicApiService(client, maxRetries: 2, initialBackoff: TimeSpan.FromMilliseconds(5));

        // Act
        var result = await sut.GetUsageAsync("sk-ant-valid-key");

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(500);
        // Initial attempt + 2 retries = 3 total attempts
        handler.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task GetUsageAsync_WhenNetworkThrowsHttpRequestException_CatchesCleanly()
    {
        // Arrange
        var handler = new TestHttpMessageHandler((_, _) =>
        {
            throw new HttpRequestException("Network failure simulation");
        });

        var client = new HttpClient(handler);
        var sut = new AnthropicApiService(client, maxRetries: 1, initialBackoff: TimeSpan.FromMilliseconds(5));

        // Act
        var result = await sut.GetUsageAsync("sk-ant-valid-key");

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Network failure simulation");
    }

    [Fact]
    public async Task ValidateApiKeyAsync_WhenKeyIsValid_ReturnsTrue()
    {
        // Arrange
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var client = new HttpClient(handler);
        var sut = new AnthropicApiService(client);

        // Act
        var isValid = await sut.ValidateApiKeyAsync("sk-ant-valid-key");

        // Assert
        isValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateApiKeyAsync_WhenKeyIsInvalid_ReturnsFalse()
    {
        // Arrange
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var client = new HttpClient(handler);
        var sut = new AnthropicApiService(client);

        // Act
        var isValid = await sut.ValidateApiKeyAsync("sk-ant-invalid-key");

        // Assert
        isValid.Should().BeFalse();
    }

    [Fact]
    public async Task GetUsageAsync_WhenOAuthTokensProvided_SendsGetToOAuthUsageEndpointWithHeaders()
    {
        // Arrange
        var tokens = new VSNAiRateTracker.Models.OAuthTokens
        {
            AccessToken = "sk-ant-oat-test-token-12345",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2)
        };

        var handler = new TestHttpMessageHandler((req, _) =>
        {
            req.Method.Should().Be(HttpMethod.Get);
            req.RequestUri!.ToString().Should().Be("https://api.anthropic.com/api/oauth/usage");
            req.Headers.Authorization!.Scheme.Should().Be("Bearer");
            req.Headers.Authorization.Parameter.Should().Be("sk-ant-oat-test-token-12345");
            req.Headers.Contains("anthropic-version").Should().BeTrue();
            req.Headers.Contains("anthropic-beta").Should().BeTrue();
            req.Headers.GetValues("anthropic-beta").Should().Contain("oauth-2025-04-20");

            var json = """
            {
                "five_hour": {
                    "utilization": 34.0,
                    "resets_at": "2026-11-09T14:00:00.000Z"
                },
                "seven_day": {
                    "utilization": 25.0,
                    "resets_at": "2026-11-12T16:00:00.000Z"
                }
            }
            """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var client = new HttpClient(handler);
        var sut = new AnthropicApiService(client);

        // Act
        var result = await sut.GetUsageAsync(apiKey: null, tokens: tokens);

        // Assert
        result.IsSuccess.Should().BeTrue();
        // Seven-day (weekly) utilization must be prioritized as the primary metric
        result.PercentConsumed.Should().BeApproximately(25.0, 0.01);
        result.SevenDayUtilization.Should().Be(25.0);
        result.FiveHourUtilization.Should().Be(34.0);
        result.CycleEnd.Should().Be(DateTimeOffset.Parse("2026-11-12T16:00:00.000Z"));
        result.CycleStart.Should().Be(DateTimeOffset.Parse("2026-11-05T16:00:00.000Z"));
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetUsageAsync_WhenOAuthUsageReturnsSevenDayFallback_ParsesCorrectly()
    {
        // Arrange
        var tokens = new VSNAiRateTracker.Models.OAuthTokens
        {
            AccessToken = "sk-ant-oat-test-token"
        };

        var handler = new TestHttpMessageHandler((_, _) =>
        {
            var json = """
            {
                "five_hour": null,
                "seven_day": {
                    "utilization": 42.5,
                    "resets_at": "2026-11-12T16:00:00.000Z"
                }
            }
            """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var client = new HttpClient(handler);
        var sut = new AnthropicApiService(client);

        // Act
        var result = await sut.GetUsageAsync(apiKey: null, tokens: tokens);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.PercentConsumed.Should().BeApproximately(42.5, 0.01);
        result.CycleEnd.Should().Be(DateTimeOffset.Parse("2026-11-12T16:00:00.000Z"));
        result.CycleStart.Should().Be(DateTimeOffset.Parse("2026-11-05T16:00:00.000Z"));
    }

    [Fact]
    public async Task GetUsageAsync_WhenOAuthUsageReturnsLimitsArray_ParsesCorrectly()
    {
        // Arrange
        var tokens = new VSNAiRateTracker.Models.OAuthTokens
        {
            AccessToken = "sk-ant-oat-test-token"
        };

        var handler = new TestHttpMessageHandler((_, _) =>
        {
            var json = """
            {
                "five_hour": null,
                "seven_day": null,
                "limits": [
                    {
                        "kind": "session",
                        "percent": 68.0,
                        "resets_at": "2026-11-09T18:00:00.000Z"
                    }
                ]
            }
            """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var client = new HttpClient(handler);
        var sut = new AnthropicApiService(client);

        // Act
        var result = await sut.GetUsageAsync(apiKey: null, tokens: tokens);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.PercentConsumed.Should().BeApproximately(68.0, 0.01);
        result.CycleEnd.Should().Be(DateTimeOffset.Parse("2026-11-09T18:00:00.000Z"));
        result.CycleStart.Should().Be(DateTimeOffset.Parse("2026-11-09T13:00:00.000Z"));
    }

    [Fact]
    public async Task GetUsageAsync_WhenOAuthUsageReturns401_MarksSessionExpired()
    {
        // Arrange
        var tokens = new VSNAiRateTracker.Models.OAuthTokens
        {
            AccessToken = "sk-ant-oat-revoked-token"
        };

        var handler = new TestHttpMessageHandler((_, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\": {\"message\": \"OAuth access token has been revoked.\"}}")
            });
        });

        var client = new HttpClient(handler);
        var sut = new AnthropicApiService(client);

        // Act
        var result = await sut.GetUsageAsync(apiKey: null, tokens: tokens);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.IsSessionExpired.Should().BeTrue();
        result.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_WhenOAuthTokensValid_ReturnsTrue()
    {
        // Arrange
        var tokens = new VSNAiRateTracker.Models.OAuthTokens
        {
            AccessToken = "sk-ant-oat-valid-token"
        };

        var handler = new TestHttpMessageHandler((_, _) =>
        {
            var json = "{\"five_hour\": {\"utilization\": 10.0, \"resets_at\": \"2026-11-09T14:00:00.000Z\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var client = new HttpClient(handler);
        var sut = new AnthropicApiService(client);

        // Act
        var isValid = await sut.ValidateCredentialsAsync(null, tokens);

        // Assert
        isValid.Should().BeTrue();
    }

    [Fact]
    public async Task LiveIntegration_WithStoredAppCredentials_RetrievesRealConsumptionSuccessfully()
    {
        // Skip during standard unit test runs; run only when LIVE_TESTS=true
        if (Environment.GetEnvironmentVariable("LIVE_TESTS") != "true")
        {
            return;
        }

        // Arrange
        var storage = new SecureStorageService();
        var config = await storage.LoadConfigAsync();
        if (config.AuthTokens == null || string.IsNullOrWhiteSpace(config.AuthTokens.AccessToken))
        {
            return; // Skip if no local credentials on test runner
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        if (config.AuthTokens.IsExpired())
        {
            var auth = new AnthropicAuthService(client);
            var refreshed = await auth.TryImportFromClaudeCliAsync();
            if (refreshed != null && !refreshed.IsExpired())
            {
                config.AuthTokens = refreshed;
                await storage.SaveConfigAsync(config);
            }
            else
            {
                refreshed = await auth.RefreshTokensAsync(config.AuthTokens);
                if (refreshed != null && !refreshed.IsExpired())
                {
                    config.AuthTokens = refreshed;
                    await storage.SaveConfigAsync(config);
                }
            }
        }

        var sut = new AnthropicApiService(client);

        // Act
        var result = await sut.GetUsageAsync(config.ApiKey, config.AuthTokens);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.PercentConsumed.Should().BeInRange(0.0, 100.0);
        result.CycleEnd.Should().BeAfter(result.CycleStart);
    }

    [Fact]
    public async Task GetUsageAsync_WhenFullOAuthPayloadProvided_ParsesAllFieldsAndPrioritizesWeekly()
    {
        // Arrange
        var tokens = new VSNAiRateTracker.Models.OAuthTokens { AccessToken = "valid-token" };
        var json = """
        {
            "five_hour": {
                "utilization": 2.0,
                "resets_at": "2026-11-09T19:00:00.000Z"
            },
            "seven_day": {
                "utilization": 30.0,
                "resets_at": "2026-11-12T16:00:00.000Z"
            },
            "seven_day_opus": { "utilization": 5.0 },
            "seven_day_sonnet": { "utilization": 28.0 },
            "extra_usage": {
                "is_enabled": true,
                "utilization": 12.5,
                "used_credits": 150.0,
                "monthly_limit": 500.0,
                "currency": "EUR"
            },
            "spend": {
                "used": { "amount_minor": 1250, "currency": "EUR", "exponent": 2 },
                "percent": 25.0
            }
        }
        """;

        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        }));
        var sut = new AnthropicApiService(new HttpClient(handler));

        // Act
        var result = await sut.GetUsageAsync(apiKey: null, tokens: tokens);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.PercentConsumed.Should().Be(30.0); // 7-day primary
        result.SevenDayUtilization.Should().Be(30.0);
        result.FiveHourUtilization.Should().Be(2.0);
        result.CycleEnd.Should().Be(DateTimeOffset.Parse("2026-11-12T16:00:00.000Z"));
        result.CycleStart.Should().Be(DateTimeOffset.Parse("2026-11-05T16:00:00.000Z"));
        result.SevenDayOpusUtilization.Should().Be(5.0);
        result.SevenDaySonnetUtilization.Should().Be(28.0);
        result.ExtraUsageEnabled.Should().BeTrue();
        result.ExtraUsageUtilization.Should().Be(12.5);
        result.ExtraUsageUsedCredits.Should().Be(150.0);
        result.ExtraUsageMonthlyLimit.Should().Be(500.0);
        result.Currency.Should().Be("EUR");
        result.SpendUsedAmount.Should().Be(12.50);
        result.SpendPercent.Should().Be(25.0);

        // Every window with a utilization value is exposed generically for telemetry
        result.Limits.Select(l => l.Name).Should().BeEquivalentTo(
            new[] { "five_hour", "seven_day", "seven_day_opus", "seven_day_sonnet", "extra_usage" });
        result.Limits.Single(l => l.Name == "seven_day").UtilizationPercent.Should().Be(30.0);
        result.Limits.Single(l => l.Name == "seven_day").ResetsAt.Should().Be(DateTimeOffset.Parse("2026-11-12T16:00:00.000Z"));
        result.Limits.Single(l => l.Name == "seven_day_opus").ResetsAt.Should().BeNull();
    }

    [Fact]
    public async Task GetUsageAsync_WhenPayloadHasUnknownWindowsLimitsArrayAndSpendLimit_ExposesThemForTelemetry()
    {
        // Arrange
        var tokens = new VSNAiRateTracker.Models.OAuthTokens { AccessToken = "valid-token" };
        var json = """
        {
            "five_hour": { "utilization": 10.0, "resets_at": "2026-11-09T19:00:00.000Z" },
            "seven_day": { "utilization": 40.0, "resets_at": "2026-11-12T16:00:00.000Z" },
            "seven_day_brand_new_window": { "utilization": 7.5, "resets_at": "2026-11-13T00:00:00.000Z" },
            "seven_day_opus": null,
            "limits": [
                { "kind": "seven_day", "group": "weekly", "percent": 99.0 },
                { "kind": "weekly_design", "group": "weekly", "percent": 3.0, "resets_at": "2026-11-14T00:00:00.000Z" }
            ],
            "spend": {
                "used": { "amount_minor": 1250, "exponent": 2 },
                "limit": { "amount_minor": 5000, "exponent": 2 },
                "percent": 25.0
            }
        }
        """;

        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        }));
        var sut = new AnthropicApiService(new HttpClient(handler));

        // Act
        var result = await sut.GetUsageAsync(apiKey: null, tokens: tokens);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SpendLimitAmount.Should().Be(50.0);
        result.Limits.Select(l => l.Name).Should().BeEquivalentTo(
            new[] { "five_hour", "seven_day", "seven_day_brand_new_window", "weekly_design" });

        // Root windows win over duplicated entries of the limits array
        result.Limits.Single(l => l.Name == "seven_day").UtilizationPercent.Should().Be(40.0);
        result.Limits.Single(l => l.Name == "seven_day_brand_new_window").UtilizationPercent.Should().Be(7.5);
        result.Limits.Single(l => l.Name == "weekly_design").ResetsAt.Should().Be(DateTimeOffset.Parse("2026-11-14T00:00:00.000Z"));
    }
}

