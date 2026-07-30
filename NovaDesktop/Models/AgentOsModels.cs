namespace NovaDesktop.Models;

public enum AgentExecutionMode
{
    Ask,
    Plan,
    Build,
    Autopilot,
    Goal
}

public enum AgentOsServiceHealth
{
    Starting,
    Ready,
    Degraded,
    Offline
}

public enum AgentGraphNodeState
{
    Pending,
    Ready,
    Running,
    Waiting,
    Completed,
    Failed,
    Skipped
}

public sealed record AgentOsServiceStatus(
    string Id,
    string Name,
    AgentOsServiceHealth Health,
    string Detail,
    DateTimeOffset UpdatedAt);

public sealed record AgentOsEventRecord(
    long Sequence,
    DateTimeOffset Timestamp,
    string Category,
    string Source,
    string Message,
    string CorrelationId,
    string Severity = "INFO")
{
    public TaskState? TaskState { get; init; }
    public double? Progress { get; init; }
    public string Stage { get; init; } = string.Empty;
}

public sealed record AgentOsKernelSnapshot(
    string KernelVersion,
    string BootId,
    DateTimeOffset BootedAt,
    AgentExecutionMode ExecutionMode,
    IReadOnlyList<AgentOsServiceStatus> Services,
    IReadOnlyList<AgentOsEventRecord> RecentEvents)
{
    public string UptimeLabel
    {
        get
        {
            var uptime = DateTimeOffset.Now - BootedAt;
            return uptime.TotalHours >= 1
                ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
                : $"{Math.Max(0, uptime.Minutes)}m {uptime.Seconds}s";
        }
    }
}

public sealed record AgentGraphNode(
    string Id,
    string Title,
    string Role,
    IReadOnlyList<string> Dependencies,
    AgentGraphNodeState State,
    double Progress,
    string Detail,
    DateTimeOffset UpdatedAt);

public sealed record AgentTaskGraphSnapshot(
    string TaskId,
    string Title,
    AgentExecutionMode Mode,
    IReadOnlyList<AgentGraphNode> Nodes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public long ExecutionSequence { get; init; }

    public double OverallProgress => Nodes.Count == 0
        ? 0
        : Nodes.Average(node => node.Progress);
}

public sealed record AgentBudgetPolicy(
    int MaxConcurrentAgents,
    int MaxToolCallsPerTask,
    int MaxModelRounds,
    int MaxRepairRounds,
    int MaxOutputCharacters)
{
    public static AgentBudgetPolicy ForMode(AgentExecutionMode mode)
        => mode switch
        {
            AgentExecutionMode.Ask => new AgentBudgetPolicy(1, 24, 24, 0, 120_000),
            AgentExecutionMode.Plan => new AgentBudgetPolicy(3, 48, 40, 0, 240_000),
            AgentExecutionMode.Build => new AgentBudgetPolicy(4, 128, 96, 3, 600_000),
            AgentExecutionMode.Autopilot => new AgentBudgetPolicy(6, 240, 180, 5, 1_000_000),
            _ => new AgentBudgetPolicy(8, 400, 280, 6, 1_500_000)
        };
}

public sealed record AgentResourceSnapshot(
    AgentBudgetPolicy Policy,
    int ActiveTasks,
    int ActiveAgents,
    int ToolCalls,
    int ModelRounds,
    DateTimeOffset UpdatedAt)
{
    public int OutputCharacters { get; init; }
    public bool IsPaused { get; init; }
    public string? LimitReason { get; init; }
}
