using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace VSNAiRateTracker.Services;

public interface IKeyProtectionProvider
{
    byte[] ProtectKey(byte[] rawKey);
    byte[] UnprotectKey(byte[] protectedKey);

    /// <summary>
    /// True when <paramref name="protectedKey"/> was readable but is stored in a legacy/weaker
    /// format and should be re-protected with <see cref="ProtectKey"/>.
    /// </summary>
    bool NeedsReprotection(byte[] protectedKey) => false;
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

/// <summary>
/// Obfuscation-only fallback for platforms without an OS secret store integration (Linux).
/// The key is derived from user and machine name, so it does not protect against an attacker
/// with access to the user's files; confidentiality relies on the 0600 file permissions.
/// </summary>
public class UnixObfuscationKeyProtectionProvider : IKeyProtectionProvider
{
    private readonly byte[] _derivedKey;

    public UnixObfuscationKeyProtectionProvider()
    {
        // Seed kept unchanged so master.key files written by previous versions stay readable
        var seed = $"{Environment.UserName}_{Environment.MachineName}_VSNAiRateTracker_MacKeySeed";
        _derivedKey = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
    }

    public byte[] ProtectKey(byte[] rawKey)
    {
        ArgumentNullException.ThrowIfNull(rawKey);
        return Xor(rawKey);
    }

    public byte[] UnprotectKey(byte[] protectedKey)
    {
        ArgumentNullException.ThrowIfNull(protectedKey);
        return Xor(protectedKey);
    }

    private byte[] Xor(byte[] input)
    {
        var result = new byte[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            result[i] = (byte)(input[i] ^ _derivedKey[i % _derivedKey.Length]);
        }
        return result;
    }
}

/// <summary>
/// macOS provider: the master key is wrapped with AES-256-GCM using a wrapping key that lives in
/// the user's login Keychain, so a copied master.key/config.enc is useless without the Keychain.
/// Files written by the previous obfuscation-only scheme are still readable and flagged for
/// re-protection.
/// </summary>
[SupportedOSPlatform("macos")]
public class MacKeychainKeyProtectionProvider : IKeyProtectionProvider
{
    public const string DefaultService = "VSNAiRateTracker";
    public const string DefaultAccount = "master-key-wrapping";

    // Layout: [Magic "VKC1" (4B)] [Nonce (12B)] [Tag (16B)] [Ciphertext (NB)]
    private static readonly byte[] Magic = "VKC1"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int WrappingKeySize = 32;

    private readonly string _service;
    private readonly string _account;
    private readonly UnixObfuscationKeyProtectionProvider _legacy = new();

    public MacKeychainKeyProtectionProvider(string service = DefaultService, string account = DefaultAccount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        _service = service;
        _account = account;
    }

    public byte[] ProtectKey(byte[] rawKey)
    {
        ArgumentNullException.ThrowIfNull(rawKey);

        var wrappingKey = GetOrCreateWrappingKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var ciphertext = new byte[rawKey.Length];

        using (var aes = new AesGcm(wrappingKey, TagSize))
        {
            aes.Encrypt(nonce, rawKey, ciphertext, tag, Magic);
        }

        var result = new byte[Magic.Length + NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(Magic, 0, result, 0, Magic.Length);
        Buffer.BlockCopy(nonce, 0, result, Magic.Length, NonceSize);
        Buffer.BlockCopy(tag, 0, result, Magic.Length + NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, result, Magic.Length + NonceSize + TagSize, ciphertext.Length);
        return result;
    }

    public byte[] UnprotectKey(byte[] protectedKey)
    {
        ArgumentNullException.ThrowIfNull(protectedKey);

        if (!HasMagic(protectedKey))
        {
            return _legacy.UnprotectKey(protectedKey);
        }

        var headerSize = Magic.Length + NonceSize + TagSize;
        if (protectedKey.Length < headerSize)
        {
            throw new CryptographicException("The protected key is truncated.");
        }

        var wrappingKey = MacKeychain.Read(_service, _account)
            ?? throw new CryptographicException("The Keychain wrapping key is missing; the master key cannot be recovered.");

        var nonce = protectedKey.AsSpan(Magic.Length, NonceSize);
        var tag = protectedKey.AsSpan(Magic.Length + NonceSize, TagSize);
        var ciphertext = protectedKey.AsSpan(headerSize);
        var plaintext = new byte[ciphertext.Length];

        using (var aes = new AesGcm(wrappingKey, TagSize))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Magic);
        }

        return plaintext;
    }

    public bool NeedsReprotection(byte[] protectedKey) => !HasMagic(protectedKey);

    /// <summary>Removes the wrapping key from the Keychain (used by tests and uninstall).</summary>
    public bool DeleteWrappingKey() => MacKeychain.Delete(_service, _account);

    private byte[] GetOrCreateWrappingKey()
    {
        var existing = MacKeychain.Read(_service, _account);
        if (existing is { Length: WrappingKeySize })
        {
            return existing;
        }

        var wrappingKey = RandomNumberGenerator.GetBytes(WrappingKeySize);
        MacKeychain.Write(_service, _account, wrappingKey);
        return wrappingKey;
    }

    private static bool HasMagic(byte[] data) =>
        data.Length >= Magic.Length && data.AsSpan(0, Magic.Length).SequenceEqual(Magic);
}

public class CrossPlatformKeyProtectionProvider : IKeyProtectionProvider
{
    private readonly IKeyProtectionProvider _underlying;

    public CrossPlatformKeyProtectionProvider()
    {
        if (OperatingSystem.IsWindows())
        {
            _underlying = new WindowsDpapiProtectionProvider();
        }
        else if (OperatingSystem.IsMacOS())
        {
            _underlying = new MacKeychainKeyProtectionProvider();
        }
        else
        {
            _underlying = new UnixObfuscationKeyProtectionProvider();
        }
    }

    public byte[] ProtectKey(byte[] rawKey) => _underlying.ProtectKey(rawKey);
    public byte[] UnprotectKey(byte[] protectedKey) => _underlying.UnprotectKey(protectedKey);
    public bool NeedsReprotection(byte[] protectedKey) => _underlying.NeedsReprotection(protectedKey);
}
