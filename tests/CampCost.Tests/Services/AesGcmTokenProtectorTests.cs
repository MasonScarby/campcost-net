using CampCost.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using Xunit;

namespace CampCost.Tests.Services;

public class AesGcmTokenProtectorTests
{
    // A valid 32-byte (256-bit) key, Base64-encoded.
    private static readonly string ValidKey = Convert.ToBase64String(new byte[32]);

    private static AesGcmTokenProtector Make(string? key = null) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:TokenKey"] = key ?? ValidKey })
            .Build());

    [Fact]
    public void Protect_then_Unprotect_round_trips()
    {
        var p = Make();
        var secret = "access-sandbox-abc123-super-secret";
        p.Unprotect(p.Protect(secret)).Should().Be(secret);
    }

    [Fact]
    public void Protect_is_nondeterministic_but_both_decrypt_equal()
    {
        var p = Make();
        var secret = "token";
        var a = p.Protect(secret);
        var b = p.Protect(secret);
        a.Should().NotBe(b);                 // fresh random nonce each time
        p.Unprotect(a).Should().Be(secret);
        p.Unprotect(b).Should().Be(secret);
    }

    [Fact]
    public void Protected_value_is_prefixed_and_not_plaintext()
    {
        var p = Make();
        var enc = p.Protect("plaintextsecret");
        enc.Should().StartWith("v1:");
        enc.Should().NotContain("plaintextsecret");
    }

    [Fact]
    public void Unprotect_passes_through_legacy_plaintext()
    {
        // Rows written before encryption have no "v1:" prefix — must keep working.
        var p = Make();
        p.Unprotect("legacy-plaintext-token").Should().Be("legacy-plaintext-token");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_and_null_pass_through(string? value)
    {
        var p = Make();
        p.Protect(value!).Should().Be(value);
        p.Unprotect(value!).Should().Be(value);
    }

    [Fact]
    public void Tampered_ciphertext_throws()
    {
        var p = Make();
        var enc = p.Protect("secret");
        // Flip a character in the Base64 body — GCM auth tag must reject it.
        var body = enc[3..].ToCharArray();
        body[10] = body[10] == 'A' ? 'B' : 'A';
        var tampered = "v1:" + new string(body);
        Action act = () => p.Unprotect(tampered);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void A_different_key_cannot_decrypt()
    {
        var enc = Make(ValidKey).Protect("secret");
        var otherKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        Action act = () => Make(otherKey).Unprotect(enc);
        act.Should().Throw<CryptographicException>();
    }

    [Theory]
    [InlineData(null)]           // missing
    [InlineData("")]             // empty
    [InlineData("not-base64!!")] // invalid base64
    [InlineData("c2hvcnQ=")]     // valid base64 but wrong length (not 32 bytes)
    public void Constructor_rejects_bad_keys(string? key)
    {
        // Build config directly so an explicit null stays null (Make()'s default-arg fallback
        // would otherwise substitute a valid key).
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:TokenKey"] = key })
            .Build();
        Action act = () => new AesGcmTokenProtector(config);
        act.Should().Throw<InvalidOperationException>();
    }
}
