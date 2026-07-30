using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public sealed class AgentSupervisorService : IDisposable
{
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly string _root;
    private readonly string _statePath;
    private readonly string _stateLockPath;
    private readonly string _taskLockRoot;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly Dictionary<string, AgentSupervisorLease> _leases =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileStream> _taskLeaseHandles =
        new(StringComparer.OrdinalIgnoreCase);
    private string _bootId = Guid.NewGuid().ToString("N")[..12];
    private DateTimeOffset _bootedAt = DateTimeOffset.Now;
    private DateTimeOffset _lastHeartbeatPersistedAt = DateTimeOffset.MinValue;

    public AgentSupervisorService(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "agent-os",
            "supervisor");
        _statePath = Path.Combine(_root, "supervisor-state.json");
        _stateLockPath = Path.Combine(_root, "supervisor-state.lock");
        _taskLockRoot = Path.Combine(_root, "task-leases");
    }

    public async Task<AgentSupervisorSnapshot> BootAsync(
        string bootId,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await using var stateFileLock = await AcquireStateFileLockAsync(cancellationToken);
            LoadUnsafe();
            var now = DateTimeOffset.Now;
            lock (_stateLock)
            {
                _bootId = bootId;
                _bootedAt = now;
                foreach (var (taskId, lease) in _leases.ToArray())
                {
                    if (lease.State is AgentSupervisorLeaseState.Active
                            or AgentSupervisorLeaseState.Paused
                        && CanAcquireTaskLock(taskId))
                    {
                        _leases[taskId] = lease with
                        {
                            State = AgentSupervisorLeaseState.Recoverable,
                            Checkpoint = string.IsNullOrWhiteSpace(lease.Checkpoint)
                                ? "Previous host stopped before releasing the task lease"
                                : lease.Checkpoint,
                            UpdatedAt = now
                        };
                    }
                }
            }
            await PersistUnsafeAsync(cancellationToken);
            return GetSnapshot();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<AgentSupervisorLease> AcquireAsync(
        TaskItem task,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await using var stateFileLock = await AcquireStateFileLockAsync(cancellationToken);
            LoadUnsafe();
            FileStream taskLeaseHandle;
            try
            {
                taskLeaseHandle = AcquireTaskLock(task.Id);
            }
            catch (IOException exception)
            {
                AgentSupervisorLease? owner;
                lock (_stateLock)
                {
                    _leases.TryGetValue(task.Id, out owner);
                }
                throw new AgentLeaseConflictException(task.Id, owner, exception);
            }

            AgentSupervisorLease lease;
            var now = DateTimeOffset.Now;
            try
            {
                lock (_stateLock)
                {
                    if (_taskLeaseHandles.ContainsKey(task.Id))
                    {
                        throw new AgentLeaseConflictException(
                            task.Id,
                            _leases.GetValueOrDefault(task.Id));
                    }

                    var attempt = _leases.TryGetValue(task.Id, out var existing)
                        ? existing.Attempt + 1
                        : 1;
                    var epoch = Math.Max(0, existing?.Epoch ?? 0) + 1;
                    lease = new AgentSupervisorLease(
                        task.Id,
                        task.Title,
                        task.WorkspaceRoot,
                        task.ExecutionMode,
                        AgentSupervisorLeaseState.Active,
                        _bootId,
                        attempt,
                        task.Stage,
                        now,
                        now,
                        now)
                    {
                        Epoch = epoch
                    };
                    _leases[task.Id] = lease;
                    _taskLeaseHandles[task.Id] = taskLeaseHandle;
                }
                await PersistUnsafeAsync(cancellationToken);
                return lease;
            }
            catch
            {
                lock (_stateLock)
                {
                    _taskLeaseHandles.Remove(task.Id);
                }
                taskLeaseHandle.Dispose();
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task HeartbeatAsync(
        string taskId,
        string checkpoint,
        bool forcePersist = false,
        CancellationToken cancellationToken = default,
        long executionSequence = 0)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await using var stateFileLock = await AcquireStateFileLockAsync(cancellationToken);
            LoadUnsafe();
            var now = DateTimeOffset.Now;
            var updated = false;
            lock (_stateLock)
            {
                if (_leases.TryGetValue(taskId, out var lease)
                    && lease.State == AgentSupervisorLeaseState.Active
                    && lease.OwnerBootId == _bootId
                    && _taskLeaseHandles.ContainsKey(taskId))
                {
                    _leases[taskId] = lease with
                    {
                        Checkpoint = string.IsNullOrWhiteSpace(checkpoint)
                            ? lease.Checkpoint
                            : checkpoint,
                        HeartbeatAt = now,
                        UpdatedAt = now,
                        ExecutionSequence = Math.Max(
                            lease.ExecutionSequence,
                            executionSequence)
                    };
                    updated = true;
                }
            }
            if (updated
                && (forcePersist || now - _lastHeartbeatPersistedAt >= TimeSpan.FromSeconds(2)))
            {
                await PersistUnsafeAsync(cancellationToken);
                _lastHeartbeatPersistedAt = now;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task ReleaseAsync(
        TaskItem task,
        CancellationToken cancellationToken = default,
        long executionSequence = 0)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await using var stateFileLock = await AcquireStateFileLockAsync(cancellationToken);
            LoadUnsafe();
            var now = DateTimeOffset.Now;
            var changed = false;
            lock (_stateLock)
            {
                if (_leases.TryGetValue(task.Id, out var lease)
                    && lease.OwnerBootId == _bootId
                    && _taskLeaseHandles.ContainsKey(task.Id))
                {
                    _leases[task.Id] = lease with
                    {
                        State = task.State switch
                        {
                            TaskState.Completed => AgentSupervisorLeaseState.Completed,
                            TaskState.Cancelled => AgentSupervisorLeaseState.Cancelled,
                            TaskState.Paused => AgentSupervisorLeaseState.Paused,
                            TaskState.BudgetExhausted => AgentSupervisorLeaseState.BudgetExhausted,
                            TaskState.Stale => AgentSupervisorLeaseState.Paused,
                            _ => AgentSupervisorLeaseState.Failed
                        },
                        Checkpoint = task.Stage,
                        HeartbeatAt = now,
                        UpdatedAt = now,
                        ExecutionSequence = Math.Max(
                            lease.ExecutionSequence,
                            executionSequence)
                    };
                    changed = true;
                }
            }
            if (changed)
            {
                await PersistUnsafeAsync(cancellationToken);
            }
            ReleaseTaskLock(task.Id);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public AgentSupervisorSnapshot GetSnapshot()
    {
        lock (_stateLock)
        {
            return new AgentSupervisorSnapshot(
                NovaProductVersion.Current,
                _bootId,
                _bootedAt,
                _leases.Values
                    .OrderByDescending(lease => lease.UpdatedAt)
                    .ToArray());
        }
    }

    private void LoadUnsafe()
    {
        if (!File.Exists(_statePath))
        {
            return;
        }
        var state = JsonSerializer.Deserialize<SupervisorState>(
            File.ReadAllText(_statePath),
            _jsonOptions);
        if (state is null)
        {
            return;
        }
        lock (_stateLock)
        {
            _leases.Clear();
            foreach (var lease in state.Leases)
            {
                _leases[lease.TaskId] = lease;
            }
        }
    }

    private async Task PersistUnsafeAsync(CancellationToken cancellationToken)
    {
        SupervisorState state;
        lock (_stateLock)
        {
            state = new SupervisorState(
                _bootId,
                _bootedAt,
                _leases.Values.ToArray(),
                DateTimeOffset.Now);
        }

        Directory.CreateDirectory(_root);
        var temporaryPath = _statePath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(state, _jsonOptions),
            cancellationToken);
        File.Move(temporaryPath, _statePath, overwrite: true);
    }

    private async Task<FileStream> AcquireStateFileLockAsync(
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var startedAt = DateTimeOffset.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _stateLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (
                DateTimeOffset.UtcNow - startedAt < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }

    private FileStream AcquireTaskLock(string taskId)
    {
        Directory.CreateDirectory(_taskLockRoot);
        return new FileStream(
            Path.Combine(_taskLockRoot, NormalizeTaskId(taskId) + ".lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.WriteThrough);
    }

    private bool CanAcquireTaskLock(string taskId)
    {
        try
        {
            using var probe = AcquireTaskLock(taskId);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void ReleaseTaskLock(string taskId)
    {
        FileStream? handle = null;
        lock (_stateLock)
        {
            if (_taskLeaseHandles.Remove(taskId, out var existing))
            {
                handle = existing;
            }
        }
        handle?.Dispose();
    }

    public void Dispose()
    {
        FileStream[] handles;
        lock (_stateLock)
        {
            handles = _taskLeaseHandles.Values.ToArray();
            _taskLeaseHandles.Clear();
        }
        foreach (var handle in handles)
        {
            handle.Dispose();
        }
        _operationGate.Dispose();
    }

    private static string NormalizeTaskId(string taskId)
    {
        var safe = new string(taskId
            .Where(character => char.IsLetterOrDigit(character)
                                || character is '-' or '_')
            .Take(80)
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "task" : safe;
    }

    private sealed record SupervisorState(
        string BootId,
        DateTimeOffset BootedAt,
        IReadOnlyList<AgentSupervisorLease> Leases,
        DateTimeOffset UpdatedAt);
}

public sealed class AgentLeaseConflictException : InvalidOperationException
{
    public AgentLeaseConflictException(
        string taskId,
        AgentSupervisorLease? owner,
        Exception? innerException = null)
        : base(
            owner is null
                ? $"任务 {taskId} 的系统租约正在被另一个宿主持有。"
                : $"任务 {taskId} 已由宿主 {owner.OwnerBootId} 持有"
                  + $"（epoch {owner.Epoch}，检查点：{owner.Checkpoint}）。",
            innerException)
    {
        TaskId = taskId;
        Owner = owner;
    }

    public string TaskId { get; }
    public AgentSupervisorLease? Owner { get; }
}
