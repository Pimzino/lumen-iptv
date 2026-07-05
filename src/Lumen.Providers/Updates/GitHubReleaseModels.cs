using System.Text.Json.Serialization;

namespace Lumen.Providers.Updates;

/// <summary>A downloadable file attached to a GitHub release.</summary>
public sealed record GitHubReleaseAsset(string Name, Uri DownloadUrl, long Size);

/// <summary>A published GitHub release with its attached assets, normalized for the updater.</summary>
public sealed record GitHubRelease(
    string TagName,
    string? Name,
    string? Body,
    bool IsPreRelease,
    Uri? HtmlUrl,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<GitHubReleaseAsset> Assets)
{
    /// <summary>Suffix identifying the Windows x64 installer asset produced by the release pipeline.</summary>
    public const string InstallerSuffix = "-win-x64-setup.exe";

    /// <summary>Name of the SHA-256 checksums manifest, when the release publishes one.</summary>
    public const string ChecksumsAssetName = "SHA256SUMS.txt";

    /// <summary>The Windows x64 installer asset, or null when the release has none.</summary>
    public GitHubReleaseAsset? InstallerAsset =>
        Assets.FirstOrDefault(a => a.Name.EndsWith(InstallerSuffix, StringComparison.OrdinalIgnoreCase))
        ?? Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

    /// <summary>The SHA-256 checksums manifest asset, or null when the release doesn't publish one.</summary>
    public GitHubReleaseAsset? ChecksumsAsset =>
        Assets.FirstOrDefault(a => a.Name.Equals(ChecksumsAssetName, StringComparison.OrdinalIgnoreCase));
}

// ------------------------------------------------------------------ wire DTOs

/// <summary>Subset of the GitHub Releases API payload the updater consumes.</summary>
internal sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAssetDto>? Assets { get; set; }
}

internal sealed class GitHubAssetDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>Source-generated serializer metadata for the GitHub Releases payloads.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GitHubReleaseDto))]
[JsonSerializable(typeof(List<GitHubReleaseDto>))]
internal sealed partial class GitHubJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
