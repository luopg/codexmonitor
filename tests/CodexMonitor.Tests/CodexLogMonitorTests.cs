using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexMonitor.Tests;

public sealed class CodexLogMonitorTests
{
    [Fact]
    public void TerminalEvent_IgnoresTrailingResponseActivity()
    {
        using var fixture = new MonitorFixture();
        fixture.WriteNewRollout("first.jsonl", UserMessage());
        fixture.InsertThread("thread-1", "first.jsonl");

        using var monitor = fixture.CreateMonitor();
        Assert.Single(monitor.ReadSnapshot().ActiveTasks);

        fixture.AppendRollout(
            "first.jsonl",
            TaskComplete(),
            ResponseItem("agent_message"));

        var snapshot = monitor.ReadSnapshot();

        Assert.Empty(snapshot.ActiveTasks);
        Assert.Equal(1, snapshot.CompletedEvents);
    }

    [Fact]
    public void InitialScan_TaskStartedAfterTerminalBeginsANewTurn()
    {
        using var fixture = new MonitorFixture();
        fixture.WriteNewRollout(
            "first.jsonl",
            UserMessage(),
            TaskComplete(),
            ResponseItem("agent_message"),
            TaskStarted(
                startedAt: "2026-09-04T11:22:33Z",
                rootTimestamp: "2026-09-04T11:22:30Z"),
            ResponseItem("reasoning"));
        fixture.InsertThread("thread-1", "first.jsonl");

        using var monitor = fixture.CreateMonitor();

        var snapshot = monitor.ReadSnapshot();

        var task = Assert.Single(snapshot.ActiveTasks);
        Assert.Equal(
            LocalDateTime("2026-09-04T11:22:33Z"),
            task.StartedAt);
        Assert.Equal(0, snapshot.CompletedEvents);
    }

    [Fact]
    public void IncrementalScan_TaskStartedWithoutUserMessageBeginsANewTurn()
    {
        using var fixture = new MonitorFixture();
        fixture.WriteNewRollout("first.jsonl", UserMessage());
        fixture.InsertThread("thread-1", "first.jsonl");

        using var monitor = fixture.CreateMonitor();
        Assert.Single(monitor.ReadSnapshot().ActiveTasks);

        fixture.AppendRollout(
            "first.jsonl",
            TaskComplete(),
            ResponseItem("agent_message"));
        Assert.Empty(monitor.ReadSnapshot().ActiveTasks);

        fixture.AppendRollout(
            "first.jsonl",
            TaskStarted(
                startedAt: null,
                rootTimestamp: "2026-09-04T12:34:56Z"),
            ResponseItem("reasoning"));

        var snapshot = monitor.ReadSnapshot();

        var task = Assert.Single(snapshot.ActiveTasks);
        Assert.Equal(
            LocalDateTime("2026-09-04T12:34:56Z"),
            task.StartedAt);
    }

    [Fact]
    public void UserMessage_AfterTerminalEvent_StartsANewTurn()
    {
        using var fixture = new MonitorFixture();
        fixture.WriteNewRollout("first.jsonl", UserMessage());
        fixture.InsertThread("thread-1", "first.jsonl");

        using var monitor = fixture.CreateMonitor();
        Assert.Single(monitor.ReadSnapshot().ActiveTasks);

        fixture.AppendRollout(
            "first.jsonl",
            TaskComplete(),
            ResponseItem("agent_message"));
        Assert.Empty(monitor.ReadSnapshot().ActiveTasks);

        fixture.AppendRollout(
            "first.jsonl",
            UserMessage("2026-09-04T11:22:33Z"),
            ResponseItem("reasoning"));

        var snapshot = monitor.ReadSnapshot();

        var task = Assert.Single(snapshot.ActiveTasks);
        Assert.Equal(
            LocalDateTime("2026-09-04T11:22:33Z"),
            task.StartedAt);
    }

    [Fact]
    public void RolloutPathChange_ResetsOffsetAndScansNewFileFromTheEnd()
    {
        using var fixture = new MonitorFixture();
        fixture.WriteNewRollout(
            "old.jsonl",
            UserMessage(),
            ResponseItem("reasoning"),
            ResponseItem("function_call"));
        fixture.WriteNewRollout(
            "new.jsonl",
            ResponseItem("agent_message"),
            TaskComplete(),
            ResponseItem("agent_message"),
            ResponseItem("custom_tool_call_output"));
        fixture.InsertThread("thread-1", "old.jsonl");

        using var monitor = fixture.CreateMonitor();
        Assert.Single(monitor.ReadSnapshot().ActiveTasks);

        fixture.ChangeRolloutPath("thread-1", "new.jsonl");

        var snapshot = monitor.ReadSnapshot();

        Assert.Empty(snapshot.ActiveTasks);
        Assert.Equal(0, snapshot.CompletedEvents);
    }

    [Fact]
    public void IncrementalRecord_CanSpanBlocksAndPollingCycles()
    {
        using var fixture = new MonitorFixture();
        fixture.WriteNewRollout("first.jsonl", UserMessage());
        fixture.InsertThread("thread-1", "first.jsonl");

        using var monitor = fixture.CreateMonitor();
        Assert.Single(monitor.ReadSnapshot().ActiveTasks);

        var terminal = JsonSerializer.Serialize(
            new
            {
                timestamp = "2026-09-04T10:01:00Z",
                padding = new string('x', 96 * 1024),
                payload = new { type = "task_complete" }
            });
        var splitAt = terminal.Length / 2;
        fixture.AppendRaw("first.jsonl", terminal[..splitAt]);

        Assert.Single(monitor.ReadSnapshot().ActiveTasks);

        fixture.AppendRaw(
            "first.jsonl",
            terminal[splitAt..]
            + Environment.NewLine
            + ResponseItem("agent_message")
            + Environment.NewLine);

        var snapshot = monitor.ReadSnapshot();

        Assert.Empty(snapshot.ActiveTasks);
        Assert.Equal(1, snapshot.CompletedEvents);
    }

    private static string UserMessage(
        string timestamp = "2026-09-04T10:00:00Z") =>
        JsonSerializer.Serialize(
            new
            {
                timestamp,
                payload = new { type = "user_message" }
            });

    private static string TaskComplete() =>
        JsonSerializer.Serialize(
            new
            {
                timestamp = "2026-09-04T10:01:00Z",
                payload = new { type = "task_complete" }
            });

    private static string TaskStarted(
        string? startedAt,
        string rootTimestamp)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "task_started"
        };
        if (startedAt is not null)
        {
            payload["started_at"] = startedAt;
        }

        return JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["timestamp"] = rootTimestamp,
                ["type"] = "event_msg",
                ["payload"] = payload
            });
    }

    private static string ResponseItem(string payloadType) =>
        JsonSerializer.Serialize(
            new
            {
                timestamp = "2026-09-04T10:01:01Z",
                type = "response_item",
                payload = new { type = payloadType }
            });

    private static DateTime LocalDateTime(string timestamp) =>
        DateTimeOffset.Parse(
            timestamp,
            CultureInfo.InvariantCulture).LocalDateTime;

    private sealed class MonitorFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"CodexMonitor.Tests-{Guid.NewGuid():N}");

        private readonly SqliteConnection _database;

        public MonitorFixture()
        {
            Directory.CreateDirectory(_root);
            var statePath = Path.Combine(_root, "state_5.sqlite");
            _database = new SqliteConnection($"Data Source={statePath}");
            _database.Open();

            using var command = _database.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE threads (
                    id TEXT PRIMARY KEY,
                    name TEXT,
                    title TEXT,
                    preview TEXT,
                    cwd TEXT NOT NULL,
                    rollout_path TEXT NOT NULL,
                    updated_at_ms INTEGER NOT NULL,
                    recency_at_ms INTEGER NOT NULL,
                    archived INTEGER NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        public CodexLogMonitor CreateMonitor() => new(_root);

        public void WriteNewRollout(string fileName, params string[] lines)
        {
            File.WriteAllText(RolloutPath(fileName), JoinLines(lines));
        }

        public void AppendRollout(string fileName, params string[] lines)
        {
            File.AppendAllText(RolloutPath(fileName), JoinLines(lines));
        }

        public void AppendRaw(string fileName, string contents)
        {
            File.AppendAllText(RolloutPath(fileName), contents);
        }

        public void InsertThread(string id, string rolloutFileName)
        {
            using var command = _database.CreateCommand();
            command.CommandText =
                """
                INSERT INTO threads (
                    id, name, title, preview, cwd, rollout_path,
                    updated_at_ms, recency_at_ms, archived)
                VALUES (
                    $id, 'Test task', '', '', $cwd, $rolloutPath,
                    $now, $now, 0);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$cwd", _root);
            command.Parameters.AddWithValue(
                "$rolloutPath",
                RolloutPath(rolloutFileName));
            command.Parameters.AddWithValue(
                "$now",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }

        public void ChangeRolloutPath(string id, string rolloutFileName)
        {
            using var command = _database.CreateCommand();
            command.CommandText =
                """
                UPDATE threads
                   SET rollout_path = $rolloutPath,
                       updated_at_ms = $now,
                       recency_at_ms = $now
                 WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue(
                "$rolloutPath",
                RolloutPath(rolloutFileName));
            command.Parameters.AddWithValue(
                "$now",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            _database.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }

        private string RolloutPath(string fileName) =>
            Path.Combine(_root, fileName);

        private static string JoinLines(IEnumerable<string> lines) =>
            string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
