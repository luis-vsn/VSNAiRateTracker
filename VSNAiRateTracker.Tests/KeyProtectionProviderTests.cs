using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using VSNAiRateTracker.Models;
using VSNAiRateTracker.Services;
using Xunit;

namespace VSNAiRateTracker.Tests;

public class KeyProtectionProviderTests : IDisposable
{
    // Unique Keychain service per test class instance so tests never touch the real app's item
    private readonly string _keychainService = "VSNAiRateTracker.Tests." + Guid.NewGuid().ToString("N");
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "VSNAiRateTrackerKeyTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                MacKeychain.Delete(_keychainService, MacKeychainKeyProtectionProvider.DefaultAccount);
                MacKeychain.Delete(_keychainService, "raw-item");
            }
            catch
            {
                // Best effort cleanup in tests
            }
        }

        if (Directory.Exists(_tempDirectory))
        {
            try { Directory.Delete(_tempDirectory, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void UnixObfuscationKeyProtectionProvider_RoundTrip_PreservesRawKey()
    {
        // Arrange
        var provider = new UnixObfuscationKeyProtectionProvider();
        var rawKey = RandomNumberGenerator.GetBytes(32);

        // Act
        var protectedKey = provider.ProtectKey(rawKey);
        var recoveredKey = provider.UnprotectKey(protectedKey);

        // Assert
        protectedKey.Should().NotEqual(rawKey);
        recoveredKey.Should().Equal(rawKey);
    }

    [Fact]
    public void CrossPlatformKeyProtectionProvider_RoundTrip_PreservesRawKey()
    {
        // On macOS the default provider would create the production Keychain item from the
        // test host (wrong ACL owner); the macOS path is covered by the Keychain tests below.
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        // Arrange
        var provider = new CrossPlatformKeyProtectionProvider();
        var rawKey = RandomNumberGenerator.GetBytes(32);

        // Act
        var protectedKey = provider.ProtectKey(rawKey);
        var recoveredKey = provider.UnprotectKey(protectedKey);

        // Assert
        recoveredKey.Should().Equal(rawKey);
    }

    [Fact]
    public void MacKeychain_WriteReadDelete_RoundTripsSecret()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var secret = RandomNumberGenerator.GetBytes(48);

        MacKeychain.Read(_keychainService, "raw-item").Should().BeNull();

        MacKeychain.Write(_keychainService, "raw-item", secret);
        MacKeychain.Read(_keychainService, "raw-item").Should().Equal(secret);

        // Overwrite replaces the existing item instead of failing as duplicate
        var replacement = RandomNumberGenerator.GetBytes(16);
        MacKeychain.Write(_keychainService, "raw-item", replacement);
        MacKeychain.Read(_keychainService, "raw-item").Should().Equal(replacement);

        MacKeychain.Delete(_keychainService, "raw-item").Should().BeTrue();
        MacKeychain.Read(_keychainService, "raw-item").Should().BeNull();
        MacKeychain.Delete(_keychainService, "raw-item").Should().BeFalse();
    }

    [Fact]
    public void MacKeychainKeyProtectionProvider_RoundTrip_WrapsWithKeychainKey()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var provider = new MacKeychainKeyProtectionProvider(_keychainService);
        var rawKey = RandomNumberGenerator.GetBytes(32);

        var protectedKey = provider.ProtectKey(rawKey);
        var recoveredKey = provider.UnprotectKey(protectedKey);

        Encoding.ASCII.GetString(protectedKey, 0, 4).Should().Be("VKC1");
        protectedKey.Length.Should().Be(4 + 12 + 16 + 32);
        protectedKey.Skip(4).Should().NotContainInConsecutiveOrder(rawKey);
        recoveredKey.Should().Equal(rawKey);
        provider.NeedsReprotection(protectedKey).Should().BeFalse();

        MacKeychain.Read(_keychainService, MacKeychainKeyProtectionProvider.DefaultAccount)
            .Should().NotBeNull().And.HaveCount(32);
    }

    [Fact]
    public void MacKeychainKeyProtectionProvider_LegacyObfuscatedKey_IsReadableAndFlaggedForReprotection()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var provider = new MacKeychainKeyProtectionProvider(_keychainService);
        var rawKey = RandomNumberGenerator.GetBytes(32);
        var legacyProtected = new UnixObfuscationKeyProtectionProvider().ProtectKey(rawKey);

        provider.UnprotectKey(legacyProtected).Should().Equal(rawKey);
        provider.NeedsReprotection(legacyProtected).Should().BeTrue();
    }

    [Fact]
    public void MacKeychainKeyProtectionProvider_WhenWrappingKeyMissing_Throws()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var provider = new MacKeychainKeyProtectionProvider(_keychainService);
        var protectedKey = provider.ProtectKey(RandomNumberGenerator.GetBytes(32));
        provider.DeleteWrappingKey().Should().BeTrue();

        var act = () => OperatingSystem.IsMacOS() ? provider.UnprotectKey(protectedKey) : null; // guard keeps CA1416 happy inside the lambda

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public async Task SecureStorageService_WithLegacyMacKeyFile_MigratesToKeychainAndKeepsConfig()
    {
        if (!OperatingSystem.IsMacOS()) return;

        // Arrange: storage written by the previous (obfuscation-only) macOS provider
        Directory.CreateDirectory(_tempDirectory);
        var legacyStorage = new SecureStorageService(_tempDirectory, new UnixObfuscationKeyProtectionProvider());
        await legacyStorage.SaveConfigAsync(new AppConfig { ApiKey = "sk-ant-legacy" });

        // Act
        var upgraded = new SecureStorageService(_tempDirectory, new MacKeychainKeyProtectionProvider(_keychainService));
        var config = await upgraded.LoadConfigAsync();

        // Assert
        config.ApiKey.Should().Be("sk-ant-legacy");
        var keyFile = File.ReadAllBytes(Path.Combine(_tempDirectory, "master.key"));
        Encoding.ASCII.GetString(keyFile, 0, 4).Should().Be("VKC1");

        var reopened = new SecureStorageService(_tempDirectory, new MacKeychainKeyProtectionProvider(_keychainService));
        (await reopened.LoadConfigAsync()).ApiKey.Should().Be("sk-ant-legacy");
    }

    [Fact]
    public void SecureStorageService_WhenProviderRequestsReprotection_RewritesMasterKey()
    {
        // Arrange
        Directory.CreateDirectory(_tempDirectory);
        var keyPath = Path.Combine(_tempDirectory, "master.key");
        var rawKey = RandomNumberGenerator.GetBytes(32);
        var legacyBytes = Enumerable.Repeat((byte)0xAA, 32).ToArray();
        var upgradedBytes = Enumerable.Repeat((byte)0xBB, 40).ToArray();
        File.WriteAllBytes(keyPath, legacyBytes);

        var provider = new Mock<IKeyProtectionProvider>();
        provider.Setup(p => p.UnprotectKey(It.IsAny<byte[]>())).Returns(rawKey);
        provider.Setup(p => p.NeedsReprotection(It.Is<byte[]>(b => b.SequenceEqual(legacyBytes)))).Returns(true);
        provider.Setup(p => p.ProtectKey(It.IsAny<byte[]>())).Returns(upgradedBytes);

        // Act
        _ = new SecureStorageService(_tempDirectory, provider.Object);

        // Assert
        File.ReadAllBytes(keyPath).Should().Equal(upgradedBytes);
        provider.Verify(p => p.ProtectKey(It.Is<byte[]>(k => k.SequenceEqual(rawKey))), Times.Once);
    }
}
