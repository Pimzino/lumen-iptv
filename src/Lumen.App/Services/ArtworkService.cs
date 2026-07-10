using System.Collections.Concurrent;
using Lumen.Core;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Lumen.Providers.Artwork;
using Serilog;

namespace Lumen.App.Services;

/// <summary>
/// Fills artwork gaps from external metadata services: TMDB when the user supplied a key,
/// keyless iTunes (movies) / TVMaze (series) otherwise. On by default. Every lookup —
/// including "found nothing" — is cached in SQLite so a title is resolved online once; a
/// bounded semaphore keeps grid enrichment from stampeding the services. Purely cosmetic:
/// failures are logged at debug and surface as "no artwork", never as errors.
/// </summary>
public sealed class ArtworkService
{
    /// <summary>Settings key (profile 0): "1"/"0", on by default.</summary>
    public const string EnabledKey = "artwork_online";

    /// <summary>Settings key (profile 0): the user's TMDB v3 key or v4 read token.</summary>
    public const string TmdbKeyKey = "artwork_tmdb_key";

    private static readonly TimeSpan NegativeTtl = TimeSpan.FromDays(7);

    /// <summary>Poster probes must never hold a page hostage to a slow host.</summary>
    private static readonly TimeSpan PosterProbeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>How long one successful download vouches for a host's other posters.</summary>
    private static readonly TimeSpan HostAliveTtl = TimeSpan.FromMinutes(10);

    private readonly IReadOnlyList<IArtworkProvider> _providers;
    private readonly IArtworkCacheRepository _cache;
    private readonly ISettingsRepository _settings;
    private readonly IEpgRepository _epg;
    private readonly IImageCache _images;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _networkGate = new(2);
    private readonly ConcurrentDictionary<(ContentKind Kind, string Key, int Year), Task<ArtworkResult?>> _inFlight = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _aliveHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _loadGate = new(1);

    private volatile bool _loaded;
    private volatile bool _enabled = true;
    private volatile string? _tmdbKey;

    public ArtworkService(
        IEnumerable<IArtworkProvider> providers,
        IArtworkCacheRepository cache,
        ISettingsRepository settings,
        IEpgRepository epg,
        IImageCache images,
        IClock clock)
    {
        _providers = providers.ToList();
        _cache = cache;
        _settings = settings;
        _epg = epg;
        _images = images;
        _clock = clock;
    }

    /// <summary>Effective switch: user setting AND not a hermetic diagnostic run.</summary>
    public bool IsEnabled => _enabled && !App.IsDiagnosticRun;

    /// <summary>Applies a settings change immediately (persistence is the caller's job).</summary>
    public void Configure(bool enabled, string? tmdbKey)
    {
        var newKey = string.IsNullOrWhiteSpace(tmdbKey) ? null : tmdbKey.Trim();
        var keyChanged = newKey is not null && newKey != _tmdbKey;

        _enabled = enabled;
        _tmdbKey = newKey;
        _loaded = true;

        // A new TMDB credential deserves a fresh look at everything the keyless sources
        // couldn't find — flush the negative entries so those titles retry immediately.
        if (keyChanged)
        {
            _ = _cache.ClearNegativeAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Resolves the poster a card should display. The provider's own URL wins when it is
    /// well-formed and its host serves images; panel junk (php-serialized fragments,
    /// relative paths) and posters on dead hosts fall back to external artwork. Null means
    /// no poster exists anywhere — callers keep their monogram.
    /// </summary>
    /// <param name="probeExactUrl">
    /// Download this exact URL rather than trusting a recent success from the same host.
    /// Detail pages use it for their hero image (which the view is about to render anyway);
    /// grids leave it off so a healthy host costs one probe per page, not one per card.
    /// </param>
    public async Task<string?> ResolvePosterAsync(
        ContentKind kind,
        string? providerPosterUrl,
        string rawName,
        int? knownYear,
        bool probeExactUrl,
        CancellationToken cancellationToken)
    {
        var usable = WebUrl.IsHttp(providerPosterUrl);

        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (!IsEnabled)
        {
            // No fallback available, so probing would only duplicate the view's own fetch.
            return usable ? providerPosterUrl : null;
        }

        if (usable)
        {
            var authority = new Uri(providerPosterUrl!).Authority;
            if (!probeExactUrl &&
                _aliveHosts.TryGetValue(authority, out var aliveAt) &&
                _clock.UtcNow - aliveAt < HostAliveTtl)
            {
                return providerPosterUrl;
            }

            if (await ProbeAsync(providerPosterUrl!, cancellationToken).ConfigureAwait(false))
            {
                _aliveHosts[authority] = _clock.UtcNow;
                return providerPosterUrl;
            }
        }

        var art = await GetArtworkAsync(kind, rawName, knownYear, cancellationToken).ConfigureAwait(false);
        return art?.PosterUrl;
    }

    /// <summary>True when the image downloaded (or was already cached) within the probe budget.</summary>
    private async Task<bool> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(PosterProbeTimeout);
        try
        {
            return await _images.GetLocalPathAsync(url, probeCts.Token).ConfigureAwait(false) is not null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false; // budget elapsed — the abandoned download still lands in the cache
        }
    }

    /// <summary>
    /// Resolves poster/backdrop urls for a catalog item whose provider art is missing.
    /// Null when disabled, offline, or no confident match exists.
    /// </summary>
    public async Task<ArtworkResult?> GetArtworkAsync(
        ContentKind kind, string rawName, int? knownYear, CancellationToken cancellationToken)
    {
        if (kind is not (ContentKind.Movie or ContentKind.Series) || string.IsNullOrWhiteSpace(rawName))
        {
            return null;
        }

        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (!IsEnabled)
        {
            return null;
        }

        var clean = TitleCleaner.Clean(rawName);
        if (clean.Title.Length == 0)
        {
            return null;
        }

        var titleKey = NameNormalizer.Normalize(clean.Title);
        if (titleKey.Length == 0)
        {
            return null;
        }

        var year = knownYear ?? clean.Year ?? 0;
        var cacheKey = (kind, titleKey, year);

        var cached = await _cache.GetAsync(kind, titleKey, year, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            if (cached.PosterUrl is not null || cached.BackdropUrl is not null)
            {
                return new ArtworkResult(cached.PosterUrl, cached.BackdropUrl, cached.Provider ?? "cache");
            }

            var age = _clock.UtcNow - DateTimeOffset.FromUnixTimeSeconds(cached.ResolvedUtc);
            if (age < NegativeTtl)
            {
                return null; // fresh negative entry — don't hammer the services
            }
        }

        // Coalesce concurrent requests for the same title (grids ask in bursts).
        var task = _inFlight.GetOrAdd(cacheKey, _ => ResolveAsync(kind, clean.Title, year));
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(cacheKey, out Task<ArtworkResult?>? _);
        }
    }

    private async Task<ArtworkResult?> ResolveAsync(ContentKind kind, string title, int year)
    {
        // Detached from the caller's token: the first navigation away must not abort a lookup
        // that several views may be waiting on (and that is about to be cached anyway).
        await _networkGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Series lookups always fetch the backdrop too — it lands in the same cache row,
            // so the detail page never needs a second online pass.
            var query = new ArtworkQuery(
                IsSeries: kind == ContentKind.Series,
                Title: title,
                Year: year == 0 ? null : year,
                WantBackdrop: true,
                TmdbApiKey: _tmdbKey);

            var sawCleanMiss = false;
            foreach (var provider in _providers)
            {
                if (!provider.CanServe(query))
                {
                    continue;
                }

                try
                {
                    var result = await provider.FindAsync(query, CancellationToken.None).ConfigureAwait(false);
                    if (result is not null && (result.PosterUrl is not null || result.BackdropUrl is not null))
                    {
                        await StoreAsync(kind, title, year, result).ConfigureAwait(false);
                        return result;
                    }

                    sawCleanMiss = true; // the service answered: it has nothing for this title
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Debug(ex, "Artwork lookup via {Provider} failed for {Title}", provider.Name, title);
                }
            }

            if (sawCleanMiss)
            {
                await StoreAsync(kind, title, year, result: null).ConfigureAwait(false);
            }

            return null;
        }
        finally
        {
            _networkGate.Release();
        }
    }

    private async Task StoreAsync(ContentKind kind, string title, int year, ArtworkResult? result)
    {
        try
        {
            await _cache.UpsertAsync(
                new ArtworkCacheEntry
                {
                    Kind = kind,
                    TitleKey = NameNormalizer.Normalize(title),
                    Year = year,
                    PosterUrl = result?.PosterUrl,
                    BackdropUrl = result?.BackdropUrl,
                    Provider = result?.Provider,
                    ResolvedUtc = _clock.UtcNow.ToUnixTimeSeconds(),
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Artwork cache write failed for {Title}", title);
        }
    }

    /// <summary>
    /// Channel-logo fallbacks from the imported guide: XMLTV feeds often carry channel icons
    /// the playlist lacks. Returns channelId → icon url for mapped channels that have one.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, string>> GetChannelLogoFallbacksAsync(
        long profileId, CancellationToken cancellationToken)
    {
        try
        {
            var mappings = await _epg.GetMappingsAsync(profileId, cancellationToken).ConfigureAwait(false);
            if (mappings.Count == 0)
            {
                return System.Collections.Immutable.ImmutableDictionary<long, string>.Empty;
            }

            var icons = (await _epg.GetEpgChannelsAsync(profileId, cancellationToken).ConfigureAwait(false))
                .Where(c => !string.IsNullOrWhiteSpace(c.IconUrl))
                .GroupBy(c => c.XmltvId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().IconUrl!, StringComparer.Ordinal);

            var result = new Dictionary<long, string>(mappings.Count);
            foreach (var mapping in mappings)
            {
                if (icons.TryGetValue(mapping.XmltvId, out var icon))
                {
                    result[mapping.ChannelId] = icon;
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Channel logo fallback lookup failed");
            return System.Collections.Immutable.ImmutableDictionary<long, string>.Empty;
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            var enabledRaw = await _settings.GetAsync(0, EnabledKey, cancellationToken).ConfigureAwait(false);
            _enabled = enabledRaw != "0";
            _tmdbKey = await _settings.GetAsync(0, TmdbKeyKey, cancellationToken).ConfigureAwait(false);
            _loaded = true;
        }
        finally
        {
            _loadGate.Release();
        }
    }
}
