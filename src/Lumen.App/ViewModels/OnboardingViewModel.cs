using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Lumen.App.Resources;
using Lumen.App.Services;
using Lumen.Core.Models;
using Lumen.Providers;
using Lumen.Providers.M3u;
using Lumen.Providers.Xtream;
using Serilog;

namespace Lumen.App.ViewModels;

/// <summary>
/// First-run and add-profile flow: welcome → add service (with a real connection test) →
/// EPG setup → import. Three interactive steps, progress dots, no dead ends.
/// </summary>
public sealed partial class OnboardingViewModel : ObservableObject, INavigationAware
{
    public const string AddModeParameter = "add";

    private readonly IXtreamClientFactory _xtreamFactory;
    private readonly IM3uPlaylistParser _m3uParser;
    private readonly ISessionService _session;
    private readonly ICatalogSyncService _catalogSync;
    private readonly IEpgSyncService _epgSync;
    private readonly IFilePickerService _filePicker;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMessenger _messenger;

    private XtreamAuthResponse? _verifiedAuth;
    private CancellationTokenSource? _testCts;

    public OnboardingViewModel(
        IXtreamClientFactory xtreamFactory,
        IM3uPlaylistParser m3uParser,
        ISessionService session,
        ICatalogSyncService catalogSync,
        IEpgSyncService epgSync,
        IFilePickerService filePicker,
        IHttpClientFactory httpClientFactory,
        IMessenger messenger)
    {
        _xtreamFactory = xtreamFactory;
        _m3uParser = m3uParser;
        _session = session;
        _catalogSync = catalogSync;
        _epgSync = epgSync;
        _filePicker = filePicker;
        _httpClientFactory = httpClientFactory;
        _messenger = messenger;
    }

    // ---- flow state ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcomeStep), nameof(IsServiceStep), nameof(IsEpgStep), nameof(IsWorkingStep))]
    private int _step;

    public bool IsWelcomeStep => Step == 0;

    public bool IsServiceStep => Step == 1;

    public bool IsEpgStep => Step == 2;

    public bool IsWorkingStep => Step == 3;

    [ObservableProperty]
    private bool _isAddMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsXtreamTab), nameof(IsM3uTab))]
    private int _serviceTab;

    public bool IsXtreamTab => ServiceTab == 0;

    public bool IsM3uTab => ServiceTab == 1;

    // ---- service form ----

    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private string _server = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private string _username = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private string _password = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private string _playlistSource = string.Empty;

    [ObservableProperty]
    private bool _playlistIsFile;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _testMessage;

    [ObservableProperty]
    private bool _testFailed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueToEpgCommand))]
    private bool _testSucceeded;

    // ---- EPG form ----

    [ObservableProperty]
    private string _epgSource = string.Empty;

    [ObservableProperty]
    private bool _epgIsFile;

    [ObservableProperty]
    private bool _importEpgNow = true;

    // ---- working step ----

    public ObservableCollection<string> WorkLog { get; } = [];

    [ObservableProperty]
    private string? _workStatus;

    [ObservableProperty]
    private bool _workFailed;

    public Task OnNavigatedToAsync(object? parameter, CancellationToken cancellationToken)
    {
        IsAddMode = Equals(parameter, AddModeParameter);
        return Task.CompletedTask;
    }

    public void OnNavigatedFrom()
    {
        _testCts?.Cancel();
    }

    [RelayCommand]
    private void Begin() => Step = 1;

    [RelayCommand]
    private void GoBack()
    {
        if (Step > 0)
        {
            Step--;
        }
    }

    [RelayCommand]
    private void SelectTab(string? tab)
    {
        ServiceTab = tab == "m3u" ? 1 : 0;
        TestMessage = null;
        TestSucceeded = false;
        TestFailed = false;
    }

    private bool CanTestConnection() =>
        IsXtreamTab
            ? !string.IsNullOrWhiteSpace(Server) &&
              !string.IsNullOrWhiteSpace(Username) &&
              !string.IsNullOrWhiteSpace(Password)
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
        _verifiedAuth = null;

        try
        {
            if (IsXtreamTab)
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
            Log.Error(ex, "Connection test failed unexpectedly");
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
        var server = XtreamUrls.NormalizeServerBase(Server);
        var client = _xtreamFactory.Create(new XtreamCredentials(server, Username.Trim(), Password));
        var auth = await client.AuthenticateAsync(cancellationToken);

        if (!auth.IsAuthenticated)
        {
            TestFailed = true;
            TestMessage = "The server rejected these credentials.";
            return;
        }

        _verifiedAuth = auth;
        Server = server;
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            ProfileName = Username.Trim();
        }

        if (!auth.IsActive)
        {
            TestSucceeded = true;
            TestFailed = false;
            TestMessage = Strings.Onboarding_XtreamExpired;
            return;
        }

        TestSucceeded = true;
        var expiry = auth.UserInfo?.ExpiresAt;
        TestMessage = expiry is null
            ? Strings.Onboarding_XtreamOkNoExpiry
            : Strings.Format(
                Strings.Onboarding_XtreamOkFormat,
                auth.UserInfo?.MaxConnections is { } max
                    ? $"{max} connection{(max == 1 ? "" : "s")}"
                    : "active",
                expiry.Value.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture));
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

        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            ProfileName = PlaylistIsFile
                ? Path.GetFileNameWithoutExtension(PlaylistSource)
                : new Uri(PlaylistSource).Host;
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

    private bool CanContinueToEpg() => TestSucceeded;

    [RelayCommand(CanExecute = nameof(CanContinueToEpg))]
    private void ContinueToEpg() => Step = 2;

    [RelayCommand]
    private async Task FinishAsync()
    {
        Step = 3;
        WorkFailed = false;
        WorkLog.Clear();

        var profile = new Profile
        {
            Name = string.IsNullOrWhiteSpace(ProfileName) ? "My TV" : ProfileName.Trim(),
            Kind = IsXtreamTab ? ProfileKind.Xtream : ProfileKind.M3u,
            ServerUrl = IsXtreamTab ? Server : null,
            Username = IsXtreamTab ? Username.Trim() : null,
            PlaylistSource = IsM3uTab ? PlaylistSource.Trim() : null,
            PlaylistIsFile = IsM3uTab && PlaylistIsFile,
            EpgSource = string.IsNullOrWhiteSpace(EpgSource) ? null : EpgSource.Trim(),
            EpgIsFile = EpgIsFile,
        };

        try
        {
            await _session.AddProfileAsync(profile, IsXtreamTab ? Password : null, CancellationToken.None);

            WorkStatus = Strings.Onboarding_StepChannels;
            var result = await Task.Run(() => _catalogSync.SyncAsync(profile, CancellationToken.None));
            WorkLog.Add(Strings.Format(
                Strings.Onboarding_SyncSummaryFormat, result.Channels, result.Movies, result.Series));

            var hasEpgSource = profile.EpgSource is not null || profile.Kind == ProfileKind.Xtream;
            if (ImportEpgNow && hasEpgSource)
            {
                WorkStatus = Strings.Onboarding_StepEpg;
                var progress = new Progress<Core.Abstractions.EpgImportProgress>(p =>
                    WorkStatus = Strings.Format(Strings.Settings_EpgProgressFormat, p.Programmes));
                var epg = await Task.Run(() => _epgSync.RefreshAsync(profile, progress, CancellationToken.None));
                WorkLog.Add(Strings.Format(Strings.Toast_EpgRefreshedFormat, epg.Programmes));
            }

            _messenger.Send(new OnboardingCompletedMessage(profile));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Onboarding import failed");
            WorkFailed = true;
            WorkStatus = ex.Message;
        }
    }

    /// <summary>After a failed import: go back to the EPG step to adjust and retry.</summary>
    [RelayCommand]
    private void RetryAfterFailure() => Step = 2;
}
