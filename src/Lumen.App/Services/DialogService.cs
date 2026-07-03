using System.Windows;
using Lumen.App.Views.Dialogs;

namespace Lumen.App.Services;

/// <summary>Modal dialogs over the shell window; view models never construct windows.</summary>
public interface IDialogService
{
    /// <summary>Confirmation dialog. True when the user confirms.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false);
}

/// <summary>Default dialog service using <see cref="ConfirmDialog"/>.</summary>
public sealed class DialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false)
    {
        var dispatcher = Application.Current.Dispatcher;
        return dispatcher.InvokeAsync(() =>
        {
            var dialog = new ConfirmDialog(title, message, confirmLabel, destructive)
            {
                Owner = Application.Current.MainWindow,
            };
            return dialog.ShowDialog() == true;
        }).Task;
    }
}
