using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Lumen.Providers.Xtream;
using Serilog;

namespace Lumen.App.Services;

/// <summary>Owns the profile list and the active profile, persisting the choice across restarts.</summary>
public interface ISessionService
{
    Profile? CurrentProfile { get; }

    IReadOnlyList<Profile> Profiles { get; }

    /// <summary>Loads profiles and restores the last active one. True when a profile is active.</summary>
    Task<bool> InitializeAsync(CancellationToken cancellationToken);

    Task SwitchProfileAsync(long profileId, CancellationToken cancellationToken);

    /// <summary>Inserts the profile (protecting the password) and makes it active.</summary>
    Task<long> AddProfileAsync(Profile profile, string? plainPassword, CancellationToken cancellationToken);

    /// <summary>
    /// Persists edits to an existing profile. A non-empty <paramref name="newPlainPassword"/>
    /// replaces the stored password; null keeps it.
    /// </summary>
    Task UpdateProfileAsync(Profile profile, string? newPlainPassword, CancellationToken cancellationToken);

    Task RemoveProfileAsync(long profileId, CancellationToken cancellationToken);

    /// <summary>Decrypts stored Xtream credentials. Null for M3U profiles.</summary>
    XtreamCredentials? GetXtreamCredentials(Profile profile);
}

/// <summary>Default <see cref="ISessionService"/>.</summary>
public sealed partial class SessionService : ObservableObject, ISessionService
{
    private const string ActiveProfileKey = "active_profile_id";

    /// <summary>Colors offered for profile avatars (shared with the profile edit page).</summary>
    internal static readonly string[] AvatarPalette =
        ["#4C8DFF", "#47CD89", "#F97066", "#B692F6", "#F7B27A", "#5FD4D6"];

    private readonly IProfileRepository _profiles;
    private readonly ISettingsRepository _settings;
    private readonly ICredentialProtector _protector;
    private readonly IClock _clock;
    private readonly IMessenger _messenger;

    [ObservableProperty]
    private Profile? _currentProfile;

    [ObservableProperty]
    private IReadOnlyList<Profile> _profilesList = [];

    public SessionService(
        IProfileRepository profiles,
        ISettingsRepository settings,
        ICredentialProtector protector,
        IClock clock,
        IMessenger messenger)
    {
        _profiles = profiles;
        _settings = settings;
        _protector = protector;
        _clock = clock;
        _messenger = messenger;
    }

    public IReadOnlyList<Profile> Profiles => ProfilesList;

    public async Task<bool> InitializeAsync(CancellationToken cancellationToken)
    {
        ProfilesList = await _profiles.GetAllAsync(cancellationToken);
        if (ProfilesList.Count == 0)
        {
            CurrentProfile = null;
            return false;
        }

        var activeRaw = await _settings.GetAsync(0, ActiveProfileKey, cancellationToken);
        var active = long.TryParse(activeRaw, out var id)
            ? ProfilesList.FirstOrDefault(p => p.Id == id)
            : null;
        CurrentProfile = active ?? ProfilesList[0];
        Log.Information("Session initialized with profile {Profile}", CurrentProfile.Name);
        return true;
    }

    public async Task SwitchProfileAsync(long profileId, CancellationToken cancellationToken)
    {
        ProfilesList = await _profiles.GetAllAsync(cancellationToken);
        var profile = ProfilesList.FirstOrDefault(p => p.Id == profileId)
            ?? throw new InvalidOperationException($"Profile {profileId} does not exist.");

        CurrentProfile = profile;
        await _settings.SetAsync(0, ActiveProfileKey,
            profileId.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        await _profiles.TouchLastUsedAsync(profileId, _clock.UtcNow.ToUnixTimeSeconds(), cancellationToken);

        _messenger.Send(new ProfileSwitchedMessage(profile));
    }

    public async Task<long> AddProfileAsync(Profile profile, string? plainPassword, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!string.IsNullOrEmpty(plainPassword))
        {
            profile.PasswordProtected = _protector.Protect(plainPassword);
        }

        profile.CreatedUtc = _clock.UtcNow.ToUnixTimeSeconds();
        profile.AvatarColor ??= AvatarPalette[Random.Shared.Next(AvatarPalette.Length)];

        var id = await _profiles.InsertAsync(profile, cancellationToken);
        await SwitchProfileAsync(id, cancellationToken);
        return id;
    }

    public async Task UpdateProfileAsync(
        Profile profile, string? newPlainPassword, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!string.IsNullOrEmpty(newPlainPassword))
        {
            profile.PasswordProtected = _protector.Protect(newPlainPassword);
        }

        await _profiles.UpdateAsync(profile, cancellationToken);
        ProfilesList = await _profiles.GetAllAsync(cancellationToken);

        // Profile is a plain POCO, so bindings only see edits to the current profile when the
        // instance itself is replaced.
        if (CurrentProfile?.Id == profile.Id)
        {
            CurrentProfile = ProfilesList.First(p => p.Id == profile.Id);
        }
    }

    public async Task RemoveProfileAsync(long profileId, CancellationToken cancellationToken)
    {
        await _profiles.DeleteAsync(profileId, cancellationToken);
        ProfilesList = await _profiles.GetAllAsync(cancellationToken);

        if (CurrentProfile?.Id == profileId)
        {
            if (ProfilesList.Count > 0)
            {
                await SwitchProfileAsync(ProfilesList[0].Id, cancellationToken);
            }
            else
            {
                CurrentProfile = null;
                await _settings.DeleteAsync(0, ActiveProfileKey, cancellationToken);
            }
        }
    }

    public XtreamCredentials? GetXtreamCredentials(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Kind != ProfileKind.Xtream ||
            string.IsNullOrWhiteSpace(profile.ServerUrl) ||
            string.IsNullOrWhiteSpace(profile.Username) ||
            profile.PasswordProtected is null)
        {
            return null;
        }

        return new XtreamCredentials(
            profile.ServerUrl,
            profile.Username,
            _protector.Unprotect(profile.PasswordProtected));
    }
}
