using System.Globalization;
using Condux.Core.CveScanning;
using Condux.Core.Events;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.ClickHouse;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Every stored and computed time is UTC whatever the ClickHouse server's own timezone is. The server
/// here runs at UTC+05:30, because a half-hour offset is what shows a bucket cut at the server's hour as
/// well as an instant shifted by its offset. The time columns declare no zone, so ClickHouse reads a
/// timezone-less time string in the server's zone and cuts hours and days there, and a time built from a
/// number or now() is in the server's zone too: the writers therefore send unix numbers, and every query
/// names 'UTC'. Every test after the first fails on this server when the writers send UTC as text, which
/// they used to; the first only proves the server is not UTC.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ClickHouseTimezoneTest(ClickHouseTimezoneTest.OffsetServer ch)
    : IClassFixture<ClickHouseTimezoneTest.OffsetServer>
{
    /// <summary>A ClickHouse whose own timezone is UTC+05:30.</summary>
    public sealed class OffsetServer() : ClickHouseFixture(ServerTimezone);

    private const string ServerTimezone = "Asia/Kolkata";

    // 2026-10-03 00:00:00 UTC, which is 05:30 on the server's clock.
    private static readonly DateTimeOffset Instant = DateTimeOffset.FromUnixTimeSeconds(1790985600);

    /// <summary>Without this the rest would pass against a UTC server and prove nothing.</summary>
    [Fact]
    public async Task The_server_runs_outside_UTC() =>
        Assert.Equal(ServerTimezone, (await QueryAsync("SELECT timezone()")).Trim());

    [Fact]
    public async Task An_event_keeps_its_instant_and_reads_back_as_UTC()
    {
        using var http = Client();
        var e = new Event { EventId = "tz1", Level = Level.Error, TimestampUnixMs = Instant.ToUnixTimeMilliseconds() + 123 };
        await new ClickHouseEventWriter(http).InsertAsync([ClickHouseEventWriter.ToRow("tz-events", 1UL, e, "fp", 90)]);

        Assert.Equal(
            (Instant.ToUnixTimeMilliseconds() + 123).ToString(CultureInfo.InvariantCulture),
            (await QueryAsync("SELECT toUnixTimestamp64Milli(timestamp) FROM condux.events WHERE project_id = 'tz-events'")).Trim());
        // The issue page shows this text as it comes, so it must be the UTC wall clock.
        var stored = Assert.Single(await new ClickHouseEventReader(http).RecentByIssueAsync("tz-events", 1UL));
        Assert.Equal("2026-10-03 00:00:00.123", stored.Timestamp);
    }

    /// <summary>
    /// Fix verification counts only the full hours after a merge. On this server an hour cut in its own
    /// zone falls on the half hour, which for a merge at 10:40 skipped the whole 11:00 bucket, so an
    /// occurrence there went uncounted and the fix could read as held.
    /// </summary>
    [Fact]
    public async Task Fix_verification_counts_the_first_full_hour_after_a_merge()
    {
        using var http = Client();
        var merge = Instant.AddHours(10).AddMinutes(40);
        await new ClickHouseIssueStatsWriter(http).InsertAsync(
        [
            ClickHouseIssueStatsWriter.ToRow("tz-stats", 1UL, merge.AddMinutes(10)),
            ClickHouseIssueStatsWriter.ToRow("tz-stats", 1UL, merge.AddMinutes(30)),
        ]);

        // The 10:50 row shares the merge's hour, so only the 11:10 one counts.
        Assert.Equal(1, await new ClickHouseIssueStatsReader(http).CountSinceAsync("tz-stats", 1UL, merge));
    }

    [Fact]
    public async Task Charts_bucket_on_UTC_hours_and_UTC_midnights()
    {
        using var http = Client();
        var seen = DateTimeOffset.UtcNow.AddHours(-3);
        await new ClickHouseIssueStatsWriter(http).InsertAsync([ClickHouseIssueStatsWriter.ToRow("tz-chart", 1UL, seen)]);
        var reader = new ClickHouseIssueStatsReader(http);

        var hourly = await reader.BucketsAsync("tz-chart", 1UL, 24, daily: false);
        var daily = await reader.BucketsAsync("tz-chart", 1UL, 168, daily: true);

        var seconds = seen.ToUnixTimeSeconds();
        Assert.Equal(seconds - (seconds % 3600), Assert.Single(hourly).Key);
        Assert.Equal(seconds - (seconds % 86400), Assert.Single(daily).Key);
    }

    /// <summary>A CVE bump's verification reads sightings after the merge, to the second.</summary>
    [Fact]
    public async Task A_sighting_keeps_its_instant()
    {
        using var http = Client();
        await new ClickHouseReleaseModuleWriter(http).InsertAsync(
        [
            ClickHouseReleaseModuleWriter.ToRow("tz-modules", "1.0.0", "production", new ReleaseModule("npm", "lodash", "4.17.21"), Instant, 90),
        ]);
        var reader = new ClickHouseReleaseModuleReader(http);

        var (justBefore, _) = await reader.ReadSightingsSinceAsync("tz-modules", "lodash", Instant.AddSeconds(-1));
        var (atIt, _) = await reader.ReadSightingsSinceAsync("tz-modules", "lodash", Instant);

        Assert.Equal(Instant, Assert.Single(justBefore).LastSeen);
        Assert.Empty(atIt);
    }

    private HttpClient Client()
    {
        var http = new HttpClient();
        ClickHouseRegistration.Configure(http, ch.BaseUrl, ch.ChUser, ch.ChPassword);
        return http;
    }

    private async Task<string> QueryAsync(string sql)
    {
        using var http = Client();
        using var resp = await http.PostAsync("/", new StringContent(sql));
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }
}
