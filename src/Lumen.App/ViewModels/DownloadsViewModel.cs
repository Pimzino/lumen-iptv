using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Lumen.App.Resources;
using Lumen.App.Services;
using Lumen.App.Services.Downloads;
using Lumen.App.Services.Playback;
using Lumen.Core;
using Lumen.Core.Abstractions;
using Serilog;

namespace Lumen.App.ViewModels;

/// <summary>
/// The Downloads page: the current profile's in-progress and completed downloads, with play,
/// pause/resume, retry, and remove actions. Binds to the shared <see cref="DownloadService"/>
/// collection so progress ticks update in place; status transitions re-bucket via messages.
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject, INavigationAware,
    IRecipient<DownloadStateChangedMessage>, IRecipient<DownloadRemovedMessage>
{
    private readonly DownloadService _downloads;
    private readonly ISessionService _session;
    private readonly PlaybackService _playback;
    private readonly IWatchHistoryRepository _watchHistory;
    private readonly IDialogService _dialogs;

    private long? _profileId;

    public DownloadsViewModel(
        DownloadService downloads,
        ISessionService session,
        PlaybackService playback,
        IWatchHistoryRepository watchHistory,
        IDialogService dialogs,
        IMessenger messenger)
    {
        _downloads = downloads;
        _session = session;
        _playback = playback;
        _watchHistory = watchHistory;
        _dialogs = dialogs;
        messenger.RegisterAll(this);
    }

    /// <summary>Queued, downloading, paused, or failed items.</summary>
    public ObservableCollection<DownloadRow> InProgress { get; } = [];

    /// <summary>Completed items (movies and episodes) ready to play offline.</summary>
    public ObservableCollection<DownloadRow> Completed { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _hasAny;

    [ObservableProperty]
    private bool _hasInProgress;

    [ObservableProperty]
    private bool _hasCompleted;

    /// <summary>The empty state shows only once loading has finished and nothing was found.</summary>
    public bool ShowEmpty => !IsLoading && !HasAny;

    public async Task OnNavigatedToAsync(object? parameter, CancellationToken cancellationToken)
    {
        var profile = _session.CurrentProfile;
        _profileId = profile?.Id;
        IsLoading = true;

        try
        {
            if (profile is not null)
            {
                await _downloads.EnsureLoadedAsync(profile.Id, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Loading downloads failed");
        }

        Rebuild();
        IsLoading = false;
    }

    public void OnNavigatedFrom()
    {
    }

    private void Rebuild()
    {
        InProgress.Clear();
        Completed.Clear();

        if (_profileId is { } profileId)
        {
            var mine = _downloads.Downloads.Where(r => r.Item.ProfileId == profileId).ToList();
            foreach (var row in mine.Where(r => r.IsInProgress).OrderByDescending(r => r.Item.CreatedUtc))
            {
                InProgress.Add(row);
            }

            foreach (var row in mine.Where(r => r.IsCompleted)
                         .OrderByDescending(r => r.Item.CompletedUtc ?? r.Item.CreatedUtc))
            {
                Completed.Add(row);
            }
        }

        HasInProgress = InProgress.Count > 0;
        HasCompleted = Completed.Count > 0;
        HasAny = HasInProgress || HasCompleted;
    }

    public void Receive(DownloadStateChangedMessage message) => RebuildOnUi();

    public void Receive(DownloadRemovedMessage message) => RebuildOnUi();

    private void RebuildOnUi()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Rebuild();
        }
        else
        {
            dispatcher.BeginInvoke(Rebuild);
        }
    }

    [RelayCommand]
    private async Task PlayAsync(DownloadRow? row)
    {
        if (row is null || !row.CanPlay)
        {
            return;
        }

        double resume = 0;
        if (_session.CurrentProfile is { } profile)
        {
            var entry = await _watchHistory.GetAsync(profile.Id, row.Kind, row.ItemKey, CancellationToken.None);
            resume = entry?.PositionSeconds ?? 0;
        }

        // A local file plays through the same VOD path; the shared ItemKey keeps resume/watched in sync.
        var url = new Uri(row.FilePath).AbsoluteUri;
        await _playback.PlayVodAsync(
            new VodPlayRequest(url, row.Kind, row.ItemKey, row.Title, row.PosterUrl, resume, row.Season, row.EpisodeNumber),
            CancellationToken.None);
    }

    [RelayCommand]
    private void Pause(DownloadRow? row)
    {
        if (row is not null)
        {
            _downloads.Pause(row.Id);
        }
    }

    [RelayCommand]
    private void Resume(DownloadRow? row)
    {
        if (row is not null)
        {
            _downloads.Resume(row.Id);
        }
    }

    [RelayCommand]
    private void Retry(DownloadRow? row)
    {
        if (row is not null)
        {
            _downloads.Retry(row.Id);
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(DownloadRow? row)
    {
        if (row is null)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            Strings.Downloads_RemoveTitle,
            Strings.Format(Strings.Downloads_RemoveBodyFormat, row.Title),
            Strings.Common_Remove,
            destructive: true);
        if (confirmed)
        {
            await _downloads.RemoveAsync(row.Id, CancellationToken.None);
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DownloadsDir);
            Process.Start(new ProcessStartInfo(AppPaths.DownloadsDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Opening the downloads folder failed");
        }
    }
}
