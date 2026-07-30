namespace NovaDesktop.Models;

public enum AgentSupervisorLeaseState
{
    Active,
    Paused,
    Recoverable,
    Completed,
    BudgetExhausted,
    Failed,
    Cancelled
}

public sealed record AgentSupervisorLease(
    string TaskId,
    string Title,
    string WorkspaceRoot,
    AgentExecutionMode Mode,
    AgentSupervisorLeaseState State,
    string OwnerBootId,
    int Attempt,
    string Checkpoint,
    DateTimeOffset AcquiredAt,
    DateTimeOffset HeartbeatAt,
    DateTimeOffset UpdatedAt)
{
    public long Epoch { get; init; }
    public long ExecutionSequence { get; init; }
}

public sealed record AgentSupervisorSnapshot(
    string SupervisorVersion,
    string BootId,
    DateTimeOffset BootedAt,
    IReadOnlyList<AgentSupervisorLease> Leases)
{
    public int ActiveCount => Leases.Count(lease =>
        lease.State is AgentSupervisorLeaseState.Active
            or AgentSupervisorLeaseState.Paused);

    public int RecoverableCount => Leases.Count(lease =>
        lease.State == AgentSupervisorLeaseState.Recoverable);
}
