using FluentAssertions;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Lumen.Data;
using Lumen.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Core.Tests.Data;

public sealed class RepositoryTests : IAsyncLifetime, IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "lumen-tests", Guid.NewGuid().ToString("N"));

    private SqliteConnectionFactory _factory = null!;
    private ProfileRepository _profiles = null!;
    private CatalogRepository _catalog = null!;
    private EpgRepository _epg = null!;
    private FavoritesRepository _favorites = null!;
    private WatchHistoryRepository _history = null!;
    private SettingsRepository _settings = null!;
    private long _profileId;

    public async Task InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(Path.Combine(_directory, "repo-tests.db"));
        var initializer = new DatabaseInitializer(
            _factory, new MigrationRunner(NullLogger<MigrationRunner>.Instance),
            NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        _profiles = new ProfileRepository(_factory);
        _catalog = new CatalogRepository(_factory);
        _epg = new EpgRepository(_factory);
        _favorites = new FavoritesRepository(_factory);
        _history = new WatchHistoryRepository(_factory);
        _settings = new SettingsRepository(_factory);

        _profileId = await _profiles.InsertAsync(new Profile
        {
            Name = "Test",
            Kind = ProfileKind.Xtream,
            ServerUrl = "http://portal.example.com",
            Username = "u",
            PasswordProtected = [1, 2, 3],
            CreatedUtc = 1000,
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Profile_RoundtripsAllFields()
    {
        var loaded = await _profiles.GetAsync(_profileId, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Test");
        loaded.Kind.Should().Be(ProfileKind.Xtream);
        loaded.ServerUrl.Should().Be("http://portal.example.com");
        loaded.PasswordProtected.Should().Equal(1, 2, 3);

        loaded.Name = "Renamed";
        loaded.PreferHls = true;
        await _profiles.UpdateAsync(loaded, CancellationToken.None);

        var reloaded = await _profiles.GetAsync(_profileId, CancellationToken.None);
        reloaded!.Name.Should().Be("Renamed");
        reloaded.PreferHls.Should().BeTrue();
    }

    [Fact]
    public async Task Categories_ReplaceKeepsRowIdsStable()
    {
        var first = new List<Category>
        {
            new() { ProviderCategoryId = "10", Name = "News", SortOrder = 0 },
            new() { ProviderCategoryId = "20", Name = "Sports", SortOrder = 1 },
        };
        await _catalog.ReplaceCategoriesAsync(_profileId, ContentKind.Live, first, CancellationToken.None);
        var initial = await _catalog.GetCategoriesAsync(_profileId, ContentKind.Live, CancellationToken.None);
        var newsId = initial.Single(c => c.Name == "News").Id;

        var second = new List<Category>
        {
            new() { ProviderCategoryId = "10", Name = "News & Docs", SortOrder = 0 },
            new() { ProviderCategoryId = "30", Name = "Kids", SortOrder = 2 },
        };
        await _catalog.ReplaceCategoriesAsync(_profileId, ContentKind.Live, second, CancellationToken.None);

        var after = await _catalog.GetCategoriesAsync(_profileId, ContentKind.Live, CancellationToken.None);
        after.Should().HaveCount(2);
        after.Single(c => c.ProviderCategoryId == "10").Id.Should().Be(newsId, "ids anchor favorites");
        after.Single(c => c.ProviderCategoryId == "10").Name.Should().Be("News & Docs");
        after.Should().NotContain(c => c.ProviderCategoryId == "20");
    }

    [Fact]
    public async Task Channels_SnapshotSync_UpdatesInsertsAndDeletes()
    {
        var snapshot1 = new List<Channel>
        {
            new() { ProviderStreamId = "101", Name = "BBC One", AddedUtc = 1 },
            new() { ProviderStreamId = "102", Name = "Sky Sports", AddedUtc = 1 },
        };
        await _catalog.UpsertChannelsAsync(_profileId, snapshot1, CancellationToken.None);
        var initial = await _catalog.GetChannelsAsync(_profileId, null, CancellationToken.None);
        var bbcId = initial.Single(c => c.Name == "BBC One").Id;

        var snapshot2 = new List<Channel>
        {
            new() { ProviderStreamId = "101", Name = "BBC One HD", LogoUrl = "http://logo/1.png", AddedUtc = 2 },
            new() { ProviderStreamId = "103", Name = "Channel 4", AddedUtc = 2 },
        };
        await _catalog.UpsertChannelsAsync(_profileId, snapshot2, CancellationToken.None);

        var after = await _catalog.GetChannelsAsync(_profileId, null, CancellationToken.None);
        after.Should().HaveCount(2);
        var bbc = after.Single(c => c.ProviderStreamId == "101");
        bbc.Id.Should().Be(bbcId, "existing rows update in place");
        bbc.Name.Should().Be("BBC One HD");
        bbc.LogoUrl.Should().Be("http://logo/1.png");
        after.Should().NotContain(c => c.ProviderStreamId == "102");
        (await _catalog.CountChannelsAsync(_profileId, CancellationToken.None)).Should().Be(2);
    }

    [Fact]
    public async Task VodItems_UpsertAndSortAndRecent()
    {
        var items = new List<VodItem>
        {
            new() { ProviderItemId = "1", Name = "Alpha", Rating = 6.1, ProviderAddedUtc = 100 },
            new() { ProviderItemId = "2", Name = "Bravo", Rating = 9.0, ProviderAddedUtc = 300 },
            new() { ProviderItemId = "3", Name = "Charlie", Rating = 7.5, ProviderAddedUtc = 200 },
        };
        await _catalog.UpsertVodItemsAsync(_profileId, ContentKind.Movie, items, CancellationToken.None);

        var byName = await _catalog.GetVodItemsAsync(
            _profileId, ContentKind.Movie, null, null, VodSortOrder.Name, 10, 0, CancellationToken.None);
        byName.Select(i => i.Name).Should().ContainInOrder("Alpha", "Bravo", "Charlie");

        var byRating = await _catalog.GetVodItemsAsync(
            _profileId, ContentKind.Movie, null, null, VodSortOrder.Rating, 10, 0, CancellationToken.None);
        byRating[0].Name.Should().Be("Bravo");

        var recent = await _catalog.GetRecentVodAsync(_profileId, ContentKind.Movie, 2, CancellationToken.None);
        recent.Select(i => i.Name).Should().ContainInOrder("Bravo", "Charlie");

        // Snapshot without item 3 removes it.
        await _catalog.UpsertVodItemsAsync(
            _profileId, ContentKind.Movie, items.Take(2).ToList(), CancellationToken.None);
        var after = await _catalog.GetVodItemsAsync(
            _profileId, ContentKind.Movie, null, null, VodSortOrder.Name, 10, 0, CancellationToken.None);
        after.Should().HaveCount(2);
    }

    [Fact]
    public async Task VodItems_SearchFiltersByNameSubstring()
    {
        var items = new List<VodItem>
        {
            new() { ProviderItemId = "1", Name = "The Matrix" },
            new() { ProviderItemId = "2", Name = "Matrix Reloaded" },
            new() { ProviderItemId = "3", Name = "Inception" },
        };
        await _catalog.UpsertVodItemsAsync(_profileId, ContentKind.Movie, items, CancellationToken.None);

        var hits = await _catalog.GetVodItemsAsync(
            _profileId, ContentKind.Movie, null, "matrix", VodSortOrder.Name, 10, 0, CancellationToken.None);
        hits.Select(i => i.Name).Should().ContainInOrder("Matrix Reloaded", "The Matrix");

        var none = await _catalog.GetVodItemsAsync(
            _profileId, ContentKind.Movie, null, "zzz", VodSortOrder.Name, 10, 0, CancellationToken.None);
        none.Should().BeEmpty();

        // Blank search means no filter.
        var blank = await _catalog.GetVodItemsAsync(
            _profileId, ContentKind.Movie, null, "   ", VodSortOrder.Name, 10, 0, CancellationToken.None);
        blank.Should().HaveCount(3);
    }

    [Fact]
    public async Task VodItems_SearchTreatsLikeWildcardsAsLiterals()
    {
        var items = new List<VodItem>
        {
            new() { ProviderItemId = "1", Name = "100% Wolf" },
            new() { ProviderItemId = "2", Name = "100 Wolves" },
            new() { ProviderItemId = "3", Name = "Snake_Eyes" },
            new() { ProviderItemId = "4", Name = "SnakeXEyes" },
        };
        await _catalog.UpsertVodItemsAsync(_profileId, ContentKind.Movie, items, CancellationToken.None);

        var percent = await _catalog.GetVodItemsAsync(
            _profileId, ContentKind.Movie, null, "100%", VodSortOrder.Name, 10, 0, CancellationToken.None);
        percent.Should().ContainSingle().Which.Name.Should().Be("100% Wolf");

        var underscore = await _catalog.GetVodItemsAsync(
            _profileId, ContentKind.Movie, null, "e_E", VodSortOrder.Name, 10, 0, CancellationToken.None);
        underscore.Should().ContainSingle().Which.Name.Should().Be("Snake_Eyes");
    }

    [Fact]
    public async Task VodItems_LookupByProviderId()
    {
        await _catalog.UpsertVodItemsAsync(_profileId, ContentKind.Movie, new List<VodItem>
        {
            new() { ProviderItemId = "movie-1", Name = "Alpha" },
        }, CancellationToken.None);

        var found = await _catalog.GetVodItemByProviderIdAsync(
            _profileId, ContentKind.Movie, "movie-1", CancellationToken.None);
        found.Should().NotBeNull();
        found!.Name.Should().Be("Alpha");

        (await _catalog.GetVodItemByProviderIdAsync(
            _profileId, ContentKind.Series, "movie-1", CancellationToken.None)).Should().BeNull("kind is part of the key");
        (await _catalog.GetVodItemByProviderIdAsync(
            _profileId, ContentKind.Movie, "missing", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Epg_NowNext_ComputesAiringAndUpcoming()
    {
        await SeedEpgAsync(
            ("bbc1.uk", 1000, 2000, "Now Showing"),
            ("bbc1.uk", 2000, 3000, "Up Next"),
            ("bbc1.uk", 3000, 4000, "Later"),
            ("idle.ch", 5000, 6000, "Future Only"));

        var nowNext = await _epg.GetNowNextAsync(
            _profileId, ["bbc1.uk", "idle.ch", "missing.ch"], nowUnix: 1500, CancellationToken.None);

        nowNext["bbc1.uk"].Now!.Title.Should().Be("Now Showing");
        nowNext["bbc1.uk"].Next!.Title.Should().Be("Up Next");
        nowNext["idle.ch"].Now.Should().BeNull();
        nowNext["idle.ch"].Next!.Title.Should().Be("Future Only");
        nowNext.Should().NotContainKey("missing.ch");
    }

    [Fact]
    public async Task Epg_RangeQuery_ReturnsOverlappingProgrammes()
    {
        await SeedEpgAsync(
            ("bbc1.uk", 1000, 2000, "A"),
            ("bbc1.uk", 2000, 3000, "B"),
            ("bbc1.uk", 3000, 4000, "C"));

        var slice = await _epg.GetProgrammesAsync(
            _profileId, ["bbc1.uk"], fromUnix: 1500, toUnix: 3500, CancellationToken.None);

        slice.Select(p => p.Title).Should().ContainInOrder("A", "B", "C");
    }

    [Fact]
    public async Task Epg_Purge_RemovesFinishedProgrammes()
    {
        await SeedEpgAsync(
            ("bbc1.uk", 1000, 2000, "Old"),
            ("bbc1.uk", 9000, 9500, "Fresh"));

        var removed = await _epg.PurgeProgrammesBeforeAsync(_profileId, cutoffUnix: 5000, CancellationToken.None);

        removed.Should().Be(1);
        var counts = await _epg.GetCountsAsync(_profileId, CancellationToken.None);
        counts.Programmes.Should().Be(1);
    }

    [Fact]
    public async Task Epg_AutoMapping_RespectsManualOverrides()
    {
        await _catalog.UpsertChannelsAsync(_profileId, new List<Channel>
        {
            new() { ProviderStreamId = "1", Name = "BBC One HD", AddedUtc = 1 },
            new() { ProviderStreamId = "2", Name = "Manual Channel", AddedUtc = 1 },
        }, CancellationToken.None);
        var channels = await _catalog.GetChannelsAsync(_profileId, null, CancellationToken.None);
        var manualChannelId = channels.Single(c => c.Name == "Manual Channel").Id;

        await SeedEpgAsync(("bbc1.uk", 1000, 2000, "X"));
        await SeedEpgChannelAsync("bbc1.uk", "BBC One");
        await SeedEpgChannelAsync("manual.target", "Something Else");

        await _epg.SetManualMappingAsync(manualChannelId, "manual.target", CancellationToken.None);
        var mapped = await _epg.ApplyAutoMappingsAsync(_profileId, CancellationToken.None);

        mapped.Should().Be(1);
        var mappings = await _epg.GetMappingsAsync(_profileId, CancellationToken.None);
        mappings.Should().HaveCount(2);
        mappings.Single(m => m.IsManual).XmltvId.Should().Be("manual.target");
        mappings.Single(m => !m.IsManual).XmltvId.Should().Be("bbc1.uk");
    }

    [Fact]
    public async Task Favorites_AddRemoveRoundtrip()
    {
        await _favorites.AddAsync(_profileId, ContentKind.Live, "42", 1111, CancellationToken.None);
        await _favorites.AddAsync(_profileId, ContentKind.Live, "42", 2222, CancellationToken.None); // idempotent

        var favorites = await _favorites.GetAllAsync(_profileId, CancellationToken.None);
        favorites.Should().ContainSingle();
        favorites[0].AddedUtc.Should().Be(1111);

        await _favorites.RemoveAsync(_profileId, ContentKind.Live, "42", CancellationToken.None);
        (await _favorites.GetAllAsync(_profileId, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task WatchHistory_UpsertsResumePosition()
    {
        var entry = new WatchHistoryEntry
        {
            ProfileId = _profileId,
            ItemKind = ContentKind.Movie,
            ItemKey = "movie-7",
            Title = "The Long Voyage",
            PositionSeconds = 600,
            DurationSeconds = 8130,
            WatchedUtc = 1000,
        };
        await _history.UpsertAsync(entry, CancellationToken.None);

        entry.PositionSeconds = 1200;
        entry.WatchedUtc = 2000;
        await _history.UpsertAsync(entry, CancellationToken.None);

        var loaded = await _history.GetAsync(_profileId, ContentKind.Movie, "movie-7", CancellationToken.None);
        loaded!.PositionSeconds.Should().Be(1200);
        loaded.Progress.Should().BeApproximately(1200.0 / 8130.0, 0.0001);

        var recent = await _history.GetRecentAsync(_profileId, 5, CancellationToken.None);
        recent.Should().ContainSingle();
    }

    [Fact]
    public async Task Settings_GlobalAndPerProfileAreIsolated()
    {
        await _settings.SetAsync(0, "active_profile", "7", CancellationToken.None);
        await _settings.SetAsync(_profileId, "buffer_ms", "1500", CancellationToken.None);
        await _settings.SetAsync(_profileId, "buffer_ms", "2000", CancellationToken.None);

        (await _settings.GetAsync(0, "active_profile", CancellationToken.None)).Should().Be("7");
        (await _settings.GetAsync(_profileId, "buffer_ms", CancellationToken.None)).Should().Be("2000");
        (await _settings.GetAsync(_profileId, "active_profile", CancellationToken.None)).Should().BeNull();

        var all = await _settings.GetAllAsync(_profileId, CancellationToken.None);
        all.Should().ContainKey("buffer_ms");

        await _settings.DeleteAsync(_profileId, "buffer_ms", CancellationToken.None);
        (await _settings.GetAsync(_profileId, "buffer_ms", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task DeletingProfile_CascadesToChildRows()
    {
        await _catalog.UpsertChannelsAsync(_profileId, new List<Channel>
        {
            new() { ProviderStreamId = "1", Name = "X", AddedUtc = 1 },
        }, CancellationToken.None);
        await _favorites.AddAsync(_profileId, ContentKind.Live, "1", 1, CancellationToken.None);

        await _profiles.DeleteAsync(_profileId, CancellationToken.None);

        (await _catalog.CountChannelsAsync(_profileId, CancellationToken.None)).Should().Be(0);
        (await _favorites.GetAllAsync(_profileId, CancellationToken.None)).Should().BeEmpty();
    }

    private async Task SeedEpgAsync(params (string Channel, long Start, long Stop, string Title)[] programmes)
    {
        var sink = new SqliteEpgImportSinkFactory(_factory).Create(_profileId);
        await using (sink.ConfigureAwait(false))
        {
            await sink.BeginAsync(CancellationToken.None);
            foreach (var (channel, start, stop, title) in programmes)
            {
                await sink.AddProgrammeAsync(new Programme
                {
                    ChannelXmltvId = channel,
                    StartUtc = start,
                    StopUtc = stop,
                    Title = title,
                }, CancellationToken.None);
            }

            await sink.CompleteAsync(CancellationToken.None);
        }
    }

    private async Task SeedEpgChannelAsync(string xmltvId, string displayName)
    {
        var connection = await _factory.OpenAsync();
        await using (connection.ConfigureAwait(false))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT OR IGNORE INTO epg_channels (profile_id, xmltv_id, display_name) VALUES ($p, $id, $name);";
            command.Parameters.AddWithValue("$p", _profileId);
            command.Parameters.AddWithValue("$id", xmltvId);
            command.Parameters.AddWithValue("$name", displayName);
            command.ExecuteNonQuery();
        }
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
