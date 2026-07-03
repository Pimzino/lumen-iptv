using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using Lumen.Data;

namespace Lumen.Core.Tests.Data;

[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialProtectorTests
{
    private readonly DpapiCredentialProtector _protector = new();

    [Fact]
    public void Protect_ThenUnprotect_RoundtripsSecret()
    {
        const string secret = "p@ssw0rd-ünïcödé-秘密";

        var blob = _protector.Protect(secret);

        _protector.Unprotect(blob).Should().Be(secret);
    }

    [Fact]
    public void Protect_DoesNotEmbedPlaintext()
    {
        const string secret = "super-secret-password";

        var blob = _protector.Protect(secret);

        Encoding.UTF8.GetString(blob).Should().NotContain(secret);
        blob.Should().NotEqual(Encoding.UTF8.GetBytes(secret));
    }

    [Fact]
    public void Protect_ProducesDistinctBlobsPerCall()
    {
        const string secret = "same-input";

        var first = _protector.Protect(secret);
        var second = _protector.Protect(secret);

        // DPAPI adds a random nonce; equality would indicate a broken implementation.
        first.Should().NotEqual(second);
    }
}
