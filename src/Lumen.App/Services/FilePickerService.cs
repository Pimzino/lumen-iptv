using Microsoft.Win32;

namespace Lumen.App.Services;

/// <summary>File-open dialogs, abstracted so view models stay window-free.</summary>
public interface IFilePickerService
{
    /// <summary>Shows an open-file dialog. Null when the user cancels.</summary>
    string? PickFile(string title, string filter);
}

public sealed class FilePickerService : IFilePickerService
{
    public string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
