using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VSNAiRateTracker.Services;

public static class BundledDefaultsCrypto
{
    private const byte PayloadVersion = 0x01;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    public const string EncryptedFileName = "defaults.enc";
    public const string PlaintextFileName = "defaults.json";

    // Application seed for bundled defaults encryption
    private static readonly byte[] EncryptionKey = SHA256.HashData(
        Encoding.UTF8.GetBytes("VSNAiRateTracker-Bundled-Defaults-AES256-GCM-v1"));

    public static byte[] Encrypt(string plaintextJson)
    {
        ArgumentNullException.ThrowIfNull(plaintextJson);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintextJson);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var ciphertext = new byte[plaintextBytes.Length];

        using (var aesGcm = new AesGcm(EncryptionKey, TagSize))
        {
            aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        }

        var payload = new byte[1 + NonceSize + TagSize + ciphertext.Length];
        payload[0] = PayloadVersion;
        Buffer.BlockCopy(nonce, 0, payload, 1, NonceSize);
        Buffer.BlockCopy(tag, 0, payload, 1 + NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, payload, 1 + NonceSize + TagSize, ciphertext.Length);

        return payload;
    }

    public static string Decrypt(byte[] encryptedPayload)
    {
        ArgumentNullException.ThrowIfNull(encryptedPayload);

        const int minSize = 1 + NonceSize + TagSize;
        if (encryptedPayload.Length < minSize)
        {
            throw new CryptographicException("The encrypted bundled defaults payload is truncated or invalid.");
        }

        var version = encryptedPayload[0];
        if (version != PayloadVersion)
        {
            throw new CryptographicException($"Unsupported bundled defaults payload version: {version}");
        }

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var ciphertextLength = encryptedPayload.Length - minSize;
        var ciphertext = new byte[ciphertextLength];
        var plaintext = new byte[ciphertextLength];

        Buffer.BlockCopy(encryptedPayload, 1, nonce, 0, NonceSize);
        Buffer.BlockCopy(encryptedPayload, 1 + NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(encryptedPayload, minSize, ciphertext, 0, ciphertextLength);

        using (var aesGcm = new AesGcm(EncryptionKey, TagSize))
        {
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return Encoding.UTF8.GetString(plaintext);
    }

    public static void EncryptFile(string inputJsonPath, string outputEncryptedPath)
    {
        ArgumentNullException.ThrowIfNull(inputJsonPath);
        ArgumentNullException.ThrowIfNull(outputEncryptedPath);

        var json = File.ReadAllText(inputJsonPath, Encoding.UTF8);
        var encryptedBytes = Encrypt(json);
        File.WriteAllBytes(outputEncryptedPath, encryptedBytes);
    }
}

