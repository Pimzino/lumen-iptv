using Lumen.Core.Models;

namespace Lumen.Core.Abstractions;

/// <summary>A single row in search results, spanning channels, VOD, and EPG.</summary>
public sealed record SearchHit(
    ContentKind Kind,
    string ItemKey,
    string Title,
    string? Subtitle,
    string? ImageUrl);

/// <summary>Grouped search results across a profile's catalog and guide.</summary>
public sealed record SearchResults(
    IReadOnlyList<SearchHit> Channels,
    IReadOnlyList<SearchHit> Movies,
    IReadOnlyList<SearchHit> Series,
    IReadOnlyList<SearchHit> Programmes)
{
    public static readonly SearchResults Empty = new([], [], [], []);

    public int TotalCount => Channels.Count + Movies.Count + Series.Count + Programmes.Count;

    public bool IsEmpty => TotalCount == 0;
}

/// <summary>
/// Cross-catalog search for a profile: channels + VOD (movies/series) + upcoming EPG programme
/// titles. Each group is capped so results stay fast and scannable.
/// </summary>
public interface ISearchRepository
{
    Task<SearchResults> SearchAsync(long profileId, string query, long nowUnix, int perGroupLimit, CancellationToken cancellationToken);
}
