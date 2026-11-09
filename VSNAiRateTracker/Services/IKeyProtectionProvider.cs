using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace VSNAiRateTracker.Services;

public interface IKeyProtectionProvider
{
    byte[] ProtectKey(byte[] rawKey);
    byte[] UnprotectKey(byte[] protectedKey);
}

public class WindowsDpapiProtectionProvider : IKeyProtectionProvider
{
    public byte[] ProtectKey(byte[] rawKey)
    {
        ArgumentNullException.ThrowIfNull(rawKey);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ProtectedData.Protect(rawKey, null, DataProtectionScope.CurrentUser);
        }

        return (byte[])rawKey.Clone();
    }

    public byte[] UnprotectKey(byte[] protectedKey)
    {
        ArgumentNullException.ThrowIfNull(protectedKey);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.CurrentUser);
        }

        return (byte[])protectedKey.Clone();
    }
}

public class MacKeyProtectionProvider : IKeyProtectionProvider
{
    private readonly byte[] _derivedKey;

    public MacKeyProtectionProvider()
    {
        var seed = $"{Environment.UserName}_{Environment.MachineName}_VSNAiRateTracker_MacKeySeed";
        _derivedKey = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
    }

    public byte[] ProtectKey(byte[] rawKey)
    {
        ArgumentNullException.ThrowIfNull(rawKey);

        var result = new byte[rawKey.Length];
        for (int i = 0; i < rawKey.Length; i++)
        {
            result[i] = (byte)(rawKey[i] ^ _derivedKey[i % _derivedKey.Length]);
        }
        return result;
    }

    public byte[] UnprotectKey(byte[] protectedKey)
    {
        ArgumentNullException.ThrowIfNull(protectedKey);

        var result = new byte[protectedKey.Length];
        for (int i = 0; i < protectedKey.Length; i++)
        {
            result[i] = (byte)(protectedKey[i] ^ _derivedKey[i % _derivedKey.Length]);
        }
        return result;
    }
}

public class CrossPlatformKeyProtectionProvider : IKeyProtectionProvider
{
    private readonly IKeyProtectionProvider _underlying;

    public CrossPlatformKeyProtectionProvider()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _underlying = new WindowsDpapiProtectionProvider();
        }
        else
        {
            _underlying = new MacKeyProtectionProvider();
        }
    }

    public byte[] ProtectKey(byte[] rawKey) => _underlying.ProtectKey(rawKey);
    public byte[] UnprotectKey(byte[] protectedKey) => _underlying.UnprotectKey(protectedKey);
}
