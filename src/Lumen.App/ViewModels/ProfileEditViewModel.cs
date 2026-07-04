using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumen.App.Resources;
using Lumen.App.Services;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Lumen.Providers;
using Lumen.Providers.M3u;
using Lumen.Providers.Xtream;
using Serilog;

namespace Lumen.App.ViewModels;

/// <summary>One selectable avatar color swatch on the profile edit page.</summary>
public sealed partial class AvatarOption : ObservableObject
{
    public AvatarOption(string color)
    {
        Color = color;
    }

    public string Color { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Backs the profile edit dialog: name, avatar color, connection details, and EPG source.
/// Saving re-syncs content when the source changed; a plain rename just persists.
/// </summary>
public sealed partial class ProfileEditViewModel : ObservableObject
{
    private readonly IProfileRepository _profiles;
    private readonly ISessionService _session;
    private readonly IXtreamClientFactory _xtreamFactory;
    private readonly IM3uPlaylistParser _m3uParser;
    private readonly ICatalogSyncService _catalogSync;
    private readonly IEpgSyncService _epgSync;
    private readonly IFilePickerService _filePicker;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IToastService _toasts;

    private Profile? _profile;
    private CancellationTokenSource? _testCts;

    public ProfileEditViewModel(
        IProfileRepository profiles,
        ISessionService session,
        IXtreamClientFactory xtreamFactory,
        IM3uPlaylistParser m3uParser,
        ICatalogSyncService catalogSync,
        IEpgSyncService epgSync,
        IFilePickerService filePicker,
        IHttpClientFactory httpClientFactory,
        IToastService toasts)
    {
        _profiles = profiles;
        _session = session;
        _xtreamFactory = xtreamFactory;
        _m3uParser = m3uParser;
        _catalogSync = catalogSync;
        _epgSync = epgSync;
        _filePicker = filePicker;
        _httpClientFactory = httpClientFactory;
        _toasts = toasts;
    }

    /// <summary>Raised when the dialog should close; true when edits were saved.</summary>
    public event EventHandler<bool>? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsM3u))]
    private bool _isXtream;

    public bool IsM3u => !IsXtream;

    [ObservableProperty]
    private string _kindLabel = string.Empty;

    // ---- form ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _profileName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestConnectionCommand))]
    private string _server = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestConnectionCommand))]
    private string _username = string.Empty;

    /// <summary>New password; empty keeps the stored one.</summary>
    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestConnectionCommand))]
    private string _playlistSource = string.Empty;

    [ObservableProperty]
    private bool _playlistIsFile;

    [ObservableProperty]
    private string _epgSource = string.Empty;

    [ObservableProperty]
    private bool _epgIsFile;

    public ObservableCollection<AvatarOption> AvatarOptions { get; } = [];

    // ---- state ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _testMessage;

    [ObservableProperty]
    private bool _testFailed;

    [ObservableProperty]
    private bool _testSucceeded;

    /// <summary>Progress line under the buttons while saving/refreshing.</summary>
    [ObservableProperty]
    private string? _saveStatus;

    [ObservableProperty]
    private string? _saveError;

    /// <summary>Loads the profile into the form. False when it no longer exists.</summary>
    public async Task<bool> InitializeAsync(long profileId, CancellationToken cancellationToken)
    {
        if (await _profiles.GetAsync(profileId, cancellationToken) is not { } profile)
        {
            return false;
        }

        _profile = profile;
        IsXtream = profile.Kind == ProfileKind.Xtream;
        KindLabel = IsXtream ? Strings.Onboarding_TabXtream : Strings.Onboarding_TabM3u;
        ProfileName = profile.Name;
        Server = profile.ServerUrl ?? string.Empty;
        Username = profile.Username ?? string.Empty;
        PlaylistSource = profile.PlaylistSource ?? string.Empty;
        PlaylistIsFile = profile.PlaylistIsFile;
        EpgSource = profile.EpgSource ?? string.Empty;
        EpgIsFile = profile.EpgIsFile;

        AvatarOptions.Clear();
        var current = profile.AvatarColor ?? SessionService.AvatarPalette[0];
        if (!SessionService.AvatarPalette.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            // Preserve a legacy/custom color by offering it alongside the palette.
            AvatarOptions.Add(new AvatarOption(current));
        }

        foreach (var color in SessionService.AvatarPalette)
        {
            AvatarOptions.Add(new AvatarOption(color));
        }

        SelectAvatar(AvatarOptions.First(o =>
            string.Equals(o.Color, current, StringComparison.OrdinalIgnoreCase)));
        return true;
    }

    /// <summary>Called by the dialog when it closes; stops any in-flight connection test.</summary>
    internal void OnDialogClosed() => _testCts?.Cancel();

    [RelayCommand]
    private void SelectAvatar(AvatarOption? option)
    {
        if (option is null)
        {
            return;
        }

        foreach (var candidate in AvatarOptions)
        {
            candidate.IsSelected = ReferenceEquals(candidate, option);
        }
    }

    [RelayCommand]
    private void BrowsePlaylist()
    {
        var path = _filePicker.PickFile(
            Strings.Onboarding_PlaylistSource, "Playlists (*.m3u;*.m3u8)|*.m3u;*.m3u8|All files (*.*)|*.*");
        if (path is not null)
        {
            PlaylistSource = path;
            PlaylistIsFile = true;
        }
    }

    [RelayCommand]
    private void BrowseEpg()
    {
        var path = _filePicker.PickFile(
            Strings.Onboarding_EpgSource, "XMLTV guides (*.xml;*.xml.gz;*.gz)|*.xml;*.xml.gz;*.gz|All files (*.*)|*.*");
        if (path is not null)
        {
            EpgSource = path;
            EpgIsFile = true;
        }
    }

    /// <summary>The password to connect with: the typed one, else the stored one.</summary>
    private string? EffectivePassword =>
        !string.IsNullOrEmpty(Password)
            ? Password
            : _profile is null ? null : _session.GetXtreamCredentials(_profile)?.Password;

    private bool CanTestConnection() =>
        IsXtream
            ? !string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(Username)
            : !string.IsNullOrWhiteSpace(PlaylistSource);

    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        _testCts?.Cancel();
        _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = _testCts.Token;

        IsBusy = true;
        TestMessage = Strings.Onboarding_Testing;
        TestSucceeded = false;
        TestFailed = false;

        try
        {
            if (IsXtream)
            {
                await TestXtreamAsync(token);
            }
            else
            {
                await TestM3uAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
            TestMessage = null;
        }
        catch (Exception ex) when (ex is XtreamApiException or FormatException or HttpRequestException or IOException)
        {
            TestFailed = true;
            TestMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Profile edit connection test failed unexpectedly");
            TestFailed = true;
            TestMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TestXtreamAsync(CancellationToken cancellationToken)
    {
        if (EffectivePassword is not { } password)
        {
            TestFailed = true;
            TestMessage = Strings.ProfileEdit_PasswordRequired;
            return;
        }

        var server = XtreamUrls.NormalizeServerBase(Server);
        var client = _xtreamFactory.Create(new XtreamCredentials(server, Username.Trim(), password));
        var auth = await client.AuthenticateAsync(cancellationToken);

        if (!auth.IsAuthenticated)
        {
            TestFailed = true;
            TestMessage = "The server rejected these credentials.";
            return;
        }

        Server = server;
        TestSucceeded = true;
        TestMessage = auth.IsActive ? Strings.Onboarding_XtreamOkNoExpiry : Strings.Onboarding_XtreamExpired;
    }

    private async Task TestM3uAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        var stream = await OpenPlaylistAsync(cancellationToken);
        await using (stream)
        {
            await foreach (var _ in _m3uParser.ParseAsync(stream, cancellationToken))
            {
                count++;
            }
        }

        if (count == 0)
        {
            TestFailed = true;
            TestMessage = "No playable entries were found in that playlist.";
            return;
        }

        TestSucceeded = true;
        TestMessage = Strings.Format(Strings.Onboarding_M3uOkFormat, count);
    }

    private async Task<Stream> OpenPlaylistAsync(CancellationToken cancellationToken)
    {
        var source = PlaylistSource.Trim();
        if (File.Exists(source))
        {
            PlaylistIsFile = true;
            return File.OpenRead(source);
        }

        PlaylistIsFile = false;
        var client = _httpClientFactory.CreateClient(ProvidersServiceCollectionExtensions.DownloadHttpClientName);
        var response = await client.GetAsync(
            new Uri(source), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    private bool CanSave() =>
        !IsBusy &&
        !string.IsNullOrWhiteSpace(ProfileName) &&
        (IsXtream
            ? !string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(Username)
            : !string.IsNullOrWhiteSpace(PlaylistSource));

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_profile is not { } profile)
        {
            return;
        }

        // The close is requested only after IsBusy resets: the dialog refuses to close
        // while busy, so raising it mid-save would be swallowed.
        var saved = false;
        SaveError = null;
        IsBusy = true;
        try
        {
            string? server = null;
            if (IsXtream)
            {
                try
                {
                    server = XtreamUrls.NormalizeServerBase(Server);
                }
                catch (FormatException ex)
                {
                    SaveError = ex.Message;
                    return;
                }
            }

            var username = Username.Trim();
            var playlist = PlaylistSource.Trim();
            var epg = string.IsNullOrWhiteSpace(EpgSource) ? null : EpgSource.Trim();
            var newPassword = string.IsNullOrEmpty(Password) ? null : Password;

            var connectionChanged = IsXtream
                ? !string.Equals(profile.ServerUrl, server, StringComparison.Ordinal) ||
                  !string.Equals(profile.Username, username, StringComparison.Ordinal) ||
                  newPassword is not null
                : !string.Equals(profile.PlaylistSource, playlist, StringComparison.Ordinal);
            var epgChanged = !string.Equals(profile.EpgSource, epg, StringComparison.Ordinal) ||
                (epg is not null && profile.EpgIsFile != EpgIsFile);

            profile.Name = ProfileName.Trim();
            profile.AvatarColor = AvatarOptions.FirstOrDefault(o => o.IsSelected)?.Color ?? profile.AvatarColor;
            if (IsXtream)
            {
                profile.ServerUrl = server;
                profile.Username = username;
            }
            else
            {
                profile.PlaylistSource = playlist;
                profile.PlaylistIsFile = PlaylistIsFile;
            }

            profile.EpgSource = epg;
            profile.EpgIsFile = epg is not null && EpgIsFile;

            await _session.UpdateProfileAsync(profile, newPassword, CancellationToken.None);

            // A changed source means the imported catalog/guide no longer reflects the profile;
            // refresh before leaving so the library isn't silently stale. The edit itself is
            // already saved, so a refresh failure only warns — Settings offers manual retries.
            try
            {
                if (connectionChanged)
                {
                    SaveStatus = Strings.ProfileEdit_RefreshingChannels;
                    await Task.Run(() => _catalogSync.SyncAsync(profile, CancellationToken.None));
                }

                var hasEpgSource = profile.EpgSource is not null || profile.Kind == ProfileKind.Xtream;
                if ((epgChanged || (connectionChanged && profile.Kind == ProfileKind.Xtream)) && hasEpgSource)
                {
                    SaveStatus = Strings.ProfileEdit_RefreshingGuide;
                    await Task.Run(() => _epgSync.RefreshAsync(profile, null, CancellationToken.None));
                }

                _toasts.Show(
                    Strings.Format(Strings.Toast_ProfileUpdatedFormat, profile.Name), ToastSeverity.Success);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Post-edit refresh failed for {Profile}", profile.Name);
                _toasts.Show(Strings.Format(Strings.Toast_SyncFailedFormat, ex.Message), ToastSeverity.Error);
            }

            saved = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving profile edits failed");
            SaveError = ex.Message;
        }
        finally
        {
            SaveStatus = null;
            IsBusy = false;
        }

        if (saved)
        {
            CloseRequested?.Invoke(this, true);
        }
    }
}
