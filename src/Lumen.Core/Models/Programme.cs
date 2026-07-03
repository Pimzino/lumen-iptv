namespace Lumen.Core.Models;

/// <summary>A single EPG programme entry.</summary>
public sealed class Programme
{
    public long Id { get; set; }

    public long ProfileId { get; set; }

    /// <summary>XMLTV channel id this programme belongs to.</summary>
    public string ChannelXmltvId { get; set; } = string.Empty;

    /// <summary>Start time, unix seconds (UTC).</summary>
    public long StartUtc { get; set; }

    /// <summary>Stop time, unix seconds (UTC).</summary>
    public long StopUtc { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Category { get; set; }

    public string? EpisodeNumber { get; set; }

    public string? IconUrl { get; set; }

    public DateTimeOffset Start => DateTimeOffset.FromUnixTimeSeconds(StartUtc);

    public DateTimeOffset Stop => DateTimeOffset.FromUnixTimeSeconds(StopUtc);

    public TimeSpan Duration => Stop - Start;

    /// <summary>Progress of this programme at <paramref name="now"/>, clamped to [0, 1].</summary>
    public double ProgressAt(DateTimeOffset now)
    {
        if (StopUtc <= StartUtc)
        {
            return 0;
        }

        var fraction = (now.ToUnixTimeSeconds() - StartUtc) / (double)(StopUtc - StartUtc);
        return Math.Clamp(fraction, 0, 1);
    }
}
