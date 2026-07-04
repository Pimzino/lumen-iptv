using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Lumen.App.ViewModels;
using Lumen.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Lumen.App.Diagnostics;

/// <summary>
/// Headless guard for the Settings channel-mapping list. A large playlist can leave tens of
/// thousands of channels without a guide match; the mapping list must virtualize (realize only the
/// visible rows) or opening Settings freezes the UI thread building a ComboBox per row. This loads
/// the real <see cref="Views.SettingsView"/> with a large row set offscreen and asserts only a
/// handful of ComboBox containers are realized.
/// </summary>
public static class SettingsBenchmark
{
    public static async Task<int> RunAsync(IServiceProvider services, string outFile)
    {
        var report = new StringBuilder();
        try
        {
            const int total = 10000;
            var vm = services.GetRequiredService<SettingsViewModel>();

            var options = new List<EpgChannelOption>();
            for (var i = 0; i < 50; i++)
            {
                options.Add(new EpgChannelOption($"id{i}", $"EPG Channel {i}"));
            }

            var populate = Stopwatch.StartNew();
            var rows = new List<ChannelMappingRow>(total);
            for (var i = 0; i < total; i++)
            {
                var channel = new Channel { Id = i, Name = $"Channel {i}" };
                rows.Add(new ChannelMappingRow(channel, null, options, static (_, _) => { }));
            }

            vm.UnmappedChannels = rows;
            vm.HasUnmappedChannels = true;
            vm.IsLoading = false; // the page content (and its mapping list) is skeleton-gated during load
            var populateMs = populate.ElapsedMilliseconds;

            var view = new Views.SettingsView { DataContext = vm };
            var host = new Window
            {
                Width = 960,
                Height = 720,
                Left = -32000,
                Top = -32000,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = view,
            };

            var layout = Stopwatch.StartNew();
            host.Show();
            host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);
            host.UpdateLayout();
            var layoutMs = layout.ElapsedMilliseconds;

            // Two ComboBoxes elsewhere on the page (EPG interval, theme) are always realized; the
            // rest would be one-per-mapping-row without virtualization.
            var comboBoxes = CountVisual<ComboBox>(view);
            host.Close();

            report.AppendLine($"rows={total}");
            report.AppendLine($"populateMs={populateMs}");
            report.AppendLine($"layoutMs={layoutMs}");
            report.AppendLine($"realizedComboBoxes={comboBoxes}");

            var pass = comboBoxes is > 0 and < 100 && layoutMs < 4000;
            report.AppendLine(pass ? "SETTINGS-RESULT=PASS" : "SETTINGS-RESULT=FAIL");
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Settings benchmark failed");
            report.AppendLine($"SETTINGS-RESULT=FAIL {ex}");
            return 1;
        }
        finally
        {
            File.WriteAllText(outFile, report.ToString());
        }
    }

    private static int CountVisual<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = 0;
        var children = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < children; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T)
            {
                count++;
            }

            count += CountVisual<T>(child);
        }

        return count;
    }
}
