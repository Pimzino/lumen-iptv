using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Lumen.App.Resources;
using Lumen.App.Services;
using Lumen.Core.Models;
using Serilog;

namespace Lumen.App.ViewModels;

/// <summary>One entry in the navigation rail.</summary>
public sealed partial class RailItem : ObservableObject
{
    public RailItem(string key, string glyph, string label)
    {
        Key = key;
        Glyph = glyph;
        Label = label;
    }

    public string Key { get; }

    public string Glyph { get; }

    public string Label { get; }

    [ObservableProperty]
    private bool _isActive;
}

/// <summary>Root view model: navigation rail, title bar state, profile switching, toasts.</summary>
public sealed partial class ShellViewModel : ObservableObject,
    IRecipient<ProfileSwitchedMessage>,
    IRecipient<OnboardingCompletedMessage>,
    IRecipient<OnboardingRequiredMessage>,
    IRecipient<AddProfileRequestedMessage>
{
    private readonly ISessionService _session;
    private readonly IToastService _toasts;

    [ObservableProperty]
    private bool _isRailExpanded;

    [ObservableProperty]
    private bool _isShellReady;

    [ObservableProperty]
    private bool _isProfileFlyoutOpen;

    public ShellViewModel(
        NavigationService navigation,
        ISessionService session,
        IToastService toasts,
        Services.Playback.PlaybackService playback,
        IMessenger messenger)
    {
        Navigation = navigation;
        _session = session;
        _toasts = toasts;
        Session = (SessionService)session;
        Playback = playback;

        RailItems =
        [
            new RailItem("home", "", Strings.Nav_Home),
            new RailItem("livetv", "", Strings.Nav_LiveTv),
            new RailItem("guide", "", Strings.Nav_Guide),
            new RailItem("movies", "", Strings.Nav_Movies),
            new RailItem("series", "", Strings.Nav_Series),
            new RailItem("search", "", Strings.Nav_Search),
            new RailItem("favorites", "", Strings.Nav_Favorites),
            new RailItem("settings", "", Strings.Nav_Settings),
        ];

        messenger.RegisterAll(this);
    }

    /// <summary>Bound by the shell's ContentControl (concrete type carries INPC).</summary>
    public NavigationService Navigation { get; }

    /// <summary>Concrete session (INPC) for profile chip bindings.</summary>
    public SessionService Session { get; }

    /// <summary>Playback state for the player layer, mini player, and immersive chrome.</summary>
    public Services.Playback.PlaybackService Playback { get; }

    public ObservableCollection<RailItem> RailItems { get; }

    /// <summary>Live toast queue rendered by the shell overlay.</summary>
    public ObservableCollection<ToastItem> ToastItems => _toasts.Items;

    [RelayCommand]
    private void OpenProfileFlyout() => IsProfileFlyoutOpen = !IsProfileFlyoutOpen;

    public async Task InitializeAsync()
    {
        var hasProfile = await _session.InitializeAsync(CancellationToken.None);
        if (!hasProfile)
        {
            IsShellReady = false;
            Navigation.NavigateTo<OnboardingViewModel>();
            return;
        }

        IsShellReady = true;
        NavigateToSection("home");
    }

    [RelayCommand]
    private void ToggleRail() => IsRailExpanded = !IsRailExpanded;

    [RelayCommand]
    private void Navigate(string? key)
    {
        if (!string.IsNullOrEmpty(key))
        {
            NavigateToSection(key);
        }
    }

    [RelayCommand]
    private async Task SwitchProfileAsync(Profile? profile)
    {
        IsProfileFlyoutOpen = false;
        if (profile is null || profile.Id == _session.CurrentProfile?.Id)
        {
            return;
        }

        try
        {
            await _session.SwitchProfileAsync(profile.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Profile switch failed");
            _toasts.Show(Strings.Format(Strings.Toast_SyncFailedFormat, ex.Message), ToastSeverity.Error);
        }
    }

    [RelayCommand]
    private void AddProfile()
    {
        IsProfileFlyoutOpen = false;
        Navigation.NavigateTo<OnboardingViewModel>(OnboardingViewModel.AddModeParameter);
        SetActiveRail(null);
    }

    public void Receive(ProfileSwitchedMessage message)
    {
        if (!IsShellReady || Navigation.IsCurrent<OnboardingViewModel>())
        {
            return;
        }

        _toasts.Show(Strings.Format(Strings.Toast_ProfileSwitchedFormat, message.Profile.Name));
        NavigateToSection("home");
    }

    public void Receive(OnboardingCompletedMessage message)
    {
        IsShellReady = true;
        _toasts.Show(
            Strings.Format(Strings.Toast_ProfileConnectedFormat, message.Profile.Name), ToastSeverity.Success);
        NavigateToSection("home");
    }

    public void Receive(OnboardingRequiredMessage message)
    {
        IsShellReady = false;
        Navigation.NavigateTo<OnboardingViewModel>();
    }

    public void Receive(AddProfileRequestedMessage message) => AddProfile();

    /// <summary>Navigates a named section and updates rail highlighting.</summary>
    public void NavigateToSection(string key)
    {
        Navigation.ClearBackStack();
        switch (key)
        {
            case "home":
                Navigation.NavigateTo<HomeViewModel>();
                break;
            case "livetv":
                Navigation.NavigateTo<LiveTvViewModel>();
                break;
            case "guide":
                Navigation.NavigateTo<GuideViewModel>();
                break;
            case "movies":
                Navigation.NavigateTo<MoviesViewModel>();
                break;
            case "series":
                Navigation.NavigateTo<SeriesViewModel>();
                break;
            case "search":
                Navigation.NavigateTo<SearchViewModel>();
                break;
            case "favorites":
                Navigation.NavigateTo<FavoritesViewModel>();
                break;
            case "settings":
                Navigation.NavigateTo<SettingsViewModel>();
                break;
            default:
                return;
        }

        SetActiveRail(key);
    }

    private void SetActiveRail(string? key)
    {
        foreach (var item in RailItems)
        {
            item.IsActive = item.Key == key;
        }
    }
}
