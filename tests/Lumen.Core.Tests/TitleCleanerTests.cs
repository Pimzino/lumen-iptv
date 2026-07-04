using FluentAssertions;
using Lumen.Core;

namespace Lumen.Core.Tests;

public sealed class TitleCleanerTests
{
    [Theory]
    [InlineData("The Matrix (1999)", "The Matrix", 1999)]
    [InlineData("EN| The Matrix (1999) [4K]", "The Matrix", 1999)]
    [InlineData("EN - Inception", "Inception", null)]
    [InlineData("NF: Stranger Things S04", "Stranger Things", null)]
    [InlineData("The.Big.Lebowski.1998.1080p", "The Big Lebowski", 1998)]
    [InlineData("Blade Runner 2049 (2017)", "Blade Runner 2049", 2017)]
    [InlineData("Wonder Woman 1984", "Wonder Woman", 1984)]
    [InlineData("1917", "1917", null)]
    [InlineData("Interstellar [MULTI-SUB] {HDR}", "Interstellar", null)]
    [InlineData("VOD | 4K | Dune Part Two (2024) HEVC", "Dune Part Two", 2024)]
    [InlineData("Breaking Bad Season 2", "Breaking Bad", null)]
    [InlineData("Dark S01E01", "Dark", null)]
    [InlineData("Oppenheimer 2023 IMAX", "Oppenheimer", 2023)]
    public void Clean_reduces_catalog_names(string raw, string expectedTitle, int? expectedYear)
    {
        var clean = TitleCleaner.Clean(raw);

        clean.Title.Should().Be(expectedTitle);
        clean.Year.Should().Be(expectedYear);
    }

    [Fact]
    public void Clean_falls_back_to_raw_when_cleaning_erases_everything()
    {
        var clean = TitleCleaner.Clean("[4K]");

        clean.Title.Should().Be("[4K]");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Clean_handles_blank_input(string? raw)
    {
        TitleCleaner.Clean(raw).Title.Should().BeEmpty();
    }

    [Fact]
    public void Clean_keeps_dotted_abbreviations_in_spaced_names()
    {
        // Enough real spaces → the dots are part of the title, not release separators.
        TitleCleaner.Clean("S.W.A.T. Under Siege (2017)").Title.Should().Be("S.W.A.T. Under Siege");
    }

    [Fact]
    public void Clean_rejects_implausible_years()
    {
        var clean = TitleCleaner.Clean("Cyber City 2199");

        clean.Title.Should().Be("Cyber City 2199");
        clean.Year.Should().BeNull();
    }
}
