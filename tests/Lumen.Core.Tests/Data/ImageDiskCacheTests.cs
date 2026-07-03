using System.Net;
using System.Text;
using FluentAssertions;
using Lumen.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Core.Tests.Data;

public sealed class ImageDiskCacheTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "lumen-tests", Guid.NewGuid().ToString("N"));

    private int _downloads;

    private ImageDiskCache CreateCache(Func<HttpResponseMessage>? responder = null, long maxBytes = long.MaxValue)
    {
        responder ??= () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("fake-image-bytes")),
        };

        var handler = new CountingHandler(() =>
        {
            Interlocked.Increment(ref _downloads);
            return responder();
        });

        return new ImageDiskCache(
            new SingleClientFactory(handler),
            NullLogger<ImageDiskCache>.Instance,
            _directory,
            "images",
            maxBytes);
    }

    [Fact]
    public async Task FirstRequest_Downloads_SecondServesFromDisk()
    {
        var cache = CreateCache();

        var first = await cache.GetLocalPathAsync("http://img.example.com/logo.png", CancellationToken.None);
        var second = await cache.GetLocalPathAsync("http://img.example.com/logo.png", CancellationToken.None);

        first.Should().NotBeNull();
        File.Exists(first).Should().BeTrue();
        second.Should().Be(first);
        _downloads.Should().Be(1);
    }

    [Fact]
    public async Task FailedDownload_ReturnsNull_AndIsNegativelyCached()
    {
        var cache = CreateCache(() => new HttpResponseMessage(HttpStatusCode.NotFound));

        var first = await cache.GetLocalPathAsync("http://img.example.com/missing.png", CancellationToken.None);
        var second = await cache.GetLocalPathAsync("http://img.example.com/missing.png", CancellationToken.None);

        first.Should().BeNull();
        second.Should().BeNull();
        _downloads.Should().Be(1, "failures are cached for a few minutes");
    }

    [Fact]
    public async Task NonHttpUrls_AreRejectedWithoutDownloading()
    {
        var cache = CreateCache();

        (await cache.GetLocalPathAsync("", CancellationToken.None)).Should().BeNull();
        (await cache.GetLocalPathAsync("file://x/y.png", CancellationToken.None)).Should().BeNull();
        _downloads.Should().Be(0);
    }

    [Fact]
    public async Task StatsAndClear_ReflectStoredFiles()
    {
        var cache = CreateCache();
        await cache.GetLocalPathAsync("http://img.example.com/a.png", CancellationToken.None);
        await cache.GetLocalPathAsync("http://img.example.com/b.png", CancellationToken.None);

        var stats = await cache.GetStatsAsync(CancellationToken.None);
        stats.FileCount.Should().Be(2);
        stats.TotalBytes.Should().BeGreaterThan(0);

        await cache.ClearAsync(CancellationToken.None);
        (await cache.GetStatsAsync(CancellationToken.None)).FileCount.Should().Be(0);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _responder;

        public CountingHandler(Func<HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder());
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
