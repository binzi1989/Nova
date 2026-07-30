using NovaDesktop.Models;

namespace NovaDesktop.Services;

public sealed class AgentResourceGovernor
{
    private readonly object _stateLock = new();
    private readonly HashSet<string> _activeTasks = new(StringComparer.OrdinalIgnoreCase);
    private AgentBudgetPolicy _policy = AgentBudgetPolicy.ForMode(AgentExecutionMode.Build);
    private TaskCompletionSource<bool> _resumeSignal = CreateCompletedSignal();
    private int _activeAgents;
    private int _toolCalls;
    private int _modelRounds;
    private int _outputCharacters;
    private bool _isPaused;
    private string? _limitReason;
    private DateTimeOffset _updatedAt = DateTimeOffset.Now;

    public void BeginTask(string taskId, AgentExecutionMode mode)
    {
        lock (_stateLock)
        {
            _policy = AgentBudgetPolicy.ForMode(mode);
            _activeTasks.Add(taskId);
            _activeAgents = Math.Max(1, _activeAgents);
            _toolCalls = 0;
            _modelRounds = 0;
            _outputCharacters = 0;
            _limitReason = null;
            _isPaused = false;
            _resumeSignal.TrySetResult(true);
            _resumeSignal = CreateCompletedSignal();
            _updatedAt = DateTimeOffset.Now;
        }
    }

    public async Task ObserveRuntimeEventAsync(
        string taskId,
        AgentRuntimeEvent runtimeEvent,
        CancellationToken cancellationToken = default)
    {
        if (runtimeEvent.Kind != AgentRuntimeEventKind.TextDelta)
        {
            await WaitForResumeAsync(cancellationToken);
        }

        lock (_stateLock)
        {
            if (!_activeTasks.Contains(taskId))
            {
                throw new InvalidOperationException(
                    $"Task {taskId} does not hold an active AgentOS resource lease.");
            }

            switch (runtimeEvent.Kind)
            {
                case AgentRuntimeEventKind.Thinking:
                    if (runtimeEvent.ModelRoundCost > 0)
                    {
                        EnsureWithinLimit(
                            _modelRounds + runtimeEvent.ModelRoundCost,
                            _policy.MaxModelRounds,
                            "模型轮次");
                        _modelRounds += runtimeEvent.ModelRoundCost;
                    }
                    break;
                case AgentRuntimeEventKind.ToolRequested:
                    EnsureWithinLimit(
                        _toolCalls + 1,
                        _policy.MaxToolCallsPerTask,
                        "工具调用");
                    _toolCalls++;
                    break;
                case AgentRuntimeEventKind.BatchStarted:
                    EnsureWithinLimit(
                        Math.Max(1, runtimeEvent.ActiveUnits),
                        _policy.MaxConcurrentAgents,
                        "并行 Agent");
                    break;
                case AgentRuntimeEventKind.TextDelta:
                    EnsureWithinLimit(
                        _outputCharacters + runtimeEvent.Detail.Length,
                        _policy.MaxOutputCharacters,
                        "输出字符");
                    _outputCharacters += runtimeEvent.Detail.Length;
                    break;
            }

            if (runtimeEvent.Kind is not AgentRuntimeEventKind.ToolBatchStarted
                and not AgentRuntimeEventKind.ToolBatchCompleted)
            {
                _activeAgents = Math.Clamp(
                    runtimeEvent.ActiveUnits,
                    0,
                    _policy.MaxConcurrentAgents);
            }
            _updatedAt = DateTimeOffset.Now;
        }
    }

    public void SetPaused(bool paused)
    {
        TaskCompletionSource<bool>? signalToRelease = null;
        lock (_stateLock)
        {
            if (_isPaused == paused)
            {
                return;
            }

            _isPaused = paused;
            if (paused)
            {
                _resumeSignal = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                signalToRelease = _resumeSignal;
            }
            _updatedAt = DateTimeOffset.Now;
        }
        signalToRelease?.TrySetResult(true);
    }

    public async Task WaitForResumeAsync(CancellationToken cancellationToken = default)
    {
        Task waitTask;
        lock (_stateLock)
        {
            waitTask = _isPaused ? _resumeSignal.Task : Task.CompletedTask;
        }
        await waitTask.WaitAsync(cancellationToken);
    }

    public void ValidateFinalOutput(string taskId, int characters)
    {
        lock (_stateLock)
        {
            if (!_activeTasks.Contains(taskId))
            {
                throw new InvalidOperationException(
                    $"Task {taskId} does not hold an active AgentOS resource lease.");
            }

            EnsureWithinLimit(
                Math.Max(_outputCharacters, Math.Max(0, characters)),
                _policy.MaxOutputCharacters,
                "输出字符");
            _outputCharacters = Math.Max(_outputCharacters, Math.Max(0, characters));
            _updatedAt = DateTimeOffset.Now;
        }
    }

    public void RecordRuntimeEvent(AgentRuntimeEvent runtimeEvent)
    {
        lock (_stateLock)
        {
            if (runtimeEvent.Kind is not AgentRuntimeEventKind.ToolBatchStarted
                and not AgentRuntimeEventKind.ToolBatchCompleted)
            {
                _activeAgents = Math.Clamp(
                    runtimeEvent.ActiveUnits,
                    0,
                    _policy.MaxConcurrentAgents);
            }
            if (runtimeEvent.Kind == AgentRuntimeEventKind.ToolRequested)
            {
                _toolCalls++;
            }
            if (runtimeEvent.Kind == AgentRuntimeEventKind.Thinking)
            {
                _modelRounds++;
            }
            _updatedAt = DateTimeOffset.Now;
        }
    }

    public void EndTask(string taskId)
    {
        TaskCompletionSource<bool>? signalToRelease = null;
        lock (_stateLock)
        {
            _activeTasks.Remove(taskId);
            if (_activeTasks.Count == 0)
            {
                _activeAgents = 0;
                _isPaused = false;
                signalToRelease = _resumeSignal;
            }
            _updatedAt = DateTimeOffset.Now;
        }
        signalToRelease?.TrySetResult(true);
    }

    public AgentResourceSnapshot GetSnapshot()
    {
        lock (_stateLock)
        {
            return new AgentResourceSnapshot(
                _policy,
                _activeTasks.Count,
                _activeAgents,
                _toolCalls,
                _modelRounds,
                _updatedAt)
            {
                OutputCharacters = _outputCharacters,
                IsPaused = _isPaused,
                LimitReason = _limitReason
            };
        }
    }

    private void EnsureWithinLimit(int requested, int limit, string resource)
    {
        if (requested <= limit)
        {
            return;
        }

        _limitReason = $"{resource}预算已用尽（{limit}）";
        _updatedAt = DateTimeOffset.Now;
        throw new AgentBudgetExceededException(resource, limit, requested);
    }

    private static TaskCompletionSource<bool> CreateCompletedSignal()
    {
        var signal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult(true);
        return signal;
    }
}

public sealed class AgentBudgetExceededException : InvalidOperationException
{
    public AgentBudgetExceededException(string resource, int limit, int requested)
        : base($"{resource}预算已用尽：上限 {limit}，下一次请求将达到 {requested}。")
    {
        Resource = resource;
        Limit = limit;
        Requested = requested;
    }

    public string Resource { get; }
    public int Limit { get; }
    public int Requested { get; }
}
