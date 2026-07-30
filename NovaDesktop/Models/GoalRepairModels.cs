namespace NovaDesktop.Models;

public enum GoalRepairAttemptStatus
{
    Planned,
    Running,
    Verifying,
    Proven,
    Partial,
    Declined,
    Failed
}

public sealed record GoalRepairTarget(
    int SignalIndex,
    string SignalId,
    string Description,
    GoalSignalStatus PreviousStatus,
    string PreviousEvidence);

public sealed record GoalRepairAttempt(
    string AttemptId,
    string TaskId,
    string MissionHash,
    int Round,
    int MaximumRounds,
    IReadOnlyList<GoalRepairTarget> Targets,
    int PreservedPassCount,
    GoalRepairAttemptStatus Status,
    string Detail,
    string BeforeFingerprint,
    string AfterFingerprint,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record GoalRepairLedger(
    string TaskId,
    string MissionHash,
    IReadOnlyList<GoalRepairAttempt> Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public int UsedRounds => Attempts.Count(attempt =>
        attempt.Status != GoalRepairAttemptStatus.Declined);
    public bool HasRemainingRounds(int maximumRounds)
        => UsedRounds < maximumRounds;
}
