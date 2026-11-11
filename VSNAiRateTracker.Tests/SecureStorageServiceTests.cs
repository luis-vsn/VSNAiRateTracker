using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using VSNAiRateTracker.Models;
using VSNAiRateTracker.Services;
using Xunit;

namespace VSNAiRateTracker.Tests;

public class SecureStorageServiceTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly ISecureStorageService _sut;
    private readonly IKeyProtectionProvider? _keyProtection;
    private readonly string _keychainService = "VSNAiRateTracker.Tests." + Guid.NewGuid().ToString("N");

    public SecureStorageServiceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "VSNAiRateTrackerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        // On macOS isolate the Keychain wrapping key so tests never create the production item
        _keyProtection = OperatingSystem.IsMacOS() ? new MacKeychainKeyProtectionProvider(_keychainService) : null;
        _sut = new SecureStorageService(_tempDirectory, _keyProtection);
    }

    public void Dispose()
    {
        if (OperatingSystem.IsMacOS())
        {
            try { MacKeychain.Delete(_keychainService, MacKeychainKeyProtectionProvider.DefaultAccount); } catch { /* best effort */ }
        }

        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, true);
            }
            catch
            {
                // Best effort cleanup in tests
            }
        }
    }

    [Fact]
    public void EncryptAndDecrypt_RoundTrip_PreservesOriginalData()
    {
        // Arrange
        var plaintext = Encoding.UTF8.GetBytes("SuperSecretAnthropicApiKey-12345");

        // Act
        var encrypted = _sut.EncryptData(plaintext);
        var decrypted = _sut.DecryptData(encrypted);

        // Assert
        encrypted.Should().NotBeEquivalentTo(plaintext);
        decrypted.Should().BeEquivalentTo(plaintext);
        Encoding.UTF8.GetString(decrypted).Should().Be("SuperSecretAnthropicApiKey-12345");
    }

    [Fact]
    public void Decrypt_WhenCiphertextTampered_ThrowsCryptographicException()
    {
        // Arrange
        var plaintext = Encoding.UTF8.GetBytes("SecretData");
        var encrypted = _sut.EncryptData(plaintext);

        // Tamper with the last byte
        encrypted[^1] ^= 0xFF;

        // Act & Assert
        Action act = () => _sut.DecryptData(encrypted);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Decrypt_WhenPayloadIsTooShort_ThrowsCryptographicException()
    {
        // Arrange
        var tooShortPayload = new byte[10];

        // Act & Assert
        Action act = () => _sut.DecryptData(tooShortPayload);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public async Task SaveConfigAndLoadConfig_RoundTrip_PreservesAllConfigProperties()
    {
        // Arrange
        var config = new AppConfig
        {
            ApiKey = "sk-ant-live-0987654321",
            SyncIntervalMinutes = 10,
            Schedule = new WorkSchedule
            {
                TimeZoneId = "UTC",
                Slots = new()
                {
                    new WorkTimeSlot(DayOfWeek.Monday, new TimeOnly(8, 30), new TimeOnly(16, 30)),
                    new WorkTimeSlot(DayOfWeek.Friday, new TimeOnly(9, 0), new TimeOnly(14, 0))
                }
            }
        };

        // Act
        await _sut.SaveConfigAsync(config);
        var loaded = await _sut.LoadConfigAsync();

        // Assert
        loaded.Should().NotBeNull();
        loaded.ApiKey.Should().Be("sk-ant-live-0987654321");
        loaded.SyncIntervalMinutes.Should().Be(10);
        loaded.Schedule.TimeZoneId.Should().Be("UTC");
        loaded.Schedule.Slots.Should().HaveCount(2);
        loaded.Schedule.Slots[0].DayOfWeek.Should().Be(DayOfWeek.Monday);
        loaded.Schedule.Slots[0].StartTime.Should().Be(new TimeOnly(8, 30));
        loaded.Schedule.Slots[1].DayOfWeek.Should().Be(DayOfWeek.Friday);
        loaded.Schedule.Slots[1].EndTime.Should().Be(new TimeOnly(14, 0));
    }

    [Fact]
    public async Task LoadConfig_WhenFileDoesNotExist_ReturnsDefaultConfig()
    {
        // Act
        var config = await _sut.LoadConfigAsync();

        // Assert
        config.Should().NotBeNull();
        config.ApiKey.Should().BeEmpty();
        config.SyncIntervalMinutes.Should().Be(5);
        config.Schedule.Slots.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Config_IsNeverWrittenInPlainText_OnDisk()
    {
        // Arrange
        const string secretKey = "sk-ant-api03-very-secret-token";
        var config = new AppConfig
        {
            ApiKey = secretKey,
            SyncIntervalMinutes = 5
        };

        // Act
        await _sut.SaveConfigAsync(config);

        // Assert
        var configFilePath = Path.Combine(_tempDirectory, "config.enc");
        File.Exists(configFilePath).Should().BeTrue();

        var rawFileBytes = await File.ReadAllBytesAsync(configFilePath);
        var rawContent = Encoding.UTF8.GetString(rawFileBytes);

        rawContent.Should().NotContain(secretKey);
    }

    [Fact]
    public async Task LoadConfig_WithBundledDefaults_AppliesTelemetryDefaultsWhenNotConfigured()
    {
        // Arrange
        var defaultsFile = Path.Combine(_tempDirectory, "test-defaults.enc");
        var defaultsJson = """
        {
          "Telemetry": {
            "Enabled": true,
            "Endpoint": "https://otlp-gateway-prod.grafana.net/otlp",
            "Headers": "Authorization=Basic dGVzdA==",
            "ServiceName": "vsn-ai-rate-tracker",
            "Protocol": "HttpProtobuf"
          }
        }
        """;
        var encryptedBytes = BundledDefaultsCrypto.Encrypt(defaultsJson);
        await File.WriteAllBytesAsync(defaultsFile, encryptedBytes);

        var service = new SecureStorageService(_tempDirectory, _keyProtection, defaultsFilePath: defaultsFile);

        // Act
        var config = await service.LoadConfigAsync();

        // Assert
        config.Should().NotBeNull();
        config.Telemetry.Should().NotBeNull();
        config.Telemetry.Enabled.Should().BeTrue();
        config.Telemetry.Endpoint.Should().Be("https://otlp-gateway-prod.grafana.net/otlp");
        config.Telemetry.Headers.Should().Be("Authorization=Basic dGVzdA==");
        config.Telemetry.ServiceName.Should().Be("vsn-ai-rate-tracker");
        config.Telemetry.Protocol.Should().Be("HttpProtobuf");
    }

    [Fact]
    public async Task LoadConfig_WithBundledDefaults_DoesNotOverwriteExistingUserConfig()
    {
        // Arrange
        var defaultsFile = Path.Combine(_tempDirectory, "test-defaults.enc");
        var defaultsJson = """
        {
          "Telemetry": {
            "Enabled": true,
            "Endpoint": "https://bundled.example.com",
            "Headers": "BundledAuth"
          }
        }
        """;
        var encryptedBytes = BundledDefaultsCrypto.Encrypt(defaultsJson);
        await File.WriteAllBytesAsync(defaultsFile, encryptedBytes);

        var service = new SecureStorageService(_tempDirectory, _keyProtection, defaultsFilePath: defaultsFile);

        // Save existing user config with custom endpoint and disabled telemetry
        var existingConfig = new AppConfig
        {
            Telemetry = new TelemetryConfig
            {
                Enabled = false,
                Endpoint = "https://custom-user-endpoint.example.com",
                Headers = "UserAuth"
            }
        };
        await service.SaveConfigAsync(existingConfig);

        // Act
        var loaded = await service.LoadConfigAsync();

        // Assert
        loaded.Telemetry.Enabled.Should().BeFalse();
        loaded.Telemetry.Endpoint.Should().Be("https://custom-user-endpoint.example.com");
        loaded.Telemetry.Headers.Should().Be("UserAuth");
    }

    [Fact]
    public void BundledDefaultsCrypto_EncryptAndDecrypt_RoundTripPreservesContent()
    {
        // Arrange
        var sampleJson = """{"Telemetry":{"Enabled":true,"Endpoint":"https://otlp.example.com","Headers":"Auth=Secret"}}""";

        // Act
        var encrypted = BundledDefaultsCrypto.Encrypt(sampleJson);
        var decrypted = BundledDefaultsCrypto.Decrypt(encrypted);

        // Assert
        encrypted.Should().NotBeNull();
        encrypted.Length.Should().BeGreaterThan(29);
        decrypted.Should().Be(sampleJson);
    }

    [Fact]
    public void BundledDefaultsCrypto_Decrypt_WhenCiphertextTampered_ThrowsCryptographicException()
    {
        // Arrange
        var sampleJson = """{"Telemetry":{"Enabled":true}}""";
        var encrypted = BundledDefaultsCrypto.Encrypt(sampleJson);

        // Tamper with ciphertext byte
        encrypted[^1] ^= 0x55;

        // Act & Assert
        Action act = () => BundledDefaultsCrypto.Decrypt(encrypted);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void BundledDefaultsCrypto_Decrypt_WhenPayloadTooShort_ThrowsCryptographicException()
    {
        // Arrange
        var truncated = new byte[15];

        // Act & Assert
        Action act = () => BundledDefaultsCrypto.Decrypt(truncated);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void BundledDefaultsCrypto_Decrypt_WhenVersionMismatch_ThrowsCryptographicException()
    {
        // Arrange
        var sampleJson = """{"Telemetry":{"Enabled":true}}""";
        var encrypted = BundledDefaultsCrypto.Encrypt(sampleJson);
        encrypted[0] = 0x99; // Invalid version

        // Act & Assert
        Action act = () => BundledDefaultsCrypto.Decrypt(encrypted);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public async Task LoadConfig_WithEncryptedBundledDefaults_DecryptsAndAppliesTelemetryDefaults()
    {
        // Arrange
        var defaultsEncFile = Path.Combine(_tempDirectory, "test-defaults.enc");
        var defaultsJson = """
        {
          "Telemetry": {
            "Enabled": true,
            "Endpoint": "https://encrypted-gateway.vsn.tv/otlp",
            "Headers": "Authorization=Bearer enc-token-secret-12345",
            "ServiceName": "vsn-ai-rate-tracker",
            "Protocol": "HttpProtobuf"
          }
        }
        """;
        var encryptedBytes = BundledDefaultsCrypto.Encrypt(defaultsJson);
        await File.WriteAllBytesAsync(defaultsEncFile, encryptedBytes);

        var service = new SecureStorageService(_tempDirectory, _keyProtection, defaultsFilePath: defaultsEncFile);

        // Act
        var config = await service.LoadConfigAsync();

        // Assert
        config.Should().NotBeNull();
        config.Telemetry.Should().NotBeNull();
        config.Telemetry.Enabled.Should().BeTrue();
        config.Telemetry.Endpoint.Should().Be("https://encrypted-gateway.vsn.tv/otlp");
        config.Telemetry.Headers.Should().Be("Authorization=Bearer enc-token-secret-12345");
        config.Telemetry.ServiceName.Should().Be("vsn-ai-rate-tracker");
        config.Telemetry.Protocol.Should().Be("HttpProtobuf");
    }
}

