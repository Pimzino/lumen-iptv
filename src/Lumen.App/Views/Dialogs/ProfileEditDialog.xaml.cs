using System.Windows;
using Lumen.App.Controls;
using Lumen.App.ViewModels;

namespace Lumen.App.Views.Dialogs;

/// <summary>Modal dialog for editing an existing profile.</summary>
public partial class ProfileEditDialog : LumenDialogWindow
{
    private readonly ProfileEditViewModel _viewModel;

    public ProfileEditDialog(ProfileEditViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // The template reserves 48px per side for the drop shadow; capping at the work
        // area keeps the card (and its pinned footer) fully on screen — the form body
        // scrolls instead.
        MaxHeight = SystemParameters.WorkArea.Height;

        viewModel.CloseRequested += OnCloseRequested;
        Closing += (_, e) => e.Cancel = _viewModel.IsBusy;
        Closed += (_, _) => _viewModel.OnDialogClosed();
    }

    private void OnCloseRequested(object? sender, bool saved)
    {
        try
        {
            DialogResult = saved;
        }
        catch (InvalidOperationException)
        {
            // Shown non-modally (diagnostics); just close.
            Close();
        }
    }
}
