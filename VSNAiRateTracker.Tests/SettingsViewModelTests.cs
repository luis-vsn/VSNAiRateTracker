using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using VSNAiRateTracker.Models;
using VSNAiRateTracker.Services;
using VSNAiRateTracker.ViewModels;
using Xunit;

namespace VSNAiRateTracker.Tests;

public class SettingsViewModelTests
{
    private readonly Mock<ISecureStorageService> _storageMock;
    private readonly Mock<IAnthropicApiService> _apiMock;
    private readonly Mock<IAnthropicAuthService> _authMock;
    private readonly SettingsViewModel _sut;

    public SettingsViewModelTests()
    {
        _storageMock = new Mock<ISecureStorageService>();
        _apiMock = new Mock<IAnthropicApiService>();
        _authMock = new Mock<IAnthropicAuthService>();
        _sut = new SettingsViewModel(_storageMock.Object, _apiMock.Object, _authMock.Object);
    }

    [Fact]
    public void LoadFromConfig_PopulatesViewModelProperties()
    {
        // Arrange
        var config = new AppConfig
        {
            ApiKey = "sk-ant-test-key",
            SyncIntervalMinutes = 15,
            Schedule = new WorkSchedule
            {
                TimeZoneId = "Europe/Madrid",
                Slots = new()
                {
                    new WorkTimeSlot(DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(17, 0)),
                    new WorkTimeSlot(DayOfWeek.Friday, new TimeOnly(8, 0), new TimeOnly(17, 0))
                }
            }
        };

        // Act
        _sut.LoadFromConfig(config);

        // Assert
        _sut.ApiKey.Should().Be("sk-ant-test-key");
        _sut.SyncIntervalMinutes.Should().Be(15);
        _sut.TimeZoneId.Should().Be("Europe/Madrid");
        _sut.IsMonday.Should().BeTrue();
        _sut.IsFriday.Should().BeTrue();
        _sut.IsTuesday.Should().BeFalse();
        _sut.StartTime.Should().Be(new TimeSpan(8, 0, 0));
        _sut.EndTime.Should().Be(new TimeSpan(17, 0, 0));
    }

    [Fact]
    public void ToConfig_MapsAllDaysAndTimesAccurately()
    {
        // Arrange
        _sut.ApiKey = "  sk-ant-saved-key  ";
        _sut.SyncIntervalMinutes = 10;
        _sut.TimeZoneId = "UTC";
        _sut.IsMonday = true;
        _sut.IsTuesday = true;
        _sut.IsWednesday = false;
        _sut.IsThursday = false;
        _sut.IsFriday = false;
        _sut.IsSaturday = false;
        _sut.IsSunday = false;
        _sut.StartTime = new TimeSpan(9, 30, 0);
        _sut.EndTime = new TimeSpan(18, 0, 0);

        // Act
        var config = _sut.ToConfig();

        // Assert
        config.ApiKey.Should().Be("sk-ant-saved-key");
        config.SyncIntervalMinutes.Should().Be(10);
        config.Schedule.Slots.Where(s => s.IsEnabled).Should().HaveCount(2);
        var monday = config.Schedule.Slots.First(s => s.DayOfWeek == DayOfWeek.Monday);
        monday.StartTime.Should().Be(new TimeOnly(9, 30));
        monday.EndTime.Should().Be(new TimeOnly(18, 0));
    }

    [Fact]
    public void ToConfig_WithCustomThresholdsAndTelemetry_MapsProperties()
    {
        _sut.OptimalMarginHours = 0.8;
        _sut.HighRateMarginHours = 4.0;
        _sut.ColorOptimalHex = "#00FF00";
        _sut.TelemetryEnabled = true;
        _sut.TelemetryEndpoint = "https://otlp.grafana.com:4318";
        _sut.TelemetryHeaders = "Authorization=Basic test";
        _sut.TelemetryServiceName = "my-tracker";
        _sut.SelectedLanguage = "English";

        var config = _sut.ToConfig();

        config.Thresholds.OptimalMarginHours.Should().Be(0.8);
        config.Thresholds.HighRateMarginHours.Should().Be(4.0);
        config.Thresholds.ColorOptimalHex.Should().Be("#00FF00");
        config.Telemetry.Enabled.Should().BeTrue();
        config.Telemetry.Endpoint.Should().Be("https://otlp.grafana.com:4318");
        config.Telemetry.Protocol.Should().Be("HttpProtobuf");
        config.Telemetry.Headers.Should().Be("Authorization=Basic test");
        config.Telemetry.ServiceName.Should().Be("my-tracker");
        config.Language.Should().Be("en");
        _sut.MachineName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task LoginWithWebAsync_WhenSuccessful_UpdatesTokensAndStorage()
    {
        // Arrange
        var tokens = new OAuthTokens
        {
            AccessToken = "at-live-123",
            RefreshToken = "rt-live-456",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        };
        _authMock.Setup(a => a.StartInteractiveLoginAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(tokens);

        // Act
        var result = await _sut.LoginWithWebAsync();

        // Assert
        result.Should().BeTrue();
        _sut.IsLoggedInWithWeb.Should().BeTrue();
        _sut.AuthTokens.Should().Be(tokens);
        _sut.SessionStatusText.Should().Contain("Web Signing");
        _storageMock.Verify(s => s.SaveConfigAsync(It.Is<AppConfig>(c => c.AuthTokens == tokens), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LogoutWebAsync_ClearsTokensAndSaves()
    {
        // Arrange
        _sut.AuthTokens = new OAuthTokens { AccessToken = "at-active" };

        // Act
        await _sut.LogoutWebAsync();

        // Assert
        _sut.IsLoggedInWithWeb.Should().BeFalse();
        _sut.AuthTokens.Should().BeNull();
        _storageMock.Verify(s => s.SaveConfigAsync(It.Is<AppConfig>(c => c.AuthTokens == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TestConnectionAsync_WhenNoCredentials_ReturnsFalseAndWarning()
    {
        // Arrange
        _sut.ApiKey = "";
        _sut.AuthTokens = null;

        // Act
        var result = await _sut.TestConnectionAsync();

        // Assert
        result.Should().BeFalse();
        _sut.IsStatusSuccess.Should().BeFalse();
        _sut.StatusMessage.Should().Contain("Inicia sesión");
    }

    [Fact]
    public async Task TestConnectionAsync_WhenCredentialsValid_SetsSuccessStatus()
    {
        // Arrange
        _sut.ApiKey = "sk-ant-valid";
        _apiMock.Setup(a => a.ValidateCredentialsAsync("sk-ant-valid", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _sut.TestConnectionAsync();

        // Assert
        result.Should().BeTrue();
        _sut.IsStatusSuccess.Should().BeTrue();
        _sut.StatusMessage.Should().Contain("correctamente");
    }

    [Fact]
    public async Task SaveAsync_SavesConfigToStorageService()
    {
        // Arrange
        _sut.ApiKey = "sk-ant-key";

        // Act
        var result = await _sut.SaveAsync();

        // Assert
        result.Should().BeTrue();
        _sut.IsStatusSuccess.Should().BeTrue();
        _storageMock.Verify(s => s.SaveConfigAsync(It.Is<AppConfig>(c => c.ApiKey == "sk-ant-key"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ColorProperties_SyncCorrectlyWithHexStrings()
    {
        _sut.ColorOptimal = Avalonia.Media.Color.Parse("#123456");
        _sut.ColorOptimalHex.Should().Be("#123456");

        _sut.ColorOptimalHex = "#654321";
        _sut.ColorOptimal.Should().Be(Avalonia.Media.Color.Parse("#654321"));
    }

    [Fact]
    public void LanguageSelection_SwitchesLocalizationAndStrings()
    {
        _sut.SelectedLanguage = "English";
        _sut["App.Title"].Should().Contain("Settings");

        _sut.SelectedLanguage = "Español";
        _sut["App.Title"].Should().Contain("Configuración");
    }

    [Fact]
    public void LanguageSelection_UpdatesDayScheduleLabelsAndThresholdColorNames()
    {
        _sut.SelectedLanguage = "English";
        _sut.DaySchedules[0].StartLabel.Should().Be("Start:");
        _sut.DaySchedules[0].EndLabel.Should().Be("End:");
        _sut["Threshold.ColorOptimal"].Should().Be("Optimal");
        _sut["Threshold.ColorWarningFast"].Should().Be("Fast");
        _sut["Threshold.ColorDangerFast"].Should().Be("Excessive");
        _sut["Threshold.ColorSlow"].Should().Be("Slow");

        _sut.SelectedLanguage = "Español";
        _sut.DaySchedules[0].StartLabel.Should().Be("Entrada:");
        _sut.DaySchedules[0].EndLabel.Should().Be("Salida:");
        _sut["Threshold.ColorOptimal"].Should().Be("Óptimo");
        _sut["Threshold.ColorWarningFast"].Should().Be("Acelerado");
        _sut["Threshold.ColorDangerFast"].Should().Be("Excesivo");
        _sut["Threshold.ColorSlow"].Should().Be("Lento");
    }
}
