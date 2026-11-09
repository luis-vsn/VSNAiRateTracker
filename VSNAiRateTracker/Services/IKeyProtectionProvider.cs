using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

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

        // Fallback for non-Windows (tests / future macOS integration)
        return (byte[])rawKey.Clone();
    }

    public byte[] UnprotectKey(byte[] protectedKey)
    {
        ArgumentNullException.ThrowIfNull(protectedKey);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.CurrentUser);
        }

        // Fallback for non-Windows
        return (byte[])protectedKey.Clone();
    }
}
