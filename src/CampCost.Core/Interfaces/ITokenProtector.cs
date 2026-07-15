namespace CampCost.Core.Interfaces;

/// <summary>
/// Encrypts sensitive secrets (Plaid access tokens) at rest. The encryption key lives in
/// configuration/secrets, NOT in the database — so a database dump alone cannot decrypt.
/// Applied transparently as an EF value converter on <c>PlaidConnection.AccessToken</c>.
/// </summary>
public interface ITokenProtector
{
    /// <summary>Encrypts a plaintext secret for storage. Null/empty passes through unchanged.</summary>
    string Protect(string plaintext);

    /// <summary>
    /// Decrypts a stored secret. Values without the current scheme prefix are treated as
    /// legacy plaintext and returned as-is (graceful migration); tampered ciphertext throws.
    /// </summary>
    string Unprotect(string stored);
}
