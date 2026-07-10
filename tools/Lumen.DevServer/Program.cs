// Lumen.DevServer — a tiny local Xtream/M3U/XMLTV fixture server for end-to-end testing.
// Usage: dotnet run [port]   (default 8777)

using System.Globalization;
using System.Net;
using System.Text;

// Structural self-check of the generated HLS/TS fixture (no server, no LibVLC).
if (args.Contains("--selftest"))
{
    Environment.Exit(HlsTsFixture.SelfTest());
}

var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 8777;
var listener = new HttpListener();
listener.Prefixes.Add($"http://localhost:{port}/");
listener.Start();
Console.WriteLine($"Lumen.DevServer listening on http://localhost:{port}/  (Ctrl+C to stop)");

var channels = new[]
{
    (Id: 101, Name: "Orbit One HD", Cat: "1", Epg: "orbit.one"),
    (Id: 102, Name: "Vertex Sports", Cat: "2", Epg: "vertex.sports"),
    (Id: 103, Name: "Cinema Prime", Cat: "1", Epg: "cinema.prime"),
    (Id: 104, Name: "Pulse News 24", Cat: "1", Epg: "pulse.news"),
    (Id: 105, Name: "Kids Cloud", Cat: "2", Epg: "kids.cloud"),
};

while (true)
{
    HttpListenerContext context;
    try
    {
        context = await listener.GetContextAsync();
    }
    catch (HttpListenerException)
    {
        break;
    }

    _ = Task.Run(() => Handle(context));
}

void Handle(HttpListenerContext context)
{
    try
    {
        var request = context.Request;
        var path = request.Url?.AbsolutePath ?? "/";
        var query = request.QueryString;
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} {request.HttpMethod} {request.Url?.PathAndQuery}");

        switch (path)
        {
            case "/player_api.php":
                HandlePlayerApi(context, query);
                break;
            case "/xmltv.php":
                Respond(context, GenerateXmltv(), "application/xml");
                break;
            case "/playlist.m3u":
                Respond(context, GeneratePlaylist(), "audio/x-mpegurl");
                break;
            case var live when live.StartsWith("/live/demo/demo/", StringComparison.Ordinal):
                // Endless "live streams". Channel 103 serves real MPEG-TS (so the client's live
                // recorder can capture it); channel 105 is deliberately flaky: it drops the
                // connection after ~8 seconds to exercise the client's reconnect logic.
                if (live.Contains("103", StringComparison.Ordinal))
                {
                    StreamLiveTs(context);
                }
                else
                {
                    StreamLiveWav(context, dropAfterSeconds: live.Contains("105", StringComparison.Ordinal) ? 8 : 0);
                }

                break;
            case var movie when movie.StartsWith("/movie/demo/demo/", StringComparison.Ordinal):
                if (movie.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    // HLS VOD fixture: a finite playlist over generated MPEG-TS segments, so the
                    // client's sout recorder path can be exercised end-to-end.
                    Respond(context, HlsTsFixture.BuildPlaylist(port), "application/vnd.apple.mpegurl");
                }
                else
                {
                    // A finite ~20-minute WAV so VOD resume/seek can be exercised.
                    StreamMovieWav(context, totalSeconds: 1200);
                }

                break;
            case var hls when hls.StartsWith("/hls/", StringComparison.Ordinal):
                ServeHlsSegment(context, hls);
                break;
            case var episode when episode.StartsWith("/series/demo/demo/", StringComparison.Ordinal):
                // A short finite WAV standing in for a series episode.
                StreamMovieWav(context, totalSeconds: 300);
                break;
            default:
                context.Response.StatusCode = 404;
                context.Response.Close();
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"error: {ex.Message}");
        try
        {
            context.Response.StatusCode = 500;
            context.Response.Close();
        }
        catch
        {
            // client went away
        }
    }
}

void HandlePlayerApi(HttpListenerContext context, System.Collections.Specialized.NameValueCollection query)
{
    var username = query["username"];
    var password = query["password"];
    if (username != "demo" || password != "demo")
    {
        Respond(context, """{"user_info":{"auth":0,"status":"Invalid"}}""", "application/json");
        return;
    }

    var action = query["action"];
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var json = action switch
    {
        null or "" =>
            $$"""
            {"user_info":{"username":"demo","auth":1,"status":"Active","exp_date":"{{now + 90 * 86400}}","is_trial":"0","active_cons":"0","max_connections":"2","allowed_output_formats":["m3u8","ts"]},
             "server_info":{"url":"localhost","port":"{{port}}","server_protocol":"http","timezone":"UTC","timestamp_now":{{now}} } }
            """,
        "get_live_categories" =>
            """[{"category_id":"1","category_name":"Entertainment","parent_id":0},{"category_id":"2","category_name":"Sports & Kids","parent_id":0}]""",
        "get_live_streams" => BuildLiveStreams(),
        "get_vod_categories" =>
            """[{"category_id":"10","category_name":"Movies","parent_id":0}]""",
        "get_vod_streams" =>
            $$"""[{"stream_id":9001,"name":"The Long Voyage","stream_icon":"","category_id":"10","rating":"7.4","added":"{{now - 86400}}","container_extension":"mp4"},{"stream_id":9002,"name":"Midnight Circuit","stream_icon":"","category_id":"10","rating":8.1,"added":"{{now - 2 * 86400}}","container_extension":"mkv"},{"stream_id":{{HlsTsFixture.MovieId}},"name":"Neon Tide","stream_icon":"","category_id":"10","rating":7.9,"added":"{{now - 3 * 86400}}","container_extension":"m3u8"}]""",
        "get_series_categories" =>
            """[{"category_id":"20","category_name":"Drama","parent_id":0}]""",
        "get_series" =>
            $$"""[{"series_id":501,"name":"Breaking Code","cover":"","category_id":"20","rating":"8.7","last_modified":"{{now - 3600}}","releaseDate":"2023-01-10"}]""",
        "get_series_info" => BuildSeriesInfo(),
        "get_vod_info" => query["vod_id"] == HlsTsFixture.MovieId.ToString(CultureInfo.InvariantCulture)
            ? $$"""{"info":{"plot":"Waves.","duration_secs":{{HlsTsFixture.DurationSeconds}} },"movie_data":{"stream_id":{{HlsTsFixture.MovieId}},"container_extension":"m3u8"} }"""
            : """{"info":{"plot":"Space.","duration_secs":8130},"movie_data":{"stream_id":9001,"container_extension":"mp4"}}""",
        "get_short_epg" or "get_simple_data_table" =>
            """{"epg_listings":[]}""",
        _ => "[]",
    };

    Respond(context, json, "application/json");
}

string BuildLiveStreams()
{
    var items = channels.Select((c, i) =>
        $$"""{"num":{{i + 1}},"name":"{{c.Name}}","stream_type":"live","stream_id":{{c.Id}},"stream_icon":"","epg_channel_id":"{{c.Epg}}","category_id":"{{c.Cat}}","tv_archive":0}""");
    return "[" + string.Join(",", items) + "]";
}

string BuildSeriesInfo()
{
    // Three seasons with per-episode metadata so the detail page's season tabs, episode
    // cards, meta lines, and next-up logic all have real material in dev/E2E runs.
    var titles = new[]
    {
        "Pilot", "Zero Day", "Stack Trace", "Fork Bomb", "Root Access", "Kernel Panic",
        "Cold Boot", "Handshake", "Dead Drop", "Backdoor", "Air Gap",
        "Checksum", "Sandbox", "Race Condition", "Terminal",
    };
    var episodes = new Dictionary<string, List<object>>();
    var episodeId = 70100;
    var titleIndex = 0;
    for (var season = 1; season <= 3; season++)
    {
        var count = season switch { 1 => 6, 2 => 5, _ => 4 };
        var list = new List<object>(count);
        for (var number = 1; number <= count; number++)
        {
            var title = titles[titleIndex++ % titles.Length];
            var aired = new DateTime(2020 + season, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(7 * (number - 1));
            list.Add(new
            {
                id = (episodeId++).ToString(CultureInfo.InvariantCulture),
                episode_num = number,
                season,
                title,
                container_extension = "mp4",
                info = new
                {
                    plot = $"S{season}E{number} — {title}: the crew digs deeper into the breach while old loyalties fray.",
                    duration_secs = 2400 + number * 60,
                    rating = 7 + number % 3 + number % 10 / 10.0,
                    releasedate = aired.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                },
            });
        }

        episodes[season.ToString(CultureInfo.InvariantCulture)] = list;
    }

    var payload = new
    {
        info = new
        {
            name = "Breaking Code",
            plot = "An underground security team races to contain a leaked exploit before it rewrites the balance of power.",
            genre = "Drama / Thriller",
            cast = "Ada Vale, Marcus Chen, Priya Nair",
            director = "R. Okafor",
            rating = 8.7,
            releaseDate = "2021-03-01",
        },
        episodes,
    };
    return System.Text.Json.JsonSerializer.Serialize(payload);
}

string GeneratePlaylist()
{
    var sb = new StringBuilder();
    sb.AppendLine("#EXTM3U");
    foreach (var c in channels)
    {
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"#EXTINF:-1 tvg-id=\"{c.Epg}\" tvg-name=\"{c.Name}\" group-title=\"Fixture TV\",{c.Name}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"http://localhost:{port}/live/demo/demo/{c.Id}.ts");
    }

    sb.AppendLine("#EXTINF:-1 group-title=\"Fixture Movies\",The Long Voyage (2024)");
    sb.AppendLine(CultureInfo.InvariantCulture, $"http://localhost:{port}/movie/demo/demo/9001.mp4");
    return sb.ToString();
}

string GenerateXmltv()
{
    var start = DateTimeOffset.UtcNow.AddHours(-6);
    var sb = new StringBuilder();
    sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
    sb.AppendLine("<tv source-info-name=\"Lumen DevServer\">");
    foreach (var c in channels)
    {
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"  <channel id=\"{c.Epg}\"><display-name>{c.Name}</display-name></channel>");
    }

    foreach (var c in channels)
    {
        for (var slot = 0; slot < 48; slot++)
        {
            var begin = start.AddMinutes(slot * 30);
            var end = begin.AddMinutes(30);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  <programme start=\"{begin:yyyyMMddHHmmss} +0000\" stop=\"{end:yyyyMMddHHmmss} +0000\" channel=\"{c.Epg}\">" +
                $"<title>{c.Name} Show {slot}</title><desc>Fixture programme.</desc></programme>");
        }
    }

    sb.AppendLine("</tv>");
    return sb.ToString();
}

// Endless live MPEG-TS (channel 103): real transport-stream bytes so recording/remuxing works.
void StreamLiveTs(HttpListenerContext context)
{
    var response = context.Response;
    response.ContentType = "video/mp2t";
    response.SendChunked = true;
    try
    {
        HlsTsFixture.StreamEndlessLive(response.OutputStream);
    }
    catch (Exception)
    {
        // client went away — normal for a live stream
        try
        {
            context.Response.Abort();
        }
        catch
        {
            // already gone
        }
    }
}

void StreamLiveWav(HttpListenerContext context, int dropAfterSeconds)
{
    const int sampleRate = 8000;
    const short channelsCount = 1;
    const short bitsPerSample = 16;

    var response = context.Response;
    response.ContentType = "audio/wav";
    response.SendChunked = true;

    try
    {
        // RIFF header with "unknown" sizes — players treat it as a live stream.
        using var header = new MemoryStream();
        using (var writer = new BinaryWriter(header, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(0xFFFFFFFF);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write(channelsCount);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channelsCount * bitsPerSample / 8);
            writer.Write((short)(channelsCount * bitsPerSample / 8));
            writer.Write(bitsPerSample);
            writer.Write("data"u8);
            writer.Write(0xFFFFFFFF);
        }

        response.OutputStream.Write(header.ToArray());

        var started = DateTime.UtcNow;
        var chunk = new byte[sampleRate / 5 * 2]; // 200 ms of 16-bit mono
        long sampleIndex = 0;
        while (true)
        {
            for (var i = 0; i < chunk.Length / 2; i++)
            {
                var value = (short)(Math.Sin(2 * Math.PI * 440 * sampleIndex / sampleRate) * 12000);
                chunk[i * 2] = (byte)(value & 0xFF);
                chunk[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
                sampleIndex++;
            }

            response.OutputStream.Write(chunk);
            Thread.Sleep(200);

            if (dropAfterSeconds > 0 && (DateTime.UtcNow - started).TotalSeconds > dropAfterSeconds)
            {
                Console.WriteLine("  (dropping flaky stream on purpose)");
                context.Response.Abort();
                return;
            }
        }
    }
    catch (Exception)
    {
        // client went away — normal for a live stream
        try
        {
            context.Response.Abort();
        }
        catch
        {
            // already gone
        }
    }
}

void StreamMovieWav(HttpListenerContext context, int totalSeconds)
{
    const int sampleRate = 8000;
    const short bitsPerSample = 16;
    var dataBytes = sampleRate * 2 * totalSeconds;
    long totalLength = 44 + dataBytes;

    var header = BuildWavHeader(dataBytes, sampleRate, bitsPerSample);

    var response = context.Response;
    response.ContentType = "audio/wav";
    response.AddHeader("Accept-Ranges", "bytes");

    // Honor a single byte range so LibVLC can seek and the downloader can resume a partial file.
    long start = 0;
    var end = totalLength - 1;
    var isRange = false;
    var rangeHeader = context.Request.Headers["Range"];
    if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
    {
        var spec = rangeHeader["bytes=".Length..].Split('-');
        if (spec.Length == 2 && long.TryParse(spec[0], out var s) && s >= 0)
        {
            start = s;
            if (long.TryParse(spec[1], out var e))
            {
                end = Math.Min(e, totalLength - 1);
            }

            if (start > end || start >= totalLength)
            {
                response.StatusCode = 416;
                response.AddHeader("Content-Range", $"bytes */{totalLength}");
                response.Close();
                return;
            }

            isRange = true;
        }
    }

    response.ContentLength64 = end - start + 1;
    if (isRange)
    {
        response.StatusCode = 206;
        response.AddHeader("Content-Range", $"bytes {start}-{end}/{totalLength}");
    }

    try
    {
        var pos = start;

        // Header bytes that fall inside the requested range.
        if (pos < 44)
        {
            var count = (int)(Math.Min(end, 43) - pos + 1);
            response.OutputStream.Write(header, (int)pos, count);
            pos += count;
        }

        // PCM bytes, generated at the correct offset so a ranged request resumes mid-file.
        var buffer = new byte[sampleRate / 5 * 2];
        while (pos <= end)
        {
            var bufLen = 0;
            while (bufLen < buffer.Length && pos <= end)
            {
                var dataOffset = pos - 44;
                var sampleIndex = dataOffset / 2;
                var value = (short)(Math.Sin(2 * Math.PI * 220 * sampleIndex / sampleRate) * 9000);
                buffer[bufLen++] = dataOffset % 2 == 0 ? (byte)(value & 0xFF) : (byte)((value >> 8) & 0xFF);
                pos++;
            }

            response.OutputStream.Write(buffer, 0, bufLen);
            Thread.Sleep(8); // pace so the client can seek before the whole file lands
        }

        response.OutputStream.Close();
    }
    catch
    {
        try { context.Response.Abort(); } catch { /* client gone */ }
    }
}

byte[] BuildWavHeader(int dataBytes, int sampleRate, short bitsPerSample)
{
    using var header = new MemoryStream();
    using (var writer = new BinaryWriter(header, Encoding.ASCII, leaveOpen: true))
    {
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataBytes);
    }

    return header.ToArray();
}

void Respond(HttpListenerContext context, string body, string contentType)
{
    var bytes = Encoding.UTF8.GetBytes(body);
    context.Response.ContentType = contentType;
    context.Response.ContentLength64 = bytes.Length;
    context.Response.OutputStream.Write(bytes);
    context.Response.Close();
}

void RespondBytes(HttpListenerContext context, byte[] bytes, string contentType)
{
    context.Response.ContentType = contentType;
    context.Response.ContentLength64 = bytes.Length;
    context.Response.OutputStream.Write(bytes);
    context.Response.Close();
}

// Serves /hls/{movieId}/seg{N}.ts from the generated fixture.
void ServeHlsSegment(HttpListenerContext context, string path)
{
    var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries); // ["hls", "9003", "segN.ts"]
    if (parts.Length == 3
        && parts[1] == HlsTsFixture.MovieId.ToString(CultureInfo.InvariantCulture)
        && parts[2].StartsWith("seg", StringComparison.Ordinal)
        && parts[2].EndsWith(".ts", StringComparison.Ordinal)
        && int.TryParse(parts[2][3..^3], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
        && index >= 0 && index < HlsTsFixture.SegmentCount)
    {
        RespondBytes(context, HlsTsFixture.GetSegment(index), "video/mp2t");
        return;
    }

    context.Response.StatusCode = 404;
    context.Response.Close();
}

/// <summary>
/// Generates the HLS VOD fixture: a short movie as MPEG-TS segments built entirely in code
/// (no media files). Each segment carries PAT + PMT + one PES per silent MPEG-1 Layer II audio
/// frame, with continuous PTS/PCR and continuity counters across segments — enough for LibVLC's
/// adaptive demuxer to play it and for the client's sout recorder to remux it to a local .ts.
/// </summary>
internal static class HlsTsFixture
{
    public const int MovieId = 9003;
    public const int SegmentCount = 8;
    public const int DurationSeconds = SegmentCount * 3; // 8 × 3.0 s = 24 s movie

    private const int FramesPerSegment = 125;  // 125 × 24 ms = 3.0 s per segment
    private const int AudioPid = 0x0100;
    private const int PmtPid = 0x1000;
    private const long PtsStart = 90_000;      // 1 s lead-in on the 90 kHz clock
    private const int PtsPerFrame = 2_160;     // 24 ms (1152 samples at 48 kHz) at 90 kHz
    private const int PacketsPerFrame = 2;     // 206-byte PES always spans exactly two TS packets

    private static readonly Lazy<byte[][]> Segments = new(() =>
        Enumerable.Range(0, SegmentCount).Select(BuildSegment).ToArray());

    // A silent MPEG-1 Layer II frame: 64 kbps, 48 kHz, mono → exactly 192 bytes per frame.
    // A zeroed body means "no bits allocated to any subband" (silence); the rest is stuffing.
    private static readonly byte[] SilentFrame = BuildSilentFrame();

    public static byte[] GetSegment(int index) => Segments.Value[index];

    /// <summary>
    /// Streams an endless live MPEG-TS to <paramref name="output"/> — the fixture behind the
    /// recordable live channel. PAT/PMT repeat every ~half second; one silent-MP2 PES lands every
    /// 24 ms with continuous PTS/continuity counters. Runs until the client disconnects (the
    /// write throws) and paces itself to real time like a broadcast.
    /// </summary>
    public static void StreamEndlessLive(Stream output)
    {
        var psiCc = 0;
        var audioCc = 0;
        long frame = 0;
        while (true)
        {
            if (frame % 20 == 0) // ~every 480 ms, and before the very first frame
            {
                output.Write(BuildPsiPacket(0x0000, BuildPatSection(), psiCc & 0x0F));
                output.Write(BuildPsiPacket(PmtPid, BuildPmtSection(), psiCc & 0x0F));
                psiCc++;
            }

            var pts = PtsStart + frame * PtsPerFrame;
            WritePesPackets(output, pts, ref audioCc);
            output.Flush();
            frame++;
            Thread.Sleep(24); // one frame of audio per real-time frame duration
        }
    }

    public static string BuildPlaylist(int port)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:3");
        sb.AppendLine("#EXT-X-TARGETDURATION:3");
        sb.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
        sb.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");
        for (var i = 0; i < SegmentCount; i++)
        {
            sb.AppendLine("#EXTINF:3.000,");
            sb.AppendLine(CultureInfo.InvariantCulture, $"http://localhost:{port}/hls/{MovieId}/seg{i}.ts");
        }

        sb.AppendLine("#EXT-X-ENDLIST");
        return sb.ToString();
    }

    private static byte[] BuildSilentFrame()
    {
        var frame = new byte[192];
        frame[0] = 0xFF; // sync
        frame[1] = 0xFD; // MPEG-1, Layer II, no CRC
        frame[2] = 0x44; // 64 kbps, 48 kHz, no padding
        frame[3] = 0xC0; // mono
        return frame;
    }

    private static byte[] BuildSegment(int segmentIndex)
    {
        using var ts = new MemoryStream();

        // HLS wants each segment openable on its own: PAT + PMT first.
        ts.Write(BuildPsiPacket(0x0000, BuildPatSection(), segmentIndex & 0x0F));
        ts.Write(BuildPsiPacket(PmtPid, BuildPmtSection(), segmentIndex & 0x0F));

        // Continuity counters and PTS continue across segments.
        var audioCc = segmentIndex * FramesPerSegment * PacketsPerFrame & 0x0F;
        for (var j = 0; j < FramesPerSegment; j++)
        {
            var globalFrame = segmentIndex * FramesPerSegment + j;
            var pts = PtsStart + (long)globalFrame * PtsPerFrame;
            WritePesPackets(ts, pts, ref audioCc);
        }

        return ts.ToArray();
    }

    private static byte[] BuildPatSection()
    {
        // table_id 0, one program: number 1 → PMT PID 0x1000.
        byte[] body =
        [
            0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00,
            0x00, 0x01, (byte)(0xE0 | (PmtPid >> 8)), PmtPid & 0xFF,
        ];
        return AppendCrc(body);
    }

    private static byte[] BuildPmtSection()
    {
        // table_id 2: PCR on the audio PID; one ES: stream_type 0x03 (MPEG-1 audio).
        byte[] body =
        [
            0x02, 0xB0, 0x12, 0x00, 0x01, 0xC1, 0x00, 0x00,
            (byte)(0xE0 | (AudioPid >> 8)), AudioPid & 0xFF, 0xF0, 0x00,
            0x03, (byte)(0xE0 | (AudioPid >> 8)), AudioPid & 0xFF, 0xF0, 0x00,
        ];
        return AppendCrc(body);
    }

    private static byte[] AppendCrc(byte[] section)
    {
        var crc = Crc32Mpeg2(section);
        var result = new byte[section.Length + 4];
        section.CopyTo(result, 0);
        result[^4] = (byte)(crc >> 24);
        result[^3] = (byte)(crc >> 16);
        result[^2] = (byte)(crc >> 8);
        result[^1] = (byte)crc;
        return result;
    }

    private static byte[] BuildPsiPacket(int pid, byte[] section, int cc)
    {
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = (byte)(0x40 | (pid >> 8)); // payload_unit_start
        packet[2] = (byte)pid;
        packet[3] = (byte)(0x10 | (cc & 0x0F)); // payload only
        packet[4] = 0x00; // pointer_field
        section.CopyTo(packet, 5);
        for (var i = 5 + section.Length; i < 188; i++)
        {
            packet[i] = 0xFF;
        }

        return packet;
    }

    /// <summary>One PES (14-byte header + 192-byte frame = 206 bytes) as exactly two TS packets.</summary>
    private static void WritePesPackets(Stream ts, long pts, ref int cc)
    {
        var pes = new byte[14 + SilentFrame.Length];
        pes[0] = 0x00;
        pes[1] = 0x00;
        pes[2] = 0x01;
        pes[3] = 0xC0; // audio stream 0
        var pesLength = 3 + 5 + SilentFrame.Length; // flags(2) + header_len(1) + PTS(5) + payload
        pes[4] = (byte)(pesLength >> 8);
        pes[5] = (byte)pesLength;
        pes[6] = 0x80; // marker bits
        pes[7] = 0x80; // PTS only
        pes[8] = 0x05; // PES header data length
        pes[9] = (byte)(0x21 | ((pts >> 29) & 0x0E));
        pes[10] = (byte)(pts >> 22);
        pes[11] = (byte)(0x01 | ((pts >> 14) & 0xFE));
        pes[12] = (byte)(pts >> 7);
        pes[13] = (byte)(0x01 | ((pts << 1) & 0xFE));
        SilentFrame.CopyTo(pes, 14);

        // Packet 1: PUSI + adaptation carrying the PCR (8 bytes) + first 176 PES bytes.
        var first = new byte[188];
        first[0] = 0x47;
        first[1] = (byte)(0x40 | (AudioPid >> 8));
        first[2] = AudioPid & 0xFF;
        first[3] = (byte)(0x30 | (cc++ & 0x0F)); // adaptation + payload
        first[4] = 0x07; // adaptation_field_length
        first[5] = 0x10; // PCR flag
        var pcrBase = pts - 3_600; // 40 ms ahead of the PTS; never negative given the 1 s lead-in
        first[6] = (byte)(pcrBase >> 25);
        first[7] = (byte)(pcrBase >> 17);
        first[8] = (byte)(pcrBase >> 9);
        first[9] = (byte)(pcrBase >> 1);
        first[10] = (byte)(((pcrBase & 1) << 7) | 0x7E); // reserved bits + extension high bit
        first[11] = 0x00; // extension low byte
        pes.AsSpan(0, 176).CopyTo(first.AsSpan(12));
        ts.Write(first);

        // Packet 2: remaining 30 PES bytes behind a 153-byte stuffing adaptation field.
        var second = new byte[188];
        second[0] = 0x47;
        second[1] = (byte)(AudioPid >> 8);
        second[2] = AudioPid & 0xFF;
        second[3] = (byte)(0x30 | (cc++ & 0x0F));
        second[4] = 153; // adaptation_field_length: flags + 152 stuffing bytes
        second[5] = 0x00;
        for (var i = 6; i < 158; i++)
        {
            second[i] = 0xFF;
        }

        pes.AsSpan(176).CopyTo(second.AsSpan(158));
        ts.Write(second);
    }

    /// <summary>CRC-32/MPEG-2 (poly 0x04C11DB7, init 0xFFFFFFFF, no reflection, no final xor).</summary>
    private static uint Crc32Mpeg2(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= (uint)b << 24;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
            }
        }

        return crc;
    }

    /// <summary>
    /// Validates the generated fixture with an independent parse: the CRC algorithm against its
    /// published check value, TS sync/packet layout, PSI tables, PES framing, and PTS/continuity
    /// across the segment boundary. Returns a process exit code.
    /// </summary>
    public static int SelfTest()
    {
        var failures = new List<string>();
        void Check(bool ok, string what)
        {
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok)
            {
                failures.Add(what);
            }
        }

        // Known check value for CRC-32/MPEG-2 over ASCII "123456789".
        Check(Crc32Mpeg2("123456789"u8) == 0x0376E6E7, "crc32/mpeg2 check value");

        var seg0 = GetSegment(0);
        var seg1 = GetSegment(1);
        var expectedPackets = 2 + FramesPerSegment * PacketsPerFrame;
        Check(seg0.Length == expectedPackets * 188, $"segment size = {expectedPackets} packets");

        var syncOk = true;
        for (var i = 0; i < seg0.Length; i += 188)
        {
            syncOk &= seg0[i] == 0x47;
        }

        Check(syncOk, "sync byte on every 188-byte boundary");

        static int Pid(byte[] ts, int packet) => ((ts[packet * 188 + 1] & 0x1F) << 8) | ts[packet * 188 + 2];
        Check(Pid(seg0, 0) == 0x0000, "packet 0 is the PAT");
        Check(Pid(seg0, 1) == PmtPid, "packet 1 is the PMT");
        Check(Pid(seg0, 2) == AudioPid, "packet 2 is audio");

        // PAT: parse section, recompute its CRC, and confirm it maps program 1 → the PMT PID.
        var patSection = seg0.AsSpan(5, 3 + (((seg0[6] & 0x0F) << 8) | seg0[7]));
        Check(Crc32Mpeg2(patSection[..^4]) ==
            ((uint)patSection[^4] << 24 | (uint)patSection[^3] << 16 | (uint)patSection[^2] << 8 | patSection[^1]),
            "PAT CRC valid");
        Check((((patSection[10] & 0x1F) << 8) | patSection[11]) == PmtPid, "PAT maps program 1 to the PMT PID");

        // PMT: valid CRC, MPEG-1 audio ES on the audio PID, PCR on the same PID.
        var pmtStart = 188 + 5;
        var pmtSection = seg0.AsSpan(pmtStart, 3 + (((seg0[pmtStart + 1] & 0x0F) << 8) | seg0[pmtStart + 2]));
        Check(Crc32Mpeg2(pmtSection[..^4]) ==
            ((uint)pmtSection[^4] << 24 | (uint)pmtSection[^3] << 16 | (uint)pmtSection[^2] << 8 | pmtSection[^1]),
            "PMT CRC valid");
        Check((((pmtSection[8] & 0x1F) << 8) | pmtSection[9]) == AudioPid, "PCR rides the audio PID");
        Check(pmtSection[12] == 0x03, "ES stream_type is MPEG-1 audio");
        Check((((pmtSection[13] & 0x1F) << 8) | pmtSection[14]) == AudioPid, "ES PID is the audio PID");

        // First audio packet: PUSI + PCR adaptation, then a well-formed PES with the expected PTS.
        var audio = seg0.AsSpan(2 * 188, 188);
        Check((audio[1] & 0x40) != 0, "audio packet has payload_unit_start");
        Check((audio[3] & 0x30) == 0x30 && audio[4] == 0x07 && (audio[5] & 0x10) != 0, "audio packet carries a PCR");
        var pesSpan = audio[12..];
        Check(pesSpan[0] == 0 && pesSpan[1] == 0 && pesSpan[2] == 1 && pesSpan[3] == 0xC0, "PES start code (audio)");
        Check(((pesSpan[4] << 8) | pesSpan[5]) == 3 + 5 + 192, "PES length covers header + one frame");
        static long DecodePts(ReadOnlySpan<byte> p) =>
            ((long)(p[9] >> 1) & 0x07) << 30 | (long)p[10] << 22 |
            ((long)(p[11] >> 1) & 0x7F) << 15 | (long)p[12] << 7 | (long)(p[13] >> 1) & 0x7F;
        Check(DecodePts(pesSpan) == PtsStart, "first PTS is the 1 s lead-in");
        Check(pesSpan[14] == 0xFF && pesSpan[15] == 0xFD && pesSpan[16] == 0x44 && pesSpan[17] == 0xC0,
            "payload starts with the silent Layer II frame header");

        // Continuity across the segment boundary: PTS and counters keep counting.
        var seg1Audio = seg1.AsSpan(2 * 188, 188);
        Check(DecodePts(seg1Audio[12..]) == PtsStart + (long)FramesPerSegment * PtsPerFrame,
            "segment 1 PTS continues the timeline");
        var lastCcSeg0 = seg0[(expectedPackets - 1) * 188 + 3] & 0x0F;
        var firstCcSeg1 = seg1Audio[3] & 0x0F;
        Check(firstCcSeg1 == ((lastCcSeg0 + 1) & 0x0F), "audio continuity counter spans segments");

        var playlist = BuildPlaylist(8777);
        Check(playlist.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal), "playlist is VOD-terminated");
        Check(playlist.Split("#EXTINF").Length - 1 == SegmentCount, "playlist lists every segment");

        Console.WriteLine(failures.Count == 0 ? "HLS-FIXTURE-SELFTEST=PASS" : "HLS-FIXTURE-SELFTEST=FAIL");
        return failures.Count == 0 ? 0 : 1;
    }
}
