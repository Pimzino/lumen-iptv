using System.Windows;
using Lumen.App.Controls;

namespace Lumen.App.Views.Dialogs;

/// <summary>Standard confirmation dialog; destructive confirmations color the action red.</summary>
public partial class ConfirmDialog : LumenDialogWindow
{
    public ConfirmDialog(string title, string message, string confirmLabel, bool destructive)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;

        if (destructive)
        {
            ConfirmButton.Background = (System.Windows.Media.Brush)FindResource("Lumen.Brush.Error");
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
