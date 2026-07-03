namespace Lumen.Core.Models;

/// <summary>A content grouping (Xtream category or M3U group-title) within a profile.</summary>
public sealed class Category
{
    public long Id { get; set; }

    public long ProfileId { get; set; }

    /// <summary>Provider-side identifier: Xtream category_id or the raw M3U group-title.</summary>
    public string ProviderCategoryId { get; set; } = string.Empty;

    /// <summary>Kind as classified at import time.</summary>
    public ContentKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    /// <summary>User override of the heuristic classification for M3U groups.</summary>
    public ContentKind? ContentKindOverride { get; set; }

    /// <summary>The kind to treat this category as, honoring the user override.</summary>
    public ContentKind EffectiveKind => ContentKindOverride ?? Kind;
}
