using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexMonitor;

internal sealed record OfficialPlanUsageWindow(
    double UsedPercent,
    double RemainingPercent,
    int WindowMinutes,
    DateTimeOffset ResetsAt);

internal sealed record OfficialPlanUsageSnapshot(
    string LimitId,
    string? PlanType,
    OfficialPlanUsageWindow? Primary,
    OfficialPlanUsageWindow? Secondary,
    double RemainingPercent,
    DateTimeOffset ObservedAt,
    string Detail);

internal sealed class OfficialPlanUsageReader
{
    internal const int DefaultMaxTailBytes = 8 * 1024 * 1024;

    private const int ReadBlockBytes = 64 * 1024;
    private const int MaxJsonLineBytes = 2 * 1024 * 1024;
    private const int MaxRolloutFiles = 32;

    private readonly string _codexHome;
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _maxTailBytes;

    public OfficialPlanUsageReader()
        : this(
            Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex"))
    {
    }

    internal OfficialPlanUsageReader(
        string codexHome,
        Func<DateTimeOffset>? clock = null,
        int maxTailBytes = DefaultMaxTailBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTailBytes, 1);

        _codexHome = codexHome;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _maxTailBytes = maxTailBytes;
    }

    public OfficialPlanUsageSnapshot? Read()
    {
        var statePath = Path.Combine(_codexHome, "state_5.sqlite");
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            using var state = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = statePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Shared,
                    DefaultTimeout = 2
                }.ToString());
            state.Open();

            var candidates = new List<RateLimitCandidate>();
            foreach (var rolloutPath in ReadRecentRolloutPaths(state))
            {
                var candidate = ReadPreferredCandidate(rolloutPath);
                if (candidate is not null)
                {
                    candidates.Add(candidate);
                }
            }

            var selected = candidates
                .Where(candidate => string.Equals(
                    candidate.LimitId,
                    "codex",
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.ObservedAt)
                .FirstOrDefault()
                ?? candidates
                    .OrderByDescending(candidate => candidate.ObservedAt)
                    .FirstOrDefault();

            return selected is null ? null : CreateSnapshot(selected);
        }
        catch (SqliteException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> ReadRecentRolloutPaths(
        SqliteConnection state)
    {
        using var command = state.CreateCommand();
        command.CommandText =
            """
            SELECT rollout_path
              FROM threads
             WHERE rollout_path IS NOT NULL
               AND rollout_path <> ''
             ORDER BY updated_at_ms DESC
             LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", MaxRolloutFiles);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            if (seen.Add(path))
            {
                yield return path;
            }
        }
    }

    private RateLimitCandidate? ReadPreferredCandidate(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                ReadBlockBytes,
                FileOptions.RandomAccess);

            var block = new byte[ReadBlockBytes];
            var reversedLine = new List<byte>(4096);
            var position = stream.Length;
            var lowerBound = Math.Max(0, position - _maxTailBytes);
            var discardingOversizedLine = false;
            RateLimitCandidate? newestValid = null;

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
                            var candidate = TryParseReversedLine(reversedLine);
                            if (candidate is not null)
                            {
                                newestValid ??= candidate;
                                if (string.Equals(
                                    candidate.LimitId,
                                    "codex",
                                    StringComparison.OrdinalIgnoreCase))
                                {
                                    return candidate;
                                }
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

            if (lowerBound == 0 && !discardingOversizedLine)
            {
                var candidate = TryParseReversedLine(reversedLine);
                if (candidate is not null)
                {
                    newestValid ??= candidate;
                    if (string.Equals(
                        candidate.LimitId,
                        "codex",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }

            return newestValid;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private RateLimitCandidate? TryParseReversedLine(List<byte> reversedLine)
    {
        if (reversedLine.Count == 0)
        {
            return null;
        }

        reversedLine.Reverse();
        try
        {
            return TryParseLine(reversedLine.ToArray());
        }
        finally
        {
            reversedLine.Reverse();
        }
    }

    private RateLimitCandidate? TryParseLine(ReadOnlyMemory<byte> line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("type", out var payloadType)
                || payloadType.ValueKind != JsonValueKind.String
                || payloadType.GetString() != "token_count"
                || !payload.TryGetProperty("rate_limits", out var rateLimits)
                || rateLimits.ValueKind != JsonValueKind.Object
                || !TryReadString(rateLimits, "limit_id", out var limitId)
                || !TryReadTimestamp(root, "timestamp", out var observedAt))
            {
                return null;
            }

            var now = _clock().ToUniversalTime();
            var primary = TryReadWindow(rateLimits, "primary", now);
            var secondary = TryReadWindow(rateLimits, "secondary", now);
            if (primary is null && secondary is null)
            {
                return null;
            }

            var planType = TryReadString(
                rateLimits,
                "plan_type",
                out var parsedPlanType)
                ? parsedPlanType
                : null;
            return new RateLimitCandidate(
                limitId,
                planType,
                primary,
                secondary,
                observedAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static OfficialPlanUsageWindow? TryReadWindow(
        JsonElement rateLimits,
        string propertyName,
        DateTimeOffset now)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var window)
            || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("used_percent", out var usedPercentValue)
            || usedPercentValue.ValueKind != JsonValueKind.Number
            || !usedPercentValue.TryGetDouble(out var usedPercent)
            || !double.IsFinite(usedPercent)
            || usedPercent is < 0 or > 100
            || !window.TryGetProperty("window_minutes", out var minutesValue)
            || minutesValue.ValueKind != JsonValueKind.Number
            || !minutesValue.TryGetInt32(out var windowMinutes)
            || windowMinutes <= 0
            || !window.TryGetProperty("resets_at", out var resetsAtValue)
            || resetsAtValue.ValueKind != JsonValueKind.Number
            || !resetsAtValue.TryGetInt64(out var resetsAtUnix))
        {
            return null;
        }

        DateTimeOffset resetsAt;
        try
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetsAtUnix);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        if (resetsAt <= now)
        {
            return null;
        }

        return new OfficialPlanUsageWindow(
            usedPercent,
            100 - usedPercent,
            windowMinutes,
            resetsAt);
    }

    private static bool TryReadString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        if (element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadTimestamp(
        JsonElement element,
        string propertyName,
        out DateTimeOffset timestamp)
    {
        if (element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                property.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal
                    | DateTimeStyles.AdjustToUniversal,
                out timestamp))
        {
            return true;
        }

        timestamp = default;
        return false;
    }

    private static OfficialPlanUsageSnapshot CreateSnapshot(
        RateLimitCandidate candidate)
    {
        var windows = new[] { candidate.Primary, candidate.Secondary }
            .OfType<OfficialPlanUsageWindow>()
            .ToArray();
        var remainingPercent = windows.Min(window => window.RemainingPercent);

        return new OfficialPlanUsageSnapshot(
            candidate.LimitId,
            candidate.PlanType,
            candidate.Primary,
            candidate.Secondary,
            remainingPercent,
            candidate.ObservedAt,
            BuildDetail(candidate, remainingPercent));
    }

    private static string BuildDetail(
        RateLimitCandidate candidate,
        double remainingPercent)
    {
        var parts = new List<string>
        {
            FormatPlanName(candidate.PlanType ?? candidate.LimitId),
            $"剩余 {FormatPercent(remainingPercent)}%"
        };

        if (candidate.Primary is not null)
        {
            parts.Add(FormatWindow("主窗口", candidate.Primary));
        }

        if (candidate.Secondary is not null)
        {
            parts.Add(FormatWindow("次窗口", candidate.Secondary));
        }

        return string.Join(" · ", parts);
    }

    private static string FormatWindow(
        string label,
        OfficialPlanUsageWindow window) =>
        $"{label} {FormatPercent(window.RemainingPercent)}%"
        + $"/{FormatDuration(window.WindowMinutes)}"
        + $"/{window.ResetsAt.ToLocalTime():MM-dd HH:mm} 重置";

    private static string FormatDuration(int minutes)
    {
        if (minutes % (24 * 60) == 0)
        {
            return $"{minutes / (24 * 60)} 天";
        }

        if (minutes % 60 == 0)
        {
            return $"{minutes / 60} 小时";
        }

        return $"{minutes} 分钟";
    }

    private static string FormatPercent(double percent) =>
        percent.ToString("0.#", CultureInfo.InvariantCulture);

    private static string FormatPlanName(string planName) =>
        planName.ToLowerInvariant() switch
        {
            "free" => "Free",
            "plus" => "Plus",
            "pro" => "Pro",
            "team" => "Team",
            "business" => "Business",
            "enterprise" => "Enterprise",
            _ => planName,
        };

    private sealed record RateLimitCandidate(
        string LimitId,
        string? PlanType,
        OfficialPlanUsageWindow? Primary,
        OfficialPlanUsageWindow? Secondary,
        DateTimeOffset ObservedAt);
}
