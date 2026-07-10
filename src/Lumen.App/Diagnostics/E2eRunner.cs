using System.IO;
using System.Text;
using Lumen.App.Services;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Lumen.App.Diagnostics;

/// <summary>
/// Headless end-to-end driver for the Phase-3 gate: adds Xtream and M3U profiles against a
/// fixture server through the same service path onboarding uses (DPAPI included), syncs the
/// catalog and EPG, and writes a machine-checkable report. <c>--e2e-verify</c> re-runs
/// against the same database to prove restart persistence.
/// </summary>
public static class E2eRunner
{
    public static async Task<int> RunAsync(IServiceProvider services, string server, string outFile)
    {
        var report = new StringBuilder();
        try
        {
            var session = services.GetRequiredService<ISessionService>();
            await session.InitializeAsync(CancellationToken.None);

            // Xtream profile through the real add-profile path.
            var xtream = new Profile
            {
                Name = "E2E Xtream",
                Kind = ProfileKind.Xtream,
                ServerUrl = server,
                Username = "demo",
            };
            await session.AddProfileAsync(xtream, "demo", CancellationToken.None);

            var catalogSync = services.GetRequiredService<ICatalogSyncService>();
            var epgSync = services.GetRequiredService<IEpgSyncService>();

            var xtreamCatalog = await catalogSync.SyncAsync(xtream, CancellationToken.None);
            report.AppendLine(
                $"xtream channels={xtreamCatalog.Channels} movies={xtreamCatalog.Movies} series={xtreamCatalog.Series}");

            var xtreamEpg = await epgSync.RefreshAsync(xtream, null, CancellationToken.None);
            report.AppendLine($"xtream-epg channels={xtreamEpg.Channels} programmes={xtreamEpg.Programmes}");

            // M3U profile with an explicit XMLTV source.
            var m3u = new Profile
            {
                Name = "E2E M3U",
                Kind = ProfileKind.M3u,
                PlaylistSource = $"{server}/playlist.m3u",
                EpgSource = $"{server}/xmltv.php",
            };
            await session.AddProfileAsync(m3u, null, CancellationToken.None);

            var m3uCatalog = await catalogSync.SyncAsync(m3u, CancellationToken.None);
            report.AppendLine(
                $"m3u channels={m3uCatalog.Channels} movies={m3uCatalog.Movies} series={m3uCatalog.Series}");

            var m3uEpg = await epgSync.RefreshAsync(m3u, null, CancellationToken.None);
            report.AppendLine($"m3u-epg channels={m3uEpg.Channels} programmes={m3uEpg.Programmes}");

            // Mapping + now/next sanity for the M3U profile (tvg-id matching).
            var epgRepository = services.GetRequiredService<IEpgRepository>();
            var mappings = await epgRepository.GetMappingsAsync(m3u.Id, CancellationToken.None);
            report.AppendLine($"m3u-mappings count={mappings.Count}");

            var nowNext = await epgRepository.GetNowNextAsync(
                m3u.Id,
                mappings.Select(m => m.XmltvId).ToList(),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                CancellationToken.None);
            var withNow = nowNext.Values.Count(n => n.Now is not null);
            report.AppendLine($"m3u-nownext channelsWithNow={withNow}");

            // DPAPI roundtrip through the stored profile.
            var profiles = await services.GetRequiredService<IProfileRepository>().GetAllAsync(CancellationToken.None);
            var stored = profiles.First(p => p.Kind == ProfileKind.Xtream);
            var credentials = session.GetXtreamCredentials(stored);
            report.AppendLine($"credentials-roundtrip={credentials?.Password == "demo"}");
            report.AppendLine($"profiles={profiles.Count}");

            var pass =
                xtreamCatalog.Channels == 5 && xtreamCatalog.Movies == 3 && xtreamCatalog.Series == 1 &&
                m3uCatalog.Channels == 5 && m3uCatalog.Movies == 1 &&
                xtreamEpg.Programmes > 0 && m3uEpg.Programmes > 0 &&
                mappings.Count == 5 && withNow == 5 &&
                credentials?.Password == "demo" && profiles.Count == 2;

            report.AppendLine(pass ? "E2E-RESULT=PASS" : "E2E-RESULT=FAIL (unexpected counts)");
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "E2E run failed");
            report.AppendLine($"E2E-RESULT=FAIL {ex}");
            return 1;
        }
        finally
        {
            File.WriteAllText(outFile, report.ToString());
        }
    }

    /// <summary>Second-run persistence check: profiles, credentials, and data must survive restart.</summary>
    public static async Task<int> VerifyAsync(IServiceProvider services, string outFile)
    {
        var report = new StringBuilder();
        try
        {
            var session = services.GetRequiredService<ISessionService>();
            var restored = await session.InitializeAsync(CancellationToken.None);
            report.AppendLine($"restored={restored} activeProfile={session.CurrentProfile?.Name}");

            var profiles = await services.GetRequiredService<IProfileRepository>().GetAllAsync(CancellationToken.None);
            var catalog = services.GetRequiredService<ICatalogRepository>();
            var epg = services.GetRequiredService<IEpgRepository>();

            var totalChannels = 0;
            long totalProgrammes = 0;
            foreach (var profile in profiles)
            {
                totalChannels += await catalog.CountChannelsAsync(profile.Id, CancellationToken.None);
                var counts = await epg.GetCountsAsync(profile.Id, CancellationToken.None);
                totalProgrammes += counts.Programmes;
            }

            report.AppendLine($"profiles={profiles.Count} channels={totalChannels} programmes={totalProgrammes}");

            var xtream = profiles.FirstOrDefault(p => p.Kind == ProfileKind.Xtream);
            var credentialsOk = xtream is not null && session.GetXtreamCredentials(xtream)?.Password == "demo";
            report.AppendLine($"credentials-roundtrip={credentialsOk}");

            var pass = restored && profiles.Count == 2 && totalChannels == 10 && totalProgrammes > 0 && credentialsOk;
            report.AppendLine(pass ? "VERIFY-RESULT=PASS" : "VERIFY-RESULT=FAIL");
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "E2E verify failed");
            report.AppendLine($"VERIFY-RESULT=FAIL {ex}");
            return 1;
        }
        finally
        {
            File.WriteAllText(outFile, report.ToString());
        }
    }
}
