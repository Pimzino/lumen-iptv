using FluentAssertions;
using Lumen.Core.Downloads;

namespace Lumen.Core.Tests.Downloads;

public sealed class SoutStringTests
{
    [Fact]
    public void BuildFileRecord_UsesForwardSlashes_AndSingleQuotesTheDestination()
    {
        var sout = SoutString.BuildFileRecord(@"C:\Users\me\Lumen\downloads\1\movie.part.ts");

        sout.Should().Be(":sout=#std{access=file,mux=ts,dst='C:/Users/me/Lumen/downloads/1/movie.part.ts'}");
    }

    [Fact]
    public void BuildFileRecord_QuotesPathsWithSpacesAndCommas()
    {
        var sout = SoutString.BuildFileRecord(@"C:\dl\Lock, Stock and Two Barrels.part.ts");

        // The single quotes keep the space/comma from splitting the config chain.
        sout.Should().Contain("dst='C:/dl/Lock, Stock and Two Barrels.part.ts'");
        sout.Should().StartWith(":sout=#std{access=file,mux=ts,");
    }

    [Fact]
    public void BuildFileRecord_RejectsBlankPath()
    {
        var act = () => SoutString.BuildFileRecord("  ");

        act.Should().Throw<ArgumentException>();
    }
}
