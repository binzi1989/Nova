using NovaDesktop.Models;
using NovaDesktop.Services;

namespace NovaDesktop.Mac;

public sealed class MacAgentOsHost : IDisposable
{
    private readonly SemaphoreSlim _bootGate = new(1, 1);
    private readonly AgentOsKernel _kernel;
    private readonly AgentTaskGraphService _taskGraph;
    private readonly AgentSupervisorService _supervisor;
    private readonly AgentResourceGovernor _governor = new();
    private readonly TaskSnapshotService _snapshots;
    private readonly TaskJournalService _journal;
    private bool _booted;

    public MacAgentOsHost(string? root = null)
    {
        var agentOsRoot = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NOVA",
            "AgentOS");
        _kernel = new AgentOsKernel(Path.Combine(agentOsRoot, "kernel"));
        _taskGraph = new AgentTaskGraphService(Path.Combine(agentOsRoot, "task-graphs"));
        _supervisor = new AgentSupervisorService(Path.Combine(agentOsRoot, "supervisor"));
        _snapshots = new TaskSnapshotService(Path.Combine(agentOsRoot, "tasks"));
        _journal = new TaskJournalService(Path.Combine(agentOsRoot, "task-journal.jsonl"));
    }

    public string Status
    {
        get
        {
            if (!_booted)
            {
                return "AGENTOS STARTING";
            }
            var kernel = _kernel.GetSnapshot();
            var ready = kernel.Services.Count(item => item.Health == AgentOsServiceHealth.Ready);
            return $"AGENTOS {kernel.KernelVersion} · {ready}/{kernel.Services.Count} SERVICES";
        }
    }

    public async Task EnsureBootedAsync(CancellationToken cancellationToken = default)
    {
        if (_booted)
        {
            return;
        }

        await _bootGate.WaitAsync(cancellationToken);
        try
        {
            if (_booted)
            {
                return;
            }
            var boot = await _kernel.BootAsync(cancellationToken);
            await _supervisor.BootAsync(boot.BootId, cancellationToken);
            await _kernel.ReportServiceAsync(
                "supervisor",
                "Agent Supervisor",
                AgentOsServiceHealth.Ready,
                "Durable macOS lease layer active",
                boot.BootId,
                cancellationToken);
            _booted = true;
        }
        finally
        {
            _bootGate.Release();
        }
    }

    public IReadOnlyList<TaskItem> LoadTasks()
        => _snapshots.LoadAll()
            .Where(snapshot => !snapshot.IsArchived)
            .Select(snapshot =>
            {
                var interrupted = snapshot.State is TaskState.Running
                    or TaskState.Waiting
                    or TaskState.BudgetExhausted;
                return new TaskItem
                {
                    Id = snapshot.TaskId,
                    Title = snapshot.Title,
                    Description = snapshot.Prompt,
                    CreatedAt = snapshot.CreatedAt,
                    WorkspaceRoot = snapshot.WorkspaceRoot,
                    Provider = snapshot.Provider,
                    Model = snapshot.Model,
                    ExecutionMode = snapshot.ExecutionMode,
                    State = interrupted ? TaskState.Paused : snapshot.State,
                    Progress = snapshot.Progress,
                    Stage = interrupted
                        ? "上次会话已安全暂停，可重新发起"
                        : snapshot.Stage,
                    Draft = snapshot.Draft,
                    Attachments = snapshot.Attachments ?? [],
                    IsArchived = snapshot.IsArchived,
                    ExecutionSequence = snapshot.ExecutionSequence,
                    Elapsed = snapshot.UpdatedAt.LocalDateTime.ToString("MM-dd HH:mm")
                };
            })
            .ToArray();

    public async Task BeginTaskAsync(
        TaskItem task,
        CancellationToken cancellationToken = default)
    {
        await EnsureBootedAsync(cancellationToken);
        await _kernel.SetExecutionModeAsync(task.ExecutionMode, task.Id, cancellationToken);
        task.State = TaskState.Running;
        task.Progress = Math.Max(2, task.Progress);
        task.Stage = "任务已进入共享 AgentOS";
        _governor.BeginTask(task.Id, task.ExecutionMode);
        var acquired = false;
        try
        {
            await _supervisor.AcquireAsync(task, cancellationToken);
            acquired = true;
            var committed = await _kernel.PublishTaskEventAsync(
                "task",
                "NOVA Mac",
                "Execution accepted by shared AgentOS.",
                task,
                cancellationToken: cancellationToken);
            await _taskGraph.CreateAsync(
                task.Id,
                task.Title,
                task.ExecutionMode,
                cancellationToken,
                committed.Sequence);
            await _snapshots.SaveAsync(task, cancellationToken);
            await _journal.AppendAsync(
                task.Id,
                "NOVA Mac",
                "任务开始",
                task.Stage,
                ActivityKind.Working,
                task.Progress);
        }
        catch
        {
            task.State = TaskState.Failed;
            task.Stage = "AgentOS 未能安全接管任务";
            if (acquired)
            {
                await _supervisor.ReleaseAsync(
                    task,
                    cancellationToken,
                    task.ExecutionSequence);
            }
            _governor.EndTask(task.Id);
            throw;
        }
    }

    public async Task ObserveAsync(
        TaskItem task,
        AgentRuntimeEvent runtimeEvent,
        CancellationToken cancellationToken = default)
    {
        await _governor.ObserveRuntimeEventAsync(task.Id, runtimeEvent, cancellationToken);
        task.State = TaskState.Running;
        task.Progress = Math.Max(task.Progress, Math.Clamp(runtimeEvent.Progress, 0, 98));
        task.Stage = string.IsNullOrWhiteSpace(runtimeEvent.Action)
            ? runtimeEvent.Detail
            : runtimeEvent.Action;
        var committed = await _kernel.PublishTaskEventAsync(
            "runtime",
            runtimeEvent.Agent,
            $"{runtimeEvent.Action}: {runtimeEvent.Detail}",
            task,
            cancellationToken: cancellationToken);
        await _taskGraph.ApplyRuntimeEventAsync(
            task.Id,
            runtimeEvent,
            cancellationToken,
            committed.Sequence);
        await _supervisor.HeartbeatAsync(
            task.Id,
            task.Stage,
            forcePersist: runtimeEvent.Kind is AgentRuntimeEventKind.BatchStarted
                or AgentRuntimeEventKind.BatchCompleted,
            cancellationToken,
            committed.Sequence);
        await _snapshots.SaveAsync(task, cancellationToken);
    }

    public async Task CompleteTaskAsync(
        TaskItem task,
        bool succeeded,
        string detail,
        int outputCharacters,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (succeeded)
            {
                _governor.ValidateFinalOutput(task.Id, outputCharacters);
            }
            task.State = succeeded ? TaskState.Completed : TaskState.Failed;
            task.Progress = succeeded ? 100 : Math.Max(task.Progress, 1);
            task.Stage = detail;
            var committed = await _kernel.PublishTaskEventAsync(
                "task",
                "NOVA Mac",
                detail,
                task,
                succeeded ? "INFO" : "ERROR",
                cancellationToken);
            await _taskGraph.CompleteAsync(
                task.Id,
                succeeded,
                detail,
                cancellationToken,
                committed.Sequence);
            await _snapshots.SaveAsync(task, cancellationToken);
            await _journal.AppendAsync(
                task.Id,
                "NOVA Mac",
                succeeded ? "任务完成" : "任务失败",
                detail,
                succeeded ? ActivityKind.Completed : ActivityKind.System,
                task.Progress);
            await _supervisor.ReleaseAsync(task, cancellationToken, committed.Sequence);
        }
        finally
        {
            _governor.EndTask(task.Id);
        }
    }

    public async Task ReportRuntimeAsync(
        string provider,
        string model,
        bool ready,
        CancellationToken cancellationToken = default)
    {
        await EnsureBootedAsync(cancellationToken);
        await _kernel.ReportServiceAsync(
            "runtime",
            "Model Runtime",
            ready ? AgentOsServiceHealth.Ready : AgentOsServiceHealth.Degraded,
            ready ? $"{provider} · {model}" : "Provider request failed",
            "mac-runtime",
            cancellationToken);
    }

    public void Dispose()
    {
        _supervisor.Dispose();
        _bootGate.Dispose();
    }
}
