using FluentAssertions;
using Lumen.Providers.Tests.Support;
using Lumen.Providers.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Providers.Tests.Updates;

public sealed class GitHubReleaseClientTests
{
    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static GitHubReleaseClient CreateClient(StubHttpMessageHandler handler) =>
        new(new SingleClientFactory(handler), NullLogger<GitHubReleaseClient>.Instance);

    private const string LatestReleaseJson =
        """
        {
          "tag_name": "v0.2.0",
          "name": "Lumen 0.2.0",
          "body": "## What's Changed\n- Added auto-updates",
          "draft": false,
          "prerelease": false,
          "html_url": "https://github.com/Pimzino/lumen-iptv/releases/tag/v0.2.0",
          "published_at": "2026-07-01T10:00:00Z",
          "assets": [
            { "name": "Lumen-0.2.0-win-x64-portable.zip", "browser_download_url": "https://example.com/portable.zip", "size": 1000 },
            { "name": "Lumen-0.2.0-win-x64-setup.exe", "browser_download_url": "https://example.com/setup.exe", "size": 52428800 },
            { "name": "SHA256SUMS.txt", "browser_download_url": "https://example.com/SHA256SUMS.txt", "size": 200 }
          ]
        }
        """;

    [Fact]
    public async Task GetLatest_Stable_ParsesReleaseAndSelectsInstaller()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson(LatestReleaseJson);
        var client = CreateClient(handler);

        var release = await client.GetLatestReleaseAsync(includePrerelease: false, CancellationToken.None);

        release.Should().NotBeNull();
        release!.TagName.Should().Be("v0.2.0");
        release.Name.Should().Be("Lumen 0.2.0");
        release.Body.Should().Contain("Added auto-updates");
        release.IsPreRelease.Should().BeFalse();
        release.HtmlUrl!.AbsoluteUri.Should().Be("https://github.com/Pimzino/lumen-iptv/releases/tag/v0.2.0");

        release.InstallerAsset.Should().NotBeNull();
        release.InstallerAsset!.Name.Should().Be("Lumen-0.2.0-win-x64-setup.exe");
        release.InstallerAsset.Size.Should().Be(52428800);
        release.InstallerAsset.DownloadUrl.AbsoluteUri.Should().Be("https://example.com/setup.exe");

        release.ChecksumsAsset!.Name.Should().Be("SHA256SUMS.txt");

        handler.Requests.Single()!.AbsolutePath.Should().Be("/repos/Pimzino/lumen-iptv/releases/latest");
    }

    [Fact]
    public async Task GetLatest_Stable_ReturnsNullWhenNoReleasesExist()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        var client = CreateClient(handler);

        var release = await client.GetLatestReleaseAsync(includePrerelease: false, CancellationToken.None);

        release.Should().BeNull();
    }

    [Fact]
    public async Task GetLatest_ReturnsNullOnServerError()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        var client = CreateClient(handler);

        var release = await client.GetLatestReleaseAsync(includePrerelease: false, CancellationToken.None);

        release.Should().BeNull();
    }

    [Fact]
    public async Task GetLatest_IncludePrerelease_UsesListAndSkipsDrafts()
    {
        const string listJson =
            """
            [
              { "tag_name": "v0.3.0-wip", "draft": true, "prerelease": true, "assets": [] },
              { "tag_name": "v0.3.0-beta.1", "draft": false, "prerelease": true,
                "assets": [ { "name": "Lumen-0.3.0-beta.1-win-x64-setup.exe",
                             "browser_download_url": "https://example.com/beta-setup.exe", "size": 42 } ] },
              { "tag_name": "v0.2.0", "draft": false, "prerelease": false, "assets": [] }
            ]
            """;
        var handler = StubHttpMessageHandler.RespondingWithJson(listJson);
        var client = CreateClient(handler);

        var release = await client.GetLatestReleaseAsync(includePrerelease: true, CancellationToken.None);

        release.Should().NotBeNull();
        release!.TagName.Should().Be("v0.3.0-beta.1");
        release.IsPreRelease.Should().BeTrue();
        release.InstallerAsset!.Name.Should().Be("Lumen-0.3.0-beta.1-win-x64-setup.exe");
        handler.Requests.Single()!.AbsolutePath.Should().Be("/repos/Pimzino/lumen-iptv/releases");
    }

    [Fact]
    public async Task GetLatest_ReturnsReleaseEvenWhenInstallerAssetMissing()
    {
        const string noAssetJson =
            """{ "tag_name": "v0.2.0", "draft": false, "prerelease": false, "assets": [] }""";
        var handler = StubHttpMessageHandler.RespondingWithJson(noAssetJson);
        var client = CreateClient(handler);

        var release = await client.GetLatestReleaseAsync(includePrerelease: false, CancellationToken.None);

        release.Should().NotBeNull();
        release!.InstallerAsset.Should().BeNull();
        release.ChecksumsAsset.Should().BeNull();
    }
}
