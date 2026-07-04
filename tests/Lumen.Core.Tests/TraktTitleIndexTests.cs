using FluentAssertions;
using Lumen.Core.Models;

namespace Lumen.Core.Tests;

/// <summary>
/// The zero-API-call title join: messy provider names folded through
/// <see cref="TitleCleaner"/> + <see cref="NameNormalizer"/> against Trakt snapshot titles.
/// </summary>
public sealed class TraktTitleIndexTests
{
    private static readonly TraktWatchedItem Matrix = new()
    {
        MediaType = TraktMediaType.Movie, TraktId = 1, TmdbId = 603, ImdbId = "tt0133093",
        Title = "The Matrix", Year = 1999, Plays = 2, LastWatchedUtc = 100,
    };

    private static readonly TraktWatchedItem Dune1984 = new()
    {
        MediaType = TraktMediaType.Movie, TraktId = 2, TmdbId = 841, Title = "Dune", Year = 1984, Plays = 1, LastWatchedUtc = 100,
    };

    private static readonly TraktWatchedItem Dune2021 = new()
    {
        MediaType = TraktMediaType.Movie, TraktId = 3, TmdbId = 438631, Title = "Dune", Year = 2021, Plays = 1, LastWatchedUtc = 100,
    };

    private static readonly TraktWatchedItem BearS1E1 = new()
    {
        MediaType = TraktMediaType.Episode, TraktId = 50, TmdbId = 136315, Title = "The Bear", Year = 2022,
        Season = 1, EpisodeNumber = 1, Plays = 1, LastWatchedUtc = 100,
    };

    private static readonly TraktWatchedItem BearS1E2 = new()
    {
        MediaType = TraktMediaType.Episode, TraktId = 50, TmdbId = 136315, Title = "The Bear", Year = 2022,
        Season = 1, EpisodeNumber = 2, Plays = 1, LastWatchedUtc = 100,
    };

    private static string Fold(string providerName) => NameNormalizer.Normalize(TitleCleaner.Clean(providerName).Title);

    [Fact]
    public void MessyProviderNames_JoinToTheRightMovie()
    {
        var index = TraktTitleIndex.Build([Matrix, Dune1984, Dune2021]);

        var clean = TitleCleaner.Clean("EN| The.Matrix.(1999) [4K HEVC]");
        var hit = index.Find(TraktMediaType.Movie, NameNormalizer.Normalize(clean.Title), clean.Year);

        hit.Should().NotBeNull();
        hit!.TraktId.Should().Be(1);
    }

    [Fact]
    public void SameTitle_DifferentYears_ResolveByYear_AndAmbiguityIsSkipped()
    {
        var index = TraktTitleIndex.Build([Matrix, Dune1984, Dune2021]);

        index.Find(TraktMediaType.Movie, Fold("Dune (2021) MULTI"), 2021)!.TraktId.Should().Be(3);
        index.Find(TraktMediaType.Movie, Fold("Dune 1984"), 1984)!.TraktId.Should().Be(2);
        index.Find(TraktMediaType.Movie, Fold("Dune"), null).Should().BeNull("two candidates with no year is ambiguous");
    }

    [Fact]
    public void YearTolerance_IsPlusMinusOne()
    {
        var index = TraktTitleIndex.Build([Matrix]);

        index.Find(TraktMediaType.Movie, Fold("The Matrix"), 2000).Should().NotBeNull();
        index.Find(TraktMediaType.Movie, Fold("The Matrix"), 2005).Should().BeNull();
    }

    [Fact]
    public void EpisodeRows_CollapseToOneShowIdentity_SeparateFromMovies()
    {
        var index = TraktTitleIndex.Build([BearS1E1, BearS1E2, Matrix]);

        var show = index.Find(TraktMediaType.Episode, Fold("US - The Bear S01"), 2022);
        show.Should().NotBeNull();
        show!.TraktId.Should().Be(50);

        index.Find(TraktMediaType.Movie, Fold("The Bear"), 2022).Should().BeNull("shows don't answer movie lookups");
    }

    [Fact]
    public void Enrich_FillsTraktIdFromProviderIds()
    {
        var index = TraktTitleIndex.Build([Matrix, BearS1E1]);

        var byTmdb = new TraktMatch { TmdbId = 603 };
        index.Enrich(byTmdb, TraktMediaType.Movie);
        byTmdb.TraktId.Should().Be(1);
        byTmdb.ImdbId.Should().Be("tt0133093");

        var byImdb = new TraktMatch { ImdbId = "tt0133093" };
        index.Enrich(byImdb, TraktMediaType.Movie);
        byImdb.TraktId.Should().Be(1);

        var unknown = new TraktMatch { TmdbId = 999999 };
        index.Enrich(unknown, TraktMediaType.Movie);
        unknown.TraktId.Should().BeNull();
    }
}
