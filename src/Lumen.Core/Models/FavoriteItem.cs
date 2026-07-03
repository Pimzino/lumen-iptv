namespace Lumen.Core.Models;

/// <summary>A favorited channel, movie, or series.</summary>
public sealed class FavoriteItem
{
    public long Id { get; set; }

    public long ProfileId { get; set; }

    public ContentKind ItemKind { get; set; }

    /// <summary>Stable key: channel row id for live, provider item id for VOD/series.</summary>
    public string ItemKey { get; set; } = string.Empty;

    /// <summary>When the item was favorited, unix seconds (UTC).</summary>
    public long AddedUtc { get; set; }
}
