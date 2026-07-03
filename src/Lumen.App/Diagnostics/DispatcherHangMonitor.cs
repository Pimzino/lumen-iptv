using System.Diagnostics;
using System.Windows.Threading;
using Serilog;

namespace Lumen.App.Diagnostics;

/// <summary>
/// Debug-only watchdog that flags UI-thread stalls over the quality-bar budget (50ms). A
/// background thread pings the dispatcher every 50ms; if a ping doesn't complete within the
/// budget, the UI thread was blocked and the stall is logged for investigation.
/// </summary>
public sealed class DispatcherHangMonitor : IDisposable
{
    private const int BudgetMs = 50;

    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    public DispatcherHangMonitor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    [Conditional("DEBUG")]
    public void Start()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Lumen.HangMonitor",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    private void Loop()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            var stopwatch = Stopwatch.StartNew();
            var completed = new ManualResetEventSlim(false);
            try
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Send, () => completed.Set());
            }
            catch (TaskCanceledException)
            {
                return;
            }

            if (!completed.Wait(2000, token))
            {
                continue; // shutting down or a very long stall; skip
            }

            var elapsed = stopwatch.Elapsed.TotalMilliseconds;
            if (elapsed > BudgetMs)
            {
                Log.Warning("UI thread stalled for {ElapsedMs:F0}ms (budget {BudgetMs}ms)", elapsed, BudgetMs);
            }

            try
            {
                Task.Delay(50, token).Wait(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
