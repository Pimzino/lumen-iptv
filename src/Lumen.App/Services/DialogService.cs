using System.Windows;
using Lumen.App.ViewModels;
using Lumen.App.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace Lumen.App.Services;

/// <summary>Modal dialogs over the shell window; view models never construct windows.</summary>
public interface IDialogService
{
    /// <summary>Confirmation dialog. True when the user confirms.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false);

    /// <summary>Opens the profile edit dialog. True when edits were saved.</summary>
    Task<bool> EditProfileAsync(long profileId);
}

/// <summary>Default dialog service using <see cref="ConfirmDialog"/>.</summary>
public sealed class DialogService : IDialogService
{
    private readonly IServiceProvider _services;

    public DialogService(IServiceProvider services)
    {
        _services = services;
    }

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

    public async Task<bool> EditProfileAsync(long profileId)
    {
        var viewModel = _services.GetRequiredService<ProfileEditViewModel>();
        if (!await viewModel.InitializeAsync(profileId, CancellationToken.None))
        {
            return false;
        }

        var dispatcher = Application.Current.Dispatcher;
        return await dispatcher.InvokeAsync(() =>
        {
            var dialog = new ProfileEditDialog(viewModel)
            {
                Owner = Application.Current.MainWindow,
            };
            return dialog.ShowDialog() == true;
        }).Task;
    }
}
