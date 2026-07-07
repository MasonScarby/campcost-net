namespace CampCost.Core.Interfaces;

/// <summary>
/// Verifies that an incoming Plaid webhook request is authentic.
/// Plaid signs every webhook with a JWT (ES256) in the `Plaid-Verification` header;
/// the JWT's `request_body_sha256` claim must match the SHA-256 of the raw body.
/// </summary>
public interface IPlaidWebhookVerifier
{
    /// <summary>
    /// Returns true only if the verification JWT is validly signed by a current
    /// Plaid webhook verification key AND its body hash matches <paramref name="rawBody"/>.
    /// Any failure (bad signature, unknown key, stale token, hash mismatch) returns false.
    /// </summary>
    Task<bool> VerifyAsync(string rawBody, string verificationJwt);
}
