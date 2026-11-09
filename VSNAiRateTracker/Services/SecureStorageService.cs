using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VSNAiRateTracker.Models;

namespace VSNAiRateTracker.Services;

public class SecureStorageService : ISecureStorageService
{
    private const byte PayloadVersion = 0x01;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32; // AES-256

    private readonly string _storageDirectory;
    private readonly string _configFilePath;
    private readonly string _keyFilePath;
    private readonly IKeyProtectionProvider _keyProtectionProvider;
    private readonly byte[] _masterKey;

    public SecureStorageService(
        string? storageDirectory = null,
        IKeyProtectionProvider? keyProtectionProvider = null)
    {
        _storageDirectory = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VSNAiRateTracker");

        _configFilePath = Path.Combine(_storageDirectory, "config.enc");
        _keyFilePath = Path.Combine(_storageDirectory, "master.key");
        _keyProtectionProvider = keyProtectionProvider ?? new WindowsDpapiProtectionProvider();

        Directory.CreateDirectory(_storageDirectory);
        _masterKey = InitializeMasterKey();
    }

    private byte[] InitializeMasterKey()
    {
        if (File.Exists(_keyFilePath))
        {
            try
            {
                var protectedKey = File.ReadAllBytes(_keyFilePath);
                var unprotectedKey = _keyProtectionProvider.UnprotectKey(protectedKey);
                if (unprotectedKey.Length == KeySize)
                {
                    return unprotectedKey;
                }
            }
            catch
            {
                // In case of corruption or machine migration, generate a fresh key
            }
        }

        var newKey = RandomNumberGenerator.GetBytes(KeySize);
        var newlyProtectedKey = _keyProtectionProvider.ProtectKey(newKey);
        File.WriteAllBytes(_keyFilePath, newlyProtectedKey);
        return newKey;
    }

    public byte[] EncryptData(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var ciphertext = new byte[plaintext.Length];

        using (var aesGcm = new AesGcm(_masterKey, TagSize))
        {
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        // Layout: [Version (1B)] [Nonce (12B)] [Tag (16B)] [Ciphertext (NB)]
        var payload = new byte[1 + NonceSize + TagSize + ciphertext.Length];
        payload[0] = PayloadVersion;
        Buffer.BlockCopy(nonce, 0, payload, 1, NonceSize);
        Buffer.BlockCopy(tag, 0, payload, 1 + NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, payload, 1 + NonceSize + TagSize, ciphertext.Length);

        return payload;
    }

    public byte[] DecryptData(byte[] encryptedPayload)
    {
        ArgumentNullException.ThrowIfNull(encryptedPayload);

        const int minSize = 1 + NonceSize + TagSize;
        if (encryptedPayload.Length < minSize)
        {
            throw new CryptographicException("The encrypted payload is truncated or invalid.");
        }

        var version = encryptedPayload[0];
        if (version != PayloadVersion)
        {
            throw new CryptographicException($"Unsupported payload version: {version}");
        }

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var ciphertextLength = encryptedPayload.Length - minSize;
        var ciphertext = new byte[ciphertextLength];
        var plaintext = new byte[ciphertextLength];

        Buffer.BlockCopy(encryptedPayload, 1, nonce, 0, NonceSize);
        Buffer.BlockCopy(encryptedPayload, 1 + NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(encryptedPayload, minSize, ciphertext, 0, ciphertextLength);

        using (var aesGcm = new AesGcm(_masterKey, TagSize))
        {
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return plaintext;
    }

    public async Task SaveConfigAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        Directory.CreateDirectory(_storageDirectory);

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(config, new JsonSerializerOptions
        {
            WriteIndented = false
        });

        var encryptedBytes = EncryptData(jsonBytes);

        var tempPath = _configFilePath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, encryptedBytes, cancellationToken);
        File.Move(tempPath, _configFilePath, overwrite: true);
    }

    public async Task<AppConfig> LoadConfigAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_configFilePath))
        {
            return new AppConfig();
        }

        try
        {
            var encryptedBytes = await File.ReadAllBytesAsync(_configFilePath, cancellationToken);
            var decryptedBytes = DecryptData(encryptedBytes);
            var config = JsonSerializer.Deserialize<AppConfig>(decryptedBytes);
            return config ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }
}

