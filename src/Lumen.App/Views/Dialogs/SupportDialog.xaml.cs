using System.Windows;
using Lumen.App.Controls;

namespace Lumen.App.Views.Dialogs;

/// <summary>
/// The occasional "support the developer" nudge. Returns <c>true</c> when the user chooses to
/// donate; "Maybe later" (the cancel action) returns <c>false</c>.
/// </summary>
public partial class SupportDialog : LumenDialogWindow
{
    public SupportDialog()
    {
        InitializeComponent();
    }

    private void OnBuyClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
