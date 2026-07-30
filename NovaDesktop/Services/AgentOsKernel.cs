using System.IO;
using System.Text.Json;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public sealed class AgentOsKernel
{
    private const int MaximumEvents = 240;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private readonly string _root;
    private readonly string _statePath;
    private readonly string _eventLedgerPath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };
    private readonly JsonSerializerOptions _ledgerJsonOptions = new();
    private readonly Dictionary<string, AgentOsServiceStatus> _services =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AgentOsEventRecord> _events = [];
    private string _bootId = Guid.NewGuid().ToString("N")[..12];
    private DateTimeOffset _bootedAt = DateTimeOffset.Now;
    private AgentExecutionMode _executionMode = AgentExecutionMode.Build;
    private long _sequence;

    public AgentOsKernel(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "agent-os");
        _statePath = Path.Combine(_root, "kernel-state.json");
        _eventLedgerPath = Path.Combine(_root, "execution-events.jsonl");
    }

    public string Root => _root;

    public async Task<AgentOsKernelSnapshot> BootAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            AgentOsEventRecord bootEvent;
            lock (_stateLock)
            {
                _bootId = Guid.NewGuid().ToString("N")[..12];
                _bootedAt = DateTimeOffset.Now;
                RegisterBuiltInServices();
                bootEvent = AppendEventUnsafe(
                    "kernel",
                    "NOVA AgentOS",
                    "Kernel boot completed.",
                    _bootId,
                    "INFO");
            }
            await AppendLedgerRecordAsync(bootEvent, cancellationToken);
            await PersistAsync(cancellationToken);
            return GetSnapshot();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public AgentOsKernelSnapshot GetSnapshot()
    {
        lock (_stateLock)
        {
            return new AgentOsKernelSnapshot(
                NovaProductVersion.Current,
                _bootId,
                _bootedAt,
                _executionMode,
                _services.Values
                    .OrderBy(service => service.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                _events.TakeLast(80).Reverse().ToArray());
        }
    }

    public async Task SetExecutionModeAsync(
        AgentExecutionMode mode,
        string correlationId = "user",
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            AgentOsEventRecord committed;
            lock (_stateLock)
            {
                if (_executionMode == mode)
                {
                    return;
                }
                _executionMode = mode;
                committed = AppendEventUnsafe(
                    "policy",
                    "Execution Mode",
                    $"Execution mode changed to {mode}.",
                    correlationId,
                    "INFO");
            }
            await AppendLedgerRecordAsync(committed, cancellationToken);
            await PersistAsync(cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task ReportServiceAsync(
        string id,
        string name,
        AgentOsServiceHealth health,
        string detail,
        string correlationId = "system",
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            AgentOsEventRecord committed;
            lock (_stateLock)
            {
                _services[id] = new AgentOsServiceStatus(
                    id,
                    name,
                    health,
                    detail,
                    DateTimeOffset.Now);
                committed = AppendEventUnsafe(
                    "service",
                    name,
                    $"{health}: {detail}",
                    correlationId,
                    health is AgentOsServiceHealth.Degraded or AgentOsServiceHealth.Offline
                        ? "WARN"
                        : "INFO");
            }
            await AppendLedgerRecordAsync(committed, cancellationToken);
            await PersistAsync(cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<AgentOsEventRecord> PublishEventAsync(
        string category,
        string source,
        string message,
        string correlationId,
        string severity = "INFO",
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            AgentOsEventRecord committed;
            lock (_stateLock)
            {
                committed = AppendEventUnsafe(
                    category,
                    source,
                    message,
                    correlationId,
                    severity);
            }
            await AppendLedgerRecordAsync(committed, cancellationToken);
            await PersistAsync(cancellationToken);
            return committed;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<AgentOsEventRecord> PublishTaskEventAsync(
        string category,
        string source,
        string message,
        TaskItem task,
        string severity = "INFO",
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            AgentOsEventRecord committed;
            lock (_stateLock)
            {
                committed = AppendEventUnsafe(
                    category,
                    source,
                    message,
                    task.Id,
                    severity) with
                {
                    TaskState = task.State,
                    Progress = task.Progress,
                    Stage = task.Stage
                };
                _events[^1] = committed;
            }
            await AppendLedgerRecordAsync(committed, cancellationToken);
            await PersistAsync(cancellationToken);
            task.ExecutionSequence = committed.Sequence;
            return committed;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void RegisterBuiltInServices()
    {
        var now = DateTimeOffset.Now;
        foreach (var service in new[]
                 {
                     new AgentOsServiceStatus("runtime", "Model Runtime", AgentOsServiceHealth.Starting, "Awaiting provider probe", now),
                     new AgentOsServiceStatus("workspace", "Workspace Router", AgentOsServiceHealth.Ready, "Workspace boundary active", now),
                     new AgentOsServiceStatus("memory", "Conversation Memory", AgentOsServiceHealth.Ready, "Durable thread memory mounted", now),
                     new AgentOsServiceStatus("tools", "Tool Broker", AgentOsServiceHealth.Ready, "Approval policy active", now),
                     new AgentOsServiceStatus("mcp", "MCP Fabric", AgentOsServiceHealth.Ready, "Registry mounted", now),
                     new AgentOsServiceStatus("skills", "Skill Fabric", AgentOsServiceHealth.Ready, "Skill registry mounted", now),
                     new AgentOsServiceStatus("scheduler", "Agent Scheduler", AgentOsServiceHealth.Ready, "Resource governor active", now),
                     new AgentOsServiceStatus("supervisor", "Agent Supervisor", AgentOsServiceHealth.Starting, "Durable lease layer booting", now),
                     new AgentOsServiceStatus("evidence", "Evidence Ledger", AgentOsServiceHealth.Ready, "Audit chain active", now)
                 })
        {
            if (!_services.ContainsKey(service.Id))
            {
                _services[service.Id] = service;
            }
        }
    }

    public IReadOnlyList<AgentOsEventRecord> ReadExecutionEvents(
        string? correlationId = null,
        long afterSequence = 0,
        int maximumEntries = 5000)
    {
        var entries = new List<AgentOsEventRecord>();
        if (File.Exists(_eventLedgerPath))
        {
            try
            {
                foreach (var line in File.ReadLines(_eventLedgerPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }
                    try
                    {
                        var entry = JsonSerializer.Deserialize<AgentOsEventRecord>(
                            line,
                            _ledgerJsonOptions);
                        if (entry is not null
                            && entry.Sequence > afterSequence
                            && (string.IsNullOrWhiteSpace(correlationId)
                                || entry.CorrelationId.Equals(
                                    correlationId,
                                    StringComparison.OrdinalIgnoreCase)))
                        {
                            entries.Add(entry);
                        }
                    }
                    catch (JsonException)
                    {
                        // A torn final record is isolated; prior committed events remain replayable.
                    }
                }
            }
            catch (IOException)
            {
                // The in-memory tail remains available while another projection is flushing.
            }
        }

        lock (_stateLock)
        {
            entries.AddRange(_events.Where(entry =>
                entry.Sequence > afterSequence
                && (string.IsNullOrWhiteSpace(correlationId)
                    || entry.CorrelationId.Equals(
                        correlationId,
                        StringComparison.OrdinalIgnoreCase))));
        }

        return entries
            .GroupBy(entry => entry.Sequence)
            .Select(group => group.Last())
            .OrderBy(entry => entry.Sequence)
            .TakeLast(Math.Clamp(maximumEntries, 1, 20_000))
            .ToArray();
    }

    public AgentOsEventRecord? GetTaskProjection(string taskId)
        => ReadExecutionEvents(taskId)
            .LastOrDefault(entry => entry.TaskState is not null);

    private AgentOsEventRecord AppendEventUnsafe(
        string category,
        string source,
        string message,
        string correlationId,
        string severity)
    {
        var committed = new AgentOsEventRecord(
            ++_sequence,
            DateTimeOffset.Now,
            category,
            source,
            message,
            correlationId,
            severity);
        _events.Add(committed);
        if (_events.Count > MaximumEvents)
        {
            _events.RemoveRange(0, _events.Count - MaximumEvents);
        }
        return committed;
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
        {
            ReconcileLedgerUnsafe();
            return;
        }
        try
        {
            var json = await File.ReadAllTextAsync(_statePath, cancellationToken);
            var state = JsonSerializer.Deserialize<PersistedKernelState>(json, _jsonOptions);
            if (state is null)
            {
                return;
            }
            lock (_stateLock)
            {
                _executionMode = state.ExecutionMode;
                _sequence = state.Sequence;
                _services.Clear();
                foreach (var service in state.Services ?? [])
                {
                    _services[service.Id] = service;
                }
                _events.Clear();
                _events.AddRange((state.Events ?? []).TakeLast(MaximumEvents));
            }
            ReconcileLedgerUnsafe();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            ReconcileLedgerUnsafe();
            lock (_stateLock)
            {
                AppendEventUnsafe(
                    "kernel",
                    "State Loader",
                    $"Persistent state could not be loaded: {exception.Message}",
                    _bootId,
                    "WARN");
            }
        }
    }

    private void ReconcileLedgerUnsafe()
    {
        if (!File.Exists(_eventLedgerPath))
        {
            return;
        }

        var restored = new List<AgentOsEventRecord>();
        try
        {
            foreach (var line in File.ReadLines(_eventLedgerPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                try
                {
                    var record = JsonSerializer.Deserialize<AgentOsEventRecord>(
                        line,
                        _ledgerJsonOptions);
                    if (record is not null)
                    {
                        restored.Add(record);
                    }
                }
                catch (JsonException)
                {
                    // Ignore a torn record and preserve the last complete monotonic prefix.
                }
            }
        }
        catch (IOException)
        {
            return;
        }

        lock (_stateLock)
        {
            var merged = _events
                .Concat(restored)
                .GroupBy(record => record.Sequence)
                .Select(group => group.Last())
                .OrderBy(record => record.Sequence)
                .ToArray();
            _sequence = Math.Max(_sequence, merged.LastOrDefault()?.Sequence ?? 0);
            _events.Clear();
            _events.AddRange(merged.TakeLast(MaximumEvents));
        }
    }

    private async Task AppendLedgerRecordAsync(
        AgentOsEventRecord record,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var payload = JsonSerializer.Serialize(record, _ledgerJsonOptions)
                      + Environment.NewLine;
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
        await using var stream = new FileStream(
            _eventLedgerPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        PersistedKernelState state;
        lock (_stateLock)
        {
            state = new PersistedKernelState(
                _executionMode,
                _sequence,
                _services.Values.ToArray(),
                _events.ToArray());
        }

        await _persistenceGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_root);
            var temporary = _statePath + ".tmp";
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(state, _jsonOptions),
                cancellationToken);
            File.Move(temporary, _statePath, overwrite: true);
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private sealed record PersistedKernelState(
        AgentExecutionMode ExecutionMode,
        long Sequence,
        IReadOnlyList<AgentOsServiceStatus>? Services,
        IReadOnlyList<AgentOsEventRecord>? Events);
}
