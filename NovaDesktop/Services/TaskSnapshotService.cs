using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public sealed record TaskSnapshot(
    string TaskId,
    string Title,
    string Prompt,
    string WorkspaceRoot,
    string Provider,
    string Model,
    TaskState State,
    double Progress,
    string Stage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    AgentExecutionMode ExecutionMode = AgentExecutionMode.Build,
    string Draft = "",
    IReadOnlyList<AgentInputAttachment>? Attachments = null,
    bool IsArchived = false,
    long ExecutionSequence = 0);

public sealed class TaskSnapshotService
{
    private readonly string _snapshotDirectory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public TaskSnapshotService(string? snapshotDirectory = null)
    {
        _snapshotDirectory = snapshotDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "tasks");
    }

    public async Task SaveAsync(TaskItem task, CancellationToken cancellationToken = default)
    {
        var snapshot = new TaskSnapshot(
            task.Id,
            task.Title,
            task.Description,
            task.WorkspaceRoot,
            task.Provider,
            task.Model,
            task.State,
            task.Progress,
            task.Stage,
            task.CreatedAt,
            DateTimeOffset.Now,
            task.ExecutionMode,
            task.Draft,
            task.Attachments,
            task.IsArchived,
            task.ExecutionSequence);
        var path = GetPath(task.Id);
        var temporaryPath = path + ".tmp";
        var json = JsonSerializer.Serialize(snapshot, _jsonOptions);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_snapshotDirectory);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public IReadOnlyList<TaskSnapshot> LoadAll()
    {
        if (!Directory.Exists(_snapshotDirectory))
        {
            return [];
        }
        var snapshots = new List<TaskSnapshot>();
        foreach (var path in Directory.EnumerateFiles(_snapshotDirectory, "*.json"))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<TaskSnapshot>(
                    File.ReadAllText(path),
                    _jsonOptions);
                if (snapshot is not null)
                {
                    snapshots.Add(snapshot);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                // A corrupt snapshot is isolated from the rest of the history.
            }
        }
        return snapshots.OrderByDescending(snapshot => snapshot.UpdatedAt).ToArray();
    }

    public IReadOnlyList<TaskSnapshot> LoadRecoverable()
        => LoadAll()
            .Where(snapshot => !snapshot.IsArchived
                && snapshot.State is TaskState.Running
                or TaskState.Paused
                or TaskState.Waiting
                or TaskState.BudgetExhausted
                or TaskState.Failed
                or TaskState.Stale)
            .Select(snapshot => snapshot with
            {
                State = snapshot.State == TaskState.Stale
                    ? TaskState.Stale
                    : TaskState.Paused,
                Stage = snapshot.State switch
                {
                    TaskState.Stale => "证据已过期 · 可重新验证",
                    TaskState.Failed => "可从失败目标重试",
                    TaskState.BudgetExhausted => "可从预算安全点继续",
                    _ => "可从快照恢复"
                }
            })
            .ToArray();

    private string GetPath(string taskId)
    {
        var safeName = string.Concat(taskId.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
        if (safeName.Length == 0)
        {
            throw new InvalidOperationException("Task ID cannot be converted to a safe snapshot name.");
        }
        return Path.Combine(_snapshotDirectory, safeName + ".json");
    }
}
