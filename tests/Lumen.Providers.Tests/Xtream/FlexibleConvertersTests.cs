using System.Text.Json;
using FluentAssertions;
using Lumen.Providers.Xtream;
using Lumen.Providers.Xtream.Json;

namespace Lumen.Providers.Tests.Xtream;

public sealed class FlexibleConvertersTests
{
    private static XtreamLiveStream ParseStream(string json) =>
        JsonSerializer.Deserialize(json, XtreamJsonContext.Default.XtreamLiveStream)!;

    private static XtreamUserInfo ParseUser(string json) =>
        JsonSerializer.Deserialize(json, XtreamJsonContext.Default.XtreamUserInfo)!;

    [Theory]
    [InlineData("""{"stream_id": 42}""", "42")]
    [InlineData("""{"stream_id": "42"}""", "42")]
    [InlineData("""{"stream_id": 42.5}""", "42.5")]
    [InlineData("""{"stream_id": true}""", "true")]
    [InlineData("""{"stream_id": false}""", "false")]
    [InlineData("""{"stream_id": {"nested": 1}}""", null)]
    [InlineData("""{"stream_id": [1,2]}""", null)]
    [InlineData("""{"stream_id": null}""", null)]
    public void FlexibleString_CoercesAnyScalar(string json, string? expected) =>
        ParseStream(json).StreamId.Should().Be(expected);

    [Theory]
    [InlineData("""{"added": 100}""", 100L)]
    [InlineData("""{"added": "100"}""", 100L)]
    [InlineData("""{"added": "100.9"}""", 100L)]
    [InlineData("""{"added": 100.9}""", 100L)]
    [InlineData("""{"added": ""}""", null)]
    [InlineData("""{"added": "null"}""", null)]
    [InlineData("""{"added": "abc"}""", null)]
    [InlineData("""{"added": true}""", 1L)]
    [InlineData("""{"added": false}""", 0L)]
    [InlineData("""{"added": {}}""", null)]
    public void FlexibleLong_CoercesAnyScalar(string json, long? expected) =>
        ParseStream(json).AddedUnix.Should().Be(expected);

    [Theory]
    [InlineData("""{"tv_archive": 1}""", true)]
    [InlineData("""{"tv_archive": 0}""", false)]
    [InlineData("""{"tv_archive": "1"}""", true)]
    [InlineData("""{"tv_archive": "0"}""", false)]
    [InlineData("""{"tv_archive": true}""", true)]
    [InlineData("""{"tv_archive": false}""", false)]
    [InlineData("""{"tv_archive": "true"}""", true)]
    [InlineData("""{"tv_archive": ""}""", null)]
    [InlineData("""{"tv_archive": "maybe"}""", null)]
    [InlineData("""{"tv_archive": []}""", null)]
    public void FlexibleBool_CoercesAnyScalar(string json, bool? expected) =>
        ParseStream(json).HasArchive.Should().Be(expected);

    [Theory]
    [InlineData("""{"max_connections": 2}""", 2)]
    [InlineData("""{"max_connections": "2"}""", 2)]
    [InlineData("""{"max_connections": ""}""", null)]
    public void FlexibleInt_CoercesAnyScalar(string json, int? expected) =>
        ParseUser(json).MaxConnections.Should().Be(expected);

    [Theory]
    [InlineData("""{"allowed_output_formats": ["ts","m3u8"]}""", 2)]
    [InlineData("""{"allowed_output_formats": "ts"}""", 1)]
    [InlineData("""{"allowed_output_formats": [null, "ts", {"x":1}, ["y"]]}""", 1)]
    [InlineData("""{"allowed_output_formats": 42}""", 0)]
    public void FlexibleStringArray_ToleratesAnyShape(string json, int expectedCount) =>
        ParseUser(json).AllowedOutputFormats.Should().HaveCount(expectedCount);

    [Fact]
    public void FlexibleDouble_ParsesNumberToken()
    {
        var vod = JsonSerializer.Deserialize(
            """{"rating": 8.5}""", XtreamJsonContext.Default.XtreamVodStream)!;
        vod.Rating.Should().Be(8.5);
    }

    [Fact]
    public void FlexibleDouble_RejectsGarbageQuietly()
    {
        var vod = JsonSerializer.Deserialize(
            """{"rating": {"x": 1}}""", XtreamJsonContext.Default.XtreamVodStream)!;
        vod.Rating.Should().BeNull();
    }

    [Fact]
    public void Serialization_WritesScalarsBack()
    {
        var stream = new XtreamLiveStream
        {
            StreamId = "42",
            Number = 3,
            Name = "Test",
            AddedUnix = 100,
            HasArchive = true,
        };
        var json = JsonSerializer.Serialize(stream, XtreamJsonContext.Default.XtreamLiveStream);
        json.Should().Contain("\"stream_id\":\"42\"").And.Contain("\"added\":100").And.Contain("\"tv_archive\":true");

        var empty = new XtreamLiveStream();
        var emptyJson = JsonSerializer.Serialize(empty, XtreamJsonContext.Default.XtreamLiveStream);
        emptyJson.Should().Contain("\"added\":null").And.Contain("\"tv_archive\":null");

        var user = new XtreamUserInfo { MaxConnections = 2, AllowedOutputFormats = ["ts"] };
        var userJson = JsonSerializer.Serialize(user, XtreamJsonContext.Default.XtreamUserInfo);
        userJson.Should().Contain("\"max_connections\":2").And.Contain("[\"ts\"]");

        var vod = new XtreamVodStream { Rating = 7.5 };
        JsonSerializer.Serialize(vod, XtreamJsonContext.Default.XtreamVodStream)
            .Should().Contain("\"rating\":7.5");
        JsonSerializer.Serialize(new XtreamVodStream(), XtreamJsonContext.Default.XtreamVodStream)
            .Should().Contain("\"rating\":null");
        JsonSerializer.Serialize(new XtreamUserInfo { MaxConnections = null }, XtreamJsonContext.Default.XtreamUserInfo)
            .Should().Contain("\"max_connections\":null");
    }

    [Fact]
    public void LargeBase64_UsesHeapPath()
    {
        var text = new string('a', 600);
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
        XtreamText.DecodeMaybeBase64(encoded).Should().Be(text);
    }

    [Fact]
    public void Base64WithReplacementChar_IsLeftUntouched()
    {
        // 0xFF is not valid UTF-8; the decode heuristic must reject it.
        var encoded = Convert.ToBase64String([0xFF, 0xFE, 0xFD, 0xFC]);
        XtreamText.DecodeMaybeBase64(encoded).Should().Be(encoded);
    }
}
