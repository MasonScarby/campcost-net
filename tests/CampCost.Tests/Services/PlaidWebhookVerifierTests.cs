using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CampCost.Infrastructure.Services;
using FluentAssertions;
using Going.Plaid.Entity;
using Microsoft.Extensions.Logging.Abstractions;

namespace CampCost.Tests.Services;

/// <summary>
/// Tests the Plaid webhook signature verifier against locally generated ES256 keys.
/// The Plaid key-fetch is substituted via a test subclass, so these tests exercise the
/// full verification pipeline: alg pinning, kid lookup, key expiry, ES256 signature,
/// iat freshness, and the request_body_sha256 body binding.
/// </summary>
public class PlaidWebhookVerifierTests : IDisposable
{
    private const string Kid = "test-kid";
    private const string Body = """{"webhook_type":"TRANSACTIONS","item_id":"item-1"}""";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly TestVerifier _sut;

    public PlaidWebhookVerifierTests()
    {
        _sut = new TestVerifier(MakeJwk(_key, Kid));
    }

    public void Dispose() => _key.Dispose();

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed class TestVerifier : PlaidWebhookVerifier
    {
        private readonly Dictionary<string, JWKPublicKey> _keys;
        public int FetchCount { get; private set; }

        public TestVerifier(params JWKPublicKey[] keys)
            : base(client: null!, NullLogger<PlaidWebhookVerifier>.Instance)
            => _keys = keys.ToDictionary(k => k.Kid);

        protected override Task<JWKPublicKey?> FetchKeyAsync(string kid)
        {
            FetchCount++;
            return Task.FromResult(_keys.TryGetValue(kid, out var key) ? key : null);
        }
    }

    private static JWKPublicKey MakeJwk(ECDsa key, string kid, int? expiredAt = null)
    {
        var p = key.ExportParameters(includePrivateParameters: false);
        return new JWKPublicKey
        {
            Alg = "ES256",
            Crv = "P-256",
            Kty = "EC",
            Use = "sig",
            Kid = kid,
            X = Base64Url(p.Q.X!),
            Y = Base64Url(p.Q.Y!),
            CreatedAt = (int)DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds(),
            ExpiredAt = expiredAt
        };
    }

    private string SignJwt(string body, string kid = Kid, long? iat = null,
        string alg = "ES256", string? bodyHashOverride = null, ECDsa? signWith = null)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(
            new { alg, typ = "JWT", kid }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = iat ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            request_body_sha256 = bodyHashOverride ?? Sha256Hex(body)
        }));
        var signature = (signWith ?? _key).SignData(
            Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256);
        return header + "." + payload + "." + Base64Url(signature);
    }

    private static string Sha256Hex(string body) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Accepts_a_correctly_signed_webhook()
    {
        (await _sut.VerifyAsync(Body, SignJwt(Body))).Should().BeTrue();
    }

    [Fact]
    public async Task Rejects_when_body_does_not_match_signed_hash()
    {
        var jwt = SignJwt(Body);
        var tamperedBody = Body.Replace("item-1", "item-2");

        (await _sut.VerifyAsync(tamperedBody, jwt)).Should().BeFalse();
    }

    [Fact]
    public async Task Rejects_signature_from_a_different_key()
    {
        using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = SignJwt(Body, signWith: attackerKey);

        (await _sut.VerifyAsync(Body, forged)).Should().BeFalse();
    }

    [Theory]
    [InlineData("none")]
    [InlineData("HS256")]
    public async Task Rejects_any_algorithm_other_than_ES256(string alg)
    {
        (await _sut.VerifyAsync(Body, SignJwt(Body, alg: alg))).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("a.b")]
    [InlineData("!!!.###.$$$")]
    public async Task Rejects_garbage_tokens(string jwt)
    {
        (await _sut.VerifyAsync(Body, jwt)).Should().BeFalse();
    }

    [Fact]
    public async Task Rejects_unknown_kid()
    {
        (await _sut.VerifyAsync(Body, SignJwt(Body, kid: "unknown-kid"))).Should().BeFalse();
    }

    [Fact]
    public async Task Rejects_stale_iat()
    {
        var staleIat = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();

        (await _sut.VerifyAsync(Body, SignJwt(Body, iat: staleIat))).Should().BeFalse();
    }

    [Fact]
    public async Task Rejects_expired_verification_key()
    {
        var expired = (int)DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var sut = new TestVerifier(MakeJwk(_key, Kid, expiredAt: expired));

        (await sut.VerifyAsync(Body, SignJwt(Body))).Should().BeFalse();
    }

    [Fact]
    public async Task Caches_the_verification_key_across_calls()
    {
        await _sut.VerifyAsync(Body, SignJwt(Body));
        await _sut.VerifyAsync(Body, SignJwt(Body));

        _sut.FetchCount.Should().Be(1);
    }
}
