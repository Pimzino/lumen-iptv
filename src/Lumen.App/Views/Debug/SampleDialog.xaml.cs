using System.Windows;
using Lumen.App.Controls;

namespace Lumen.App.Views.Debug;

/// <summary>Gallery-only dialog exercising the LumenDialogWindow chrome.</summary>
public partial class SampleDialog : LumenDialogWindow
{
    public SampleDialog()
    {
        InitializeComponent();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
