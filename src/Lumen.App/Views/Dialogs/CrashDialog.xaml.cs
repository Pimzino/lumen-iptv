using System.Diagnostics;
using System.IO;
using System.Windows;
using Lumen.App.Controls;
using Lumen.Core;

namespace Lumen.App.Views.Dialogs;

/// <summary>Styled crash dialog shown by the global exception handler — never a raw .NET window.</summary>
public partial class CrashDialog : LumenDialogWindow
{
    public CrashDialog(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            Process.Start(new ProcessStartInfo(AppPaths.LogsDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not open the logs folder");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
