using System.Windows;
using Lumen.App.Controls;
using Lumen.App.ViewModels;

namespace Lumen.App.Views.Dialogs;

/// <summary>
/// Shows an available update: version, release notes, live download progress, and the install /
/// skip / later actions. Bound to the shell-owned <see cref="UpdateViewModel"/> so progress keeps
/// updating while the dialog is open.
/// </summary>
public partial class UpdateDialog : LumenDialogWindow
{
    public UpdateDialog(UpdateViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    // "Skip this version" and "Open release page" run their command (via binding) and then close.
    // "Install now" shuts the whole app down, so it needs no close handler; "Later" uses IsCancel.
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
