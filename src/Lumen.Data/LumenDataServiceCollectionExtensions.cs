using Lumen.Core.Abstractions;
using Lumen.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Lumen.Data;

/// <summary>Composition helpers for the data layer.</summary>
public static class LumenDataServiceCollectionExtensions
{
    /// <summary>Registers SQLite plumbing, repositories, the EPG import sink, and the image cache.</summary>
    public static IServiceCollection AddLumenData(this IServiceCollection services, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        services.AddSingleton<IDbConnectionFactory>(_ => new SqliteConnectionFactory(databasePath));
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<DatabaseInitializer>();

        services.AddSingleton<IProfileRepository, ProfileRepository>();
        services.AddSingleton<ICatalogRepository, CatalogRepository>();
        services.AddSingleton<IEpgRepository, EpgRepository>();
        services.AddSingleton<IFavoritesRepository, FavoritesRepository>();
        services.AddSingleton<IWatchHistoryRepository, WatchHistoryRepository>();
        services.AddSingleton<IDownloadRepository, DownloadRepository>();
        services.AddSingleton<IRecordingRepository, RecordingRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<ISearchRepository, SearchRepository>();
        services.AddSingleton<IArtworkCacheRepository, ArtworkCacheRepository>();
        services.AddSingleton<ITraktMatchRepository, TraktMatchRepository>();
        services.AddSingleton<ITraktWatchedRepository, TraktWatchedRepository>();
        services.AddSingleton<IEpgImportSinkFactory, SqliteEpgImportSinkFactory>();

        services.AddHttpClient();
        services.AddSingleton<IImageCache, ImageDiskCache>();
        return services;
    }
}
