using Lumen.Providers.Http;
using Lumen.Providers.M3u;
using Lumen.Providers.Xmltv;
using Lumen.Providers.Xtream;
using Microsoft.Extensions.DependencyInjection;

namespace Lumen.Providers;

/// <summary>Composition helpers for the provider layer.</summary>
public static class ProvidersServiceCollectionExtensions
{
    /// <summary>Named HttpClient for large streamed downloads (playlists, XMLTV).</summary>
    public const string DownloadHttpClientName = "download";

    /// <summary>Named HttpClient for image fetches.</summary>
    public const string ImagesHttpClientName = "images";

    public static IServiceCollection AddLumenProviders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddTransient<TransientRetryHandler>();

        services.AddHttpClient(XtreamClientFactory.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Lumen/1.0");
            })
            .AddHttpMessageHandler<TransientRetryHandler>();

        // Streams multi-hundred-MB playlists/EPGs; per-request CancellationTokens govern lifetime.
        services.AddHttpClient(DownloadHttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Lumen/1.0");
        });

        services.AddHttpClient(ImagesHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Lumen/1.0");
        });

        services.AddSingleton<IXtreamClientFactory, XtreamClientFactory>();
        services.AddSingleton<IM3uPlaylistParser, M3uPlaylistParser>();
        services.AddSingleton<IXmltvParser, XmltvParser>();
        return services;
    }
}
