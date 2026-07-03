using System.Windows.Controls;
using Lumen.App.ViewModels;

namespace Lumen.App.Views;

/// <summary>Movies / Series poster grid (both bound to <see cref="VodLibraryViewModel"/>).</summary>
public partial class VodLibraryView : UserControl
{
    public VodLibraryView()
    {
        InitializeComponent();
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Trigger the next page when within ~2 rows of the bottom (infinite scroll).
        if (DataContext is VodLibraryViewModel vm &&
            e.ExtentHeight > 0 &&
            e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 640 &&
            vm.LoadMoreCommand.CanExecute(null))
        {
            vm.LoadMoreCommand.Execute(null);
        }
    }
}
