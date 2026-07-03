using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Lumen.App.Resources;
using Lumen.App.Services;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
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
    private bool _suppress;

    public ChannelMappingRow(Channel channel, string? currentXmltvId, Action<ChannelMappingRow, string?> onChanged)
    {
        Channel = channel;
        _onChanged = onChanged;
        _selectedXmltvId = currentXmltvId;
    }

    public Channel Channel { get; }

    public string Name => Channel.Name;

    [ObservableProperty]
    private string? _selectedXmltvId;

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
    private readonly IDialogService _dialogs;
    private readonly IToastService _toasts;
    private readonly IMessenger _messenger;

    private bool _loaded;

    public SettingsViewModel(
        ISessionService session,
        IProfileRepository profiles,
        IEpgRepository epgRepository,
        IEpgSyncService epgSync,
        ICatalogSyncService catalogSync,
        ICatalogRepository catalog,
        IImageCache imageCache,
        ISettingsRepository settings,
        IDialogService dialogs,
        IToastService toasts,
        IMessenger messenger)
    {
        _session = session;
        _profiles = profiles;
        _epgRepository = epgRepository;
        _epgSync = epgSync;
        _catalogSync = catalogSync;
        _catalog = catalog;
        _imageCache = imageCache;
        _settings = settings;
        _dialogs = dialogs;
        _toasts = toasts;
        _messenger = messenger;
    }

    public ObservableCollection<ProfileEntry> Profiles { get; } = [];

    public ObservableCollection<ChannelMappingRow> UnmappedChannels { get; } = [];

    public ObservableCollection<EpgChannelOption> EpgChannelOptions { get; } = [];

    /// <summary>True during the initial page load only — manual refreshes keep the page visible.</summary>
    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _mappingSummary = string.Empty;

    [ObservableProperty]
    private bool _hasUnmappedChannels;

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
    private string _cacheSummary = string.Empty;

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

    public void OnNavigatedFrom()
    {
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

        var stats = await _imageCache.GetStatsAsync(cancellationToken);
        CacheSummary = Strings.Format(Strings.Settings_ImageCacheFormat, FormatBytes(stats.TotalBytes));

        await ReloadMappingAsync(cancellationToken);
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

        EpgChannelOptions.Clear();
        EpgChannelOptions.Add(new EpgChannelOption(null, Strings.Settings_MappingNone));
        foreach (var epg in epgChannels.OrderBy(e => e.DisplayName ?? e.XmltvId, StringComparer.OrdinalIgnoreCase))
        {
            EpgChannelOptions.Add(new EpgChannelOption(epg.XmltvId, epg.DisplayName ?? epg.XmltvId));
        }

        // Surface channels whose guide is unmatched (or unset), the ones worth a manual fix.
        UnmappedChannels.Clear();
        foreach (var channel in channels)
        {
            if (!mappings.ContainsKey(channel.Id))
            {
                UnmappedChannels.Add(new ChannelMappingRow(channel, null, OnMappingChanged));
            }
        }

        HasUnmappedChannels = UnmappedChannels.Count > 0;
        MappingSummary = UnmappedChannels.Count == 0
            ? Strings.Settings_MappingAllMatched
            : Strings.Format(Strings.Settings_MappingUnmatchedFormat, UnmappedChannels.Count);
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

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => string.Create(
            CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024 * 1024):0.#} GB"),
        >= 1024L * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
        >= 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes} B"),
    };
}
