using FluentAssertions;
using Lumen.Providers.Xtream;

namespace Lumen.Providers.Tests.Xtream;

public sealed class XtreamTextTests
{
    [Theory]
    [InlineData("RXZlbmluZyBOZXdz", "Evening News")]
    [InlineData("VGhlIGRheSdzIGV2ZW50cy4=", "The day's events.")]
    [InlineData("UGxhaW4=", "Plain")]
    public void Decodes_GenuineBase64(string input, string expected) =>
        XtreamText.DecodeMaybeBase64(input).Should().Be(expected);

    [Theory]
    [InlineData("NEWS")] // valid base64 alphabet but decodes to control bytes — keep original
    [InlineData("Regular title")]
    [InlineData("Title!")]
    [InlineData("")]
    [InlineData(null)]
    public void LeavesNonBase64Untouched(string? input) =>
        XtreamText.DecodeMaybeBase64(input).Should().Be(input);
}
