using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lumen.App.Services;

public enum ToastSeverity
{
    Info,
    Success,
    Error,
}

/// <summary>A toast currently on screen.</summary>
public sealed partial class ToastItem : ObservableObject
{
    public ToastItem(string message, ToastSeverity severity)
    {
        Message = message;
        Severity = severity;
    }

    public string Message { get; }

    public ToastSeverity Severity { get; }
}

/// <summary>Queues transient notifications rendered by the shell's toast host.</summary>
public interface IToastService
{
    ObservableCollection<ToastItem> Items { get; }

    void Show(string message, ToastSeverity severity = ToastSeverity.Info);
}

/// <summary>Default toast service; safe to call from any thread.</summary>
public sealed class ToastService : IToastService
{
    private const int MaxVisible = 4;
    private static readonly TimeSpan InfoDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ErrorDuration = TimeSpan.FromSeconds(7);

    public ObservableCollection<ToastItem> Items { get; } = [];

    public void Show(string message, ToastSeverity severity = ToastSeverity.Info)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            var item = new ToastItem(message, severity);
            while (Items.Count >= MaxVisible)
            {
                Items.RemoveAt(0);
            }

            Items.Add(item);

            var timer = new DispatcherTimer
            {
                Interval = severity == ToastSeverity.Error ? ErrorDuration : InfoDuration,
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Items.Remove(item);
            };
            timer.Start();
        });
    }
}
