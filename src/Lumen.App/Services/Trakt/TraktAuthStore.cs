using Lumen.Core.Abstractions;
using Lumen.Providers.Trakt;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services.Trakt;

/// <summary>Settings keys for the app-global Trakt connection (all under profile 0).</summary>
public static class TraktSettingsKeys
{
    public const string ClientId = "trakt_client_id";

    /// <summary>DPAPI-protected, base64-encoded.</summary>
    public const string ClientSecret = "trakt_client_secret";

    /// <summary>DPAPI-protected, base64-encoded.</summary>
    public const string AccessToken = "trakt_access_token";

    /// <summary>DPAPI-protected, base64-encoded.</summary>
    public const string RefreshToken = "trakt_refresh_token";

    public const string TokenExpiresUtc = "trakt_token_expires_utc";
    public const string Username = "trakt_username";
    public const string ScrobbleEnabled = "trakt_scrobble_enabled";
    public const string SyncEnabled = "trakt_sync_enabled";
    public const string LastSyncUtc = "trakt_last_sync_utc";
    public const string LastActivities = "trakt_last_activities";
}

/// <summary>
/// The Trakt connection's credential store: the user's API app id/secret and OAuth tokens,
/// DPAPI-protected in the app-global settings row. Hands out a valid access token, refreshing
/// it single-flight when it nears expiry.
/// </summary>
public sealed class TraktAuthStore
{
    /// <summary>Refresh this far before the advertised expiry (tokens last days; margin is cheap).</summary>
    private const long RefreshMarginSeconds = 12 * 3600;

    private readonly ISettingsRepository _settings;
    private readonly ICredentialProtector _protector;
    private readonly ITraktClient _client;
    private readonly IClock _clock;
    private readonly ILogger<TraktAuthStore> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public TraktAuthStore(
        ISettingsRepository settings,
        ICredentialProtector protector,
        ITraktClient client,
        IClock clock,
        ILogger<TraktAuthStore> logger)
    {
        _settings = settings;
        _protector = protector;
        _client = client;
        _clock = clock;
        _logger = logger;
    }

    public async Task<TraktAppCredentials?> GetAppCredentialsAsync(CancellationToken cancellationToken)
    {
        var clientId = await _settings.GetAsync(0, TraktSettingsKeys.ClientId, cancellationToken).ConfigureAwait(false);
        var secret = await ReadProtectedAsync(TraktSettingsKeys.ClientSecret, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret)
            ? null
            : new TraktAppCredentials(clientId.Trim(), secret);
    }

    /// <summary>Both fields as stored (either may be null) — for prefilling the settings form.</summary>
    public async Task<(string? ClientId, string? ClientSecret)> GetAppCredentialsRawAsync(CancellationToken cancellationToken)
    {
        var clientId = await _settings.GetAsync(0, TraktSettingsKeys.ClientId, cancellationToken).ConfigureAwait(false);
        var secret = await ReadProtectedAsync(TraktSettingsKeys.ClientSecret, cancellationToken).ConfigureAwait(false);
        return (clientId, secret);
    }

    public async Task SetAppCredentialsAsync(string? clientId, string? clientSecret, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            await _settings.DeleteAsync(0, TraktSettingsKeys.ClientId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _settings.SetAsync(0, TraktSettingsKeys.ClientId, clientId.Trim(), cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            await _settings.DeleteAsync(0, TraktSettingsKeys.ClientSecret, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WriteProtectedAsync(TraktSettingsKeys.ClientSecret, clientSecret.Trim(), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StoreTokensAsync(TraktTokenResponse tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.AccessToken is null || tokens.RefreshToken is null)
        {
            throw new InvalidOperationException("Trakt token response was incomplete.");
        }

        var issuedAt = tokens.CreatedAt > 0 ? tokens.CreatedAt : _clock.UtcNow.ToUnixTimeSeconds();
        var expires = issuedAt + Math.Max(tokens.ExpiresIn, 3600);
        await WriteProtectedAsync(TraktSettingsKeys.AccessToken, tokens.AccessToken, cancellationToken).ConfigureAwait(false);
        await WriteProtectedAsync(TraktSettingsKeys.RefreshToken, tokens.RefreshToken, cancellationToken).ConfigureAwait(false);
        await _settings.SetAsync(
            0, TraktSettingsKeys.TokenExpiresUtc,
            expires.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops tokens and account identity; the API app id/secret stay for a reconnect.</summary>
    public async Task ClearTokensAsync(CancellationToken cancellationToken)
    {
        await _settings.DeleteAsync(0, TraktSettingsKeys.AccessToken, cancellationToken).ConfigureAwait(false);
        await _settings.DeleteAsync(0, TraktSettingsKeys.RefreshToken, cancellationToken).ConfigureAwait(false);
        await _settings.DeleteAsync(0, TraktSettingsKeys.TokenExpiresUtc, cancellationToken).ConfigureAwait(false);
        await _settings.DeleteAsync(0, TraktSettingsKeys.Username, cancellationToken).ConfigureAwait(false);
    }

    public Task<string?> GetUsernameAsync(CancellationToken cancellationToken) =>
        _settings.GetAsync(0, TraktSettingsKeys.Username, cancellationToken);

    public Task SetUsernameAsync(string username, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        return _settings.SetAsync(0, TraktSettingsKeys.Username, username, cancellationToken);
    }

    public async Task<bool> IsConnectedAsync(CancellationToken cancellationToken) =>
        await ReadProtectedAsync(TraktSettingsKeys.AccessToken, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// A ready-to-use access grant, refreshed when close to expiry. Null when not connected or
    /// the refresh grant was rejected (the user must reconnect in Settings).
    /// </summary>
    public async Task<TraktAccess?> GetValidAccessAsync(CancellationToken cancellationToken)
    {
        var access = await ReadAccessAsync(cancellationToken).ConfigureAwait(false);
        if (access is null)
        {
            return null;
        }

        var expires = await ReadExpiryAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow.ToUnixTimeSeconds();
        if (expires - now > RefreshMarginSeconds)
        {
            return access;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while we waited.
            expires = await ReadExpiryAsync(cancellationToken).ConfigureAwait(false);
            now = _clock.UtcNow.ToUnixTimeSeconds();
            if (expires - now > RefreshMarginSeconds)
            {
                return await ReadAccessAsync(cancellationToken).ConfigureAwait(false);
            }

            var app = await GetAppCredentialsAsync(cancellationToken).ConfigureAwait(false);
            var refreshToken = await ReadProtectedAsync(TraktSettingsKeys.RefreshToken, cancellationToken).ConfigureAwait(false);
            if (app is null || refreshToken is null)
            {
                // Can't refresh; the current token may still have life left.
                return expires > now ? access : null;
            }

            var fresh = await _client.RefreshTokenAsync(app, refreshToken, cancellationToken).ConfigureAwait(false);
            if (fresh?.AccessToken is null)
            {
                if (fresh is null && expires > now)
                {
                    return access; // transient refresh failure; ride out the remaining validity
                }

                _logger.LogWarning("Trakt session could not be renewed; disconnecting until the user reconnects");
                await ClearTokensAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            await StoreTokensAsync(fresh, cancellationToken).ConfigureAwait(false);
            return new TraktAccess(app.ClientId, fresh.AccessToken);
        }
        catch (TraktApiException ex)
        {
            _logger.LogDebug(ex, "Trakt token refresh failed transiently");
            var stillValid = await ReadExpiryAsync(cancellationToken).ConfigureAwait(false) > _clock.UtcNow.ToUnixTimeSeconds();
            return stillValid ? access : null;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<TraktAccess?> ReadAccessAsync(CancellationToken cancellationToken)
    {
        var clientId = await _settings.GetAsync(0, TraktSettingsKeys.ClientId, cancellationToken).ConfigureAwait(false);
        var token = await ReadProtectedAsync(TraktSettingsKeys.AccessToken, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(clientId) || token is null
            ? null
            : new TraktAccess(clientId.Trim(), token);
    }

    private async Task<long> ReadExpiryAsync(CancellationToken cancellationToken)
    {
        var raw = await _settings.GetAsync(0, TraktSettingsKeys.TokenExpiresUtc, cancellationToken).ConfigureAwait(false);
        return long.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private async Task<string?> ReadProtectedAsync(string key, CancellationToken cancellationToken)
    {
        var stored = await _settings.GetAsync(0, key, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(Convert.FromBase64String(stored));
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            _logger.LogWarning(ex, "Stored Trakt secret {Key} could not be decrypted; treating as absent", key);
            return null;
        }
    }

    private Task WriteProtectedAsync(string key, string secret, CancellationToken cancellationToken) =>
        _settings.SetAsync(0, key, Convert.ToBase64String(_protector.Protect(secret)), cancellationToken);
}
