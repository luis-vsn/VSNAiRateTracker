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
    private readonly SettingsViewModel _sut;

    public SettingsViewModelTests()
    {
        _storageMock = new Mock<ISecureStorageService>();
        _apiMock = new Mock<IAnthropicApiService>();
        _sut = new SettingsViewModel(_storageMock.Object, _apiMock.Object);
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
        config.Schedule.Slots.Should().HaveCount(2);
        config.Schedule.Slots[0].DayOfWeek.Should().Be(DayOfWeek.Monday);
        config.Schedule.Slots[0].StartTime.Should().Be(new TimeOnly(9, 30));
        config.Schedule.Slots[0].EndTime.Should().Be(new TimeOnly(18, 0));
    }

    [Fact]
    public async Task TestConnectionAsync_WhenApiKeyIsEmpty_ReturnsFalseAndWarning()
    {
        // Arrange
        _sut.ApiKey = "";

        // Act
        var result = await _sut.TestConnectionAsync();

        // Assert
        result.Should().BeFalse();
        _sut.IsStatusSuccess.Should().BeFalse();
        _sut.StatusMessage.Should().Contain("Introduce una clave");
        _apiMock.Verify(a => a.ValidateApiKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TestConnectionAsync_WhenApiKeyIsValid_SetsSuccessStatus()
    {
        // Arrange
        _sut.ApiKey = "sk-ant-valid";
        _apiMock.Setup(a => a.ValidateApiKeyAsync("sk-ant-valid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _sut.TestConnectionAsync();

        // Assert
        result.Should().BeTrue();
        _sut.IsStatusSuccess.Should().BeTrue();
        _sut.StatusMessage.Should().Contain("correctamente");
    }

    [Fact]
    public async Task TestConnectionAsync_WhenApiKeyIsInvalid_SetsErrorStatus()
    {
        // Arrange
        _sut.ApiKey = "sk-ant-invalid";
        _apiMock.Setup(a => a.ValidateApiKeyAsync("sk-ant-invalid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _sut.TestConnectionAsync();

        // Assert
        result.Should().BeFalse();
        _sut.IsStatusSuccess.Should().BeFalse();
        _sut.StatusMessage.Should().Contain("inválida");
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
}

