using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumen.Providers.Xtream.Json;

/// <summary>
/// Converters tolerant of the JSON Xtream panels actually emit: numbers as strings,
/// strings as numbers, empty strings for missing values, booleans as 0/1, and
/// occasional objects where scalars belong. A parse failure yields null, never an exception.
/// </summary>
internal sealed class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var integer)
                    ? integer.ToString(CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString(CultureInfo.InvariantCulture);
            case JsonTokenType.True:
                return "true";
            case JsonTokenType.False:
                return "false";
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

internal sealed class FlexibleLongConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var integer))
                {
                    return integer;
                }

                return (long)reader.GetDouble();
            case JsonTokenType.String:
                var text = reader.GetString()?.Trim();
                if (string.IsNullOrEmpty(text) || string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating)
                    ? (long)floating
                    : null;
            case JsonTokenType.True:
                return 1;
            case JsonTokenType.False:
                return 0;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteNumberValue(value.Value);
        }
    }
}

internal sealed class FlexibleIntConverter : JsonConverter<int?>
{
    private static readonly FlexibleLongConverter Inner = new();

    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = Inner.Read(ref reader, typeof(long?), options);
        return value is null ? null : (int)Math.Clamp(value.Value, int.MinValue, int.MaxValue);
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteNumberValue(value.Value);
        }
    }
}

internal sealed class FlexibleDoubleConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.GetDouble();
            case JsonTokenType.String:
                var text = reader.GetString()?.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    return null;
                }

                // Some panels localize decimals ("7,5").
                text = text.Replace(',', '.');
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteNumberValue(value.Value);
        }
    }
}

internal sealed class FlexibleBoolConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
                return false;
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var number) && number != 0;
            case JsonTokenType.String:
                var text = reader.GetString()?.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    return null;
                }

                if (text is "1")
                {
                    return true;
                }

                if (text is "0")
                {
                    return false;
                }

                return bool.TryParse(text, out var parsed) ? parsed : null;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteBooleanValue(value.Value);
        }
    }
}

/// <summary>Accepts a JSON array of strings, a single string, or garbage (→ empty).</summary>
internal sealed class FlexibleStringArrayConverter : JsonConverter<IReadOnlyList<string>?>
{
    public override IReadOnlyList<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var single = reader.GetString();
                return string.IsNullOrWhiteSpace(single) ? [] : [single];
            case JsonTokenType.StartArray:
                var items = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var item = reader.GetString();
                        if (!string.IsNullOrWhiteSpace(item))
                        {
                            items.Add(item);
                        }
                    }
                    else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    {
                        reader.Skip();
                    }
                }

                return items;
            default:
                reader.Skip();
                return [];
        }
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string>? value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value ?? [])
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }
}
