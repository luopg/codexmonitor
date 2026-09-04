namespace CodexMonitor;

internal sealed record MonitorSnapshot(
    IReadOnlyList<ActiveProject> ActiveTasks,
    int ProjectCount,
    string? Error,
    DateTime UpdatedAt,
    int CompletedEvents = 0);
