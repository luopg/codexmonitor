#nullable enable

namespace CodexMonitor;

internal sealed record BalanceSnapshot(
    string ProviderName,
    decimal? Remaining,
    string Unit,
    string? PlanName,
    decimal? TodayCost,
    string? Error,
    DateTime UpdatedAt,
    bool IsConfigured);
