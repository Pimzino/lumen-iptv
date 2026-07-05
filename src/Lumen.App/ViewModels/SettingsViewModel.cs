using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Lumen.App.Resources;
using Lumen.App.Services;
using Lumen.App.Services.Trakt;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Lumen.Providers.Trakt;
using Serilog;

namespace Lumen.App.ViewModels;

/// <summary>A profile row on the settings page.</summary>
public sealed partial class ProfileEntry : ObservableObject
{
    public ProfileEntry(Profile profile, bool isActive)
    {
        Profile = profile;
        IsActive = isActive;
    }

    public Profile Profile { get; }

    public string Name => Profile.Name;

    public string KindLabel => Profile.Kind == ProfileKind.Xtream ? "Xtream" : "M3U";

    public string AvatarColor => Profile.AvatarColor ?? "#4C8DFF";

    public string Monogram =>
        string.IsNullOrEmpty(Profile.Name) ? "?" : Profile.Name[..1].ToUpperInvariant();

    [ObservableProperty]
    private bool _isActive;
}

/// <summary>An EPG-channel choice offered for manual mapping.</summary>
public sealed record EpgChannelOption(string? XmltvId, string Display);

/// <summary>A playlist channel that has no (or a manual) guide mapping.</summary>
public sealed partial class ChannelMappingRow : ObservableObject
{
    private readonly Action<ChannelMappingRow, string?> _onChanged;
    private readonly IReadOnlyList<EpgChannelOption> _allOptions;
    private bool _suppress;

    public ChannelMappingRow(
        Channel channel,
        string? currentXmltvId,
        IReadOnlyList<EpgChannelOption> options,
        Action<ChannelMappingRow, string?> onChanged)
    {
        Channel = channel;
        _onChanged = onChanged;
        _allOptions = options;
        _options = options;
        _selectedXmltvId = currentXmltvId;
    }

    public Channel Channel { get; }

    public string Name => Channel.Name;

    [ObservableProperty]
    private string? _selectedXmltvId;

    /// <summary>The dropdown's (possibly filtered) guide-channel choices.</summary>
    [ObservableProperty]
    private IReadOnlyList<EpgChannelOption> _options;

    [ObservableProperty]
    private string _optionsFilter = string.Empty;

    internal void SetWithoutNotify(string? xmltvId)
    {
        _suppress = true;
        SelectedXmltvId = xmltvId;
        _suppress = false;
    }

    partial void OnSelectedXmltvIdChanged(string? value)
    {
        if (!_suppress)
        {
            _onChanged(this, value);
        }
    }

    partial void OnOptionsFilterChanged(string value)
    {
        var filter = value.Trim();
        if (filter.Length == 0)
        {
            Options = _allOptions;
            return;
        }

        var matches = new List<EpgChannelOption>();
        foreach (var option in _allOptions)
        {
            // The "(no guide)" sentinel and the current selection are pinned: dropping the
            // selected option from ItemsSource would clear SelectedValue through the two-way
            // binding and erase the stored mapping mid-keystroke.
            if (option.XmltvId is null
                || option.XmltvId == SelectedXmltvId
                || option.Display.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(option);
            }
        }

        Options = matches;
    }
}

/// <summary>Settings: profile management, EPG refresh, channel mapping, playback, storage, about.</summary>
public sealed partial class SettingsViewModel : ObservableObject, INavigationAware
{
    private const string EpgIntervalKey = "epg_interval_hours";
    private const string HardwareAccelerationKey = "hardware_acceleration";

    private readonly ISessionService _session;
    private readonly IProfileRepository _profiles;
    private readonly IEpgRepository _epgRepository;
    private readonly IEpgSyncService _epgSync;
    private readonly ICatalogSyncService _catalogSync;
    private readonly ICatalogRepository _catalog;
    private readonly IImageCache _imageCache;
    private readonly ISettingsRepository _settings;
    private readonly IArtworkCacheRepository _artworkCache;
    private readonly ArtworkService _artworkService;
    private readonly IDialogService _dialogs;
    private readonly IToastService _toasts;
    private readonly IMessenger _messenger;
    private readonly TraktService _trakt;
    private readonly TraktAuthStore _traktStore;
    private readonly TraktSyncService _traktSync;
    private readonly AccountService _accounts;
    private readonly IClock _clock;
    private readonly System.Windows.Threading.DispatcherTimer _mappingFilterDebounce;

    private bool _loaded;
    private List<ChannelMappingRow> _allUnmapped = [];
    private CancellationTokenSource? _traktConnectCts;

    public SettingsViewModel(
        ISessionService session,
        IProfileRepository profiles,
        IEpgRepository epgRepository,
        IEpgSyncService epgSync,
        ICatalogSyncService catalogSync,
        ICatalogRepository catalog,
        IImageCache imageCache,
        ISettingsRepository settings,
        IArtworkCacheRepository artworkCache,
        ArtworkService artworkService,
        IDialogService dialogs,
        IToastService toasts,
        IMessenger messenger,
        TraktService trakt,
        TraktAuthStore traktStore,
        TraktSyncService traktSync,
        AccountService accounts,
        IClock clock)
    {
        _session = session;
        _profiles = profiles;
        _epgRepository = epgRepository;
        _epgSync = epgSync;
        _catalogSync = catalogSync;
        _catalog = catalog;
        _imageCache = imageCache;
        _settings = settings;
        _artworkCache = artworkCache;
        _artworkService = artworkService;
        _dialogs = dialogs;
        _toasts = toasts;
        _messenger = messenger;
        _trakt = trakt;
        _traktStore = traktStore;
        _traktSync = traktSync;
        _accounts = accounts;
        _clock = clock;

        _mappingFilterDebounce = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _mappingFilterDebounce.Tick += (_, _) =>
        {
            _mappingFilterDebounce.Stop();
            ApplyMappingFilter();
        };
    }

    public ObservableCollection<ProfileEntry> Profiles { get; } = [];

    /// <summary>Replaced wholesale on filter changes — thousands of rows, one collection reset.</summary>
    [ObservableProperty]
    private IReadOnlyList<ChannelMappingRow> _unmappedChannels = [];

    /// <summary>True during the initial page load only — manual refreshes keep the page visible.</summary>
    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _mappingSummary = string.Empty;

    [ObservableProperty]
    private bool _hasUnmappedChannels;

    [ObservableProperty]
    private string _mappingFilterText = string.Empty;

    /// <summary>True when a mapping filter is active and hides every row.</summary>
    [ObservableProperty]
    private bool _mappingFilterHasNoMatches;

    [ObservableProperty]
    private string _epgCounts = string.Empty;

    [ObservableProperty]
    private string? _epgProgress;

    [ObservableProperty]
    private bool _isEpgRefreshing;

    [ObservableProperty]
    private bool _isCatalogRefreshing;

    [ObservableProperty]
    private int _epgIntervalIndex;

    [ObservableProperty]
    private bool _preferHls;

    [ObservableProperty]
    private bool _hardwareAcceleration = true;

    [ObservableProperty]
    private string _streamUserAgent = string.Empty;

    [ObservableProperty]
    private bool _artworkOnline = true;

    [ObservableProperty]
    private string _artworkTmdbKey = string.Empty;

    [ObservableProperty]
    private string _cacheSummary = string.Empty;

    // ------------------------------------------------------------------ Account (Xtream)

    /// <summary>Gates the whole Account card — hidden for M3U profiles.</summary>
    [ObservableProperty]
    private bool _isCurrentProfileXtream;

    [ObservableProperty]
    private bool _isAccountLoading;

    [ObservableProperty]
    private bool _accountLoadFailed;

    /// <summary>True once a snapshot has loaded — drives the details block's visibility.</summary>
    [ObservableProperty]
    private bool _accountReady;

    [ObservableProperty]
    private string _accountStatus = string.Empty;

    /// <summary>Active and unexpired — colors the status green vs. red.</summary>
    [ObservableProperty]
    private bool _accountStatusIsHealthy;

    [ObservableProperty]
    private string _expiryText = string.Empty;

    [ObservableProperty]
    private bool _showExpirySoonWarning;

    [ObservableProperty]
    private string _connectionsText = string.Empty;

    [ObservableProperty]
    private string? _connectionsAvailableText;

    /// <summary>Every connection is in use — surfaced as the playback-failure hint.</summary>
    [ObservableProperty]
    private bool _showConnectionsWarning;

    [ObservableProperty]
    private string _trialText = string.Empty;

    [ObservableProperty]
    private string _createdText = string.Empty;

    [ObservableProperty]
    private string _allowedFormatsText = string.Empty;

    [ObservableProperty]
    private string _serverTimeText = string.Empty;

    // ------------------------------------------------------------------ Trakt

    [ObservableProperty]
    private string _traktClientId = string.Empty;

    [ObservableProperty]
    private string _traktClientSecret = string.Empty;

    [ObservableProperty]
    private bool _isTraktConnected;

    [ObservableProperty]
    private string? _traktConnectedLabel;

    /// <summary>"Enter code XXXX at trakt.tv/activate" while device sign-in is pending.</summary>
    [ObservableProperty]
    private string? _traktConnectCode;

    [ObservableProperty]
    private string? _traktConnectStatus;

    [ObservableProperty]
    private bool _isTraktConnecting;

    [ObservableProperty]
    private bool _traktScrobbleEnabled = true;

    [ObservableProperty]
    private bool _traktSyncEnabled = true;

    [ObservableProperty]
    private bool _isTraktSyncing;

    [ObservableProperty]
    private string _traktLastSyncLabel = string.Empty;

    public string VersionLine => Strings.Format(
        Strings.Settings_VersionFormat,
        typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "dev");

    public async Task OnNavigatedToAsync(object? parameter, CancellationToken cancellationToken)
    {
        IsLoading = true;
        try
        {
            await ReloadAsync(cancellationToken);
            _loaded = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void OnNavigatedFrom() => _mappingFilterDebounce.Stop();

    partial void OnMappingFilterTextChanged(string value)
    {
        _mappingFilterDebounce.Stop();
        if (string.IsNullOrWhiteSpace(value))
        {
            ApplyMappingFilter();
        }
        else
        {
            _mappingFilterDebounce.Start();
        }
    }

    private void ApplyMappingFilter()
    {
        var filter = MappingFilterText.Trim();
        UnmappedChannels = filter.Length == 0
            ? _allUnmapped
            : _allUnmapped.Where(r => r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        MappingFilterHasNoMatches = UnmappedChannels.Count == 0 && _allUnmapped.Count > 0;
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        Profiles.Clear();
        foreach (var profile in await _profiles.GetAllAsync(cancellationToken))
        {
            Profiles.Add(new ProfileEntry(profile, profile.Id == _session.CurrentProfile?.Id));
        }

        var current = _session.CurrentProfile;
        if (current is not null)
        {
            var counts = await _epgRepository.GetCountsAsync(current.Id, cancellationToken);
            EpgCounts = Strings.Format(Strings.Settings_EpgCountsFormat, counts.Channels, counts.Programmes);
            PreferHls = current.PreferHls;
            StreamUserAgent = current.StreamUserAgent ?? string.Empty;
        }

        IsCurrentProfileXtream = current?.Kind == ProfileKind.Xtream;

        var intervalRaw = await _settings.GetAsync(0, EpgIntervalKey, cancellationToken);
        EpgIntervalIndex = intervalRaw switch
        {
            "6" => 0,
            "12" => 1,
            "24" => 2,
            "0" => 3,
            _ => 1,
        };

        var hw = await _settings.GetAsync(0, HardwareAccelerationKey, cancellationToken);
        HardwareAcceleration = hw != "0";

        var artwork = await _settings.GetAsync(0, ArtworkService.EnabledKey, cancellationToken);
        ArtworkOnline = artwork != "0";
        ArtworkTmdbKey = await _settings.GetAsync(0, ArtworkService.TmdbKeyKey, cancellationToken) ?? string.Empty;

        var stats = await _imageCache.GetStatsAsync(cancellationToken);
        CacheSummary = Strings.Format(Strings.Settings_ImageCacheFormat, FormatBytes(stats.TotalBytes));

        await ReloadTraktAsync(cancellationToken);
        await ReloadMappingAsync(cancellationToken);

        // Account details load in the background: a slow or unreachable panel must never hold up
        // the rest of the page behind the loading skeleton. The card shows its own spinner.
        if (current is { Kind: ProfileKind.Xtream })
        {
            _ = LoadAccountAsync(current, cancellationToken);
        }
    }

    private async Task LoadAccountAsync(Profile profile, CancellationToken cancellationToken)
    {
        IsAccountLoading = true;
        AccountLoadFailed = false;
        AccountReady = false;
        try
        {
            var info = await _accounts.GetAccountInfoAsync(profile, cancellationToken);
            if (info is null)
            {
                // Credentials vanished mid-session; nothing to show.
                IsCurrentProfileXtream = false;
                return;
            }

            ApplyAccountInfo(info);
            AccountReady = true;
        }
        catch (OperationCanceledException)
        {
            // Navigated away before the panel answered — drop it silently.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load Xtream account details");
            AccountLoadFailed = true;
        }
        finally
        {
            IsAccountLoading = false;
        }
    }

    private void ApplyAccountInfo(AccountInfo info)
    {
        var now = _clock.UtcNow;

        AccountStatus = string.IsNullOrWhiteSpace(info.Status) ? "—" : info.Status;
        var expired = info.ExpiresAt is { } end && end < now;
        AccountStatusIsHealthy = info.IsActive && !expired;

        if (info.ExpiresAt is { } expiry)
        {
            var date = expiry.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
            if (expired)
            {
                ExpiryText = Strings.Format(Strings.Settings_AccountExpiryExpiredFormat, date);
                ShowExpirySoonWarning = false;
            }
            else
            {
                var days = (int)Math.Ceiling((expiry - now).TotalDays);
                ExpiryText = days <= 1
                    ? Strings.Format(Strings.Settings_AccountExpiryTomorrowFormat, date)
                    : Strings.Format(Strings.Settings_AccountExpiryInDaysFormat, date, days);
                ShowExpirySoonWarning = days <= 7;
            }
        }
        else
        {
            ExpiryText = Strings.Settings_AccountExpiryNever;
            ShowExpirySoonWarning = false;
        }

        if (info.MaxConnections is { } max && max > 0)
        {
            ConnectionsText = Strings.Format(
                Strings.Settings_AccountConnectionsFormat, info.ActiveConnections ?? 0, max);
            ConnectionsAvailableText = info.ConnectionsAvailable is { } available
                ? Strings.Format(Strings.Settings_AccountConnectionsAvailableFormat, available)
                : null;
        }
        else
        {
            ConnectionsText = info.ActiveConnections is { } active
                ? Strings.Format(Strings.Settings_AccountConnectionsInUseOnlyFormat, active)
                : "—";
            ConnectionsAvailableText = null;
        }

        ShowConnectionsWarning = info.AllConnectionsInUse;

        TrialText = info.IsTrial ? Strings.Settings_AccountTrialYes : Strings.Settings_AccountTrialNo;

        CreatedText = info.CreatedAt is { } created
            ? created.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture)
            : "—";

        AllowedFormatsText = info.AllowedFormats.Count > 0
            ? string.Join(", ", info.AllowedFormats)
            : "—";

        ServerTimeText = (info.ServerTimeNow, info.ServerTimezone) switch
        {
            ({ } time, { } zone) => Strings.Format(Strings.Settings_AccountServerTimeFormat, time, zone),
            ({ } time, null) => time,
            (null, { } zone) => zone,
            _ => "—",
        };
    }

    [RelayCommand]
    private async Task RefreshAccountAsync()
    {
        if (_session.CurrentProfile is { Kind: ProfileKind.Xtream } profile)
        {
            await LoadAccountAsync(profile, CancellationToken.None);
        }
    }

    private async Task ReloadTraktAsync(CancellationToken cancellationToken)
    {
        await _trakt.InitializeAsync(cancellationToken);
        var (clientId, clientSecret) = await _traktStore.GetAppCredentialsRawAsync(cancellationToken);
        TraktClientId = clientId ?? string.Empty;
        TraktClientSecret = clientSecret ?? string.Empty;
        IsTraktConnected = _trakt.IsConnected;
        TraktConnectedLabel = _trakt.Username is { } user
            ? Strings.Format(Strings.Settings_TraktConnectedFormat, user)
            : null;

        var scrobble = await _settings.GetAsync(0, TraktSettingsKeys.ScrobbleEnabled, cancellationToken);
        TraktScrobbleEnabled = !string.Equals(scrobble, "false", StringComparison.OrdinalIgnoreCase);
        var sync = await _settings.GetAsync(0, TraktSettingsKeys.SyncEnabled, cancellationToken);
        TraktSyncEnabled = !string.Equals(sync, "false", StringComparison.OrdinalIgnoreCase);

        await RefreshTraktLastSyncAsync(cancellationToken);
    }

    private async Task RefreshTraktLastSyncAsync(CancellationToken cancellationToken)
    {
        var lastSync = await _traktSync.GetLastSyncUtcAsync(cancellationToken);
        TraktLastSyncLabel = lastSync > 0
            ? Strings.Format(
                Strings.Settings_TraktLastSyncFormat,
                DateTimeOffset.FromUnixTimeSeconds(lastSync).ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
            : Strings.Settings_TraktNeverSynced;
    }

    private async Task ReloadMappingAsync(CancellationToken cancellationToken)
    {
        var profile = _session.CurrentProfile;
        if (profile is null)
        {
            return;
        }

        var channels = await _catalog.GetChannelsAsync(profile.Id, null, cancellationToken);
        var epgChannels = await _epgRepository.GetEpgChannelsAsync(profile.Id, cancellationToken);
        var mappings = (await _epgRepository.GetMappingsAsync(profile.Id, cancellationToken))
            .ToDictionary(m => m.ChannelId, m => m.XmltvId);

        // One shared master list; each row filters its own dropdown view over it.
        var options = new List<EpgChannelOption> { new(null, Strings.Settings_MappingNone) };
        foreach (var epg in epgChannels.OrderBy(e => e.DisplayName ?? e.XmltvId, StringComparer.OrdinalIgnoreCase))
        {
            options.Add(new EpgChannelOption(epg.XmltvId, epg.DisplayName ?? epg.XmltvId));
        }

        // Surface channels whose guide is unmatched (or unset), the ones worth a manual fix.
        _allUnmapped = [];
        foreach (var channel in channels)
        {
            if (!mappings.ContainsKey(channel.Id))
            {
                _allUnmapped.Add(new ChannelMappingRow(channel, null, options, OnMappingChanged));
            }
        }

        ApplyMappingFilter();
        HasUnmappedChannels = _allUnmapped.Count > 0;
        MappingSummary = _allUnmapped.Count == 0
            ? Strings.Settings_MappingAllMatched
            : Strings.Format(Strings.Settings_MappingUnmatchedFormat, _allUnmapped.Count);
    }

    private void OnMappingChanged(ChannelMappingRow row, string? xmltvId)
    {
        _ = _epgRepository.SetManualMappingAsync(row.Channel.Id, xmltvId, CancellationToken.None);
    }

    [RelayCommand]
    private async Task SwitchProfileAsync(ProfileEntry? entry)
    {
        if (entry is null || entry.IsActive)
        {
            return;
        }

        await _session.SwitchProfileAsync(entry.Profile.Id, CancellationToken.None);
    }

    [RelayCommand]
    private async Task RemoveProfileAsync(ProfileEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            Strings.Settings_RemoveProfileTitle,
            Strings.Format(Strings.Settings_RemoveProfileBodyFormat, entry.Name),
            Strings.Common_Remove,
            destructive: true);
        if (!confirmed)
        {
            return;
        }

        await _session.RemoveProfileAsync(entry.Profile.Id, CancellationToken.None);
        if (_session.CurrentProfile is null)
        {
            _messenger.Send(new OnboardingRequiredMessage());
            return;
        }

        await ReloadAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task EditProfileAsync(ProfileEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (await _dialogs.EditProfileAsync(entry.Profile.Id))
        {
            await ReloadAsync(CancellationToken.None);
        }
    }

    [RelayCommand]
    private void AddProfile() => _messenger.Send(new AddProfileRequestedMessage());

    [RelayCommand]
    private async Task RefreshEpgAsync()
    {
        var profile = _session.CurrentProfile;
        if (profile is null || IsEpgRefreshing)
        {
            return;
        }

        IsEpgRefreshing = true;
        EpgProgress = Strings.Common_Loading;
        try
        {
            var progress = new Progress<EpgImportProgress>(p =>
                EpgProgress = Strings.Format(Strings.Settings_EpgProgressFormat, p.Programmes));
            var result = await Task.Run(() => _epgSync.RefreshAsync(profile, progress, CancellationToken.None));
            _toasts.Show(
                Strings.Format(Strings.Toast_EpgRefreshedFormat, result.Programmes), ToastSeverity.Success);
            var counts = await _epgRepository.GetCountsAsync(profile.Id, CancellationToken.None);
            EpgCounts = Strings.Format(Strings.Settings_EpgCountsFormat, counts.Channels, counts.Programmes);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Manual EPG refresh failed");
            _toasts.Show(Strings.Toast_EpgFailed, ToastSeverity.Error);
        }
        finally
        {
            IsEpgRefreshing = false;
            EpgProgress = null;
        }
    }

    [RelayCommand]
    private async Task RefreshCatalogAsync()
    {
        var profile = _session.CurrentProfile;
        if (profile is null || IsCatalogRefreshing)
        {
            return;
        }

        IsCatalogRefreshing = true;
        try
        {
            var result = await Task.Run(() => _catalogSync.SyncAsync(profile, CancellationToken.None));
            _toasts.Show(
                Strings.Format(Strings.Toast_CatalogRefreshedFormat, result.Channels), ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Manual catalog refresh failed");
            _toasts.Show(Strings.Format(Strings.Toast_SyncFailedFormat, ex.Message), ToastSeverity.Error);
        }
        finally
        {
            IsCatalogRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task ClearImageCacheAsync()
    {
        await _imageCache.ClearAsync(CancellationToken.None);

        // Clear resolved artwork lookups too — this is the escape hatch for a wrong poster.
        await _artworkCache.ClearAsync(CancellationToken.None);

        var stats = await _imageCache.GetStatsAsync(CancellationToken.None);
        CacheSummary = Strings.Format(Strings.Settings_ImageCacheFormat, FormatBytes(stats.TotalBytes));
        _toasts.Show(Strings.Toast_CacheCleared, ToastSeverity.Success);
    }

    partial void OnEpgIntervalIndexChanged(int value)
    {
        if (!_loaded)
        {
            return;
        }

        var hours = value switch
        {
            0 => "6",
            1 => "12",
            2 => "24",
            _ => "0",
        };
        _ = _settings.SetAsync(0, EpgIntervalKey, hours, CancellationToken.None);
    }

    partial void OnPreferHlsChanged(bool value)
    {
        if (!_loaded || _session.CurrentProfile is not { } profile || profile.PreferHls == value)
        {
            return;
        }

        profile.PreferHls = value;
        _ = _profiles.UpdateAsync(profile, CancellationToken.None);
    }

    partial void OnStreamUserAgentChanged(string value)
    {
        if (!_loaded || _session.CurrentProfile is not { } profile)
        {
            return;
        }

        // Empty means "fall back to the app default"; store null rather than a blank string.
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (profile.StreamUserAgent == normalized)
        {
            return;
        }

        profile.StreamUserAgent = normalized;
        _ = _profiles.UpdateAsync(profile, CancellationToken.None);
    }

    partial void OnHardwareAccelerationChanged(bool value)
    {
        if (_loaded)
        {
            _ = _settings.SetAsync(0, HardwareAccelerationKey, value ? "1" : "0", CancellationToken.None);
        }
    }

    partial void OnArtworkOnlineChanged(bool value)
    {
        if (_loaded)
        {
            _ = _settings.SetAsync(0, ArtworkService.EnabledKey, value ? "1" : "0", CancellationToken.None);
            _artworkService.Configure(value, ArtworkTmdbKey);
        }
    }

    partial void OnArtworkTmdbKeyChanged(string value)
    {
        if (_loaded)
        {
            _ = _settings.SetAsync(0, ArtworkService.TmdbKeyKey, value.Trim(), CancellationToken.None);
            _artworkService.Configure(ArtworkOnline, value);
        }
    }

    partial void OnTraktClientIdChanged(string value)
    {
        if (_loaded)
        {
            _ = _traktStore.SetAppCredentialsAsync(value, TraktClientSecret, CancellationToken.None);
        }
    }

    partial void OnTraktClientSecretChanged(string value)
    {
        if (_loaded)
        {
            _ = _traktStore.SetAppCredentialsAsync(TraktClientId, value, CancellationToken.None);
        }
    }

    partial void OnTraktScrobbleEnabledChanged(bool value)
    {
        if (_loaded)
        {
            _ = _settings.SetAsync(0, TraktSettingsKeys.ScrobbleEnabled, value ? "true" : "false", CancellationToken.None);
        }
    }

    partial void OnTraktSyncEnabledChanged(bool value)
    {
        if (_loaded)
        {
            _ = _settings.SetAsync(0, TraktSettingsKeys.SyncEnabled, value ? "true" : "false", CancellationToken.None);
        }
    }

    /// <summary>Starts device sign-in and waits for the user to approve the code on trakt.tv.</summary>
    [RelayCommand]
    private async Task ConnectTraktAsync()
    {
        if (IsTraktConnecting)
        {
            return;
        }

        var app = await _traktStore.GetAppCredentialsAsync(CancellationToken.None);
        if (app is null)
        {
            _toasts.Show(Strings.Toast_TraktNeedCredentials, ToastSeverity.Error);
            return;
        }

        IsTraktConnecting = true;
        TraktConnectStatus = Strings.Common_Loading;
        _traktConnectCts = new CancellationTokenSource();
        try
        {
            var code = await _trakt.StartDeviceAuthAsync(app, _traktConnectCts.Token);
            TraktConnectCode = Strings.Format(Strings.Settings_TraktCodeFormat, code.UserCode);
            TraktConnectStatus = Strings.Settings_TraktWaiting;

            var connected = await _trakt.WaitForApprovalAsync(app, code, _traktConnectCts.Token);
            if (connected)
            {
                IsTraktConnected = true;
                TraktConnectedLabel = _trakt.Username is { } user
                    ? Strings.Format(Strings.Settings_TraktConnectedFormat, user)
                    : Strings.Settings_Trakt;
                _toasts.Show(Strings.Toast_TraktConnected, ToastSeverity.Success);

                // First sync in the background so watched ticks appear without further clicks.
                _ = Task.Run(() => _traktSync.SyncNowAsync(force: true, CancellationToken.None));
            }
            else
            {
                _toasts.Show(Strings.Toast_TraktConnectFailed, ToastSeverity.Error);
            }
        }
        catch (OperationCanceledException)
        {
            // user cancelled the pairing
        }
        catch (TraktApiException ex)
        {
            Log.Warning(ex, "Trakt connect failed");
            _toasts.Show(ex.Message, ToastSeverity.Error);
        }
        finally
        {
            IsTraktConnecting = false;
            TraktConnectCode = null;
            TraktConnectStatus = null;
            _traktConnectCts?.Dispose();
            _traktConnectCts = null;
        }
    }

    [RelayCommand]
    private void CancelTraktConnect() => _traktConnectCts?.Cancel();

    [RelayCommand]
    private void OpenTraktActivate()
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("https://trakt.tv/activate") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Opening trakt.tv/activate failed");
        }
    }

    [RelayCommand]
    private async Task DisconnectTraktAsync()
    {
        _traktConnectCts?.Cancel();
        await _trakt.DisconnectAsync(CancellationToken.None);
        IsTraktConnected = false;
        TraktConnectedLabel = null;
        _toasts.Show(Strings.Toast_TraktDisconnected, ToastSeverity.Success);
    }

    [RelayCommand]
    private async Task TraktSyncNowAsync()
    {
        if (IsTraktSyncing)
        {
            return;
        }

        IsTraktSyncing = true;
        try
        {
            var ok = await Task.Run(() => _traktSync.SyncNowAsync(force: true, CancellationToken.None));
            await RefreshTraktLastSyncAsync(CancellationToken.None);
            _toasts.Show(
                ok ? Strings.Toast_TraktSyncDone : Strings.Toast_TraktSyncFailed,
                ok ? ToastSeverity.Success : ToastSeverity.Error);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Manual Trakt sync failed");
            _toasts.Show(Strings.Toast_TraktSyncFailed, ToastSeverity.Error);
        }
        finally
        {
            IsTraktSyncing = false;
        }
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => string.Create(
            CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024 * 1024):0.#} GB"),
        >= 1024L * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
        >= 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes} B"),
    };
}
