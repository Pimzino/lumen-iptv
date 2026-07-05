using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumen.App.Resources;
using Lumen.App.Services;
using Serilog;

namespace Lumen.App.ViewModels;

/// <summary>
/// Shell-owned view model for the update indicator and dialog. Mirrors <see cref="UpdateService"/>
/// state onto observable properties (marshaling to the UI thread) and exposes the user actions:
/// open the dialog, install, skip, open the release page, and check now.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly UpdateService _updates;
    private readonly IDialogService _dialogs;
    private readonly IToastService _toasts;

    private string? _notifiedVersion;

    public UpdateViewModel(UpdateService updates, IDialogService dialogs, IToastService toasts)
    {
        _updates = updates;
        _dialogs = dialogs;
        _toasts = toasts;

        _updates.Changed += OnUpdatesChanged;
        Apply(_updates.Snapshot);
    }

    /// <summary>True while an update is available, downloading, or ready — drives the rail indicator.</summary>
    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private bool _isReadyToInstall;

    /// <summary>True for installer builds that can update in place; false steers to the release page.</summary>
    [ObservableProperty]
    private bool _canAutoUpdate;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _railTooltip = string.Empty;

    [ObservableProperty]
    private string _currentVersion = string.Empty;

    [ObservableProperty]
    private string _currentVersionLine = string.Empty;

    [ObservableProperty]
    private string? _availableVersion;

    [ObservableProperty]
    private string? _availableVersionLine;

    [ObservableProperty]
    private string? _releaseNotes;

    [ObservableProperty]
    private bool _hasReleaseNotes;

    private void OnUpdatesChanged(object? sender, EventArgs e)
    {
        var snapshot = _updates.Snapshot;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply(snapshot);
        }
        else
        {
            dispatcher.BeginInvoke(() => Apply(snapshot));
        }
    }

    private void Apply(UpdateSnapshot snapshot)
    {
        CurrentVersion = snapshot.CurrentVersion;
        CurrentVersionLine = Strings.Format(Strings.Update_CurrentFormat, snapshot.CurrentVersion);
        AvailableVersion = snapshot.AvailableVersion;
        CanAutoUpdate = snapshot.CanAutoUpdate;
        Percent = snapshot.Percent;
        IsDownloading = snapshot.Status == UpdateStatus.Downloading;
        IsReadyToInstall = snapshot.Status == UpdateStatus.ReadyToInstall;
        IsUpdateAvailable = snapshot.Status is UpdateStatus.Available or UpdateStatus.Downloading or UpdateStatus.ReadyToInstall;

        ReleaseNotes = snapshot.ReleaseNotes;
        HasReleaseNotes = !string.IsNullOrWhiteSpace(snapshot.ReleaseNotes);
        AvailableVersionLine = snapshot.AvailableVersion is { } v
            ? Strings.Format(Strings.Update_AvailableFormat, v)
            : null;

        StatusText = BuildStatusText(snapshot);
        RailTooltip = BuildRailTooltip(snapshot);
        InstallNowCommand.NotifyCanExecuteChanged();

        MaybeNotify(snapshot);
    }

    private static string BuildStatusText(UpdateSnapshot snapshot) => snapshot.Status switch
    {
        UpdateStatus.Checking => Strings.Update_StatusChecking,
        UpdateStatus.Downloading => Strings.Format(
            Strings.Update_StatusDownloadingFormat,
            (int)Math.Round(snapshot.Percent),
            SettingsViewModel.FormatBytes(snapshot.BytesReceived),
            SettingsViewModel.FormatBytes(snapshot.TotalBytes),
            SettingsViewModel.FormatBytes((long)snapshot.BytesPerSecond)),
        UpdateStatus.ReadyToInstall => Strings.Update_StatusReady,
        UpdateStatus.Available => Strings.Update_StatusAvailable,
        UpdateStatus.Failed => Strings.Update_StatusFailed,
        _ => string.Empty,
    };

    private static string BuildRailTooltip(UpdateSnapshot snapshot) => snapshot.Status switch
    {
        UpdateStatus.Downloading => Strings.Update_RailDownloadingTooltip,
        UpdateStatus.ReadyToInstall => Strings.Update_RailReadyTooltip,
        _ => Strings.Update_RailAvailableTooltip,
    };

    private void MaybeNotify(UpdateSnapshot snapshot)
    {
        if (snapshot.Status == UpdateStatus.Idle)
        {
            _notifiedVersion = null;
            return;
        }

        // Nudge once per version, when the update first becomes actionable.
        var actionable = snapshot.Status == UpdateStatus.ReadyToInstall
            || (snapshot.Status == UpdateStatus.Available && !snapshot.CanAutoUpdate);
        if (!actionable || snapshot.AvailableVersion is not { } version || version == _notifiedVersion)
        {
            return;
        }

        _notifiedVersion = version;
        var message = snapshot.Status == UpdateStatus.ReadyToInstall
            ? Strings.Format(Strings.Update_ToastReadyFormat, version)
            : Strings.Format(Strings.Update_ToastAvailableFormat, version);
        _toasts.Show(message);
    }

    [RelayCommand]
    private async Task OpenDialogAsync()
    {
        try
        {
            await _dialogs.ShowUpdateAsync(this);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to show the update dialog");
        }
    }

    private bool CanInstallNow() => IsReadyToInstall;

    [RelayCommand(CanExecute = nameof(CanInstallNow))]
    private void InstallNow()
    {
        switch (_updates.TryStartInstaller())
        {
            case InstallLaunchResult.Launched:
                Application.Current?.Shutdown();
                break;
            case InstallLaunchResult.Declined:
                _toasts.Show(Strings.Update_ToastPostponed);
                break;
            case InstallLaunchResult.Failed:
                _toasts.Show(Strings.Update_ToastFailed, ToastSeverity.Error);
                break;
            case InstallLaunchResult.NotReady:
            default:
                break;
        }
    }

    [RelayCommand]
    private async Task SkipVersionAsync()
    {
        await _updates.SkipCurrentVersionAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void OpenReleasePage() => _updates.OpenReleasePage();

    [RelayCommand]
    private async Task CheckNowAsync()
    {
        try
        {
            await _updates.CheckAsync(manual: true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Manual update check failed");
        }
    }

    /// <summary>Formats the unix-seconds last-check time for the Settings status line.</summary>
    public static string FormatLastChecked(long? lastCheckUtc) => lastCheckUtc is { } unix
        ? DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : "—";
}
