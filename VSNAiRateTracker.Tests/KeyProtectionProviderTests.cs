using System;
using System.Security.Cryptography;
using FluentAssertions;
using VSNAiRateTracker.Services;
using Xunit;

namespace VSNAiRateTracker.Tests;

public class KeyProtectionProviderTests
{
    [Fact]
    public void MacKeyProtectionProvider_RoundTrip_PreservesRawKey()
    {
        // Arrange
        var provider = new MacKeyProtectionProvider();
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
        // Arrange
        var provider = new CrossPlatformKeyProtectionProvider();
        var rawKey = RandomNumberGenerator.GetBytes(32);

        // Act
        var protectedKey = provider.ProtectKey(rawKey);
        var recoveredKey = provider.UnprotectKey(protectedKey);

        // Assert
        recoveredKey.Should().Equal(rawKey);
    }
}

