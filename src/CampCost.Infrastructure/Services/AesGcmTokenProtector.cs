using System.Security.Cryptography;
using CampCost.Core.Interfaces;
using Microsoft.Extensions.Configuration;

namespace CampCost.Infrastructure.Services;

/// <summary>
/// AES-256-GCM authenticated encryption for secrets at rest (Plaid access tokens).
///
/// Stored format: <c>"v1:" + Base64(nonce[12] || tag[16] || ciphertext)</c>.
/// GCM gives confidentiality AND integrity — a tampered ciphertext fails the tag check on
/// decrypt (throws) rather than returning garbage. A fresh random nonce per encryption means
/// the same token encrypts to a different value every time (so ciphertext isn't a stable key).
///
/// The 256-bit key comes from configuration key <c>Encryption:TokenKey</c> (Base64 of 32 bytes),
/// supplied via env var / user-secrets — never committed, never in the database. This is why a
/// DB compromise alone cannot decrypt tokens. Rotate by generating a new key (note: existing
/// ciphertext must be re-encrypted on rotation; tokens can also simply be re-linked).
///
/// Registered as a SINGLETON: it is stateless and the EF model caches the value-converter
/// expression that captures this instance, so the captured reference must stay valid for the
/// process lifetime.
/// </summary>
public sealed class AesGcmTokenProtector : ITokenProtector
{
    private const string Prefix = "v1:";
    private const int NonceSize = 12; // 96-bit nonce — the GCM standard
    private const int TagSize = 16;   // 128-bit auth tag
    private const int KeySize = 32;   // 256-bit key

    private readonly byte[] _key;

    public AesGcmTokenProtector(IConfiguration config)
    {
        var b64 = config["Encryption:TokenKey"];
        if (string.IsNullOrWhiteSpace(b64))
            throw new InvalidOperationException(
                "Encryption:TokenKey is not configured. Set it to a Base64-encoded 32-byte key " +
                "via user-secrets (dev) or an environment variable (prod). Generate one with: " +
                "openssl rand -base64 32");

        try
        {
            _key = Convert.FromBase64String(b64);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Encryption:TokenKey must be valid Base64.");
        }

        if (_key.Length != KeySize)
            throw new InvalidOperationException(
                $"Encryption:TokenKey must decode to {KeySize} bytes (256-bit); got {_key.Length}.");
    }

    public string Protect(string plaintext)
    {
        // Preserve empty/unset tokens unchanged so "no connection" state round-trips.
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;

        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(_key, TagSize))
            aes.Encrypt(nonce, plainBytes, cipher, tag);

        // nonce || tag || ciphertext
        var blob = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, blob, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, blob, NonceSize + TagSize, cipher.Length);

        return Prefix + Convert.ToBase64String(blob);
    }

    public string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored))
            return stored;

        // Legacy plaintext (pre-encryption rows) has no scheme prefix — return as-is so
        // existing connections keep working; they get encrypted on the next write.
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;

        var blob = Convert.FromBase64String(stored[Prefix.Length..]);
        if (blob.Length < NonceSize + TagSize)
            throw new CryptographicException("Encrypted token is malformed (too short).");

        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipher = blob.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using (var aes = new AesGcm(_key, TagSize))
            aes.Decrypt(nonce, cipher, tag, plain); // throws CryptographicException on tamper

        return System.Text.Encoding.UTF8.GetString(plain);
    }
}
