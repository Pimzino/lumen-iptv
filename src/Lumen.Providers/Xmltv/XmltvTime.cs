namespace Lumen.Providers.Xmltv;

/// <summary>
/// Fast, allocation-free parser for XMLTV timestamps: <c>yyyyMMddHHmmss ±HHMM</c>,
/// with seconds/minutes/offset optional and a UTC default when no offset is given.
/// </summary>
public static class XmltvTime
{
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Parses to unix seconds (UTC). Returns false on malformed input.</summary>
    public static bool TryParse(ReadOnlySpan<char> value, out long unixSeconds)
    {
        unixSeconds = 0;
        value = value.Trim();

        // Split the digit block from the optional offset part.
        var digitsLength = 0;
        while (digitsLength < value.Length && char.IsAsciiDigit(value[digitsLength]))
        {
            digitsLength++;
        }

        // Need at least yyyyMMdd; longer blocks add HH, mm, ss.
        if (digitsLength is < 8 or > 14 || digitsLength % 2 != 0)
        {
            return false;
        }

        var digits = value[..digitsLength];
        var rest = value[digitsLength..].Trim();

        var year = ParseDigits(digits[..4]);
        var month = ParseDigits(digits.Slice(4, 2));
        var day = ParseDigits(digits.Slice(6, 2));
        var hour = digitsLength >= 10 ? ParseDigits(digits.Slice(8, 2)) : 0;
        var minute = digitsLength >= 12 ? ParseDigits(digits.Slice(10, 2)) : 0;
        var second = digitsLength >= 14 ? ParseDigits(digits.Slice(12, 2)) : 0;

        if (year is < 1970 or > 2100 || month is < 1 or > 12 || day is < 1 or > 31 ||
            hour > 23 || minute > 59 || second > 60)
        {
            return false;
        }

        if (second == 60)
        {
            second = 59; // leap second — close enough for a TV guide
        }

        if (!TryGetOffsetMinutes(rest, out var offsetMinutes))
        {
            return false;
        }

        DateTime clock;
        try
        {
            clock = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false; // e.g. February 30th
        }

        unixSeconds = (long)(clock - UnixEpoch).TotalSeconds - offsetMinutes * 60L;
        return true;
    }

    private static bool TryGetOffsetMinutes(ReadOnlySpan<char> rest, out int offsetMinutes)
    {
        offsetMinutes = 0;
        if (rest.IsEmpty)
        {
            return true; // no offset → UTC
        }

        if (rest.Equals("UTC", StringComparison.OrdinalIgnoreCase) ||
            rest.Equals("GMT", StringComparison.OrdinalIgnoreCase) ||
            rest.Equals("Z", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var sign = rest[0] switch
        {
            '+' => 1,
            '-' => -1,
            _ => 0,
        };

        if (sign == 0)
        {
            return false;
        }

        var body = rest[1..];

        // ±HHMM or ±HH:MM
        Span<char> compact = stackalloc char[4];
        var written = 0;
        foreach (var c in body)
        {
            if (c == ':')
            {
                continue;
            }

            if (!char.IsAsciiDigit(c) || written == 4)
            {
                return false;
            }

            compact[written++] = c;
        }

        if (written is not (2 or 4))
        {
            return false;
        }

        var hours = ParseDigits(compact[..2]);
        var minutes = written == 4 ? ParseDigits(compact.Slice(2, 2)) : 0;
        if (hours > 14 || minutes > 59)
        {
            return false;
        }

        offsetMinutes = sign * (hours * 60 + minutes);
        return true;
    }

    private static int ParseDigits(ReadOnlySpan<char> digits)
    {
        var result = 0;
        foreach (var c in digits)
        {
            result = result * 10 + (c - '0');
        }

        return result;
    }
}
