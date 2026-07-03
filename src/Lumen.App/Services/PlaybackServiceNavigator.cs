using Lumen.App.Services.Playback;
using Lumen.Core.Models;

namespace Lumen.App.Services;

/// <summary>
/// Thin seam letting non-Live-TV pages (Guide, Search, Favorites, Home) start playback and
/// jump into the full player without taking a dependency on the whole Live TV view model.
/// </summary>
public sealed class PlaybackServiceNavigator
{
    private readonly PlaybackService _playback;

    public PlaybackServiceNavigator(PlaybackService playback)
    {
        _playback = playback;
    }

    /// <summary>Plays a channel and enters the full player.</summary>
    public void PlayChannel(Channel channel, IReadOnlyList<Channel>? zapList = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _ = _playback.PlayChannelAsync(channel, zapList, preview: false, CancellationToken.None);
        _playback.EnterFullPlayer();
    }
}
