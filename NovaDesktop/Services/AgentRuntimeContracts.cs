using NovaDesktop.Models;

namespace NovaDesktop.Services;

public sealed record AgentRunRequest(
    string TaskId,
    string Prompt,
    string WorkspaceRoot,
    string ApiKey,
    string Provider,
    string Model,
    AgentExecutionMode ExecutionMode = AgentExecutionMode.Build,
    bool AllowParallelDelegation = true,
    IReadOnlyList<string>? AllowedWriteScopes = null,
    IReadOnlyList<AgentInputAttachment>? Attachments = null,
    string? Endpoint = null,
    int? MaxModelRoundsOverride = null,
    int? MaxTokensPerRequest = null,
    string? AgentPackId = null,
    IReadOnlySet<string>? AllowedToolNames = null);

public sealed record AgentRuntimeEvent(
    AgentRuntimeEventKind Kind,
    string Agent,
    string Action,
    string Detail,
    double Progress = 0,
    int ActiveUnits = 1)
{
    public int ModelRoundCost { get; init; }
}

public enum AgentRuntimeEventKind
{
    Thinking,
    ToolRequested,
    ToolRunning,
    ToolCompleted,
    ToolBatchStarted,
    ToolBatchCompleted,
    BatchStarted,
    BatchCompleted,
    TextDelta,
    Message,
    Completed,
    Failed
}

public sealed record ToolApprovalRequest(
    string ToolName,
    string Title,
    string Description,
    string ArgumentsPreview,
    string PreviewKind = "arguments",
    string? ChangePreview = null,
    int Additions = 0,
    int Deletions = 0);

public sealed record AgentRunResult(
    string ResponseId,
    string FinalText,
    int ToolCalls,
    string Provider,
    string Model)
{
    public int MutatingToolCalls { get; init; }
}

public interface IAgentRuntime
{
    Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        Func<AgentRuntimeEvent, Task> onEvent,
        Func<ToolApprovalRequest, Task<bool>> requestApproval,
        CancellationToken cancellationToken);
}
