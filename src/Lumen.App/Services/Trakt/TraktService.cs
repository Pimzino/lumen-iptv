using CommunityToolkit.Mvvm.ComponentModel;
using Lumen.Core.Abstractions;
using Lumen.Providers.Trakt;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services.Trakt;

/// <summary>
/// Connection lifecycle for the app-global Trakt account: device-code sign-in, disconnect, and
/// observable connected/username state for the settings page.
/// </summary>
public sealed partial class TraktService : ObservableObject
{
    private readonly ITraktClient _client;
    private readonly TraktAuthStore _store;
    private readonly ITraktMatchRepository _matches;
    private readonly IClock _clock;
    private readonly ILogger<TraktService> _logger;
    private bool _initialized;

    public TraktService(
        ITraktClient client,
        TraktAuthStore store,
        ITraktMatchRepository matches,
        IClock clock,
        ILogger<TraktService> logger)
    {
        _client = client;
        _store = store;
        _matches = matches;
        _clock = clock;
        _logger = logger;
    }

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string? _username;

    /// <summary>Loads the persisted connection state once (safe to call repeatedly).</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        Username = await _store.GetUsernameAsync(cancellationToken);
        IsConnected = await _store.IsConnectedAsync(cancellationToken);
    }

    /// <summary>Starts device sign-in; the caller shows the returned user code + activate URL.</summary>
    public Task<TraktDeviceCodeResponse> StartDeviceAuthAsync(
        TraktAppCredentials app, CancellationToken cancellationToken) =>
        _client.StartDeviceAuthAsync(app, cancellationToken);

    /// <summary>
    /// Polls until the user approves the device code (or it expires/denies). On approval the
    /// tokens are stored, the username resolved, and stale negative matches flushed so the new
    /// account gets a fresh matching pass. Returns true when connected.
    /// </summary>
    public async Task<bool> WaitForApprovalAsync(
        TraktAppCredentials app, TraktDeviceCodeResponse code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(code);
        if (code.DeviceCode is null)
        {
            return false;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, code.Interval));
        var deadline = _clock.UtcNow.AddSeconds(Math.Max(60, code.ExpiresIn));
        while (_clock.UtcNow < deadline)
        {
            await Task.Delay(interval, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            TraktDeviceTokenResult result;
            try
            {
                result = await _client.PollDeviceTokenAsync(app, code.DeviceCode, cancellationToken);
            }
            catch (TraktApiException ex)
            {
                _logger.LogDebug(ex, "Device token poll failed transiently; retrying");
                continue;
            }

            switch (result.Status)
            {
                case TraktDeviceTokenStatus.Authorized when result.Tokens is not null:
                    await CompleteConnectAsync(app, result.Tokens, cancellationToken);
                    return true;
                case TraktDeviceTokenStatus.Pending:
                    continue;
                case TraktDeviceTokenStatus.SlowDown:
                    interval += TimeSpan.FromSeconds(1);
                    continue;
                default:
                    _logger.LogInformation("Trakt device sign-in ended without approval ({Status})", result.Status);
                    return false;
            }
        }

        return false;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _store.ClearTokensAsync(cancellationToken);
        IsConnected = false;
        Username = null;
    }

    private async Task CompleteConnectAsync(
        TraktAppCredentials app, TraktTokenResponse tokens, CancellationToken cancellationToken)
    {
        await _store.StoreTokensAsync(tokens, cancellationToken);

        try
        {
            var username = await _client.GetUsernameAsync(
                new TraktAccess(app.ClientId, tokens.AccessToken!), cancellationToken);
            if (!string.IsNullOrWhiteSpace(username))
            {
                await _store.SetUsernameAsync(username, cancellationToken);
                Username = username;
            }
        }
        catch (TraktApiException ex)
        {
            _logger.LogDebug(ex, "Could not resolve the Trakt username right after connect");
        }

        // A different account (or a first connect) deserves a fresh look at previous "no match" results.
        await _matches.ClearNegativeAsync(cancellationToken);
        IsConnected = true;
    }
}
