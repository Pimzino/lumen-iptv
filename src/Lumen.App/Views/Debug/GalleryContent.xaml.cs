using System.Windows;
using System.Windows.Controls;

namespace Lumen.App.Views.Debug;

/// <summary>
/// Renders every styled control for review. Hidden from normal navigation; hosted by
/// <see cref="GalleryWindow"/> (run with --gallery) or captured headlessly (--gallery-shot).
/// </summary>
public partial class GalleryContent : UserControl
{
    public GalleryContent()
    {
        InitializeComponent();
    }

    /// <summary>Named sections captured individually by the screenshot harness.</summary>
    public IReadOnlyDictionary<string, FrameworkElement> Sections => new Dictionary<string, FrameworkElement>
    {
        ["colors"] = SectionColors,
        ["typography"] = SectionTypography,
        ["buttons"] = SectionButtons,
        ["inputs"] = SectionInputs,
        ["lists"] = SectionLists,
        ["cards"] = SectionCards,
        ["progress"] = SectionProgress,
        ["toasts"] = SectionToasts,
    };

    private void OnOpenDialogClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SampleDialog { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }
}
