using System.Windows;
using Lumen.App.Controls;

namespace Lumen.App.Views.Dialogs;

/// <summary>A one-field text prompt (e.g. renaming a recording). Confirm returns the text.</summary>
public partial class TextPromptDialog : LumenDialogWindow
{
    public TextPromptDialog(string title, string initialValue, string confirmLabel)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        ValueBox.Text = initialValue;
        ConfirmButton.Content = confirmLabel;

        // Ready to type over the current value as soon as the dialog opens.
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    /// <summary>The entered text once the dialog is confirmed.</summary>
    public string Value => ValueBox.Text;

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
