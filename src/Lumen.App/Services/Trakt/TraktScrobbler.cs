using System.ComponentModel;
using Lumen.App.Services.Playback;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Lumen.Providers.Trakt;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services.Trakt;

/// <summary>
/// Mirrors VOD playback to Trakt: start on play, pause on pause, stop with the final progress
/// when the item ends, is replaced, or the app closes (Trakt records a watched play at ≥80%).
/// Hosted so it exists without being injected anywhere; failures only ever log — playback
/// must never notice Trakt.
/// </summary>
public sealed class TraktScrobbler : IHostedService
{
    private readonly PlaybackService _playback;
    private readonly TraktAuthStore _store;
    private readonly TraktMatchService _matcher;
    private readonly ITraktClient _client;
    private readonly ISettingsRepository _settings;
    private readonly ISessionService _session;
    private readonly ICatalogRepository _catalog;
    private readonly ILogger<TraktScrobbler> _logger;

    // Sends are serialized so a fast pause/stop can't overtake its own start.
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private Session? _current;
    private double _progressPercent;

    public TraktScrobbler(
        PlaybackService playback,
        TraktAuthStore store,
        TraktMatchService matcher,
        ITraktClient client,
        ISettingsRepository settings,
        ISessionService session,
        ICatalogRepository catalog,
        ILogger<TraktScrobbler> logger)
    {
        _playback = playback;
        _store = store;
        _matcher = matcher;
        _client = client;
        _settings = settings;
        _session = session;
        _catalog = catalog;
        _logger = logger;
    }

    /// <summary>One tracked VOD play: the request plus its resolved Trakt identity (once known).</summary>
    private sealed class Session
    {
        public required VodPlayRequest Request { get; init; }

        public required long ProfileId { get; init; }

        public TraktScrobbleItem? Item { get; set; }

        public bool ResolveAttempted { get; set; }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _playback.PropertyChanged += OnPlaybackPropertyChanged;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _playback.PropertyChanged -= OnPlaybackPropertyChanged;

        // Final stop on app exit, inside the host's shutdown budget.
        var session = _current;
        _current = null;
        if (session is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await SendAsync(session, TraktScrobbleAction.Stop, _progressPercent, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutdown budget spent
            }
        }
    }

    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Raised on the dispatcher; do only cheap reads here and hand real work to the pool.
        switch (e.PropertyName)
        {
            case nameof(PlaybackService.PositionSeconds):
                if (_playback.IsVod && _playback.DurationSeconds > 0)
                {
                    _progressPercent = Math.Clamp(_playback.PositionSeconds / _playback.DurationSeconds * 100, 0, 100);
                }

                break;

            case nameof(PlaybackService.CurrentVod):
            {
                var next = _playback.CurrentVod;
                var nextProfile = _session.CurrentProfile?.Id;
                var previous = _current;
                var previousProgress = _progressPercent;
                _current = next is null || nextProfile is null
                    ? null
                    : new Session { Request = next, ProfileId = nextProfile.Value };
                _progressPercent = 0;
                if (previous is not null && !ReferenceEquals(previous.Request, next))
                {
                    _ = RunSafeAsync(() => SendAsync(previous, TraktScrobbleAction.Stop, previousProgress, CancellationToken.None));
                }

                break;
            }

            case nameof(PlaybackService.State):
            {
                var session = _current;
                if (session is null)
                {
                    break;
                }

                switch (_playback.State)
                {
                    case PlaybackState.Playing:
                        var startProgress = _progressPercent;
                        _ = RunSafeAsync(() => SendAsync(session, TraktScrobbleAction.Start, startProgress, CancellationToken.None));
                        break;
                    case PlaybackState.Paused:
                        var pauseProgress = _progressPercent;
                        _ = RunSafeAsync(() => SendAsync(session, TraktScrobbleAction.Pause, pauseProgress, CancellationToken.None));
                        break;
                    case PlaybackState.Idle or PlaybackState.Error:
                        // Natural end (or a VOD error) keeps CurrentVod set — the state change is
                        // the end-of-playback signal. Clear the session so a later Stop() (which
                        // nulls CurrentVod) doesn't double-send.
                        _current = null;
                        var stopProgress = _progressPercent;
                        _ = RunSafeAsync(() => SendAsync(session, TraktScrobbleAction.Stop, stopProgress, CancellationToken.None));
                        break;
                }

                break;
            }
        }
    }

    private async Task RunSafeAsync(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Trakt scrobble task failed");
        }
    }

    private async Task<bool> IsScrobbleEnabledAsync(CancellationToken cancellationToken)
    {
        var value = await _settings.GetAsync(0, TraktSettingsKeys.ScrobbleEnabled, cancellationToken).ConfigureAwait(false);
        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SendAsync(
        Session session, TraktScrobbleAction action, double progressPercent, CancellationToken cancellationToken)
    {
        if (!await IsScrobbleEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var access = await _store.GetValidAccessAsync(cancellationToken).ConfigureAwait(false);
        if (access is null)
        {
            return;
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var item = await EnsureResolvedAsync(session, cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                return;
            }

            var outcome = await _client.ScrobbleAsync(access, action, item, progressPercent, cancellationToken)
                .ConfigureAwait(false);
            switch (outcome)
            {
                case TraktScrobbleOutcome.Recorded or TraktScrobbleOutcome.Duplicate:
                    _logger.LogDebug(
                        "Trakt scrobble {Action} {Outcome} for {Title} at {Progress:0}%",
                        action, outcome, session.Request.Title, progressPercent);
                    break;
                case TraktScrobbleOutcome.Unauthorized:
                    _logger.LogWarning("Trakt rejected the session while scrobbling; reconnect in Settings");
                    break;
                default:
                    _logger.LogDebug("Trakt scrobble {Action} failed for {Title}", action, session.Request.Title);
                    break;
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Resolves the session's Trakt identity once; misses are remembered for the session.</summary>
    private async Task<TraktScrobbleItem?> EnsureResolvedAsync(Session session, CancellationToken cancellationToken)
    {
        if (session.ResolveAttempted)
        {
            return session.Item;
        }

        session.ResolveAttempted = true;
        var request = session.Request;
        var providerItemId = request.Kind == ContentKind.Series
            ? request.ItemKey[..Math.Max(0, request.ItemKey.IndexOf(':', StringComparison.Ordinal))]
            : request.ItemKey;
        if (providerItemId.Length == 0)
        {
            return null;
        }

        var vodItem = await _catalog.GetVodItemByProviderIdAsync(
            session.ProfileId, request.Kind, providerItemId, cancellationToken).ConfigureAwait(false);
        if (vodItem is null)
        {
            return null;
        }

        var match = await _matcher.ResolveAsync(
            session.ProfileId, vodItem, providerTmdbId: null, providerImdbId: null, allowSearch: true,
            cancellationToken).ConfigureAwait(false);
        if (match is null)
        {
            _logger.LogDebug("No Trakt match for {Title}; playback won't be scrobbled", request.Title);
            return null;
        }

        var ids = new TraktIds { Trakt = match.TraktId, Tmdb = match.TmdbId, Imdb = match.ImdbId };
        session.Item = request.Kind == ContentKind.Movie
            ? new TraktScrobbleItem(ids, null, null, null)
            : request.Season is { } season && request.EpisodeNumber is { } number
                ? new TraktScrobbleItem(null, ids, season, number)
                : null;
        return session.Item;
    }
}
