using Lumen.Core.Models;

namespace Lumen.App;

/// <summary>Sent after the active profile changes; pages reload their data.</summary>
public sealed record ProfileSwitchedMessage(Profile Profile);

/// <summary>Sent after an EPG import completes for the active profile.</summary>
public sealed record EpgRefreshedMessage(long ProfileId, long Programmes);

/// <summary>Sent when playback moves to a different channel.</summary>
public sealed record ChannelChangedMessage(Channel Channel);

/// <summary>Sent after a catalog (channels/VOD) sync completes for the active profile.</summary>
public sealed record CatalogRefreshedMessage(long ProfileId);

/// <summary>Sent when onboarding finishes; the shell restores its chrome and goes Home.</summary>
public sealed record OnboardingCompletedMessage(Profile Profile);

/// <summary>Sent when the app needs onboarding (no profiles); the shell hides its chrome.</summary>
public sealed record OnboardingRequiredMessage;

/// <summary>Sent when a page asks for the add-profile flow (shell owns navigation).</summary>
public sealed record AddProfileRequestedMessage;
