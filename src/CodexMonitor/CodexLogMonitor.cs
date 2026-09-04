using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexMonitor;

internal sealed class CodexLogMonitor : IDisposable
{
    private const int ReadBlockBytes = 64 * 1024;
    private const int MaxInitialScanBytes = 256 * 1024 * 1024;
    private const int MaxJsonLineBytes = 8 * 1024 * 1024;

    private static readonly TimeSpan ActiveFreshness = TimeSpan.FromMinutes(30);

    private sealed class RolloutTracker
    {
        public RolloutTracker(string path)
        {
            Path = path;
        }

        public string Path { get; set; }

        public long Offset { get; set; }

        public bool IsActive { get; set; }

        public bool HasKnownState { get; set; }

        public DateTime StartedAt { get; set; } = DateTime.Now;

        // A terminal event is a hard boundary. Response items sometimes arrive after
        // task_complete/turn_aborted and must not reactivate the task. Only a new
        // user_message begins the next turn.
        public bool WaitingForUserStart { get; set; }

        // Incremental reads can stop in the middle of a JSONL record. Keep only the
        // unfinished record and resume it on the next poll.
        public List<byte> PendingLine { get; private set; } = new(4096);

        public bool DiscardingOversizedLine { get; set; }

        public void ClearPendingLine()
        {
            PendingLine = PendingLine.Capacity > ReadBlockBytes * 4
                ? new List<byte>(4096)
                : PendingLine;
            PendingLine.Clear();
        }

        public void Reset(string path)
        {
            Path = path;
            Offset = 0;
            IsActive = false;
            HasKnownState = false;
            StartedAt = DateTime.Now;
            WaitingForUserStart = false;
            DiscardingOversizedLine = false;
            ClearPendingLine();
        }
    }

    private enum RolloutSignal
    {
        None,
        Activity,
        UserStart,
        Terminal
    }

    private readonly string _codexHome;

    private readonly Dictionary<string, RolloutTracker> _trackers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, ActiveProject> _metadata =
        new(StringComparer.OrdinalIgnoreCase);

    private SqliteConnection? _state;
    private int _completedEvents;

    public CodexLogMonitor()
        : this(
            Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex"))
    {
    }

    internal CodexLogMonitor(string codexHome)
    {
        _codexHome = codexHome;
    }

    public MonitorSnapshot ReadSnapshot()
    {
        var statePath = Path.Combine(_codexHome, "state_5.sqlite");
        if (!File.Exists(statePath))
        {
            return new MonitorSnapshot(
                Array.Empty<ActiveProject>(),
                0,
                $"未找到 {statePath}",
                DateTime.Now);
        }

        EnsureConnection(statePath);
        _completedEvents = 0;
        RefreshThreads();

        var activeTasks = _trackers
            .Where(item => item.Value.IsActive)
            .Select(item =>
                _metadata.TryGetValue(item.Key, out var project)
                    ? project with { StartedAt = item.Value.StartedAt }
                    : new ActiveProject(
                        item.Key,
                        "Codex 任务",
                        "未知项目",
                        item.Value.StartedAt))
            .OrderBy(project => project.StartedAt)
            .ToArray();

        var projectCount = activeTasks
            .Select(project => project.Cwd)
            .Where(cwd => !string.IsNullOrWhiteSpace(cwd))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return new MonitorSnapshot(
            activeTasks,
            projectCount,
            null,
            DateTime.Now,
            _completedEvents);
    }

    private void EnsureConnection(string statePath)
    {
        if (_state is not null)
        {
            return;
        }

        _state = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = statePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = 2
            }.ToString());
        _state.Open();
    }

    private void RefreshThreads()
    {
        using var command = _state!.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   COALESCE(NULLIF(name,''), NULLIF(title,''), NULLIF(preview,''), 'Codex 任务'),
                   cwd, rollout_path, updated_at_ms
              FROM threads
             WHERE archived = 0 AND recency_at_ms >= $cutoff
             ORDER BY recency_at_ms DESC, updated_at_ms DESC
             LIMIT 200
            """;
        command.Parameters.AddWithValue(
            "$cutoff",
            DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds());

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var title = reader.GetString(1);
                var cwd = NormalizePath(reader.GetString(2));
                var rolloutPath = reader.GetString(3);
                var databaseUpdatedUtc =
                    reader.IsDBNull(4) || reader.GetInt64(4) <= 0
                        ? DateTime.MinValue
                        : DateTimeOffset
                            .FromUnixTimeMilliseconds(reader.GetInt64(4))
                            .UtcDateTime;

                seen.Add(id);
                _metadata[id] = new ActiveProject(id, title, cwd, DateTime.Now);

                if (!_trackers.TryGetValue(id, out var tracker))
                {
                    tracker = new RolloutTracker(rolloutPath);
                    _trackers[id] = tracker;
                    ReadRollout(tracker, initial: true);
                    ApplyRecentDatabaseFallback(tracker, databaseUpdatedUtc);
                }
                else if (!string.Equals(
                             tracker.Path,
                             rolloutPath,
                             StringComparison.OrdinalIgnoreCase))
                {
                    // A thread can be assigned a new rollout file for a later turn.
                    // Offsets and state from the old file are invalid for the new path.
                    tracker.Reset(rolloutPath);
                    ReadRollout(tracker, initial: true);
                    ApplyRecentDatabaseFallback(tracker, databaseUpdatedUtc);
                }
                else
                {
                    ReadRollout(tracker, initial: false);
                }

                if (tracker.IsActive
                    && !IsRecentlyUpdated(rolloutPath, databaseUpdatedUtc))
                {
                    tracker.IsActive = false;
                }
            }
        }

        foreach (var id in _trackers.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            _trackers.Remove(id);
            _metadata.Remove(id);
        }
    }

    private static void ApplyRecentDatabaseFallback(
        RolloutTracker tracker,
        DateTime databaseUpdatedUtc)
    {
        if (!tracker.HasKnownState
            && DateTime.UtcNow - databaseUpdatedUtc <= TimeSpan.FromMinutes(2))
        {
            tracker.IsActive = true;
        }
    }

    private static bool IsRecentlyUpdated(
        string path,
        DateTime databaseUpdatedUtc)
    {
        try
        {
            var fileUpdatedUtc = File.Exists(path)
                ? File.GetLastWriteTimeUtc(path)
                : DateTime.MinValue;
            var lastUpdatedUtc = fileUpdatedUtc > databaseUpdatedUtc
                ? fileUpdatedUtc
                : databaseUpdatedUtc;
            return DateTime.UtcNow - lastUpdatedUtc <= ActiveFreshness;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void ReadRollout(RolloutTracker tracker, bool initial)
    {
        if (string.IsNullOrWhiteSpace(tracker.Path)
            || !File.Exists(tracker.Path))
        {
            return;
        }

        try
        {
            using var stream = new FileStream(
                tracker.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                ReadBlockBytes,
                FileOptions.SequentialScan);

            var length = stream.Length;
            if (tracker.Offset > length)
            {
                tracker.Reset(tracker.Path);
                initial = true;
            }

            if (initial)
            {
                tracker.Offset = length;
                tracker.ClearPendingLine();
                tracker.DiscardingOversizedLine = false;
                ReadLatestStateFromEnd(stream, tracker, length);
                return;
            }

            if (tracker.Offset >= length)
            {
                return;
            }

            ReadIncremental(stream, tracker, length);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ReadIncremental(
        FileStream stream,
        RolloutTracker tracker,
        long length)
    {
        var buffer = new byte[ReadBlockBytes];
        stream.Position = tracker.Offset;

        while (tracker.Offset < length)
        {
            var requested = (int)Math.Min(buffer.Length, length - tracker.Offset);
            var bytesRead = stream.Read(buffer, 0, requested);
            if (bytesRead == 0)
            {
                break;
            }

            ProcessIncrementalBlock(tracker, buffer.AsSpan(0, bytesRead));
            tracker.Offset += bytesRead;
        }
    }

    private void ProcessIncrementalBlock(
        RolloutTracker tracker,
        ReadOnlySpan<byte> block)
    {
        foreach (var value in block)
        {
            if (value == (byte)'\n')
            {
                if (!tracker.DiscardingOversizedLine
                    && tracker.PendingLine.Count > 0)
                {
                    var line = Encoding.UTF8.GetString(
                        tracker.PendingLine.ToArray());
                    ApplyRolloutLine(tracker, line, initial: false);
                }

                tracker.ClearPendingLine();
                tracker.DiscardingOversizedLine = false;
                continue;
            }

            if (value == (byte)'\r' || tracker.DiscardingOversizedLine)
            {
                continue;
            }

            if (tracker.PendingLine.Count < MaxJsonLineBytes)
            {
                tracker.PendingLine.Add(value);
            }
            else
            {
                tracker.ClearPendingLine();
                tracker.DiscardingOversizedLine = true;
            }
        }
    }

    private void ReadLatestStateFromEnd(
        FileStream stream,
        RolloutTracker tracker,
        long length)
    {
        var block = new byte[ReadBlockBytes];
        var reversedLine = new List<byte>(4096);
        var position = length;
        var lowerBound = Math.Max(0, length - MaxInitialScanBytes);
        var discardingOversizedLine = false;

        while (position > lowerBound)
        {
            var blockStart = Math.Max(lowerBound, position - block.Length);
            var blockLength = checked((int)(position - blockStart));
            stream.Position = blockStart;

            var bytesRead = 0;
            while (bytesRead < blockLength)
            {
                var count = stream.Read(
                    block,
                    bytesRead,
                    blockLength - bytesRead);
                if (count == 0)
                {
                    break;
                }

                bytesRead += count;
            }

            for (var index = bytesRead - 1; index >= 0; index--)
            {
                var value = block[index];
                if (value == (byte)'\n')
                {
                    if (!discardingOversizedLine)
                    {
                        var signal = TryApplyReversedLine(
                            tracker,
                            reversedLine);
                        if (signal is RolloutSignal.Terminal
                            or RolloutSignal.UserStart)
                        {
                            return;
                        }
                    }

                    reversedLine.Clear();
                    discardingOversizedLine = false;
                }
                else if (value != (byte)'\r' && !discardingOversizedLine)
                {
                    if (reversedLine.Count < MaxJsonLineBytes)
                    {
                        reversedLine.Add(value);
                    }
                    else
                    {
                        reversedLine.Clear();
                        discardingOversizedLine = true;
                    }
                }
            }

            position = blockStart;
        }

        if (!discardingOversizedLine)
        {
            TryApplyReversedLine(tracker, reversedLine);
        }
    }

    private RolloutSignal TryApplyReversedLine(
        RolloutTracker tracker,
        List<byte> reversedLine)
    {
        if (reversedLine.Count == 0)
        {
            return RolloutSignal.None;
        }

        reversedLine.Reverse();
        try
        {
            var line = Encoding.UTF8.GetString(reversedLine.ToArray());
            return ApplyRolloutLine(tracker, line, initial: true);
        }
        finally
        {
            reversedLine.Reverse();
        }
    }

    private RolloutSignal ApplyRolloutLine(
        RolloutTracker tracker,
        string line,
        bool initial)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("payload", out var payload)
                || !payload.TryGetProperty("type", out var payloadType))
            {
                return RolloutSignal.None;
            }

            switch (payloadType.GetString())
            {
                case "task_started":
                    tracker.WaitingForUserStart = false;
                    tracker.IsActive = true;
                    tracker.HasKnownState = true;
                    tracker.StartedAt = ReadTaskStartedTimestamp(root, payload);
                    return RolloutSignal.UserStart;

                case "user_message":
                    tracker.WaitingForUserStart = false;
                    tracker.IsActive = true;
                    tracker.HasKnownState = true;
                    tracker.StartedAt = ReadTimestamp(root);
                    return RolloutSignal.UserStart;

                case "task_complete":
                    if (!initial)
                    {
                        _completedEvents++;
                    }

                    SetTerminalState(tracker);
                    return RolloutSignal.Terminal;

                case "turn_aborted":
                    SetTerminalState(tracker);
                    return RolloutSignal.Terminal;

                case "reasoning":
                case "custom_tool_call":
                case "custom_tool_call_output":
                case "function_call":
                case "function_call_output":
                case "agent_message":
                case "message":
                    if (root.TryGetProperty("type", out var rootType)
                        && rootType.GetString() == "response_item")
                    {
                        if (!tracker.WaitingForUserStart)
                        {
                            tracker.IsActive = true;
                            tracker.HasKnownState = true;
                        }

                        return RolloutSignal.Activity;
                    }

                    break;
            }
        }
        catch (JsonException)
        {
        }

        return RolloutSignal.None;
    }

    private static DateTime ReadTaskStartedTimestamp(
        JsonElement root,
        JsonElement payload) =>
        TryReadTimestamp(payload, "started_at", out var startedAt)
            ? startedAt
            : ReadTimestamp(root);

    private static DateTime ReadTimestamp(JsonElement root)
    {
        if (TryReadTimestamp(root, "timestamp", out var timestamp))
        {
            return timestamp;
        }

        return DateTime.Now;
    }

    private static bool TryReadTimestamp(
        JsonElement element,
        string propertyName,
        out DateTime timestamp)
    {
        if (element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            timestamp = parsed.LocalDateTime;
            return true;
        }

        timestamp = default;
        return false;
    }

    private static void SetTerminalState(RolloutTracker tracker)
    {
        tracker.IsActive = false;
        tracker.HasKnownState = true;
        tracker.WaitingForUserStart = true;
    }

    private static string NormalizePath(string path) =>
        path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            ? path[4..]
            : path;

    public void Dispose()
    {
        _state?.Dispose();
    }
}
