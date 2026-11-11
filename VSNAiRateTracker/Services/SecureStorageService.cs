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
    private readonly string? _defaultsFilePath;
    private readonly IKeyProtectionProvider _keyProtectionProvider;
    private readonly byte[] _masterKey;

    public SecureStorageService(
        string? storageDirectory = null,
        IKeyProtectionProvider? keyProtectionProvider = null,
        string? defaultsFilePath = null)
    {
        _storageDirectory = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VSNAiRateTracker");

        _configFilePath = Path.Combine(_storageDirectory, "config.enc");
        _keyFilePath = Path.Combine(_storageDirectory, "master.key");
        _defaultsFilePath = defaultsFilePath;
        _keyProtectionProvider = keyProtectionProvider ?? new CrossPlatformKeyProtectionProvider();

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
                    if (_keyProtectionProvider.NeedsReprotection(protectedKey))
                    {
                        TryReprotectMasterKey(unprotectedKey);
                    }
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
        ApplyUnixSecurePermissions(_keyFilePath);
        return newKey;
    }

    private void TryReprotectMasterKey(byte[] masterKey)
    {
        try
        {
            var reprotected = _keyProtectionProvider.ProtectKey(masterKey);
            var tempPath = _keyFilePath + ".tmp";
            File.WriteAllBytes(tempPath, reprotected);
            ApplyUnixSecurePermissions(tempPath);
            File.Move(tempPath, _keyFilePath, overwrite: true);
        }
        catch
        {
            // Keep the legacy-protected key; the upgrade is retried on the next start
        }
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
        ApplyUnixSecurePermissions(_configFilePath);
    }

    public async Task<AppConfig> LoadConfigAsync(CancellationToken cancellationToken = default)
    {
        AppConfig config;
        if (!File.Exists(_configFilePath))
        {
            config = new AppConfig();
        }
        else
        {
            try
            {
                var encryptedBytes = await File.ReadAllBytesAsync(_configFilePath, cancellationToken);
                var decryptedBytes = DecryptData(encryptedBytes);
                config = JsonSerializer.Deserialize<AppConfig>(decryptedBytes) ?? new AppConfig();
            }
            catch
            {
                config = new AppConfig();
            }
        }

        ApplyBundledDefaults(config);
        return config;
    }

    private void ApplyBundledDefaults(AppConfig config)
    {
        if (config.Telemetry != null && !string.IsNullOrWhiteSpace(config.Telemetry.Endpoint))
        {
            return;
        }

        string? resolvedPath = null;
        if (!string.IsNullOrWhiteSpace(_defaultsFilePath) && File.Exists(_defaultsFilePath))
        {
            resolvedPath = _defaultsFilePath;
        }
        else if (string.IsNullOrWhiteSpace(_defaultsFilePath))
        {
            var encPath = Path.Combine(AppContext.BaseDirectory, BundledDefaultsCrypto.EncryptedFileName);
            if (File.Exists(encPath))
            {
                resolvedPath = encPath;
            }
        }

        if (resolvedPath == null || !File.Exists(resolvedPath))
        {
            return;
        }

        try
        {
            var bytes = File.ReadAllBytes(resolvedPath);
            var json = BundledDefaultsCrypto.Decrypt(bytes);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Telemetry", out var telProp) && telProp.ValueKind == JsonValueKind.Object)
            {
                config.Telemetry ??= new TelemetryConfig();

                if (telProp.TryGetProperty("Enabled", out var eProp) && (eProp.ValueKind == JsonValueKind.True || eProp.ValueKind == JsonValueKind.False))
                {
                    config.Telemetry.Enabled = eProp.GetBoolean();
                }
                if (telProp.TryGetProperty("Endpoint", out var epProp) && epProp.ValueKind == JsonValueKind.String)
                {
                    config.Telemetry.Endpoint = epProp.GetString() ?? string.Empty;
                }
                if (telProp.TryGetProperty("Headers", out var hProp) && hProp.ValueKind == JsonValueKind.String)
                {
                    config.Telemetry.Headers = hProp.GetString() ?? string.Empty;
                }
                if (telProp.TryGetProperty("ServiceName", out var sProp) && sProp.ValueKind == JsonValueKind.String)
                {
                    config.Telemetry.ServiceName = sProp.GetString() ?? "vsn-ai-rate-tracker";
                }
                if (telProp.TryGetProperty("Protocol", out var pProp) && pProp.ValueKind == JsonValueKind.String)
                {
                    config.Telemetry.Protocol = pProp.GetString() ?? "HttpProtobuf";
                }
            }
        }
        catch
        {
            // Silently ignore corrupted or unreadable defaults
        }
    }

    private static void ApplyUnixSecurePermissions(string path)
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Silently ignore if file system does not support Unix file permissions
            }
        }
    }
}

