namespace NovaDesktop.Models;

public enum TaskFailureKind
{
    Model,
    Network,
    Tool,
    Permission,
    Budget,
    Build,
    Verification,
    HostInterruption,
    SideEffectUncertain,
    Configuration,
    Unknown
}

public enum FailureRecoveryAction
{
    Retry,
    Resume,
    ReconnectModel,
    ReviewPermission,
    ReviewSideEffect,
    FixBuild,
    Reverify,
    RestoreWorkspace,
    InspectDiagnostics
}

public sealed record TaskFailureRecord(
    string IncidentId,
    string TaskId,
    TaskFailureKind Kind,
    string Code,
    string Title,
    string UserMessage,
    FailureRecoveryAction RecoveryAction,
    string RecoveryLabel,
    bool Retryable,
    bool BlocksAutomaticReplay,
    string Stage,
    string ExceptionType,
    DateTimeOffset OccurredAt)
{
    public string StatusLabel
        => Kind switch
        {
            TaskFailureKind.Budget => "BUDGET EXHAUSTED",
            TaskFailureKind.Permission => "PERMISSION BLOCKED",
            TaskFailureKind.Build => "BUILD FAILED",
            TaskFailureKind.Verification => "VERIFY FAILED",
            TaskFailureKind.Network => "NETWORK INTERRUPTED",
            TaskFailureKind.Model => "MODEL INTERRUPTED",
            TaskFailureKind.HostInterruption => "HOST INTERRUPTED",
            TaskFailureKind.SideEffectUncertain => "ACTION REVIEW",
            TaskFailureKind.Configuration => "SETUP REQUIRED",
            TaskFailureKind.Tool => "TOOL INTERRUPTED",
            _ => "ATTENTION"
        };
}
