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

    /// <summary>One-field text prompt. The entered text, or null when cancelled.</summary>
    Task<string?> PromptTextAsync(string title, string initialValue, string confirmLabel);

    /// <summary>Opens the profile edit dialog. True when edits were saved.</summary>
    Task<bool> EditProfileAsync(long profileId);

    /// <summary>Shows the support ("buy me a coffee") prompt. True when the user chooses to donate.</summary>
    Task<bool> ShowSupportPromptAsync();

    /// <summary>Shows the update dialog (version, notes, progress, actions), bound to the shared view model.</summary>
    Task ShowUpdateAsync(UpdateViewModel viewModel);
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

    public Task<string?> PromptTextAsync(string title, string initialValue, string confirmLabel)
    {
        var dispatcher = Application.Current.Dispatcher;
        return dispatcher.InvokeAsync(() =>
        {
            var dialog = new TextPromptDialog(title, initialValue, confirmLabel)
            {
                Owner = Application.Current.MainWindow,
            };
            return dialog.ShowDialog() == true ? dialog.Value : null;
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

    public Task<bool> ShowSupportPromptAsync()
    {
        var dispatcher = Application.Current.Dispatcher;
        return dispatcher.InvokeAsync(() =>
        {
            var dialog = new SupportDialog { Owner = Application.Current.MainWindow };
            return dialog.ShowDialog() == true;
        }).Task;
    }

    public Task ShowUpdateAsync(UpdateViewModel viewModel)
    {
        var dispatcher = Application.Current.Dispatcher;
        return dispatcher.InvokeAsync(() =>
        {
            var dialog = new UpdateDialog(viewModel) { Owner = Application.Current.MainWindow };
            dialog.ShowDialog();
        }).Task;
    }
}
