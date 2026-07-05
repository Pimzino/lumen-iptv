using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using Lumen.App.Services;
using Lumen.App.Theming;
using Lumen.App.ViewModels;
using Lumen.App.Views.Debug;
using Lumen.Core;
using Lumen.Core.Abstractions;
using Lumen.Data;
using Lumen.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Lumen.App;

/// <summary>
/// Composition root. Builds the generic host, initializes logging and the database,
/// then shows the main window.
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private Diagnostics.DispatcherHangMonitor? _hangMonitor;

    /// <summary>
    /// Service access for visual-only plumbing (video surface attachment, async image
    /// loading) that cannot take constructor injection. View models never use this.
    /// </summary>
    public static T GetService<T>()
        where T : notnull
    {
        var host = ((App)Current)._host
            ?? throw new InvalidOperationException("The host is not initialized yet.");
        return host.Services.GetRequiredService<T>();
    }

    static App()
    {
        // Must run before any XAML parses: motion durations are baked into storyboards
        // by markup extensions reading this flag.
        MotionSettings.AnimationsEnabled = SystemParameters.ClientAreaAnimation;
    }

    /// <summary>True when launched with any diagnostic/e2e flag; gates non-hermetic behavior.</summary>
    internal static bool IsDiagnosticRun { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Diagnostic runs must stay deterministic and hermetic: screenshot gates get opaque
        // backgrounds (RenderTargetBitmap cannot see a DWM backdrop, only its alpha hole) and
        // no external network lookups fire against live services.
        IsDiagnosticRun = e.Args.Any(a => a.StartsWith("--", StringComparison.Ordinal));
        Controls.WindowFx.DisableBackdropForDiagnostics = IsDiagnosticRun;

        // The window is created after async initialization, so keep the app alive explicitly
        // until MainWindow exists.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        ConfigureLogging();
        RegisterGlobalExceptionHandlers();

        if (TryHandleDiagnosticArgs(e.Args))
        {
            return;
        }

        _ = InitializeAsync();
    }

    /// <summary>
    /// Debug entry points: <c>--gallery</c> opens the design-system gallery,
    /// <c>--gallery-shot [dir]</c> captures it to PNGs and exits, and
    /// <c>--show-support</c> previews the support reminder dialog.
    /// </summary>
    private bool TryHandleDiagnosticArgs(string[] args)
    {
        var shotIndex = Array.IndexOf(args, "--gallery-shot");
        if (shotIndex >= 0)
        {
            var directory = shotIndex + 1 < args.Length && !args[shotIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[shotIndex + 1]
                : Path.Combine(AppPaths.DataRoot, "gallery");
            _ = CaptureGalleryAsync(directory);
            return true;
        }

        if (args.Contains("--gallery"))
        {
            var window = new GalleryWindow();
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            return true;
        }

        if (args.Contains("--show-support"))
        {
            ShowSupportPreview();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Dev-only: previews the support ("buy me a coffee") reminder dialog on demand, bypassing the
    /// once-a-fortnight gate. Needs no database, host, or profile — launch with
    /// <c>Lumen.exe --show-support</c>.
    /// </summary>
    private void ShowSupportPreview()
    {
        // Render with the real chrome (the "--" arg would otherwise disable the backdrop).
        Controls.WindowFx.DisableBackdropForDiagnostics = false;

        var dialog = new Views.Dialogs.SupportDialog
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(Services.SupportService.DonationUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to open donation page from the support preview");
            }
        }

        Shutdown(0);
    }

    private async Task CaptureGalleryAsync(string directory)
    {
        try
        {
            var window = new GalleryWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            window.Show();

            // Let layout, font loading, and the 150ms initial transitions settle.
            await Task.Delay(600);

            window.CaptureSections(directory);
            window.Close();
            Log.Information("Gallery captured to {Directory}", directory);
            Shutdown(0);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Gallery capture failed");
            Shutdown(-2);
        }
    }

    private static void ConfigureLogging()
    {
        Directory.CreateDirectory(AppPaths.LogsDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Debug()
            .WriteTo.File(
                Path.Combine(AppPaths.LogsDir, "lumen-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 20 * 1024 * 1024,
                rollOnFileSizeLimit: true)
            .CreateLogger();

        Log.Information(
            "Lumen starting (version {Version})", typeof(App).Assembly.GetName().Version);
    }

    private async Task InitializeAsync()
    {
        try
        {
            var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            var databasePath = ArgValue(args, "--db") ?? AppPaths.DatabasePath;

            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSerilog();
            ConfigureServices(builder.Services, databasePath);

            _host = builder.Build();
            await _host.StartAsync();

            var initializer = _host.Services.GetRequiredService<DatabaseInitializer>();
            await Task.Run(() => initializer.InitializeAsync());

            // Headless end-to-end modes (Phase gates); no UI.
            if (ArgValue(args, "--e2e") is { } server)
            {
                var outFile = ArgValue(args, "--out") ?? Path.Combine(AppPaths.DataRoot, "e2e-result.txt");
                Shutdown(await Diagnostics.E2eRunner.RunAsync(_host.Services, server, outFile));
                return;
            }

            if (args.Contains("--e2e-verify"))
            {
                var outFile = ArgValue(args, "--out") ?? Path.Combine(AppPaths.DataRoot, "e2e-verify.txt");
                Shutdown(await Diagnostics.E2eRunner.VerifyAsync(_host.Services, outFile));
                return;
            }

            if (args.Contains("--e2e-play"))
            {
                var outFile = ArgValue(args, "--out") ?? Path.Combine(AppPaths.DataRoot, "e2e-play.txt");
                Shutdown(await Diagnostics.E2ePlayRunner.RunAsync(_host.Services, outFile));
                return;
            }

            if (args.Contains("--scroll-bench"))
            {
                var outFile = ArgValue(args, "--out") ?? Path.Combine(AppPaths.DataRoot, "scroll-bench.txt");
                Shutdown(await Diagnostics.ScrollBenchmark.RunAsync(outFile));
                return;
            }

            if (args.Contains("--settings-bench"))
            {
                var outFile = ArgValue(args, "--out") ?? Path.Combine(AppPaths.DataRoot, "settings-bench.txt");
                Shutdown(await Diagnostics.SettingsBenchmark.RunAsync(_host.Services, outFile));
                return;
            }

            if (args.Contains("--probe-stream"))
            {
                var outFile = ArgValue(args, "--out") ?? Path.Combine(AppPaths.DataRoot, "probe-stream.txt");
                Shutdown(await Diagnostics.StreamProbe.RunAsync(_host.Services, outFile));
                return;
            }

            if (args.Contains("--guide-bench"))
            {
                var outFile = ArgValue(args, "--out") ?? Path.Combine(AppPaths.DataRoot, "guide-bench.txt");
                Shutdown(await Diagnostics.GuideBenchmark.RunAsync(outFile, ArgValue(args, "--shots")));
                return;
            }

            if (args.Contains("--e2e-resume"))
            {
                var outFile = ArgValue(args, "--out") ?? Path.Combine(AppPaths.DataRoot, "e2e-resume.txt");
                Shutdown(await Diagnostics.E2eResumeRunner.RunAsync(_host.Services, outFile));
                return;
            }

            if (args.Contains("--search-bench"))
            {
                var outFile = ArgValue(args, "--out") ?? Path.Combine(AppPaths.DataRoot, "search-bench.txt");
                Shutdown(await Diagnostics.SearchBenchmark.RunAsync(outFile));
                return;
            }

            if (args.Contains("--glow-probe"))
            {
                var outFile = ArgValue(args, "--out") ?? Path.Combine(AppPaths.DataRoot, "glow-probe.txt");
                Shutdown(Diagnostics.AmbientGlowProbe.Run(outFile));
                return;
            }

            if (args.Contains("--soak"))
            {
                var minutes = double.TryParse(ArgValue(args, "--minutes"), out var m) ? m : 30;
                var outFile = ArgValue(args, "--out") ?? Path.Combine(AppPaths.DataRoot, "soak.txt");
                Shutdown(await Diagnostics.SoakRunner.RunAsync(
                    _host.Services, TimeSpan.FromMinutes(minutes), outFile));
                return;
            }

            var shellShotDir = ArgValue(args, "--shot-shell");
            var fullscreenProbe = args.Contains("--e2e-fullscreen");
            var vodUiProbe = args.Contains("--e2e-vod-ui");
            var window = _host.Services.GetRequiredService<MainWindow>();
            if (shellShotDir is not null || fullscreenProbe || vodUiProbe)
            {
                // Show the real window (Style + chrome trigger live) but keep it offscreen and
                // out of the taskbar so the diagnostic doesn't flash a window at the user.
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.ShowActivated = false;
                window.ShowInTaskbar = false;

                // Optional capture size ("1280x2200") so long scrolling pages can be reviewed
                // in full — the offscreen window has no monitor to clamp it.
                if (ArgValue(args, "--shot-size")?.Split('x') is [{ } w, { } h]
                    && double.TryParse(w, out var shotWidth) && double.TryParse(h, out var shotHeight))
                {
                    window.Width = shotWidth;
                    window.Height = shotHeight;
                }
            }

            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();

            // Debug-only UI-thread stall watchdog (no-op in Release).
            _hangMonitor = new Diagnostics.DispatcherHangMonitor(Dispatcher);
            _hangMonitor.Start();

            // Pre-warm LibVLC (native DLL load, player, video surface) in parallel with shell
            // init and the first page load, so an early channel click doesn't queue behind a
            // cold native init that can take many seconds on a busy first launch.
            _ = _host.Services.GetRequiredService<Services.Playback.PlaybackService>()
                .WarmUpAsync(CancellationToken.None);

            await _host.Services.GetRequiredService<ShellViewModel>().InitializeAsync();

            if (shellShotDir is not null)
            {
                await CaptureShellShotsAsync(window, shellShotDir);
            }

            if (fullscreenProbe)
            {
                var outFile = ArgValue(args, "--out") ?? Path.Combine(AppPaths.DataRoot, "e2e-fullscreen.txt");
                Shutdown(await Diagnostics.FullPlayerRunner.RunAsync(_host.Services, window, outFile));
                return;
            }

            if (vodUiProbe)
            {
                var outFile = ArgValue(args, "--out") ?? Path.Combine(AppPaths.DataRoot, "e2e-vod-ui.txt");
                Shutdown(await Diagnostics.VodUiProbe.RunAsync(_host.Services, window, outFile));
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Lumen failed to start");
            MessageBox.Show(
                $"Lumen failed to start.\n\n{ex.Message}\n\nLogs: {AppPaths.LogsDir}",
                "Lumen",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    /// <summary>Captures the main pages to PNGs for design review, then exits.</summary>
    private async Task CaptureShellShotsAsync(MainWindow window, string directory)
    {
        var shell = _host!.Services.GetRequiredService<ShellViewModel>();
        var catalog = _host.Services.GetRequiredService<Core.Abstractions.ICatalogRepository>();
        var session = _host.Services.GetRequiredService<Services.ISessionService>();
        Directory.CreateDirectory(directory);

        async Task SnapAsync(string name)
        {
            await Task.Delay(900);
            Diagnostics.VisualCapture.SaveWindow(window, Path.Combine(directory, name));
        }

        await SnapAsync("shell-startup.png");

        if (shell.IsShellReady)
        {
            // Seed a favorite channel so Home/Favorites show real content in the shots.
            if (session.CurrentProfile is { } shotProfile)
            {
                var favorites = _host!.Services.GetRequiredService<Core.Abstractions.IFavoritesRepository>();
                var channels = await catalog.GetChannelsAsync(shotProfile.Id, null, CancellationToken.None);
                foreach (var channel in channels.Take(3))
                {
                    await favorites.AddAsync(
                        shotProfile.Id, Core.Models.ContentKind.Live,
                        channel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds(), CancellationToken.None);
                }
            }

            shell.NavigateToSection("home");
            await Task.Delay(700);
            await SnapAsync("home.png");

            shell.NavigateToSection("livetv");
            await Task.Delay(600);
            if (shell.Navigation.CurrentViewModel is ViewModels.LiveTvViewModel liveTv && liveTv.Channels.Count > 0)
            {
                liveTv.SelectedChannel = liveTv.Channels[0];
            }

            await SnapAsync("livetv.png");

            // Synthetic "Favorites" category (second pinned row) filtered to the seeded channels.
            if (shell.Navigation.CurrentViewModel is ViewModels.LiveTvViewModel liveTvFavorites
                && liveTvFavorites.Categories.Count > 1)
            {
                liveTvFavorites.SelectedCategory = liveTvFavorites.Categories[1];
                await Task.Delay(500);
                await SnapAsync("livetv-favorites.png");
            }

            shell.NavigateToSection("guide");
            await Task.Delay(900);
            await SnapAsync("guide-live.png");

            shell.NavigateToSection("movies");
            await Task.Delay(900);
            await SnapAsync("movies.png");
            if (shell.Navigation.CurrentViewModel is ViewModels.MoviesViewModel moviesVm && moviesVm.Items.Count > 0)
            {
                moviesVm.OpenDetailCommand.Execute(moviesVm.Items[0]);
                await Task.Delay(1200);
                await SnapAsync("movie-detail.png");
                shell.NavigateToSection("movies");
            }

            // Series only exist on Xtream profiles; hop over when another kind is active.
            if (session.CurrentProfile?.Kind != Core.Models.ProfileKind.Xtream &&
                session.Profiles.FirstOrDefault(p => p.Kind == Core.Models.ProfileKind.Xtream) is { } xtream)
            {
                await session.SwitchProfileAsync(xtream.Id, CancellationToken.None);
                await Task.Delay(400);
            }

            shell.NavigateToSection("series");
            await Task.Delay(900);
            if (shell.Navigation.CurrentViewModel is ViewModels.SeriesViewModel seriesVm && seriesVm.Items.Count > 0)
            {
                seriesVm.OpenDetailCommand.Execute(seriesVm.Items[0]);
                await Task.Delay(1200);
                await SnapAsync("series-detail.png");

                // Second season selected, so the tab strip + episode swap is reviewable.
                if (shell.Navigation.CurrentViewModel is ViewModels.VodDetailViewModel detailVm &&
                    detailVm.Seasons.Count > 1)
                {
                    detailVm.SelectedSeason = detailVm.Seasons[1];
                    await SnapAsync("series-detail-s2.png");
                }

                shell.NavigateToSection("series");
            }

            shell.NavigateToSection("favorites");
            await Task.Delay(700);
            await SnapAsync("favorites.png");

            // Search is now a title-bar dropdown rather than a page: drive it on the shell so the
            // capture shows the live results popover over whatever page is behind it.
            shell.NavigateToSection("home");
            await Task.Delay(300);
            shell.Search.Query = "sh"; // ≥2 chars triggers the debounced search + opens the dropdown
            shell.Search.IsOpen = true;
            await Task.Delay(700);
            await SnapAsync("search.png");
            shell.Search.Query = string.Empty;

            shell.NavigateToSection("settings");
            await SnapAsync("settings.png");

            if (session.CurrentProfile is { } editProfile)
            {
                // Shown non-modally so the capture flow keeps running; offscreen via Owner.
                var editVm = _host!.Services.GetRequiredService<ViewModels.ProfileEditViewModel>();
                if (await editVm.InitializeAsync(editProfile.Id, CancellationToken.None))
                {
                    var editDialog = new Views.Dialogs.ProfileEditDialog(editVm)
                    {
                        Owner = window,
                        ShowActivated = false,
                    };
                    editDialog.Show();
                    await Task.Delay(900);
                    Diagnostics.VisualCapture.SaveWindow(
                        editDialog, Path.Combine(directory, "profile-edit.png"));
                    editDialog.Close();
                }
            }

            shell.IsRailExpanded = true;
            await SnapAsync("rail-expanded.png");
            shell.IsRailExpanded = false;

            // The player overlay renders over the video's native airspace, which
            // RenderTargetBitmap cannot capture; the overlay design is reviewed live and
            // its behavior is validated by the --e2e-play gate. Nothing to snapshot here.
        }

        shell.Navigation.NavigateTo<ViewModels.OnboardingViewModel>(
            ViewModels.OnboardingViewModel.AddModeParameter);
        if (shell.Navigation.CurrentViewModel is ViewModels.OnboardingViewModel onboarding)
        {
            await SnapAsync("onboarding-welcome.png");
            onboarding.Step = 1;
            await SnapAsync("onboarding-service.png");
            onboarding.Step = 2;
            await SnapAsync("onboarding-epg.png");
        }

        Log.Information("Shell screenshots captured to {Directory}", directory);
        Shutdown(0);
    }

    private static string? ArgValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : null;
    }

    private static void ConfigureServices(IServiceCollection services, string databasePath)
    {
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<ICredentialProtector, DpapiCredentialProtector>();
        services.AddLumenData(databasePath);
        services.AddLumenProviders();

        // Cross-cutting app services
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());
        services.AddSingleton<SessionService>();
        services.AddSingleton<ISessionService>(sp => sp.GetRequiredService<SessionService>());
        services.AddSingleton<IToastService, ToastService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IFilePickerService, FilePickerService>();
        services.AddSingleton<ICatalogSyncService, CatalogSyncService>();
        services.AddSingleton<IEpgSyncService, EpgSyncService>();
        services.AddSingleton<ImageSourceCache>();
        services.AddSingleton<ArtworkService>();
        services.AddSingleton<Services.Playback.PlaybackService>();
        services.AddSingleton<Services.Playback.IPlaybackService>(sp =>
            sp.GetRequiredService<Services.Playback.PlaybackService>());
        services.AddSingleton<Services.PlaybackServiceNavigator>();
        services.AddSingleton<Services.VodService>();
        services.AddSingleton<Services.AccountService>();
        services.AddSingleton<Services.SupportService>();
        services.AddHostedService<Services.EpgRefreshScheduler>();

        // Trakt: connection + matching + two-way sync; the scrobbler and scheduler are hosted
        // so they run without being injected anywhere.
        services.AddSingleton<Services.Trakt.TraktAuthStore>();
        services.AddSingleton<Services.Trakt.TraktService>();
        services.AddSingleton<Services.Trakt.TraktMatchService>();
        services.AddSingleton<Services.Trakt.TraktSyncService>();
        services.AddHostedService<Services.Trakt.TraktScrobbler>();
        services.AddHostedService<Services.Trakt.TraktSyncScheduler>();

        // Shell + pages (pages are transient: fresh state per navigation)
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<OnboardingViewModel>();
        services.AddTransient<ProfileEditViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<LiveTvViewModel>();
        services.AddTransient<GuideViewModel>();
        services.AddTransient<MoviesViewModel>();
        services.AddTransient<SeriesViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddTransient<FavoritesViewModel>();
        services.AddTransient<VodDetailViewModel>();
    }

    private volatile bool _crashDialogOpen;

    /// <summary>
    /// When set, dispatcher exceptions are logged and swallowed without showing the crash dialog.
    /// Used by headless diagnostics that deliberately drive UI paths and assert on faults.
    /// </summary>
    internal static volatile bool SuppressCrashDialog;

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled dispatcher exception");

            // Keep running; surface a styled dialog (never a raw .NET crash window), and never
            // stack multiple dialogs if faults cascade.
            args.Handled = true;
            if (SuppressCrashDialog)
            {
                return;
            }

            if (!_crashDialogOpen && MainWindow is not null)
            {
                ShowCrashDialog(args.Exception.Message);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled AppDomain exception");
    }

    private void ShowCrashDialog(string message)
    {
        try
        {
            _crashDialogOpen = true;
            var dialog = new Views.Dialogs.CrashDialog(message);
            if (MainWindow is { IsLoaded: true } owner && owner != dialog)
            {
                dialog.Owner = owner;
            }

            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to show crash dialog");
        }
        finally
        {
            _crashDialogOpen = false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _hangMonitor?.Dispose();
            if (_host is not null)
            {
                // Watch-history writes run on pool threads; drain the last one so quitting
                // right after pausing doesn't lose the resume position.
                try
                {
                    _host.Services.GetRequiredService<Services.Playback.PlaybackService>()
                        .FlushProgressAsync().Wait(TimeSpan.FromSeconds(2));
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Watch-history flush at exit failed");
                }

                _host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                _host.Dispose();
            }
        }
        finally
        {
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }
}
