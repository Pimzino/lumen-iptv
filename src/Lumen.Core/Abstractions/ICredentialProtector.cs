namespace Lumen.Core.Abstractions;

/// <summary>
/// Protects account secrets at rest. Implementations must never persist plaintext;
/// the returned blob is what gets stored.
/// </summary>
public interface ICredentialProtector
{
    /// <summary>Encrypts a secret for the current user.</summary>
    byte[] Protect(string secret);

    /// <summary>Decrypts a blob produced by <see cref="Protect"/>.</summary>
    string Unprotect(byte[] blob);
}
