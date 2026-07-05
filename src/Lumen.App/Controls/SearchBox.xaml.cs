using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Lumen.App.ViewModels;

namespace Lumen.App.Controls;

/// <summary>
/// Title-bar search entry point: a text field whose results resolve live into a dropdown
/// underneath it (no page navigation). Bound to a shell-owned <see cref="SearchViewModel"/>.
/// </summary>
public partial class SearchBox : UserControl
{
    public SearchBox()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private SearchViewModel? ViewModel => DataContext as SearchViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SearchViewModel oldVm)
        {
            oldVm.FocusRequested = null;
        }

        if (e.NewValue is SearchViewModel newVm)
        {
            newVm.FocusRequested = FocusTextBox;
        }
    }

    private void FocusTextBox() =>
        Dispatcher.BeginInvoke(
            () =>
            {
                Box.Focus();
                Box.CaretIndex = Box.Text.Length;
                Keyboard.Focus(Box);
            },
            System.Windows.Threading.DispatcherPriority.Input);

    // Re-focusing the field (after clicking away) reopens the dropdown when a query is present.
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (ViewModel is { HasQuery: true } vm)
        {
            vm.IsOpen = true;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (ViewModel is not { } vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape when vm.IsOpen || vm.HasQuery:
                vm.Query = string.Empty;
                vm.IsOpen = false;
                e.Handled = true;
                break;

            // From the text box, Down arrow drops keyboard focus into the first result.
            case Key.Down when vm.IsOpen && vm.HasResults && Box.IsKeyboardFocused:
                if (FindFirstFocusable(ResultsPopup.Child) is { } first)
                {
                    first.Focus();
                    e.Handled = true;
                }

                break;
        }
    }

    private static IInputElement? FindFirstFocusable(DependencyObject? root)
    {
        if (root is null)
        {
            return null;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ButtonBase { Focusable: true } button)
            {
                return button;
            }

            if (FindFirstFocusable(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
