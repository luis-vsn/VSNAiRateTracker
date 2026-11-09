using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using VSNAiRateTracker.Models;
using VSNAiRateTracker.Services;
using Xunit;

namespace VSNAiRateTracker.Tests;

public class SyncOrchestratorTests
{
    private readonly Mock<ISecureStorageService> _storageMock;
    private readonly Mock<IAnthropicApiService> _apiMock;
    private readonly Mock<IBurnDownCalculator> _calculatorMock;
    private readonly Mock<ITrayIconManager> _trayMock;
    private readonly Mock<IAnthropicAuthService> _authMock;

    public SyncOrchestratorTests()
    {
        _storageMock = new Mock<ISecureStorageService>();
        _apiMock = new Mock<IAnthropicApiService>();
        _calculatorMock = new Mock<IBurnDownCalculator>();
        _trayMock = new Mock<ITrayIconManager>();
        _authMock = new Mock<IAnthropicAuthService>();
    }

    private SyncOrchestrator CreateSut(Action? openSettings = null, Action? shutdown = null)
    {
        return new SyncOrchestrator(
            _storageMock.Object,
            _apiMock.Object,
            _calculatorMock.Object,
            _trayMock.Object,
            _authMock.Object,
            openSettingsAction: openSettings,
            shutdownAction: shutdown);
    }

    [Fact]
    public async Task SyncOnceAsync_WhenNoCredentials_SetsQuestionOrLoginStatus()
    {
        // Arrange
        _storageMock.Setup(s => s.LoadConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppConfig { ApiKey = "", AuthTokens = null });

        var sut = CreateSut();

        // Act
        await sut.SyncOnceAsync();

        // Assert
        _trayMock.Verify(t => t.SetStatus("LOG", It.Is<string>(s => s.Contains("Inicia sesión")), 0.0), Times.Once);
        _apiMock.Verify(a => a.GetUsageAsync(It.IsAny<string?>(), It.IsAny<OAuthTokens?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncOnceAsync_WhenOAuthTokenNearingExpiration_RefreshesTokenAutomatically()
    {
        // Arrange
        var expiredTokens = new OAuthTokens
        {
            AccessToken = "at-old",
            RefreshToken = "rt-valid",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2) // Expires in 2 min (within 5 min buffer)
        };

        var newTokens = new OAuthTokens
        {
            AccessToken = "at-new",
            RefreshToken = "rt-valid",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2)
        };

        var schedule = WorkSchedule.CreateDefault();
        var config = new AppConfig { AuthTokens = expiredTokens, Schedule = schedule };

        _storageMock.Setup(s => s.LoadConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        _authMock.Setup(a => a.RefreshTokensAsync(expiredTokens, It.IsAny<CancellationToken>()))
            .ReturnsAsync(newTokens);

        var cycleStart = DateTimeOffset.UtcNow.AddHours(-2);
        var cycleEnd = DateTimeOffset.UtcNow.AddHours(3);
        _apiMock.Setup(a => a.GetUsageAsync(config.ApiKey, newTokens, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AnthropicUsageResult.Success(30.0, cycleStart, cycleEnd));

        var calculatedState = new UsageState { DeltaHours = 2.0, Status = RateStatus.Green };
        _calculatorMock.Setup(c => c.Calculate(schedule, 30.0, cycleStart, cycleEnd, It.IsAny<DateTimeOffset>()))
            .Returns(calculatedState);

        var sut = CreateSut();

        // Act
        await sut.SyncOnceAsync();

        // Assert
        _authMock.Verify(a => a.RefreshTokensAsync(expiredTokens, It.IsAny<CancellationToken>()), Times.Once);
        _storageMock.Verify(s => s.SaveConfigAsync(It.Is<AppConfig>(c => c.AuthTokens == newTokens), It.IsAny<CancellationToken>()), Times.Once);
        _trayMock.Verify(t => t.UpdateState(calculatedState), Times.Once);
    }

    [Fact]
    public async Task SyncOnceAsync_WhenOAuthTokenExpiredAndRefreshFails_SetsSessionExpiredStatus()
    {
        // Arrange
        var expiredTokens = new OAuthTokens
        {
            AccessToken = "at-old",
            RefreshToken = "rt-expired",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-10) // Already expired
        };

        var config = new AppConfig { AuthTokens = expiredTokens };
        _storageMock.Setup(s => s.LoadConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        _authMock.Setup(a => a.RefreshTokensAsync(expiredTokens, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OAuthTokens?)null); // Refresh failed

        var sut = CreateSut();

        // Act
        await sut.SyncOnceAsync();

        // Assert
        _trayMock.Verify(t => t.SetStatus("LOG", It.Is<string>(s => s.Contains("expirada")), -2.0), Times.Once);
        _apiMock.Verify(a => a.GetUsageAsync(It.IsAny<string?>(), It.IsAny<OAuthTokens?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncOnceAsync_WhenApiReturns401_NotifiesSessionExpired()
    {
        // Arrange
        var tokens = new OAuthTokens
        {
            AccessToken = "at-valid-looking",
            RefreshToken = "rt-revoked",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        var config = new AppConfig { AuthTokens = tokens };
        _storageMock.Setup(s => s.LoadConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        // API says 401 Unauthorized
        _apiMock.Setup(a => a.GetUsageAsync(config.ApiKey, tokens, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AnthropicUsageResult.Failure("Session revoked", 401));

        // Refresh also fails
        _authMock.Setup(a => a.RefreshTokensAsync(tokens, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OAuthTokens?)null);

        var sut = CreateSut();

        // Act
        await sut.SyncOnceAsync();

        // Assert
        _trayMock.Verify(t => t.SetStatus("LOG", It.Is<string>(s => s.Contains("expirada")), -2.0), Times.Once);
    }

    [Fact]
    public async Task SyncOnceAsync_WhenApiSucceeds_CalculatesAndUpdatesTrayState()
    {
        // Arrange
        var schedule = WorkSchedule.CreateDefault();
        _storageMock.Setup(s => s.LoadConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppConfig { ApiKey = "sk-ant-valid", Schedule = schedule });

        var cycleStart = DateTimeOffset.UtcNow.AddHours(-2);
        var cycleEnd = DateTimeOffset.UtcNow.AddHours(3);
        var apiResult = AnthropicUsageResult.Success(40.0, cycleStart, cycleEnd);

        _apiMock.Setup(a => a.GetUsageAsync("sk-ant-valid", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(apiResult);

        var calculatedState = new UsageState
        {
            PercentConsumed = 40.0,
            DeltaHours = 3.5,
            Status = RateStatus.Green
        };

        _calculatorMock.Setup(c => c.Calculate(
            schedule,
            40.0,
            cycleStart,
            cycleEnd,
            It.IsAny<DateTimeOffset>()))
            .Returns(calculatedState);

        var sut = CreateSut();

        // Act
        await sut.SyncOnceAsync();

        // Assert
        _trayMock.Verify(t => t.UpdateState(calculatedState), Times.Once);
    }

    [Fact]
    public void OpenSettings_InvokesCallback()
    {
        // Arrange
        bool opened = false;
        var sut = CreateSut(openSettings: () => opened = true);

        // Act
        sut.OpenSettings();

        // Assert
        opened.Should().BeTrue();
    }

    [Fact]
    public void Shutdown_InvokesShutdownActionAndDisposesTray()
    {
        // Arrange
        bool shutdownCalled = false;
        var sut = CreateSut(shutdown: () => shutdownCalled = true);

        // Act
        sut.Shutdown();

        // Assert
        shutdownCalled.Should().BeTrue();
        _trayMock.Verify(t => t.Dispose(), Times.Once);
    }
}
