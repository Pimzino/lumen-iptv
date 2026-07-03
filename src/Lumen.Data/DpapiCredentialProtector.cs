using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Lumen.Core.Abstractions;

namespace Lumen.Data;

/// <summary>
/// <see cref="ICredentialProtector"/> backed by Windows DPAPI, scoped to the current user.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialProtector : ICredentialProtector
{
    private static readonly byte[] Entropy = "Lumen.Credentials.v1"u8.ToArray();

    public byte[] Protect(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
    }

    public string Unprotect(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser));
    }
}
