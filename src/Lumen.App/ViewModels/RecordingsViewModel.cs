using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Lumen.App.Resources;
using Lumen.App.Services;
using Lumen.App.Services.Playback;
using Lumen.App.Services.Recordings;
using Lumen.Core;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Serilog;

namespace Lumen.App.ViewModels;

/// <summary>
/// The Recordings page: the current profile's in-progress capture and finished recordings, with
/// stop, play, and remove actions. Binds to the shared <see cref="RecordingService"/> rows so the
/// active capture's elapsed/size tick in place; transitions re-bucket via messages.
/// </summary>
public sealed partial class RecordingsViewModel : ObservableObject, INavigationAware,
    IRecipient<RecordingStateChangedMessage>
{
    private readonly RecordingService _recordings;
    private readonly ISessionService _session;
    private readonly PlaybackService _playback;
    private readonly IWatchHistoryRepository _watchHistory;
    private readonly IDialogService _dialogs;

    private long? _profileId;

    public RecordingsViewModel(
        RecordingService recordings,
        ISessionService session,
        PlaybackService playback,
        IWatchHistoryRepository watchHistory,
        IDialogService dialogs,
        IMessenger messenger)
    {
        _recordings = recordings;
        _session = session;
        _playback = playback;
        _watchHistory = watchHistory;
        _dialogs = dialogs;
        messenger.RegisterAll(this);
    }

    /// <summary>The running capture plus any failed rows (kept visible until removed).</summary>
    public ObservableCollection<RecordingRow> InProgress { get; } = [];

    /// <summary>Finished recordings ready to play offline.</summary>
    public ObservableCollection<RecordingRow> Completed { get; } = [];

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
                await _recordings.EnsureLoadedAsync(profile.Id, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Loading recordings failed");
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
            var mine = _recordings.Recordings.Where(r => r.Item.ProfileId == profileId).ToList();
            foreach (var row in mine.Where(r => r.IsRecording || r.IsFailed)
                         .OrderByDescending(r => r.Item.StartedUtc))
            {
                InProgress.Add(row);
            }

            foreach (var row in mine.Where(r => r.IsCompleted).OrderByDescending(r => r.Item.StartedUtc))
            {
                Completed.Add(row);
            }
        }

        HasInProgress = InProgress.Count > 0;
        HasCompleted = Completed.Count > 0;
        HasAny = HasInProgress || HasCompleted;
    }

    public void Receive(RecordingStateChangedMessage message)
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
    private async Task PlayAsync(RecordingRow? row)
    {
        if (row is null || !row.CanPlay)
        {
            return;
        }

        double resume = 0;
        var itemKey = $"rec:{row.Id}";
        if (_session.CurrentProfile is { } profile)
        {
            var entry = await _watchHistory.GetAsync(profile.Id, ContentKind.Live, itemKey, CancellationToken.None);
            resume = entry?.PositionSeconds ?? 0;
        }

        // ContentKind.Live keeps recordings out of Trakt (scrobbler and push both skip Live)
        // while the per-recording key still gives watch-history resume.
        var url = new Uri(row.FilePath).AbsoluteUri;
        await _playback.PlayVodAsync(
            new VodPlayRequest(url, ContentKind.Live, itemKey, row.Title, row.LogoUrl, resume),
            CancellationToken.None);
    }

    [RelayCommand]
    private Task StopAsync(RecordingRow? row) =>
        row is null ? Task.CompletedTask : _recordings.StopRecordingAsync(row.Id);

    [RelayCommand]
    private async Task RenameAsync(RecordingRow? row)
    {
        if (row is null)
        {
            return;
        }

        var newTitle = await _dialogs.PromptTextAsync(
            Strings.Recordings_RenameTitle, row.Title, Strings.Recordings_Rename);
        if (newTitle is not null && newTitle.Trim() != row.Title)
        {
            await _recordings.RenameAsync(row.Id, newTitle, CancellationToken.None);
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(RecordingRow? row)
    {
        if (row is null)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            Strings.Recordings_RemoveTitle,
            Strings.Format(Strings.Recordings_RemoveBodyFormat, row.Title),
            Strings.Common_Remove,
            destructive: true);
        if (confirmed)
        {
            await _recordings.RemoveAsync(row.Id, CancellationToken.None);
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.RecordingsDir);
            Process.Start(new ProcessStartInfo(AppPaths.RecordingsDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Opening the recordings folder failed");
        }
    }
}
