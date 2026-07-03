using FluentAssertions;
using Lumen.Providers;
using Lumen.Providers.M3u;
using Lumen.Providers.Xmltv;
using Lumen.Providers.Xtream;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Providers.Tests;

public sealed class ProvidersModuleTests
{
    [Fact]
    public void AddLumenProviders_RegistersFactoriesParsersAndHttpClients()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddLumenProviders();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IM3uPlaylistParser>().Should().BeOfType<M3uPlaylistParser>();
        provider.GetRequiredService<IXmltvParser>().Should().BeOfType<XmltvParser>();

        var factory = provider.GetRequiredService<IXtreamClientFactory>();
        var client = factory.Create(new XtreamCredentials("http://x", "u", "p"));
        client.Credentials.Username.Should().Be("u");

        var httpFactory = provider.GetRequiredService<IHttpClientFactory>();
        using var xtream = httpFactory.CreateClient(XtreamClientFactory.HttpClientName);
        xtream.Timeout.Should().Be(TimeSpan.FromSeconds(15));
        using var download = httpFactory.CreateClient(ProvidersServiceCollectionExtensions.DownloadHttpClientName);
        download.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
        using var images = httpFactory.CreateClient(ProvidersServiceCollectionExtensions.ImagesHttpClientName);
        images.Timeout.Should().Be(TimeSpan.FromSeconds(30));
    }
}
