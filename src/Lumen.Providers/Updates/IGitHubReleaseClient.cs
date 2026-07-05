namespace Lumen.Providers.Updates;

/// <summary>Reads the latest release for the app from the GitHub Releases API.</summary>
public interface IGitHubReleaseClient
{
    /// <summary>
    /// Returns the newest published release, or null when none is available or the request fails
    /// (network error, rate limit, malformed payload). When <paramref name="includePrerelease"/>
    /// is false only full releases are considered; when true the newest non-draft release —
    /// pre-release or final — is returned.
    /// </summary>
    Task<GitHubRelease?> GetLatestReleaseAsync(bool includePrerelease, CancellationToken cancellationToken);
}
