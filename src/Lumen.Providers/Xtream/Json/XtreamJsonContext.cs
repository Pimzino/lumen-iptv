using System.Text.Json.Serialization;

namespace Lumen.Providers.Xtream.Json;

/// <summary>Source-generated serializer metadata for all Xtream payloads.</summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(XtreamAuthResponse))]
[JsonSerializable(typeof(XtreamCategory))]
[JsonSerializable(typeof(XtreamLiveStream))]
[JsonSerializable(typeof(XtreamVodStream))]
[JsonSerializable(typeof(XtreamSeries))]
[JsonSerializable(typeof(XtreamVodInfo))]
[JsonSerializable(typeof(XtreamSeriesInfo))]
[JsonSerializable(typeof(XtreamEpisode))]
[JsonSerializable(typeof(List<XtreamEpisode>))]
[JsonSerializable(typeof(XtreamShortEpgResponse))]
internal sealed partial class XtreamJsonContext : JsonSerializerContext
{
}
