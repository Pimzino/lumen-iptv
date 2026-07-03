using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Lumen.App.ViewModels;
using Lumen.Core.Models;

namespace Lumen.App.Diagnostics;

/// <summary>
/// Phase-4 gate: verifies the Live TV channel list stays smooth with 10,000 channels.
/// Binds a real virtualized ListBox to 10k items and scrolls it in steps, measuring
/// per-frame render time and how many containers materialize (recycling proof).
/// </summary>
public static class ScrollBenchmark
{
    public static async Task<int> RunAsync(string outFile)
    {
        var report = new StringBuilder();
        try
        {
            const int channelCount = 10_000;
            var items = new List<ChannelListItem>(channelCount);
            for (var i = 0; i < channelCount; i++)
            {
                var channel = new Channel { Id = i, Name = $"Channel {i:D5}" };
                items.Add(new ChannelListItem(channel)
                {
                    NowTitle = $"Programme {i}",
                    NowTimeRange = "20:00–21:00",
                    NowProgress = i % 100,
                });
            }

            var listBox = new ListBox
            {
                Style = (Style)Application.Current.FindResource("Lumen.ListBox.Channels"),
                ItemsSource = items,
                Width = 360,
                Height = 720,
            };
            listBox.ItemTemplate = BuildRowTemplate();

            var window = new Window
            {
                Width = 400,
                Height = 720,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                ShowInTaskbar = false,
                Left = -32000,
                Top = -32000,
                Background = (Brush)Application.Current.FindResource("Lumen.Brush.Bg.Base"),
                Content = listBox,
            };
            window.Show();

            await DispatcherYield();
            var scrollViewer = FindScrollViewer(listBox)
                ?? throw new InvalidOperationException("No ScrollViewer in the channel list.");

            // Warm up.
            scrollViewer.ScrollToVerticalOffset(0);
            await DispatcherYield();

            var frameTimes = new List<double>();
            var stopwatch = new Stopwatch();
            const int steps = 200;
            var stepSize = scrollViewer.ScrollableHeight / steps;

            for (var step = 0; step < steps; step++)
            {
                stopwatch.Restart();
                scrollViewer.ScrollToVerticalOffset(step * stepSize);
                await RenderYield();
                stopwatch.Stop();
                frameTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
            }

            // Container count proves recycling: a naive list would realize all 10k.
            var realized = CountContainers(listBox);

            frameTimes.Sort();
            var median = frameTimes[frameTimes.Count / 2];
            var p95 = frameTimes[(int)(frameTimes.Count * 0.95)];
            var max = frameTimes[^1];
            var over16 = frameTimes.Count(t => t > 16.7);

            report.AppendLine($"channels={channelCount}");
            report.AppendLine($"realizedContainers={realized}");
            report.AppendLine($"frames={frameTimes.Count}");
            report.AppendLine($"medianFrameMs={median:F2}");
            report.AppendLine($"p95FrameMs={p95:F2}");
            report.AppendLine($"maxFrameMs={max:F2}");
            report.AppendLine($"framesOver16ms={over16}");

            window.Close();

            // 60fps ⇒ 16.7ms budget. Gate: realized containers bounded (recycling works),
            // and p95 frame under budget.
            var pass = realized < 100 && p95 < 16.7;
            report.AppendLine(pass ? "SCROLL-RESULT=PASS" : "SCROLL-RESULT=FAIL");
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Scroll benchmark failed");
            report.AppendLine($"SCROLL-RESULT=FAIL {ex}");
            return 1;
        }
        finally
        {
            File.WriteAllText(outFile, report.ToString());
        }
    }

    private static DataTemplate BuildRowTemplate()
    {
        // Mirrors the real channel row: monogram + name + now/next + progress.
        var xaml = """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Grid>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width="Auto" />
                  <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>
                <Border Width="40" Height="40" CornerRadius="8"
                        Background="{StaticResource Lumen.Brush.Accent.Subtle}">
                  <TextBlock Text="{Binding Monogram}" Foreground="{StaticResource Lumen.Brush.Accent}"
                             HorizontalAlignment="Center" VerticalAlignment="Center" />
                </Border>
                <StackPanel Grid.Column="1" Margin="12,0,0,0" VerticalAlignment="Center">
                  <TextBlock Style="{StaticResource Lumen.Text.CaptionStrong}" Text="{Binding Name}" />
                  <TextBlock Style="{StaticResource Lumen.Text.Micro}" Text="{Binding NowTitle}" />
                  <ProgressBar Value="{Binding NowProgress}" Maximum="100" />
                </StackPanel>
              </Grid>
            </DataTemplate>
            """;
        using var reader = new System.Xml.XmlTextReader(new StringReader(xaml));
        return (DataTemplate)System.Windows.Markup.XamlReader.Load(reader);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static int CountContainers(ItemsControl itemsControl)
    {
        var count = 0;
        for (var i = 0; i < itemsControl.Items.Count; i++)
        {
            if (itemsControl.ItemContainerGenerator.ContainerFromIndex(i) is not null)
            {
                count++;
            }
        }

        return count;
    }

    private static Task DispatcherYield() =>
        Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task;

    private static async Task RenderYield()
    {
        var tcs = new TaskCompletionSource();

        void OnRendering(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnRendering;
            tcs.TrySetResult();
        }

        CompositionTarget.Rendering += OnRendering;
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render).Task;
        await tcs.Task;
    }
}
