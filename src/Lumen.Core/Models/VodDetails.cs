namespace Lumen.Core.Models;

/// <summary>Extended metadata for a movie, fetched on demand from the provider.</summary>
public sealed class MovieDetails
{
    public string? Plot { get; set; }

    public string? Genre { get; set; }

    public string? Director { get; set; }

    public string? Cast { get; set; }

    public int? DurationSeconds { get; set; }

    public string? BackdropUrl { get; set; }

    public string? TrailerUrl { get; set; }

    public double? Rating { get; set; }

    public string? ReleaseDate { get; set; }

    public string? ContainerExtension { get; set; }
}

/// <summary>Extended metadata for a series, including its episode tree.</summary>
public sealed class SeriesDetails
{
    public string? Plot { get; set; }

    public string? Genre { get; set; }

    public string? Cast { get; set; }

    public string? Director { get; set; }

    public double? Rating { get; set; }

    public string? ReleaseDate { get; set; }

    public string? BackdropUrl { get; set; }

    public IReadOnlyList<SeriesSeason> Seasons { get; set; } = [];
}

/// <summary>A season grouping of episodes.</summary>
public sealed class SeriesSeason
{
    public int Number { get; set; }

    public IReadOnlyList<SeriesEpisode> Episodes { get; set; } = [];
}

/// <summary>A playable series episode.</summary>
public sealed class SeriesEpisode
{
    /// <summary>Provider episode id used to build the stream URL.</summary>
    public string ProviderEpisodeId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public int Season { get; set; }

    public int Number { get; set; }

    public string? Plot { get; set; }

    public int? DurationSeconds { get; set; }

    public string? PosterUrl { get; set; }

    public string? ContainerExtension { get; set; }

    public double? Rating { get; set; }

    public string? AirDate { get; set; }
}
