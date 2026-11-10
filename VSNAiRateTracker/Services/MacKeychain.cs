using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace VSNAiRateTracker.Services;

/// <summary>
/// Minimal interop with the macOS Keychain (Security.framework SecItem API) for generic passwords.
/// </summary>
[SupportedOSPlatform("macos")]
public static class MacKeychain
{
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const int ErrSecSuccess = 0;
    private const int ErrSecDuplicateItem = -25299;
    private const int ErrSecItemNotFound = -25300;
    private const uint KCFStringEncodingUtf8 = 0x08000100;

    private static readonly Lazy<Symbols> LazySymbols = new(Symbols.Load);

    /// <summary>
    /// Reads a generic password. Returns null when the item does not exist.
    /// When <paramref name="account"/> is null, the first item matching the service is returned.
    /// </summary>
    public static byte[]? Read(string service, string? account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        var s = LazySymbols.Value;

        var pairs = new List<(IntPtr key, IntPtr value)>
        {
            (s.SecClass, s.SecClassGenericPassword),
            (s.SecAttrService, CreateString(service)),
            (s.SecReturnData, s.CFBooleanTrue),
            (s.SecMatchLimit, s.SecMatchLimitOne)
        };
        if (account != null)
        {
            pairs.Add((s.SecAttrAccount, CreateString(account)));
        }

        var query = CreateDictionary(s, pairs);
        try
        {
            var status = SecItemCopyMatching(query, out var result);
            if (status == ErrSecItemNotFound)
            {
                return null;
            }
            ThrowOnError(status, "SecItemCopyMatching");

            try
            {
                var length = (int)CFDataGetLength(result);
                var bytes = new byte[length];
                if (length > 0)
                {
                    Marshal.Copy(CFDataGetBytePtr(result), bytes, 0, length);
                }
                return bytes;
            }
            finally
            {
                CFRelease(result);
            }
        }
        finally
        {
            CFRelease(query);
            ReleaseOwnedValues(s, pairs);
        }
    }

    /// <summary>
    /// Creates or replaces a generic password, readable only while this device is unlocked
    /// and never migrated to other devices.
    /// </summary>
    public static void Write(string service, string account, byte[] secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentNullException.ThrowIfNull(secret);
        var s = LazySymbols.Value;

        Delete(service, account);

        var pairs = new List<(IntPtr key, IntPtr value)>
        {
            (s.SecClass, s.SecClassGenericPassword),
            (s.SecAttrService, CreateString(service)),
            (s.SecAttrAccount, CreateString(account)),
            (s.SecAttrLabel, CreateString(service)),
            (s.SecAttrAccessible, s.SecAttrAccessibleAfterFirstUnlockThisDeviceOnly),
            (s.SecValueData, CreateData(secret))
        };

        var attributes = CreateDictionary(s, pairs);
        try
        {
            var status = SecItemAdd(attributes, IntPtr.Zero);
            ThrowOnError(status, status == ErrSecDuplicateItem ? "SecItemAdd (duplicate)" : "SecItemAdd");
        }
        finally
        {
            CFRelease(attributes);
            ReleaseOwnedValues(s, pairs);
        }
    }

    /// <summary>Deletes a generic password. Returns false when it did not exist.</summary>
    public static bool Delete(string service, string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        var s = LazySymbols.Value;

        var pairs = new List<(IntPtr key, IntPtr value)>
        {
            (s.SecClass, s.SecClassGenericPassword),
            (s.SecAttrService, CreateString(service)),
            (s.SecAttrAccount, CreateString(account))
        };

        var query = CreateDictionary(s, pairs);
        try
        {
            var status = SecItemDelete(query);
            if (status == ErrSecItemNotFound)
            {
                return false;
            }
            ThrowOnError(status, "SecItemDelete");
            return true;
        }
        finally
        {
            CFRelease(query);
            ReleaseOwnedValues(s, pairs);
        }
    }

    private static void ThrowOnError(int status, string operation)
    {
        if (status == ErrSecSuccess)
        {
            return;
        }

        string? message = null;
        var cfMessage = SecCopyErrorMessageString(status, IntPtr.Zero);
        if (cfMessage != IntPtr.Zero)
        {
            try
            {
                message = CFStringToString(cfMessage);
            }
            finally
            {
                CFRelease(cfMessage);
            }
        }

        throw new MacKeychainException(status, $"{operation} failed with OSStatus {status}: {message ?? "unknown error"}");
    }

    private static IntPtr CreateDictionary(Symbols s, List<(IntPtr key, IntPtr value)> pairs)
    {
        var keys = new IntPtr[pairs.Count];
        var values = new IntPtr[pairs.Count];
        for (int i = 0; i < pairs.Count; i++)
        {
            keys[i] = pairs[i].key;
            values[i] = pairs[i].value;
        }

        var dict = CFDictionaryCreate(IntPtr.Zero, keys, values, pairs.Count, s.KeyCallBacks, s.ValueCallBacks);
        if (dict == IntPtr.Zero)
        {
            throw new MacKeychainException(0, "CFDictionaryCreate returned NULL.");
        }
        return dict;
    }

    /// <summary>Releases the values we created (strings/data); framework constants are not owned.</summary>
    private static void ReleaseOwnedValues(Symbols s, List<(IntPtr key, IntPtr value)> pairs)
    {
        foreach (var (_, value) in pairs)
        {
            if (value != IntPtr.Zero && !s.IsConstant(value))
            {
                CFRelease(value);
            }
        }
    }

    private static IntPtr CreateString(string value)
    {
        var handle = CFStringCreateWithCString(IntPtr.Zero, value, KCFStringEncodingUtf8);
        if (handle == IntPtr.Zero)
        {
            throw new MacKeychainException(0, "CFStringCreateWithCString returned NULL.");
        }
        return handle;
    }

    private static IntPtr CreateData(byte[] bytes)
    {
        var handle = CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
        if (handle == IntPtr.Zero)
        {
            throw new MacKeychainException(0, "CFDataCreate returned NULL.");
        }
        return handle;
    }

    private static string? CFStringToString(IntPtr cfString)
    {
        var length = CFStringGetLength(cfString);
        var maxSize = CFStringGetMaximumSizeForEncoding(length, KCFStringEncodingUtf8) + 1;
        var buffer = new byte[maxSize];
        if (!CFStringGetCString(cfString, buffer, maxSize, KCFStringEncodingUtf8))
        {
            return null;
        }
        var terminator = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, terminator < 0 ? buffer.Length : terminator);
    }

    private sealed class Symbols
    {
        public IntPtr SecClass;
        public IntPtr SecClassGenericPassword;
        public IntPtr SecAttrService;
        public IntPtr SecAttrAccount;
        public IntPtr SecAttrLabel;
        public IntPtr SecAttrAccessible;
        public IntPtr SecAttrAccessibleAfterFirstUnlockThisDeviceOnly;
        public IntPtr SecValueData;
        public IntPtr SecReturnData;
        public IntPtr SecMatchLimit;
        public IntPtr SecMatchLimitOne;
        public IntPtr CFBooleanTrue;
        public IntPtr KeyCallBacks;
        public IntPtr ValueCallBacks;

        private HashSet<IntPtr> _constants = new();

        public bool IsConstant(IntPtr value) => _constants.Contains(value);

        public static Symbols Load()
        {
            var security = NativeLibrary.Load(SecurityFramework);
            var coreFoundation = NativeLibrary.Load(CoreFoundationFramework);

            static IntPtr Constant(IntPtr lib, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(lib, name));

            var s = new Symbols
            {
                SecClass = Constant(security, "kSecClass"),
                SecClassGenericPassword = Constant(security, "kSecClassGenericPassword"),
                SecAttrService = Constant(security, "kSecAttrService"),
                SecAttrAccount = Constant(security, "kSecAttrAccount"),
                SecAttrLabel = Constant(security, "kSecAttrLabel"),
                SecAttrAccessible = Constant(security, "kSecAttrAccessible"),
                SecAttrAccessibleAfterFirstUnlockThisDeviceOnly = Constant(security, "kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly"),
                SecValueData = Constant(security, "kSecValueData"),
                SecReturnData = Constant(security, "kSecReturnData"),
                SecMatchLimit = Constant(security, "kSecMatchLimit"),
                SecMatchLimitOne = Constant(security, "kSecMatchLimitOne"),
                CFBooleanTrue = Constant(coreFoundation, "kCFBooleanTrue"),
                // The callback structs are passed by address, not dereferenced
                KeyCallBacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryKeyCallBacks"),
                ValueCallBacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryValueCallBacks")
            };

            s._constants = new HashSet<IntPtr>
            {
                s.SecClassGenericPassword,
                s.SecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
                s.SecMatchLimitOne,
                s.CFBooleanTrue
            };
            return s;
        }
    }

    [DllImport(SecurityFramework)]
    private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [DllImport(SecurityFramework)]
    private static extern int SecItemAdd(IntPtr attributes, IntPtr result);

    [DllImport(SecurityFramework)]
    private static extern int SecItemDelete(IntPtr query);

    [DllImport(SecurityFramework)]
    private static extern IntPtr SecCopyErrorMessageString(int status, IntPtr reserved);

    [DllImport(CoreFoundationFramework)]
    private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint numValues, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [DllImport(CoreFoundationFramework, CharSet = CharSet.Ansi)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr, uint encoding);

    [DllImport(CoreFoundationFramework)]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [DllImport(CoreFoundationFramework)]
    private static extern nint CFDataGetLength(IntPtr data);

    [DllImport(CoreFoundationFramework)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CoreFoundationFramework)]
    private static extern nint CFStringGetLength(IntPtr str);

    [DllImport(CoreFoundationFramework)]
    private static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);

    [DllImport(CoreFoundationFramework)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(IntPtr str, byte[] buffer, nint bufferSize, uint encoding);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFRelease(IntPtr cf);
}

public class MacKeychainException : Exception
{
    public int Status { get; }

    public MacKeychainException(int status, string message) : base(message)
    {
        Status = status;
    }
}
