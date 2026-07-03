using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Lumen.App.Services;

/// <summary>Implemented by view models that load data when navigated to.</summary>
public interface INavigationAware
{
    /// <summary>
    /// Called after the view model becomes current. The token cancels when the user
    /// navigates away — all page loads must flow it.
    /// </summary>
    Task OnNavigatedToAsync(object? parameter, CancellationToken cancellationToken);

    /// <summary>Called when the view model stops being current.</summary>
    void OnNavigatedFrom();
}

/// <summary>ViewModel-first navigation for the shell content area.</summary>
public interface INavigationService
{
    object? CurrentViewModel { get; }

    bool CanGoBack { get; }

    /// <summary>Resolves a fresh view model, cancels the previous page's loads, and navigates.</summary>
    void NavigateTo<TViewModel>(object? parameter = null)
        where TViewModel : class;

    /// <summary>Returns to the previous page (used by detail pages).</summary>
    void GoBack();

    bool IsCurrent<TViewModel>();
}

/// <summary>
/// Default navigation service. Exposed as an ObservableObject so the shell binds
/// ContentControl.Content directly to <see cref="CurrentViewModel"/>.
/// </summary>
public sealed partial class NavigationService : ObservableObject, INavigationService
{
    private readonly IServiceProvider _services;
    private readonly Stack<(Type ViewModel, object? Parameter)> _backStack = new();

    [ObservableProperty]
    private object? _currentViewModel;

    private CancellationTokenSource? _navigationCts;
    private Type? _currentType;
    private object? _currentParameter;

    public NavigationService(IServiceProvider services)
    {
        _services = services;
    }

    public bool CanGoBack => _backStack.Count > 0;

    public void NavigateTo<TViewModel>(object? parameter = null)
        where TViewModel : class
    {
        if (_currentType is not null)
        {
            _backStack.Push((_currentType, _currentParameter));
        }

        Activate(typeof(TViewModel), parameter);
    }

    public void GoBack()
    {
        if (_backStack.Count == 0)
        {
            return;
        }

        var (type, parameter) = _backStack.Pop();
        Activate(type, parameter);
    }

    private void Activate(Type viewModelType, object? parameter)
    {
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _navigationCts = new CancellationTokenSource();

        (CurrentViewModel as INavigationAware)?.OnNavigatedFrom();

        var viewModel = _services.GetRequiredService(viewModelType);
        _currentType = viewModelType;
        _currentParameter = parameter;
        CurrentViewModel = viewModel;

        if (viewModel is INavigationAware aware)
        {
            _ = RunNavigationAsync(aware, parameter, _navigationCts.Token);
        }
    }

    public bool IsCurrent<TViewModel>() => CurrentViewModel is TViewModel;

    /// <summary>Clears back history. Called on top-level (rail) navigation so Back stays page-local.</summary>
    public void ClearBackStack() => _backStack.Clear();

    private static async Task RunNavigationAsync(
        INavigationAware target, object? parameter, CancellationToken cancellationToken)
    {
        try
        {
            await target.OnNavigatedToAsync(parameter, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Navigated away mid-load; expected.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Page load failed for {ViewModel}", target.GetType().Name);
        }
    }
}
