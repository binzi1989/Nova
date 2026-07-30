namespace NovaDesktop.Models;

public enum GoalRunPhase
{
    Exploring,
    Chartered,
    Executing,
    Verifying,
    Proven,
    Partial,
    Blocked,
    Failed,
    Interrupted,
    Stale
}

public enum GoalSignalStatus
{
    Pending,
    Investigating,
    Pass,
    Unverified,
    Fail,
    Blocked,
    Stale
}

public enum EvidenceFreshness
{
    Untracked,
    Fresh,
    Stale
}

public sealed record GoalOutcomeSignal(
    string Id,
    int Index,
    string Description,
    GoalSignalStatus Status,
    string Evidence,
    int Confidence,
    DateTimeOffset UpdatedAt);

public sealed record GoalOutcomeLedger(
    string TaskId,
    string MissionHash,
    string MissionTitle,
    string MissionOutcome,
    GoalRunPhase Phase,
    IReadOnlyList<GoalOutcomeSignal> Signals,
    string AssessmentStatus,
    int AssessmentProofScore,
    string CouncilVerdict,
    int CouncilConfidence,
    string Detail,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public EvidenceFreshness Freshness { get; init; } = EvidenceFreshness.Untracked;
    public string EvidenceWorkspaceRoot { get; init; } = string.Empty;
    public string EvidenceFingerprint { get; init; } = string.Empty;
    public DateTimeOffset? EvidenceCapturedAt { get; init; }
    public int EvidenceFileCount { get; init; }
    public long EvidenceHashedBytes { get; init; }

    public bool IsProven
        => Phase == GoalRunPhase.Proven
           && Freshness != EvidenceFreshness.Stale
           && Signals.Count > 0
           && Signals.All(signal => signal.Status == GoalSignalStatus.Pass);
}
