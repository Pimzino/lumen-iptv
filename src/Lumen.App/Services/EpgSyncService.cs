using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.Messaging;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Lumen.Providers;
using Lumen.Providers.Xmltv;
using Lumen.Providers.Xtream;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services;

/// <summary>Downloads and imports a profile's XMLTV guide, then refreshes channel mappings.</summary>
public interface IEpgSyncService
{
    /// <summary>True while a refresh is running (guards double-starts).</summary>
    bool IsRefreshing { get; }

    Task<EpgImportResult> RefreshAsync(
        Profile profile, IProgress<EpgImportProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>Default <see cref="IEpgSyncService"/>.</summary>
public sealed class EpgSyncService : IEpgSyncService
{
    private readonly IXmltvParser _parser;
    private readonly IEpgImportSinkFactory _sinkFactory;
    private readonly IEpgRepository _epgRepository;
    private readonly ISessionService _session;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IClock _clock;
    private readonly IMessenger _messenger;
    private readonly ILogger<EpgSyncService> _logger;
    private int _refreshing;

    public EpgSyncService(
        IXmltvParser parser,
        IEpgImportSinkFactory sinkFactory,
        IEpgRepository epgRepository,
        ISessionService session,
        IHttpClientFactory httpClientFactory,
        IClock clock,
        IMessenger messenger,
        ILogger<EpgSyncService> logger)
    {
        _parser = parser;
        _sinkFactory = sinkFactory;
        _epgRepository = epgRepository;
        _session = session;
        _httpClientFactory = httpClientFactory;
        _clock = clock;
        _messenger = messenger;
        _logger = logger;
    }

    public bool IsRefreshing => _refreshing == 1;

    public async Task<EpgImportResult> RefreshAsync(
        Profile profile, IProgress<EpgImportProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            throw new InvalidOperationException("An EPG refresh is already running.");
        }

        try
        {
            var stream = await OpenEpgSourceAsync(profile, cancellationToken);
            EpgImportResult result;
            await using (stream.ConfigureAwait(true))
            {
                var sink = _sinkFactory.Create(profile.Id);
                await using (sink.ConfigureAwait(true))
                {
                    result = await _parser.ParseAsync(stream, sink, progress, cancellationToken);
                }
            }

            var cutoff = _clock.UtcNow.ToUnixTimeSeconds() - 24 * 3600;
            var purged = await _epgRepository.PurgeProgrammesBeforeAsync(profile.Id, cutoff, cancellationToken);
            var mapped = await _epgRepository.ApplyAutoMappingsAsync(profile.Id, cancellationToken);

            _logger.LogInformation(
                "EPG refresh for {Profile}: {Programmes} programmes ({Skipped} skipped), {Purged} purged, {Mapped} channels mapped",
                profile.Name, result.Programmes, result.Skipped, purged, mapped);

            _messenger.Send(new EpgRefreshedMessage(profile.Id, result.Programmes));
            return result;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private async Task<Stream> OpenEpgSourceAsync(Profile profile, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(profile.EpgSource))
        {
            if (profile.EpgIsFile)
            {
                return File.OpenRead(profile.EpgSource);
            }

            return await DownloadAsync(new Uri(profile.EpgSource), cancellationToken);
        }

        if (profile.Kind == ProfileKind.Xtream &&
            _session.GetXtreamCredentials(profile) is { } credentials)
        {
            var url = XtreamUrls.Xmltv(credentials.Server, credentials.Username, credentials.Password);
            return await DownloadAsync(url, cancellationToken);
        }

        throw new InvalidOperationException("This profile has no EPG source configured.");
    }

    private async Task<Stream> DownloadAsync(Uri url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(ProvidersServiceCollectionExtensions.DownloadHttpClientName);
        var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }
}
