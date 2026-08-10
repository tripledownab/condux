using System.Globalization;
using System.Text.Json;
using Condux.Core.Auth;
using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Grouping;
using Condux.Core.Projects;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Npgsql;

namespace Condux.DemoSeed;

internal sealed record AccountInfo(long OrgId, long ProjectId, Guid ProjectPublicId, string DsnKey);

/// <summary>
/// Seeds a running dev stack's databases with the demo account and a realistic checkout dataset (issues,
/// hourly stats, sampled events, Conductor fixes). Reuses the real repositories + ClickHouse writers, so the
/// data matches exactly what the pipeline would produce. A fixed RNG seed makes the result reproducible.
/// Targets a FRESH database: it bails if the demo user already exists rather than double-seeding.
/// </summary>
internal sealed class DemoSeeder(string postgres, HttpClient clickHouse)
{
    private const string Email = "demo@condux.dev";
    private const string Password = "condux-demo-2026";
    private const string OrgSlug = "demo";
    private const string OrgName = "Demo";
    private const int TeamTier = 2;
    private const string ProjectName = "Checkout API";
    private const int RetentionDays = 90;

    private readonly Random _random = new(20260803); // fixed seed -> reproducible volumes
    private readonly IssueRepository _issues = new(postgres);
    private readonly PostgresFixStore _fixes = new(postgres);
    private readonly PostgresFixVerification _verification = new(postgres);
    private readonly ClickHouseIssueStatsWriter _stats = new(clickHouse);
    private readonly ClickHouseEventWriter _events = new(clickHouse);

    public async Task RunAsync()
    {
        var account = await EnsureAccountAsync();
        if (account is null)
        {
            Console.WriteLine($"Demo user {Email} already exists — the database is not fresh, so nothing was seeded.");
            Console.WriteLine("Reseed against a fresh stack (a new worktree, or drop the compose volumes first).");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var issueIds = new Dictionary<string, long>();
        foreach (var spec in DemoCatalog.Issues)
        {
            issueIds[spec.Fingerprint] = await SeedIssueAsync(account.ProjectId, spec, now);
            Console.WriteLine($"  issue: {spec.Title}");
        }

        await SeedFixesAsync(account.ProjectId, now, issueIds);
        PrintSummary(account);
    }

    private async Task<AccountInfo?> EnsureAccountAsync()
    {
        var users = new UserRepository(postgres);
        if (await users.GetByEmailAsync(Email) is not null)
        {
            return null;
        }

        var user = await users.CreateAsync(Email, PasswordHasher.Hash(Password));
        await users.MarkOnboardedAsync(user.Id);
        var org = await new OrgRepository(postgres).CreateAsync(OrgSlug, OrgName, TeamTier);
        await new OrgMemberRepository(postgres).AddAsync(org.Id, user.Id, OrgRole.Owner);
        var project = await new ProjectRepository(postgres).CreateAsync(org.Id, ProjectName, "javascript");
        var key = await new DsnKeyRepository(postgres).CreateAsync(project.Id, "default", DsnKeyGenerator.NewPublicKey());
        return new AccountInfo(org.Id, project.Id, project.PublicId, key.PublicKey);
    }

    private async Task<long> SeedIssueAsync(long projectId, IssueSpec spec, DateTimeOffset now)
    {
        var project = projectId.ToString(CultureInfo.InvariantCulture);
        var grouping = new Grouping(spec.Fingerprint, spec.Title, spec.Culprit);
        var upsert = await _issues.UpsertAsync(projectId, grouping, spec.Level, now.AddDays(-spec.FirstSeenDaysAgo), spec.Release);
        var issueId = (ulong)upsert.Id;

        await _stats.InsertAsync(BuildStatRows(project, issueId, spec, now));
        var sampleSize = Math.Clamp(spec.WeekEvents / 8 + 4, 4, 12);
        await _events.InsertAsync(BuildEventRows(project, issueId, spec, now, sampleSize));
        await SetEventCountAsync(upsert.Id, spec.WeekEvents + spec.PrevWeekEvents, now.AddHours(-1 - _random.Next(60)));

        if (spec.Status == SeedStatus.Resolved)
        {
            await _issues.UpdateStatusAsync(projectId, upsert.PublicId, 2);
        }
        else if (spec.Status == SeedStatus.Regressed)
        {
            await _issues.UpdateStatusAsync(projectId, upsert.PublicId, 2);
            // Reopen with a recent event so activated_at lands in this week (a regression the digest counts).
            await _issues.UpsertAsync(projectId, grouping, spec.Level, now.AddDays(-1), spec.Release);
        }

        return upsert.Id;
    }

    private async Task SeedFixesAsync(long projectId, DateTimeOffset now, IReadOnlyDictionary<string, long> issueIds)
    {
        await new RepoLinkRepository(postgres).LinkAsync(projectId, DemoCatalog.RepoFullName, "main");
        var prNumber = 100;
        foreach (var spec in DemoCatalog.Fixes)
        {
            if (issueIds.TryGetValue(spec.IssueFingerprint, out var issueId))
            {
                await SeedFixAsync(issueId, spec, now, ++prNumber);
            }
        }
    }

    private async Task SeedFixAsync(long issueId, FixSpec spec, DateTimeOffset now, int prNumber)
    {
        var id = Guid.NewGuid();
        var branch = $"condux/fix-{spec.IssueFingerprint}";
        var prUrl = $"https://github.com/{DemoCatalog.RepoFullName}/pull/{prNumber}";
        var created = now.AddDays(-spec.CreatedDaysAgo);

        var fix = new FixSuggestion(
            id, issueId, DemoCatalog.RepoFullName, FixStatus.Succeeded, "anthropic", spec.Model, branch, prUrl,
            spec.Summary, created, created);
        await _fixes.InsertAsync(fix);
        // InsertAsync does not persist token usage; UpdateAsync does (drives the priced spend).
        await _fixes.UpdateAsync(fix with { InputTokens = spec.InputTokens, OutputTokens = spec.OutputTokens });
        await _fixes.AppendAuditAsync(id, "demo@condux.dev", "requested", "{}");
        await _fixes.AppendAuditAsync(id, "conductor", "draft_pr_opened",
            JsonSerializer.Serialize(new { inputTokens = spec.InputTokens, outputTokens = spec.OutputTokens, prUrl }));

        if (spec.MergedDaysAgo is { } mergedDaysAgo)
        {
            await _verification.MarkMergedAsync(DemoCatalog.RepoFullName, branch, now.AddDays(-mergedDaysAgo));
        }
        if (spec.VerifiedDaysAgo is { } verifiedDaysAgo)
        {
            await _verification.SetVerifyStatusAsync(id, VerifyStatus.Held, now.AddDays(-verifiedDaysAgo));
        }
    }

    // Spread this-week and prior-week volume across a handful of hourly buckets (aggregated counts, not one
    // row per event) so the charts and the weekly-summary week-over-week split look realistic.
    private List<IssueStatsRow> BuildStatRows(string project, ulong issueId, IssueSpec spec, DateTimeOffset now)
    {
        var rows = new List<IssueStatsRow>();
        Spread(rows, project, issueId, spec.WeekEvents, now.AddDays(-7), now);
        Spread(rows, project, issueId, spec.PrevWeekEvents, now.AddDays(-14), now.AddDays(-7));
        return rows;
    }

    private void Spread(
        List<IssueStatsRow> rows, string project, ulong issueId, int total, DateTimeOffset from, DateTimeOffset to)
    {
        if (total <= 0)
        {
            return;
        }

        var windowHours = (int)(to - from).TotalHours;
        var buckets = Math.Clamp(total / 6 + 2, 3, 18);
        var remaining = total;
        for (var i = 0; i < buckets && remaining > 0; i++)
        {
            var at = from.AddHours(_random.Next(windowHours));
            var count = i == buckets - 1 ? remaining : Math.Min(remaining, Math.Max(1, remaining / (buckets - i) + _random.Next(4)));
            remaining -= count;
            rows.Add(StatRow(project, issueId, at, (ulong)count));
        }
    }

    private static IssueStatsRow StatRow(string project, ulong issueId, DateTimeOffset at, ulong count)
    {
        var utc = at.UtcDateTime;
        var bucket = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var seen = utc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        return new IssueStatsRow(project, issueId, bucket, count, seen, seen);
    }

    private List<EventRow> BuildEventRows(string project, ulong issueId, IssueSpec spec, DateTimeOffset now, int count)
    {
        var rows = new List<EventRow>(count);
        for (var i = 0; i < count; i++)
        {
            var at = now.AddDays(-_random.Next(6)).AddHours(-_random.Next(24));
            var e = BuildEvent(spec, at, $"u-{spec.Fingerprint}-{i}");
            rows.Add(ClickHouseEventWriter.ToRow(project, issueId, e, spec.Fingerprint, RetentionDays));
        }
        return rows;
    }

    private static Event BuildEvent(IssueSpec spec, DateTimeOffset at, string userKey) => new()
    {
        EventId = Guid.NewGuid().ToString("N"),
        TimestampUnixMs = at.ToUnixTimeMilliseconds(),
        Platform = spec.Platform,
        Level = spec.Level,
        Message = spec.ExceptionMessage,
        Environment = "production",
        Release = spec.Release,
        UserKey = userKey,
        Exceptions =
        [
            new ExceptionValue
            {
                Type = spec.ExceptionType,
                Value = spec.ExceptionMessage,
                Handled = spec.Level < Level.Error, // Error/Fatal seed as unhandled crashes
                Stacktrace = new Stacktrace { Frames = [.. spec.Frames.Select(ParseFrame)] },
            },
        ],
        Tags = new Dictionary<string, string> { ["environment"] = "production", ["release"] = spec.Release },
    };

    // "function@src/file.ts:42" -> a Frame. node_modules frames are out-of-app.
    private static Frame ParseFrame(string frame)
    {
        var at = frame.IndexOf('@', StringComparison.Ordinal);
        var function = frame[..at];
        var location = frame[(at + 1)..];
        var colon = location.LastIndexOf(':');
        var file = location[..colon];
        var line = int.Parse(location[(colon + 1)..], CultureInfo.InvariantCulture);
        return new Frame
        {
            Function = function,
            Filename = file,
            AbsPath = file,
            Lineno = line,
            InApp = !file.Contains("node_modules", StringComparison.Ordinal),
        };
    }

    // The grouped-issue event_count is denormalized (the consumer increments it per event); set it directly to
    // the seeded total so the issue list shows realistic counts without replaying every event.
    private async Task SetEventCountAsync(long issueId, long eventCount, DateTimeOffset lastSeen)
    {
        await using var conn = new NpgsqlConnection(postgres);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE issues SET event_count = @count, last_seen = @last WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("count", eventCount);
        cmd.Parameters.AddWithValue("last", lastSeen);
        cmd.Parameters.AddWithValue("id", issueId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void PrintSummary(AccountInfo account)
    {
        var scheme = Environment.GetEnvironmentVariable("CONDUX_INGEST_SCHEME") ?? "http";
        var host = Environment.GetEnvironmentVariable("CONDUX_INGEST_HOST") ?? "localhost:9010";
        Console.WriteLine();
        Console.WriteLine("Done. Demo data seeded:");
        Console.WriteLine($"  Login:    {Email} / {Password}   (dashboard: http://localhost:3000)");
        Console.WriteLine($"  Org:      {OrgName} (Team)    Project: {ProjectName}");
        Console.WriteLine($"  Project:  {account.ProjectPublicId}");
        Console.WriteLine($"  DSN:      {scheme}://{account.DsnKey}@{host}/{account.ProjectPublicId}");
        Console.WriteLine($"  Seeded:   {DemoCatalog.Issues.Count} issues, {DemoCatalog.Fixes.Count} Conductor fixes");
    }
}
