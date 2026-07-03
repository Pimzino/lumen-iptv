using System.Windows;
using System.Windows.Controls;
using Lumen.App.Controls.Epg;
using Lumen.App.Services;
using Lumen.App.ViewModels;

namespace Lumen.App.Views;

/// <summary>EPG guide page. Visual-only glue between the custom panel and the view model.</summary>
public partial class GuideView : UserControl
{
    public GuideView()
    {
        InitializeComponent();
        Guide.ProgrammeActivated += OnProgrammeActivated;
        DataContextChanged += OnDataContextChanged;
    }

    private GuideViewModel? ViewModel => DataContext as GuideViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is GuideViewModel oldVm)
        {
            oldVm.ScrollToTimeRequested = null;
        }

        if (e.NewValue is GuideViewModel newVm)
        {
            // The VM asks the panel to scroll (jump-to-now, day change); done here so the
            // VM stays free of visual-tree references.
            newVm.ScrollToTimeRequested = moment =>
                Dispatcher.InvokeAsync(() => Guide.ScrollToTime(moment));
        }
    }

    private void OnProgrammeActivated(object? sender, ProgrammeActivatedEventArgs e)
    {
        ViewModel?.ShowProgramme(e.Row, e.Programme);
    }

    private void OnAddReminderClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProgramme is { } detail)
        {
            // Reminders are a v1 stretch; acknowledge with a toast (spec lists reminder in the
            // flyout, recording is a non-goal).
            App.GetService<IToastService>().Show(
                Lumen.App.Resources.Strings.Format(
                    Lumen.App.Resources.Strings.Guide_ReminderSetFormat, detail.Title),
                ToastSeverity.Success);
            ViewModel.CloseFlyoutCommand.Execute(null);
        }
    }
}
