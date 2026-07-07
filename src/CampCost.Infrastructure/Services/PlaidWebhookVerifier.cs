using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CampCost.Core.Interfaces;
using Going.Plaid;
using Going.Plaid.Entity;
using Going.Plaid.WebhookVerificationKey;
using Microsoft.Extensions.Logging;

namespace CampCost.Infrastructure.Services;

/// <summary>
/// Verifies the `Plaid-Verification` header on incoming webhooks, per Plaid's docs:
/// https://plaid.com/docs/api/webhooks/webhook-verification/
///
/// The header is a JWT signed with ES256. Verification steps:
///   1. Header must declare alg=ES256 and carry a `kid`.
///   2. The public key for that `kid` is fetched from Plaid `/webhook_verification_key/get`
///      (authenticated with our client_id/secret) and cached in-memory — a forger cannot
///      get their key served from Plaid's endpoint under our credentials.
///   3. The ES256 signature must verify against that key.
///   4. `iat` must be within the last 5 minutes (replay protection, per Plaid docs).
///   5. The `request_body_sha256` claim must equal the SHA-256 of the raw request body
///      (compared in constant time).
/// Any failure at any step → false. Deliberately no distinction is surfaced to callers.
/// </summary>
public class PlaidWebhookVerifier : IPlaidWebhookVerifier
{
    private static readonly TimeSpan MaxTokenAge = TimeSpan.FromMinutes(5);

    private readonly PlaidClient _client;
    private readonly ILogger<PlaidWebhookVerifier> _logger;

    // Plaid rotates verification keys rarely; an unknown kid triggers a fetch and the
    // result is cached for the process lifetime. Expired keys are rejected on use.
    private readonly ConcurrentDictionary<string, JWKPublicKey> _keyCache = new();

    public PlaidWebhookVerifier(PlaidClient client, ILogger<PlaidWebhookVerifier> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<bool> VerifyAsync(string rawBody, string verificationJwt)
    {
        try
        {
            var parts = verificationJwt.Split('.');
            if (parts.Length != 3) return Fail("JWT does not have three segments");

            using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));

            // Pin the algorithm BEFORE doing anything with the key — never trust `alg`
            // values like "none" or an HMAC downgrade.
            if (!header.RootElement.TryGetProperty("alg", out var alg) || alg.GetString() != "ES256")
                return Fail("JWT alg is not ES256");

            if (!header.RootElement.TryGetProperty("kid", out var kidProp) ||
                kidProp.GetString() is not { Length: > 0 } kid)
                return Fail("JWT has no kid");

            var key = await GetKeyAsync(kid);
            if (key is null) return Fail($"No verification key for kid {kid}");

            if (key.ExpiredAt is { } expiredAt &&
                DateTimeOffset.FromUnixTimeSeconds(expiredAt) < DateTimeOffset.UtcNow)
                return Fail($"Verification key {kid} is expired");

            if (key.Kty != "EC" || key.Crv != "P-256")
                return Fail($"Verification key {kid} is not an EC P-256 key");

            // ES256 JWT signatures are raw r||s (IEEE P1363) — ECDsa.VerifyData's default.
            using var ecdsa = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = Base64UrlDecode(key.X),
                    Y = Base64UrlDecode(key.Y)
                }
            });

            var signedData = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            var signature = Base64UrlDecode(parts[2]);
            if (!ecdsa.VerifyData(signedData, signature, HashAlgorithmName.SHA256))
                return Fail("JWT signature is invalid");

            using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));

            // Replay protection: Plaid instructs rejecting tokens older than 5 minutes.
            if (!payload.RootElement.TryGetProperty("iat", out var iatProp) ||
                !iatProp.TryGetInt64(out var iat))
                return Fail("JWT has no iat claim");

            var issuedAt = DateTimeOffset.FromUnixTimeSeconds(iat);
            var now = DateTimeOffset.UtcNow;
            if (issuedAt < now - MaxTokenAge || issuedAt > now + TimeSpan.FromMinutes(1))
                return Fail("JWT iat is outside the accepted window");

            // Finally: the signed body hash must match the body we actually received.
            if (!payload.RootElement.TryGetProperty("request_body_sha256", out var hashProp) ||
                hashProp.GetString() is not { Length: > 0 } claimedHash)
                return Fail("JWT has no request_body_sha256 claim");

            var actualHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(rawBody)));

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actualHash.ToLowerInvariant()),
                    Encoding.ASCII.GetBytes(claimedHash.ToLowerInvariant())))
                return Fail("Request body hash does not match request_body_sha256 claim");

            return true;
        }
        catch (Exception ex)
        {
            // Malformed base64/JSON/key material etc. — reject, never throw to the caller.
            _logger.LogWarning(ex, "Plaid webhook verification threw — rejecting webhook");
            return false;
        }
    }

    private async Task<JWKPublicKey?> GetKeyAsync(string kid)
    {
        if (_keyCache.TryGetValue(kid, out var cached))
            return cached;

        var key = await FetchKeyAsync(kid);
        if (key is not null)
            _keyCache[kid] = key;
        return key;
    }

    /// <summary>
    /// Fetches the verification key for <paramref name="kid"/> from Plaid.
    /// Virtual so tests can substitute a locally generated key.
    /// </summary>
    protected virtual async Task<JWKPublicKey?> FetchKeyAsync(string kid)
    {
        var response = await _client.WebhookVerificationKeyGetAsync(
            new WebhookVerificationKeyGetRequest { KeyId = kid });

        if (!response.IsSuccessStatusCode || response.Key is null)
        {
            _logger.LogWarning(
                "Plaid /webhook_verification_key/get failed for kid {Kid}: {Error}",
                kid, response.Error?.ErrorMessage ?? response.StatusCode.ToString());
            return null;
        }
        return response.Key;
    }

    private bool Fail(string reason)
    {
        _logger.LogWarning("Rejected Plaid webhook: {Reason}", reason);
        return false;
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
