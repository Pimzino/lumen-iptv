using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Lumen.Providers.Updates;

/// <summary>Default <see cref="IGitHubReleaseClient"/> over the GitHub REST API.</summary>
public sealed class GitHubReleaseClient : IGitHubReleaseClient
{
    /// <summary>Named HttpClient used for GitHub API calls (User-Agent + Accept headers preset).</summary>
    public const string HttpClientName = "github-releases";

    /// <summary>Owner of the source repository the app updates from.</summary>
    public const string RepositoryOwner = "Pimzino";

    /// <summary>Name of the source repository the app updates from.</summary>
    public const string RepositoryName = "lumen-iptv";

    private static readonly Uri LatestReleaseUri =
        new($"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest");

    private static readonly Uri ReleaseListUri =
        new($"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases?per_page=20");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubReleaseClient> _logger;

    public GitHubReleaseClient(IHttpClientFactory httpClientFactory, ILogger<GitHubReleaseClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<GitHubRelease?> GetLatestReleaseAsync(bool includePrerelease, CancellationToken cancellationToken)
    {
        var http = _httpClientFactory.CreateClient(HttpClientName);
        try
        {
            return includePrerelease
                ? await GetNewestFromListAsync(http, cancellationToken).ConfigureAwait(false)
                : await GetLatestStableAsync(http, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Could not reach GitHub to check for updates");
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("GitHub update check timed out");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "GitHub release payload could not be parsed");
            return null;
        }
    }

    private async Task<GitHubRelease?> GetLatestStableAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(LatestReleaseUri, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // No full releases published yet.
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("GitHub returned {Status} for the latest release", (int)response.StatusCode);
            return null;
        }

        var dto = await response.Content
            .ReadFromJsonAsync(GitHubJsonContext.Default.GitHubReleaseDto, cancellationToken)
            .ConfigureAwait(false);
        return Map(dto);
    }

    private async Task<GitHubRelease?> GetNewestFromListAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(ReleaseListUri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("GitHub returned {Status} for the release list", (int)response.StatusCode);
            return null;
        }

        var releases = await response.Content
            .ReadFromJsonAsync(GitHubJsonContext.Default.ListGitHubReleaseDto, cancellationToken)
            .ConfigureAwait(false);
        if (releases is null)
        {
            return null;
        }

        // GitHub returns releases newest-first; take the first published (non-draft) one.
        foreach (var dto in releases)
        {
            if (dto.Draft)
            {
                continue;
            }

            return Map(dto);
        }

        return null;
    }

    private static GitHubRelease? Map(GitHubReleaseDto? dto)
    {
        if (dto?.TagName is not { Length: > 0 } tag)
        {
            return null;
        }

        var assets = new List<GitHubReleaseAsset>();
        if (dto.Assets is not null)
        {
            foreach (var asset in dto.Assets)
            {
                if (asset.Name is { Length: > 0 } name
                    && Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var url))
                {
                    assets.Add(new GitHubReleaseAsset(name, url, asset.Size));
                }
            }
        }

        Uri.TryCreate(dto.HtmlUrl, UriKind.Absolute, out var htmlUrl);
        return new GitHubRelease(tag, dto.Name, dto.Body, dto.Prerelease, htmlUrl, dto.PublishedAt, assets);
    }
}
