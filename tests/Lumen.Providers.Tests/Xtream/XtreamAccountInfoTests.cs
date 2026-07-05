using FluentAssertions;
using Lumen.Providers.Tests.Support;
using Lumen.Providers.Xtream;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Providers.Tests.Xtream;

/// <summary>
/// Auth-response → <see cref="Lumen.Core.Models.AccountInfo"/> mapping. Panels wire the numbers
/// as strings as often as numbers, so this goes through the real client parse, not a hand-built DTO.
/// </summary>
public sealed class XtreamAccountInfoTests
{
    private static XtreamClient CreateClient(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler), new XtreamCredentials("http://portal.example.com:8080", "u", "p"),
            NullLogger<XtreamClient>.Instance);

    [Fact]
    public async Task MapsEveryField_WithStringEncodedNumbers()
    {
        // exp_date / active_cons / max_connections / created_at / is_trial arrive as strings here.
        var handler = StubHttpMessageHandler.RespondingWithJson(
            """
            {
              "user_info": {
                "username": "u", "auth": 1, "status": "Active",
                "exp_date": "1795372800", "is_trial": "0",
                "active_cons": "1", "max_connections": "2",
                "created_at": "1600000000",
                "allowed_output_formats": ["ts", "m3u8", "mp4"]
              },
              "server_info": { "timezone": "Europe/London", "time_now": "2026-07-05 01:00:00" }
            }
            """);

        var auth = await CreateClient(handler).AuthenticateAsync(CancellationToken.None);
        var account = XtreamMapper.ToAccountInfo(auth);

        account.Status.Should().Be("Active");
        account.IsActive.Should().BeTrue();
        account.IsTrial.Should().BeFalse();
        account.ActiveConnections.Should().Be(1);
        account.MaxConnections.Should().Be(2);
        account.ConnectionsAvailable.Should().Be(1);
        account.AllConnectionsInUse.Should().BeFalse();
        account.ExpiresAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1795372800));
        account.CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1600000000));
        account.AllowedFormats.Should().Equal("ts", "m3u8", "mp4");
        account.ServerTimezone.Should().Be("Europe/London");
        account.ServerTimeNow.Should().Be("2026-07-05 01:00:00");
    }

    [Fact]
    public async Task FlagsAllConnectionsInUse_WhenActiveMeetsMax()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson(
            """{ "user_info": { "username": "u", "auth": 1, "status": "Active", "active_cons": 2, "max_connections": 2 } }""");

        var auth = await CreateClient(handler).AuthenticateAsync(CancellationToken.None);
        var account = XtreamMapper.ToAccountInfo(auth);

        account.AllConnectionsInUse.Should().BeTrue();
        account.ConnectionsAvailable.Should().Be(0);
    }

    [Fact]
    public async Task ToleratesAbsentFields()
    {
        // A bare panel: no expiry, no connection counts, no formats, no server_info.
        var handler = StubHttpMessageHandler.RespondingWithJson(
            """{ "user_info": { "username": "u", "auth": 1, "status": "Active" } }""");

        var auth = await CreateClient(handler).AuthenticateAsync(CancellationToken.None);
        var account = XtreamMapper.ToAccountInfo(auth);

        account.ExpiresAt.Should().BeNull("a missing exp_date means a lifetime account");
        account.IsTrial.Should().BeFalse();
        account.ConnectionsAvailable.Should().BeNull();
        account.AllConnectionsInUse.Should().BeFalse();
        account.AllowedFormats.Should().BeEmpty();
        account.ServerTimezone.Should().BeNull();
    }
}
