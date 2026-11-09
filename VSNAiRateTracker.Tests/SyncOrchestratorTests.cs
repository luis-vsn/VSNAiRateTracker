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

    public SyncOrchestratorTests()
    {
        _storageMock = new Mock<ISecureStorageService>();
        _apiMock = new Mock<IAnthropicApiService>();
        _calculatorMock = new Mock<IBurnDownCalculator>();
        _trayMock = new Mock<ITrayIconManager>();
    }

    [Fact]
    public async Task SyncOnceAsync_WhenApiKeyIsEmpty_SetsQuestionMarkStatus()
    {
        // Arrange
        _storageMock.Setup(s => s.LoadConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppConfig { ApiKey = "" });

        var sut = new SyncOrchestrator(
            _storageMock.Object,
            _apiMock.Object,
            _calculatorMock.Object,
            _trayMock.Object);

        // Act
        await sut.SyncOnceAsync();

        // Assert
        _trayMock.Verify(t => t.SetStatus("?", It.Is<string>(s => s.Contains("API Key")), 0.0), Times.Once);
        _apiMock.Verify(a => a.GetUsageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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

        _apiMock.Setup(a => a.GetUsageAsync("sk-ant-valid", It.IsAny<CancellationToken>()))
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

        var sut = new SyncOrchestrator(
            _storageMock.Object,
            _apiMock.Object,
            _calculatorMock.Object,
            _trayMock.Object);

        // Act
        await sut.SyncOnceAsync();

        // Assert
        _trayMock.Verify(t => t.UpdateState(calculatedState), Times.Once);
    }

    [Fact]
    public async Task SyncOnceAsync_WhenApiFails_SetsErrorStatusOnTray()
    {
        // Arrange
        _storageMock.Setup(s => s.LoadConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppConfig { ApiKey = "sk-ant-valid" });

        _apiMock.Setup(a => a.GetUsageAsync("sk-ant-valid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AnthropicUsageResult.Failure("Quota exhausted", 429));

        var sut = new SyncOrchestrator(
            _storageMock.Object,
            _apiMock.Object,
            _calculatorMock.Object,
            _trayMock.Object);

        // Act
        await sut.SyncOnceAsync();

        // Assert
        _trayMock.Verify(t => t.SetStatus("ERR", It.Is<string>(s => s.Contains("Quota exhausted")), -3.0), Times.Once);
        _calculatorMock.Verify(c => c.Calculate(It.IsAny<WorkSchedule>(), It.IsAny<double>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    [Fact]
    public void OpenSettings_InvokesCallback()
    {
        // Arrange
        bool opened = false;
        var sut = new SyncOrchestrator(
            _storageMock.Object,
            _apiMock.Object,
            _calculatorMock.Object,
            _trayMock.Object,
            openSettingsAction: () => opened = true);

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
        var sut = new SyncOrchestrator(
            _storageMock.Object,
            _apiMock.Object,
            _calculatorMock.Object,
            _trayMock.Object,
            shutdownAction: () => shutdownCalled = true);

        // Act
        sut.Shutdown();

        // Assert
        shutdownCalled.Should().BeTrue();
        _trayMock.Verify(t => t.Dispose(), Times.Once);
    }
}
