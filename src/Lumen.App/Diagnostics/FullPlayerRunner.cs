using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Lumen.App.Controls;
using Lumen.App.Services;
using Lumen.App.Services.Playback;
using Lumen.App.ViewModels;
using Lumen.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Lumen.App.Diagnostics;

/// <summary>
/// Headless regression gate for the full-player path and the animated control templates that only
/// come alive once rendered. It shows the real <see cref="MainWindow"/> (offscreen) so its window
/// Style and the <c>LumenChrome.IsImmersive</c> trigger are live, then:
/// <list type="bullet">
/// <item>plays a channel and enters/exits/re-enters full player — exercising the window chrome;</item>
/// <item>renders the spinner and skeleton templates (whose <c>Loaded</c> storyboards resolve names
/// in a ControlTemplate namescope — the failure mode behind the "name cannot be found" crash);</item>
/// <item>drives a forced stream drop so the real reconnect banner + spinner render in full player.</item>
/// </list>
/// Any dispatcher exception during the run — not just a synchronous throw — fails the gate. Requires
/// a database prepared by <c>--e2e</c> and a running DevServer.
/// </summary>
public static class FullPlayerRunner
{
    public static async Task<int> RunAsync(IServiceProvider services, Window window, string outFile)
    {
        var report = new StringBuilder();
        var faults = new List<string>();

        void OnFault(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            faults.Add($"{e.Exception.GetType().Name}: {e.Exception.Message}");
            e.Handled = true;
        }

        // Deliberately drive UI paths; swallow the crash dialog and collect faults instead.
        App.SuppressCrashDialog = true;
        Application.Current.DispatcherUnhandledException += OnFault;
        try
        {
            var session = services.GetRequiredService<ISessionService>();
            var profile = session.CurrentProfile;
            if (profile is null)
            {
                await session.InitializeAsync(CancellationToken.None);
                profile = session.CurrentProfile;
            }

            if (profile is null)
            {
                report.AppendLine("FULLSCREEN-RESULT=FAIL no-profile");
                return 1;
            }

            var channels = await services.GetRequiredService<ICatalogRepository>()
                .GetChannelsAsync(profile.Id, null, CancellationToken.None);
            report.AppendLine($"channels={channels.Count}");

            var playback = services.GetRequiredService<PlaybackService>();
            playback.IsMuted = true;

            // Reach Playing on a stable stream.
            await playback.PlayChannelAsync(channels[0], channels, preview: false, CancellationToken.None);
            var played = await WaitForAsync(() => playback.State == PlaybackState.Playing, TimeSpan.FromSeconds(15));
            report.AppendLine($"play state={playback.State}");

            // Enter full player — flips the window chrome (caption -> 0) via the bound trigger.
            RunOnUi(window, playback.EnterFullPlayer, faults, "enter");
            await PumpAsync(window);
            var entered = playback.IsFullPlayerActive;
            report.AppendLine($"enter fullPlayer={entered}");

            // True fullscreen must cover the taskbar: topmost + monitor-sized, not a work-area
            // maximize (which left the taskbar showing — the reported bug).
            var playerVm = services.GetRequiredService<PlayerViewModel>();
            RunOnUi(window, () => playerVm.ToggleFullscreenCommand.Execute(null), faults, "enter-fullscreen");
            await PumpAsync(window);
            var fsTopmost = window.Topmost;
            var fsCoversWidth = window.ActualWidth >= SystemParameters.PrimaryScreenWidth - 2;
            report.AppendLine($"fullscreen topmost={fsTopmost} width={window.ActualWidth:F0} " +
                $"screenW={SystemParameters.PrimaryScreenWidth:F0} covers={fsCoversWidth}");
            RunOnUi(window, () => playerVm.ToggleFullscreenCommand.Execute(null), faults, "exit-fullscreen");
            await PumpAsync(window);
            var fsExitedTopmost = window.Topmost;
            report.AppendLine($"fullscreen-exit topmost={fsExitedTopmost}");

            // Render the animated control templates (spinner + skeleton). Their Loaded storyboards
            // resolve target names in a ControlTemplate namescope — the crash surface we're gating.
            await SmokeAnimatedTemplatesAsync(window);
            report.AppendLine("animated-templates rendered");

            // Fire a real toast so its Loaded slide-in animation runs in the live shell (the
            // ToastShift path — a DataTemplate namescope with the same failure mode).
            RunOnUi(window, () => services.GetRequiredService<IToastService>()
                .Show("Diagnostics self-test", ToastSeverity.Success), faults, "toast");
            await PumpAsync(window);
            await PumpAsync(window);
            report.AppendLine("toast fired");

            // Drive the deliberately flaky fixture stream so the real reconnect banner + spinner
            // render while in full player (the user's exact scenario).
            var flaky = channels.FirstOrDefault(c => c.StreamUrl?.Contains("105", StringComparison.Ordinal) == true);
            var reconnectRendered = false;
            if (flaky is not null)
            {
                await playback.PlayChannelAsync(flaky, channels, preview: false, CancellationToken.None);
                await WaitForAsync(() => playback.State == PlaybackState.Playing, TimeSpan.FromSeconds(15));
                reconnectRendered = await WaitForAsync(
                    () => playback.State == PlaybackState.Reconnecting, TimeSpan.FromSeconds(25));
                await PumpAsync(window);
                await PumpAsync(window);
            }

            report.AppendLine($"reconnect banner rendered={reconnectRendered}");

            // Exit to the mini player — this must spin up the separate picture-in-picture window.
            RunOnUi(window, () => playback.ExitFullPlayer(PlayerExitMode.MiniPlayer), faults, "exit");
            await PumpAsync(window);

            var pip = Application.Current.Windows.OfType<MiniPlayerWindow>().FirstOrDefault();
            var pipOk = pip is { Topmost: true, ShowInTaskbar: false } && pip.IsVisible
                && !ReferenceEquals(pip, window);
            report.AppendLine($"pipWindow present={pip is not null} topmost={pip?.Topmost} " +
                $"taskbar={pip?.ShowInTaskbar} visible={pip?.IsVisible} separate={pip is not null && !ReferenceEquals(pip, window)}");

            // The PiP controls live in a Popup above the video HWND — confirm the chrome realizes
            // its command buttons and that the Popup content actually got the PlayerViewModel
            // DataContext (else the command bindings are null and the buttons do nothing).
            var pipControls = pip is not null
                ? window.Dispatcher.Invoke(pip.OpenControlsForDiagnostics)
                : "buttons=0 clickButtons=0 vm=False";
            var pipControlsOk = pipControls.Contains("vm=True", StringComparison.Ordinal)
                && pipControls.Contains("clickButtons=4", StringComparison.Ordinal);
            report.AppendLine($"pipControls {pipControls} ok={pipControlsOk}");

            // Re-enter — should hide the PiP window and return to full player.
            RunOnUi(window, playback.EnterFullPlayer, faults, "reenter");
            await PumpAsync(window);
            var pipHidden = pip is null || !pip.IsVisible;
            report.AppendLine($"reenter fullPlayer={playback.IsFullPlayerActive} pipHidden={pipHidden}");

            playback.Stop();
            await PumpAsync(window);

            foreach (var fault in faults)
            {
                report.AppendLine($"ERROR {fault}");
            }

            var pass = played && entered && fsTopmost && fsCoversWidth && !fsExitedTopmost
                && pipOk && pipControlsOk && pipHidden && faults.Count == 0;
            report.AppendLine(pass ? "FULLSCREEN-RESULT=PASS" : "FULLSCREEN-RESULT=FAIL");
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Full-player probe failed");
            report.AppendLine($"FULLSCREEN-RESULT=FAIL {ex}");
            return 1;
        }
        finally
        {
            Application.Current.DispatcherUnhandledException -= OnFault;
            App.SuppressCrashDialog = false;
            File.WriteAllText(outFile, report.ToString());
        }
    }

    /// <summary>
    /// Hosts one of each animated template (<c>Lumen.Spinner</c>, <see cref="SkeletonBlock"/>) in a
    /// throwaway offscreen window and pumps the dispatcher so their <c>Loaded</c> storyboards run.
    /// A namescope-resolution failure surfaces through the app's dispatcher handler, which the
    /// caller has hooked.
    /// </summary>
    private static async Task SmokeAnimatedTemplatesAsync(Window owner)
    {
        Window? probe = null;
        owner.Dispatcher.Invoke(() =>
        {
            var panel = new StackPanel { Margin = new Thickness(8) };
            if (Application.Current.TryFindResource("Lumen.Spinner") is Style spinnerStyle)
            {
                panel.Children.Add(new ContentControl { Style = spinnerStyle });
            }

            panel.Children.Add(new SkeletonBlock { Height = 40, Width = 180, Margin = new Thickness(0, 8, 0, 0) });

            probe = new Window
            {
                Width = 240,
                Height = 240,
                Left = -32000,
                Top = -32000,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = panel,
            };
            probe.Show();
        });

        // Let Loaded fire and the looped storyboards begin (and throw, if they're going to).
        for (var i = 0; i < 4; i++)
        {
            await PumpAsync(owner);
        }

        owner.Dispatcher.Invoke(() => probe?.Close());
    }

    private static void RunOnUi(Window window, Action action, List<string> faults, string label)
    {
        window.Dispatcher.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                faults.Add($"{label}: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    private static async Task PumpAsync(Window window)
    {
        await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(120);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return condition();
    }
}
