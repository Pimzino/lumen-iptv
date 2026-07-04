using FluentAssertions;
using Lumen.Core.Models;
using Lumen.Data;
using Lumen.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Core.Tests.Data;

/// <summary>Watched/completion semantics on watch history, plus the Trakt cache repositories.</summary>
public sealed class WatchedStateRepositoryTests : IAsyncLifetime, IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "lumen-tests", Guid.NewGuid().ToString("N"));

    private SqliteConnectionFactory _factory = null!;
    private WatchHistoryRepository _history = null!;
    private TraktMatchRepository _matches = null!;
    private TraktWatchedRepository _watched = null!;
    private CatalogRepository _catalog = null!;
    private long _profileId;

    public async Task InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(Path.Combine(_directory, "watched-tests.db"));
        var initializer = new DatabaseInitializer(
            _factory, new MigrationRunner(NullLogger<MigrationRunner>.Instance),
            NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        _history = new WatchHistoryRepository(_factory);
        _matches = new TraktMatchRepository(_factory);
        _watched = new TraktWatchedRepository(_factory);
        _catalog = new CatalogRepository(_factory);

        var profiles = new ProfileRepository(_factory);
        _profileId = await profiles.InsertAsync(new Profile
        {
            Name = "Test",
            Kind = ProfileKind.Xtream,
            ServerUrl = "http://portal.example.com",
            Username = "u",
            PasswordProtected = [1, 2, 3],
            CreatedUtc = 1000,
        }, CancellationToken.None);
    }

    private WatchHistoryEntry MovieEntry(string key = "m1") => new()
    {
        ProfileId = _profileId,
        ItemKind = ContentKind.Movie,
        ItemKey = key,
        Title = "Movie " + key,
        PositionSeconds = 100,
        DurationSeconds = 1000,
        WatchedUtc = 5000,
    };

    [Fact]
    public async Task Upsert_CompletionNeverRegresses_AndPlayCountAccumulates()
    {
        var finished = MovieEntry();
        finished.PositionSeconds = 0;
        finished.Completed = true;
        finished.PlayCount = 1;
        finished.CompletedUtc = 6000;
        await _history.UpsertAsync(finished, CancellationToken.None);

        // A later partial-progress save (rewatch) must keep the watched flag and count.
        var partial = MovieEntry();
        partial.PositionSeconds = 250;
        partial.WatchedUtc = 7000;
        await _history.UpsertAsync(partial, CancellationToken.None);

        var loaded = await _history.GetAsync(_profileId, ContentKind.Movie, "m1", CancellationToken.None);
        loaded!.Completed.Should().BeTrue("completion never regresses");
        loaded.PlayCount.Should().Be(1);
        loaded.CompletedUtc.Should().Be(6000);
        loaded.PositionSeconds.Should().Be(250, "resume point still tracks the rewatch");

        // Finishing again credits one more play.
        var again = MovieEntry();
        again.PositionSeconds = 0;
        again.Completed = true;
        again.PlayCount = 1;
        again.CompletedUtc = 8000;
        await _history.UpsertAsync(again, CancellationToken.None);

        loaded = await _history.GetAsync(_profileId, ContentKind.Movie, "m1", CancellationToken.None);
        loaded!.PlayCount.Should().Be(2);
        loaded.CompletedUtc.Should().Be(8000);
    }

    [Fact]
    public async Task Upsert_ZeroFilledLiveEntry_LeavesWatchedColumnsAlone()
    {
        var live = new WatchHistoryEntry
        {
            ProfileId = _profileId,
            ItemKind = ContentKind.Live,
            ItemKey = "42",
            Title = "Channel",
            WatchedUtc = 5000,
        };
        await _history.UpsertAsync(live, CancellationToken.None);
        await _history.UpsertAsync(live, CancellationToken.None);

        var loaded = await _history.GetAsync(_profileId, ContentKind.Live, "42", CancellationToken.None);
        loaded!.Completed.Should().BeFalse();
        loaded.PlayCount.Should().Be(0);
        loaded.CompletedUtc.Should().BeNull();
        loaded.Season.Should().BeNull();
    }

    [Fact]
    public async Task SetCompleted_InsertsWhenMissing_AndClearsResumePosition()
    {
        // Mark a never-played episode watched manually.
        var entry = new WatchHistoryEntry
        {
            ProfileId = _profileId,
            ItemKind = ContentKind.Series,
            ItemKey = "show1:ep5",
            Title = "Show · S1E5",
            WatchedUtc = 5000,
            PlayCount = 1,
            CompletedUtc = 5000,
            Season = 1,
            EpisodeNumber = 5,
        };
        await _history.SetCompletedAsync(entry, completed: true, CancellationToken.None);

        var loaded = await _history.GetAsync(_profileId, ContentKind.Series, "show1:ep5", CancellationToken.None);
        loaded!.Completed.Should().BeTrue();
        loaded.PlayCount.Should().Be(1);
        loaded.Season.Should().Be(1);
        loaded.EpisodeNumber.Should().Be(5);

        // Watching it mid-way then marking watched again clears the resume point but keeps plays.
        var midway = new WatchHistoryEntry
        {
            ProfileId = _profileId,
            ItemKind = ContentKind.Series,
            ItemKey = "show1:ep5",
            Title = "Show · S1E5",
            PositionSeconds = 300,
            DurationSeconds = 1200,
            WatchedUtc = 6000,
        };
        await _history.UpsertAsync(midway, CancellationToken.None);
        await _history.SetCompletedAsync(entry, completed: true, CancellationToken.None);

        loaded = await _history.GetAsync(_profileId, ContentKind.Series, "show1:ep5", CancellationToken.None);
        loaded!.PositionSeconds.Should().Be(0, "watched items must not nag to resume");
        loaded.PlayCount.Should().Be(1, "re-marking watched is not another play");

        // Unwatch resets completion, plays, and position — the row itself stays.
        await _history.SetCompletedAsync(entry, completed: false, CancellationToken.None);
        loaded = await _history.GetAsync(_profileId, ContentKind.Series, "show1:ep5", CancellationToken.None);
        loaded!.Completed.Should().BeFalse();
        loaded.PlayCount.Should().Be(0);
        loaded.CompletedUtc.Should().BeNull();
    }

    [Fact]
    public async Task GetForSeries_IsolatesKeyPrefix()
    {
        await _history.UpsertAsync(SeriesEntry("10", "a", 1), CancellationToken.None);
        await _history.UpsertAsync(SeriesEntry("10", "b", 2), CancellationToken.None);
        await _history.UpsertAsync(SeriesEntry("100", "c", 3), CancellationToken.None); // shares the "10" prefix text
        await _history.UpsertAsync(SeriesEntry("9", "d", 4), CancellationToken.None);

        var rows = await _history.GetForSeriesAsync(_profileId, "10", CancellationToken.None);

        rows.Should().HaveCount(2);
        rows.Select(r => r.ItemKey).Should().BeEquivalentTo("10:a", "10:b");
    }

    [Fact]
    public async Task GetByKeys_ChunksPastTheParameterLimit()
    {
        for (var i = 0; i < 15; i++)
        {
            await _history.UpsertAsync(MovieEntry($"movie{i}"), CancellationToken.None);
        }

        // 900 keys forces three chunks; only the 15 stored rows come back.
        var keys = Enumerable.Range(0, 900).Select(i => $"movie{i}").ToList();
        var rows = await _history.GetByKeysAsync(_profileId, ContentKind.Movie, keys, CancellationToken.None);

        rows.Should().HaveCount(15);
    }

    [Fact]
    public async Task GetSeriesWatchSummary_CountsCompletedAsOneAndPartialsAsFractions()
    {
        // s1: one finished episode (position resets to 0) + one watched halfway.
        var finished = SeriesEntry("s1", "e1", watchedUtc: 100);
        finished.PositionSeconds = 0;
        finished.Completed = true;
        finished.PlayCount = 1;
        await _history.UpsertAsync(finished, CancellationToken.None);

        var half = SeriesEntry("s1", "e2", watchedUtc: 300);
        half.PositionSeconds = 300;
        half.DurationSeconds = 600;
        await _history.UpsertAsync(half, CancellationToken.None);

        await _history.UpsertAsync(SeriesEntry("s2", "e9", watchedUtc: 200), CancellationToken.None);

        var summaries = await _history.GetSeriesWatchSummaryAsync(
            _profileId, ["s1", "s2", "s3"], CancellationToken.None);

        summaries.Should().HaveCount(2);
        summaries["s1"].WatchedUnits.Should().BeApproximately(1.5, 0.001);
        summaries["s1"].CompletedCount.Should().Be(1);
        summaries["s2"].WatchedUnits.Should().BeApproximately(0.1, 0.001); // 60s of 600s
        summaries["s2"].CompletedCount.Should().Be(0);
    }

    [Fact]
    public async Task SeriesEpisodeTotal_PersistsAndSurvivesCatalogRefresh()
    {
        var snapshot = new List<VodItem>
        {
            new() { ProviderItemId = "show9", Name = "Show Nine", Kind = ContentKind.Series },
        };
        await _catalog.UpsertVodItemsAsync(_profileId, ContentKind.Series, snapshot, CancellationToken.None);
        var item = (await _catalog.GetVodItemsAsync(
            _profileId, ContentKind.Series, null, null, Lumen.Core.Abstractions.VodSortOrder.Name, 10, 0,
            CancellationToken.None)).Single();
        item.EpisodeTotal.Should().BeNull();

        await _catalog.SetSeriesEpisodeTotalAsync(item.Id, 28, CancellationToken.None);

        // A provider snapshot refresh must not wipe the cached total.
        await _catalog.UpsertVodItemsAsync(_profileId, ContentKind.Series, snapshot, CancellationToken.None);

        var reloaded = await _catalog.GetVodItemAsync(item.Id, CancellationToken.None);
        reloaded!.EpisodeTotal.Should().Be(28);
    }

    [Fact]
    public async Task TraktMatch_UpsertAndNegativeClear()
    {
        var positive = new TraktMatch
        {
            ProfileId = _profileId,
            ItemKind = ContentKind.Movie,
            ItemKey = "m1",
            TraktId = 11,
            TmdbId = 603,
            Method = TraktMatchMethod.TitleJoin,
            MatchedUtc = 1000,
        };
        var negative = new TraktMatch
        {
            ProfileId = _profileId,
            ItemKind = ContentKind.Movie,
            ItemKey = "m2",
            Method = TraktMatchMethod.Search,
            MatchedUtc = 1000,
        };
        await _matches.UpsertAsync(positive, CancellationToken.None);
        await _matches.UpsertAsync(negative, CancellationToken.None);

        (await _matches.GetAsync(_profileId, ContentKind.Movie, "m2", CancellationToken.None))!
            .IsNegative.Should().BeTrue();

        await _matches.ClearNegativeAsync(CancellationToken.None);

        (await _matches.GetAsync(_profileId, ContentKind.Movie, "m2", CancellationToken.None)).Should().BeNull();
        (await _matches.GetAsync(_profileId, ContentKind.Movie, "m1", CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task TraktWatched_ReplaceAllAndShowQueries()
    {
        var first = new List<TraktWatchedItem>
        {
            new() { MediaType = TraktMediaType.Movie, TraktId = 1, Title = "Old Movie", Plays = 1, LastWatchedUtc = 10 },
        };
        await _watched.ReplaceAllAsync(first, CancellationToken.None);

        var second = new List<TraktWatchedItem>
        {
            new() { MediaType = TraktMediaType.Movie, TraktId = 2, TmdbId = 603, Title = "The Matrix", Year = 1999, Plays = 2, LastWatchedUtc = 20 },
            new() { MediaType = TraktMediaType.Episode, TraktId = 50, Title = "The Bear", Season = 1, EpisodeNumber = 1, Plays = 1, LastWatchedUtc = 30 },
            new() { MediaType = TraktMediaType.Episode, TraktId = 50, Title = "The Bear", Season = 1, EpisodeNumber = 2, Plays = 1, LastWatchedUtc = 40 },
        };
        await _watched.ReplaceAllAsync(second, CancellationToken.None);

        (await _watched.GetAllAsync(CancellationToken.None)).Should().HaveCount(3, "replace drops the old snapshot");
        (await _watched.GetMoviesAsync(CancellationToken.None)).Single().Title.Should().Be("The Matrix");
        (await _watched.GetEpisodesForShowAsync(50, CancellationToken.None)).Should().HaveCount(2);

        await _watched.DeleteAsync(TraktMediaType.Episode, 50, 1, 2, CancellationToken.None);
        (await _watched.GetEpisodesForShowAsync(50, CancellationToken.None)).Single().EpisodeNumber.Should().Be(1);
    }

    private WatchHistoryEntry SeriesEntry(string seriesId, string episodeId, long watchedUtc) => new()
    {
        ProfileId = _profileId,
        ItemKind = ContentKind.Series,
        ItemKey = $"{seriesId}:{episodeId}",
        Title = $"{seriesId} {episodeId}",
        PositionSeconds = 60,
        DurationSeconds = 600,
        WatchedUtc = watchedUtc,
    };

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
