namespace CodexMonitor;

internal sealed record ActiveProject(
    string ThreadId,
    string Title,
    string Cwd,
    DateTime StartedAt);
