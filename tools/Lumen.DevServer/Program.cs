// Lumen.DevServer — a tiny local Xtream/M3U/XMLTV fixture server for end-to-end testing.
// Usage: dotnet run [port]   (default 8777)

using System.Globalization;
using System.Net;
using System.Text;

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
                // Endless WAV "live stream". Channel 105 is deliberately flaky: it drops
                // the connection after ~8 seconds to exercise the client's reconnect logic.
                StreamLiveWav(context, dropAfterSeconds: live.Contains("105", StringComparison.Ordinal) ? 8 : 0);
                break;
            case var movie when movie.StartsWith("/movie/demo/demo/", StringComparison.Ordinal):
                // A finite ~20-minute WAV so VOD resume/seek can be exercised.
                StreamMovieWav(context, totalSeconds: 1200);
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
            $$"""[{"stream_id":9001,"name":"The Long Voyage","stream_icon":"","category_id":"10","rating":"7.4","added":"{{now - 86400}}","container_extension":"mp4"},{"stream_id":9002,"name":"Midnight Circuit","stream_icon":"","category_id":"10","rating":8.1,"added":"{{now - 2 * 86400}}","container_extension":"mkv"}]""",
        "get_series_categories" =>
            """[{"category_id":"20","category_name":"Drama","parent_id":0}]""",
        "get_series" =>
            $$"""[{"series_id":501,"name":"Breaking Code","cover":"","category_id":"20","rating":"8.7","last_modified":"{{now - 3600}}","releaseDate":"2023-01-10"}]""",
        "get_series_info" => BuildSeriesInfo(),
        "get_vod_info" =>
            """{"info":{"plot":"Space.","duration_secs":8130},"movie_data":{"stream_id":9001,"container_extension":"mp4"}}""",
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

    var response = context.Response;
    response.ContentType = "audio/wav";
    response.ContentLength64 = 44 + dataBytes;

    try
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

        response.OutputStream.Write(header.ToArray());

        var chunk = new byte[sampleRate / 5 * 2];
        long sampleIndex = 0;
        var written = 0;
        while (written < dataBytes)
        {
            for (var i = 0; i < chunk.Length / 2; i++)
            {
                var value = (short)(Math.Sin(2 * Math.PI * 220 * sampleIndex / sampleRate) * 9000);
                chunk[i * 2] = (byte)(value & 0xFF);
                chunk[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
                sampleIndex++;
            }

            var toWrite = Math.Min(chunk.Length, dataBytes - written);
            response.OutputStream.Write(chunk, 0, toWrite);
            written += toWrite;
            Thread.Sleep(20); // pace so the client can seek before the whole file lands
        }
    }
    catch
    {
        try { context.Response.Abort(); } catch { /* client gone */ }
    }
}

void Respond(HttpListenerContext context, string body, string contentType)
{
    var bytes = Encoding.UTF8.GetBytes(body);
    context.Response.ContentType = contentType;
    context.Response.ContentLength64 = bytes.Length;
    context.Response.OutputStream.Write(bytes);
    context.Response.Close();
}
