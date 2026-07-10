using FluentAssertions;
using Lumen.Core.Models;
using Lumen.Data;
using Lumen.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Core.Tests.Data;

public sealed class RecordingRepositoryTests : IAsyncLifetime, IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "lumen-tests", Guid.NewGuid().ToString("N"));

    private SqliteConnectionFactory _factory = null!;
    private ProfileRepository _profiles = null!;
    private RecordingRepository _recordings = null!;
    private long _profileId;
    private long _otherProfileId;

    public async Task InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(Path.Combine(_directory, "recording-tests.db"));
        var initializer = new DatabaseInitializer(
            _factory, new MigrationRunner(NullLogger<MigrationRunner>.Instance),
            NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        _profiles = new ProfileRepository(_factory);
        _recordings = new RecordingRepository(_factory);

        _profileId = await _profiles.InsertAsync(
            new Profile { Name = "A", Kind = ProfileKind.Xtream, CreatedUtc = 1000 }, CancellationToken.None);
        _otherProfileId = await _profiles.InsertAsync(
            new Profile { Name = "B", Kind = ProfileKind.Xtream, CreatedUtc = 1000 }, CancellationToken.None);
    }

    private Recording NewRecording(string channel, DownloadStatus status = DownloadStatus.Downloading) => new()
    {
        ProfileId = _profileId,
        ChannelId = 42,
        ChannelName = channel,
        ProgrammeTitle = "News at Nine",
        LogoUrl = "http://logo/" + channel + ".png",
        FilePath = @"C:\recordings\1\" + channel + "-20260709-2100.ts",
        Status = status,
        StartedUtc = 5000,
    };

    [Fact]
    public async Task Insert_RoundtripsAllFields()
    {
        var id = await _recordings.InsertAsync(NewRecording("BBC One"), CancellationToken.None);
        id.Should().BeGreaterThan(0);

        var all = await _recordings.GetAllAsync(_profileId, CancellationToken.None);
        var loaded = all.Should().ContainSingle().Subject;
        loaded.Id.Should().Be(id);
        loaded.ChannelId.Should().Be(42);
        loaded.ChannelName.Should().Be("BBC One");
        loaded.ProgrammeTitle.Should().Be("News at Nine");
        loaded.LogoUrl.Should().Be("http://logo/BBC One.png");
        loaded.Status.Should().Be(DownloadStatus.Downloading);
        loaded.StartedUtc.Should().Be(5000);
        loaded.StoppedUtc.Should().BeNull();
        loaded.DurationSeconds.Should().BeNull();
        loaded.SizeBytes.Should().Be(0);
    }

    [Fact]
    public async Task UpdateStatus_FinalizePersistsStopDurationAndSize()
    {
        var id = await _recordings.InsertAsync(NewRecording("Sky Sports"), CancellationToken.None);

        await _recordings.UpdateStatusAsync(
            id, DownloadStatus.Completed, null, stoppedUtc: 5900, durationSeconds: 900, sizeBytes: 123456,
            CancellationToken.None);

        var loaded = (await _recordings.GetAllAsync(_profileId, CancellationToken.None)).Single(r => r.Id == id);
        loaded.Status.Should().Be(DownloadStatus.Completed);
        loaded.StoppedUtc.Should().Be(5900);
        loaded.DurationSeconds.Should().Be(900);
        loaded.SizeBytes.Should().Be(123456);
        loaded.Error.Should().BeNull();
    }

    [Fact]
    public async Task UpdateStatus_FailureKeepsErrorMessage()
    {
        var id = await _recordings.InsertAsync(NewRecording("Pulse News"), CancellationToken.None);

        await _recordings.UpdateStatusAsync(
            id, DownloadStatus.Failed, "The stream did not start.", null, null, 0, CancellationToken.None);

        var loaded = (await _recordings.GetAllAsync(_profileId, CancellationToken.None)).Single(r => r.Id == id);
        loaded.Status.Should().Be(DownloadStatus.Failed);
        loaded.Error.Should().Be("The stream did not start.");
    }

    [Fact]
    public async Task GetByStatus_SpansAllProfiles()
    {
        await _recordings.InsertAsync(NewRecording("One"), CancellationToken.None);
        await _recordings.InsertAsync(NewRecording("Two", DownloadStatus.Completed), CancellationToken.None);
        await _recordings.InsertAsync(new Recording
        {
            ProfileId = _otherProfileId,
            ChannelName = "Other",
            FilePath = @"C:\recordings\2\other.ts",
            Status = DownloadStatus.Downloading,
            StartedUtc = 1,
        }, CancellationToken.None);

        var recording = await _recordings.GetByStatusAsync(DownloadStatus.Downloading, CancellationToken.None);

        recording.Select(r => r.ChannelName).Should().BeEquivalentTo(["One", "Other"]);
    }

    [Fact]
    public async Task GetAll_OrdersNewestFirst_AndIsProfileScoped()
    {
        var older = NewRecording("Old");
        older.StartedUtc = 100;
        var newer = NewRecording("New");
        newer.StartedUtc = 900;
        await _recordings.InsertAsync(older, CancellationToken.None);
        await _recordings.InsertAsync(newer, CancellationToken.None);

        var all = await _recordings.GetAllAsync(_profileId, CancellationToken.None);
        all.Select(r => r.ChannelName).Should().ContainInOrder("New", "Old");
        (await _recordings.GetAllAsync(_otherProfileId, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateTitle_SetsAndClearsCustomTitle()
    {
        var id = await _recordings.InsertAsync(NewRecording("Rename Me"), CancellationToken.None);

        await _recordings.UpdateTitleAsync(id, "Match of the Day", CancellationToken.None);
        var renamed = (await _recordings.GetAllAsync(_profileId, CancellationToken.None)).Single(r => r.Id == id);
        renamed.CustomTitle.Should().Be("Match of the Day");

        await _recordings.UpdateTitleAsync(id, null, CancellationToken.None);
        var cleared = (await _recordings.GetAllAsync(_profileId, CancellationToken.None)).Single(r => r.Id == id);
        cleared.CustomTitle.Should().BeNull();
    }

    [Fact]
    public async Task Delete_RemovesRow()
    {
        var id = await _recordings.InsertAsync(NewRecording("Gone"), CancellationToken.None);
        await _recordings.DeleteAsync(id, CancellationToken.None);

        (await _recordings.GetAllAsync(_profileId, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task DeletingProfile_CascadesToRecordings()
    {
        await _recordings.InsertAsync(NewRecording("Cascade"), CancellationToken.None);

        var forCleanup = await _recordings.GetByProfileForCleanupAsync(_profileId, CancellationToken.None);
        forCleanup.Should().ContainSingle().Which.FilePath.Should().Contain("Cascade");

        await _profiles.DeleteAsync(_profileId, CancellationToken.None);

        (await _recordings.GetAllAsync(_profileId, CancellationToken.None)).Should().BeEmpty();
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
