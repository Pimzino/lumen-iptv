using FluentAssertions;
using Lumen.Core.Models;
using Lumen.Data;
using Lumen.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Core.Tests.Data;

public sealed class DownloadRepositoryTests : IAsyncLifetime, IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "lumen-tests", Guid.NewGuid().ToString("N"));

    private SqliteConnectionFactory _factory = null!;
    private ProfileRepository _profiles = null!;
    private DownloadRepository _downloads = null!;
    private long _profileId;
    private long _otherProfileId;

    public async Task InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(Path.Combine(_directory, "download-tests.db"));
        var initializer = new DatabaseInitializer(
            _factory, new MigrationRunner(NullLogger<MigrationRunner>.Instance),
            NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        _profiles = new ProfileRepository(_factory);
        _downloads = new DownloadRepository(_factory);

        _profileId = await _profiles.InsertAsync(
            new Profile { Name = "A", Kind = ProfileKind.Xtream, CreatedUtc = 1000 }, CancellationToken.None);
        _otherProfileId = await _profiles.InsertAsync(
            new Profile { Name = "B", Kind = ProfileKind.Xtream, CreatedUtc = 1000 }, CancellationToken.None);
    }

    private DownloadItem NewMovie(string itemKey, DownloadStatus status = DownloadStatus.Queued) => new()
    {
        ProfileId = _profileId,
        Kind = ContentKind.Movie,
        ItemKey = itemKey,
        ProviderItemId = itemKey,
        ContainerExtension = "mp4",
        Title = "Movie " + itemKey,
        PosterUrl = "http://poster/" + itemKey + ".jpg",
        IsHls = false,
        FilePath = @"C:\downloads\1\movie-" + itemKey + ".mp4",
        Status = status,
        TotalBytes = 1000,
        DownloadedBytes = 0,
        ProgressPermille = 0,
        CreatedUtc = 100,
    };

    [Fact]
    public async Task Insert_RoundtripsAllFields()
    {
        var episode = new DownloadItem
        {
            ProfileId = _profileId,
            Kind = ContentKind.Series,
            ItemKey = "series-9:ep-3",
            SeriesItemKey = "series-9",
            ProviderItemId = "ep-3",
            ContainerExtension = "m3u8",
            Title = "Show · S1E3",
            Season = 1,
            EpisodeNumber = 3,
            IsHls = true,
            FilePath = @"C:\downloads\1\show-s1e3.ts",
            Status = DownloadStatus.Downloading,
            TotalBytes = null,
            DownloadedBytes = 0,
            ProgressPermille = 420,
            CreatedUtc = 200,
        };

        var id = await _downloads.InsertAsync(episode, CancellationToken.None);
        id.Should().BeGreaterThan(0);

        var loaded = await _downloads.GetByItemKeyAsync(
            _profileId, ContentKind.Series, "series-9:ep-3", CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.SeriesItemKey.Should().Be("series-9");
        loaded.ProviderItemId.Should().Be("ep-3");
        loaded.ContainerExtension.Should().Be("m3u8");
        loaded.Season.Should().Be(1);
        loaded.EpisodeNumber.Should().Be(3);
        loaded.IsHls.Should().BeTrue();
        loaded.Status.Should().Be(DownloadStatus.Downloading);
        loaded.TotalBytes.Should().BeNull();
        loaded.ProgressPermille.Should().Be(420);
    }

    [Fact]
    public async Task Insert_IsIdempotent_ReturnsSameIdWithoutDuplicating()
    {
        var first = await _downloads.InsertAsync(NewMovie("m1"), CancellationToken.None);
        var second = await _downloads.InsertAsync(NewMovie("m1"), CancellationToken.None);

        second.Should().Be(first);
        (await _downloads.GetAllAsync(_profileId, CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task UpdateProgress_And_UpdateStatus_Persist()
    {
        var id = await _downloads.InsertAsync(NewMovie("m2"), CancellationToken.None);

        await _downloads.UpdateProgressAsync(id, 512, 2048, 250, CancellationToken.None);
        await _downloads.UpdateStatusAsync(id, DownloadStatus.Completed, null, 9999, CancellationToken.None);

        var loaded = await _downloads.GetByItemKeyAsync(_profileId, ContentKind.Movie, "m2", CancellationToken.None);
        loaded!.DownloadedBytes.Should().Be(512);
        loaded.TotalBytes.Should().Be(2048);
        loaded.ProgressPermille.Should().Be(250);
        loaded.Status.Should().Be(DownloadStatus.Completed);
        loaded.CompletedUtc.Should().Be(9999);
        loaded.Error.Should().BeNull();
    }

    [Fact]
    public async Task GetByStatuses_SpansAllProfiles()
    {
        await _downloads.InsertAsync(NewMovie("m3", DownloadStatus.Downloading), CancellationToken.None);
        await _downloads.InsertAsync(NewMovie("m4", DownloadStatus.Completed), CancellationToken.None);
        await _downloads.InsertAsync(
            new DownloadItem
            {
                ProfileId = _otherProfileId,
                Kind = ContentKind.Movie,
                ItemKey = "m5",
                ProviderItemId = "m5",
                Title = "Other",
                FilePath = @"C:\downloads\2\m5.mp4",
                Status = DownloadStatus.Queued,
                CreatedUtc = 50,
            }, CancellationToken.None);

        var interrupted = await _downloads.GetByStatusesAsync(
            [DownloadStatus.Queued, DownloadStatus.Downloading], CancellationToken.None);

        interrupted.Select(d => d.ItemKey).Should().BeEquivalentTo(["m3", "m5"]);
    }

    [Fact]
    public async Task GetAll_OrdersNewestFirst_AndIsProfileScoped()
    {
        var older = NewMovie("old");
        older.CreatedUtc = 100;
        var newer = NewMovie("new");
        newer.CreatedUtc = 500;
        await _downloads.InsertAsync(older, CancellationToken.None);
        await _downloads.InsertAsync(newer, CancellationToken.None);

        var all = await _downloads.GetAllAsync(_profileId, CancellationToken.None);
        all.Select(d => d.ItemKey).Should().ContainInOrder("new", "old");
        (await _downloads.GetAllAsync(_otherProfileId, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_RemovesRow()
    {
        var id = await _downloads.InsertAsync(NewMovie("m6"), CancellationToken.None);
        await _downloads.DeleteAsync(id, CancellationToken.None);

        (await _downloads.GetByItemKeyAsync(_profileId, ContentKind.Movie, "m6", CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task DeletingProfile_CascadesToDownloads()
    {
        await _downloads.InsertAsync(NewMovie("m7"), CancellationToken.None);

        var forCleanup = await _downloads.GetByProfileForCleanupAsync(_profileId, CancellationToken.None);
        forCleanup.Should().ContainSingle().Which.FilePath.Should().Contain("movie-m7");

        await _profiles.DeleteAsync(_profileId, CancellationToken.None);

        (await _downloads.GetAllAsync(_profileId, CancellationToken.None)).Should().BeEmpty();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
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
}
