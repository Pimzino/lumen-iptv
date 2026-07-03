using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lumen.App.ViewModels;

namespace Lumen.App.Views;

/// <summary>Global search page.</summary>
public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => FocusSearchBox();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SearchViewModel oldVm)
        {
            oldVm.FocusRequested = null;
        }

        if (e.NewValue is SearchViewModel newVm)
        {
            newVm.FocusRequested = FocusSearchBox;
        }
    }

    private void FocusSearchBox() =>
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text.Length;
            Keyboard.Focus(SearchBox);
        }, System.Windows.Threading.DispatcherPriority.Input);
}
