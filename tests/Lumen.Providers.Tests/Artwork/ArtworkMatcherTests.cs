using FluentAssertions;
using Lumen.Providers.Artwork;

namespace Lumen.Providers.Tests.Artwork;

public sealed class ArtworkMatcherTests
{
    [Fact]
    public void Exact_title_and_year_scores_highest()
    {
        var exact = ArtworkMatcher.Score("The Matrix", 1999, "The Matrix", 1999);
        var wrongYear = ArtworkMatcher.Score("The Matrix", 2021, "The Matrix", 1999);

        exact.Should().BeGreaterThan(wrongYear);
        exact.Should().BeGreaterThanOrEqualTo(ArtworkMatcher.Threshold);
    }

    [Fact]
    public void Title_with_trailing_year_form_matches()
    {
        // Catalog said "Wonder Woman" + year 1984; the real film is titled "Wonder Woman 1984".
        var score = ArtworkMatcher.Score("Wonder Woman 1984", 2020, "Wonder Woman", 1984);

        score.Should().BeGreaterThanOrEqualTo(ArtworkMatcher.Threshold);
    }

    [Fact]
    public void Unrelated_titles_stay_below_threshold()
    {
        var score = ArtworkMatcher.Score("Cooking with Carla", null, "The Matrix", 1999);

        score.Should().BeLessThan(ArtworkMatcher.Threshold);
    }

    [Fact]
    public void Case_and_diacritics_are_folded()
    {
        var score = ArtworkMatcher.Score("AMÉLIE", 2001, "Amelie", 2001);

        score.Should().BeGreaterThanOrEqualTo(ArtworkMatcher.Threshold);
    }

    [Fact]
    public void Year_off_by_one_still_matches_exact_title()
    {
        // Providers disagree about premiere years across regions constantly.
        var score = ArtworkMatcher.Score("Dark", 2018, "Dark", 2017);

        score.Should().BeGreaterThanOrEqualTo(ArtworkMatcher.Threshold);
    }

    [Theory]
    [InlineData("2019-05-30", 2019)]
    [InlineData("1999", 1999)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("bad", null)]
    public void YearOf_parses_leading_year(string? date, int? expected)
    {
        ArtworkMatcher.YearOf(date).Should().Be(expected);
    }
}
