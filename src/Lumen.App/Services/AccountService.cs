using Lumen.Core.Models;
using Lumen.Providers.Xtream;

namespace Lumen.App.Services;

/// <summary>
/// Fetches the current Xtream account's live status (connections, expiry, limits) for display.
/// Re-authenticates on demand — the same one-shot call timeshift already uses — so the connection
/// count is real rather than a stale cache. Returns null for non-Xtream profiles; throws on a
/// network/auth failure so callers can distinguish "not applicable" from "couldn't reach the panel".
/// </summary>
public sealed class AccountService
{
    private readonly IXtreamClientFactory _xtreamFactory;
    private readonly ISessionService _session;

    public AccountService(IXtreamClientFactory xtreamFactory, ISessionService session)
    {
        _xtreamFactory = xtreamFactory;
        _session = session;
    }

    /// <summary>Null when the profile isn't Xtream or has no stored credentials.</summary>
    public async Task<AccountInfo?> GetAccountInfoAsync(Profile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Kind != ProfileKind.Xtream || _session.GetXtreamCredentials(profile) is not { } credentials)
        {
            return null;
        }

        var client = _xtreamFactory.Create(credentials);
        var auth = await client.AuthenticateAsync(cancellationToken);
        return XtreamMapper.ToAccountInfo(auth);
    }
}
