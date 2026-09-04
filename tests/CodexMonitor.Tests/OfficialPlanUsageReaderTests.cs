using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexMonitor.Tests;

public sealed class OfficialPlanUsageReaderTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse(
            "2026-09-04T10:00:00Z",
            CultureInfo.InvariantCulture);

    [Fact]
    public void Read_ParsesBothWindowsAndUsesLowestRemainingPercent()
    {
        using var fixture = new UsageFixture();
        fixture.WriteRollout(
            "usage.jsonl",
            RateLimitEvent(
                "2026-09-04T09:59:00Z",
                "codex",
                "pro",
                Window(used: 12.5, minutes: 300, resetsInSeconds: 3600),
                Window(used: 47, minutes: 10080, resetsInSeconds: 86400)));
        fixture.InsertThread("thread-1", "usage.jsonl", updatedAt: 2);

        var snapshot = fixture.CreateReader().Read();

        Assert.NotNull(snapshot);
        Assert.Equal("codex", snapshot.LimitId);
        Assert.Equal("pro", snapshot.PlanType);
        Assert.Equal(87.5, snapshot.Primary!.RemainingPercent);
        Assert.Equal(300, snapshot.Primary.WindowMinutes);
        Assert.Equal(53, snapshot.Secondary!.RemainingPercent);
        Assert.Equal(10080, snapshot.Secondary.WindowMinutes);
        Assert.Equal(53, snapshot.RemainingPercent);
        Assert.Contains("剩余 53%", snapshot.Detail);
    }

    [Fact]
    public void Read_PrefersCodexBucketOverMoreRecentOtherBucket()
    {
        using var fixture = new UsageFixture();
        fixture.WriteRollout(
            "codex.jsonl",
            RateLimitEvent(
                "2026-09-04T09:55:00Z",
                "codex",
                "pro",
                Window(used: 20, minutes: 10080, resetsInSeconds: 86400)));
        fixture.WriteRollout(
            "other.jsonl",
            RateLimitEvent(
                "2026-09-04T09:59:00Z",
                "other-bucket",
                "pro",
                Window(used: 90, minutes: 60, resetsInSeconds: 3600)));
        fixture.InsertThread("codex-thread", "codex.jsonl", updatedAt: 1);
        fixture.InsertThread("other-thread", "other.jsonl", updatedAt: 2);

        var snapshot = fixture.CreateReader().Read();

        Assert.NotNull(snapshot);
        Assert.Equal("codex", snapshot.LimitId);
        Assert.Equal(80, snapshot.RemainingPercent);
    }

    [Fact]
    public void Read_PrefersOlderCodexBucketWithinTheSameRollout()
    {
        using var fixture = new UsageFixture();
        fixture.WriteRollout(
            "usage.jsonl",
            RateLimitEvent(
                "2026-09-04T09:55:00Z",
                "codex",
                "pro",
                Window(used: 20, minutes: 10080, resetsInSeconds: 86400)),
            RateLimitEvent(
                "2026-09-04T09:59:00Z",
                "other-bucket",
                "pro",
                Window(used: 90, minutes: 60, resetsInSeconds: 3600)));
        fixture.InsertThread("thread-1", "usage.jsonl", updatedAt: 1);

        var snapshot = fixture.CreateReader().Read();

        Assert.NotNull(snapshot);
        Assert.Equal("codex", snapshot.LimitId);
        Assert.Equal(80, snapshot.RemainingPercent);
    }

    [Fact]
    public void Read_UsesNewestValidFallbackWhenCodexBucketIsAbsent()
    {
        using var fixture = new UsageFixture();
        fixture.WriteRollout(
            "usage.jsonl",
            RateLimitEvent(
                "2026-09-04T09:50:00Z",
                "older-bucket",
                null,
                Window(used: 10, minutes: 60, resetsInSeconds: 3600)),
            RateLimitEvent(
                "2026-09-04T09:59:00Z",
                "newer-bucket",
                null,
                Window(used: 30, minutes: 60, resetsInSeconds: 3600)));
        fixture.InsertThread("thread-1", "usage.jsonl", updatedAt: 1);

        var snapshot = fixture.CreateReader().Read();

        Assert.NotNull(snapshot);
        Assert.Equal("newer-bucket", snapshot.LimitId);
        Assert.Equal(70, snapshot.RemainingPercent);
    }

    [Fact]
    public void Read_SkipsMalformedAndExpiredEntries()
    {
        using var fixture = new UsageFixture();
        fixture.WriteRollout(
            "usage.jsonl",
            RateLimitEvent(
                "2026-09-04T09:50:00Z",
                "codex",
                "plus",
                Window(used: 25, minutes: 300, resetsInSeconds: 3600)),
            RateLimitEvent(
                "2026-09-04T09:58:00Z",
                "codex",
                "plus",
                Window(used: 25, minutes: 300, resetsInSeconds: -1)),
            MalformedRateLimitEvent("2026-09-04T09:59:00Z"));
        fixture.InsertThread("thread-1", "usage.jsonl", updatedAt: 1);

        var snapshot = fixture.CreateReader().Read();

        Assert.NotNull(snapshot);
        Assert.Equal(75, snapshot.RemainingPercent);
        Assert.Equal(
            DateTimeOffset.Parse(
                "2026-09-04T09:50:00Z",
                CultureInfo.InvariantCulture),
            snapshot.ObservedAt);
    }

    [Fact]
    public void Read_DoesNotScanPastConfiguredTailLimit()
    {
        using var fixture = new UsageFixture();
        fixture.WriteRollout(
            "usage.jsonl",
            RateLimitEvent(
                "2026-09-04T09:50:00Z",
                "codex",
                "pro",
                Window(used: 10, minutes: 300, resetsInSeconds: 3600)),
            JsonSerializer.Serialize(new { padding = new string('x', 4096) }));
        fixture.InsertThread("thread-1", "usage.jsonl", updatedAt: 1);

        var snapshot = fixture.CreateReader(maxTailBytes: 512).Read();

        Assert.Null(snapshot);
    }

    private static Dictionary<string, object> Window(
        double used,
        int minutes,
        int resetsInSeconds) =>
        new()
        {
            ["used_percent"] = used,
            ["window_minutes"] = minutes,
            ["resets_at"] = Now.ToUnixTimeSeconds() + resetsInSeconds
        };

    private static string RateLimitEvent(
        string timestamp,
        string limitId,
        string? planType,
        Dictionary<string, object> primary,
        Dictionary<string, object>? secondary = null) =>
        JsonSerializer.Serialize(
            new
            {
                timestamp,
                type = "event_msg",
                payload = new
                {
                    type = "token_count",
                    rate_limits = new
                    {
                        limit_id = limitId,
                        plan_type = planType,
                        primary,
                        secondary
                    }
                }
            });

    private static string MalformedRateLimitEvent(string timestamp) =>
        JsonSerializer.Serialize(
            new
            {
                timestamp,
                type = "event_msg",
                payload = new
                {
                    type = "token_count",
                    rate_limits = new
                    {
                        limit_id = "codex",
                        primary = new
                        {
                            used_percent = 150,
                            window_minutes = 0,
                            resets_at = "not-a-timestamp"
                        }
                    }
                }
            });

    private sealed class UsageFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"CodexMonitor.OfficialUsage.Tests-{Guid.NewGuid():N}");

        private readonly SqliteConnection _database;

        public UsageFixture()
        {
            Directory.CreateDirectory(_root);
            _database = new SqliteConnection(
                $"Data Source={Path.Combine(_root, "state_5.sqlite")}");
            _database.Open();

            using var command = _database.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE threads (
                    id TEXT PRIMARY KEY,
                    rollout_path TEXT NOT NULL,
                    updated_at_ms INTEGER NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        public OfficialPlanUsageReader CreateReader(int maxTailBytes = 8192) =>
            new(_root, () => Now, maxTailBytes);

        public void WriteRollout(string fileName, params string[] lines)
        {
            File.WriteAllText(
                Path.Combine(_root, fileName),
                string.Join(Environment.NewLine, lines)
                    + Environment.NewLine);
        }

        public void InsertThread(
            string id,
            string rolloutFileName,
            long updatedAt)
        {
            using var command = _database.CreateCommand();
            command.CommandText =
                """
                INSERT INTO threads (id, rollout_path, updated_at_ms)
                VALUES ($id, $rolloutPath, $updatedAt);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue(
                "$rolloutPath",
                Path.Combine(_root, rolloutFileName));
            command.Parameters.AddWithValue("$updatedAt", updatedAt);
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            _database.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }
    }
}
