using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumen.Providers.Xtream.Json;

/// <summary>
/// get_series_info "episodes" arrives as either an object keyed by season
/// (<c>{"1":[…],"2":[…]}</c>) or a bare array of season arrays. Individual episodes
/// that fail to parse are skipped, never fatal.
/// </summary>
internal sealed class XtreamEpisodesConverter : JsonConverter<Dictionary<string, List<XtreamEpisode>>?>
{
    public override Dictionary<string, List<XtreamEpisode>>? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
            {
                var result = new Dictionary<string, List<XtreamEpisode>>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        continue;
                    }

                    var season = reader.GetString() ?? "0";
                    reader.Read();
                    result[season] = ReadEpisodeList(ref reader);
                }

                return result;
            }

            case JsonTokenType.StartArray:
            {
                var result = new Dictionary<string, List<XtreamEpisode>>();
                var index = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    index++;
                    result[index.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                        ReadEpisodeList(ref reader);
                }

                return result;
            }

            default:
                reader.Skip();
                return null;
        }
    }

    private static List<XtreamEpisode> ReadEpisodeList(ref Utf8JsonReader reader)
    {
        var episodes = new List<XtreamEpisode>();
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return episodes;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            using var element = JsonDocument.ParseValue(ref reader);
            try
            {
                var episode = element.RootElement.Deserialize(XtreamJsonContext.Default.XtreamEpisode);
                if (episode is not null)
                {
                    episodes.Add(episode);
                }
            }
            catch (JsonException)
            {
                // Skip the malformed episode; the rest of the season still loads.
            }
        }

        return episodes;
    }

    public override void Write(
        Utf8JsonWriter writer, Dictionary<string, List<XtreamEpisode>>? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Serializing Xtream episode maps is not supported.");
}
