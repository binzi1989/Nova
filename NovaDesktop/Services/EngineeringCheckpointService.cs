using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NovaDesktop.Services;

public sealed record EngineeringCheckpoint(
    string TaskId,
    string Phase,
    string WorkspaceRoot,
    string GitBranch,
    IReadOnlyList<EngineeringChangedFile> ChangedFiles,
    int Additions,
    int Deletions,
    string DiffSha256,
    DateTimeOffset CapturedAt);

public sealed class EngineeringCheckpointService
{
    private readonly string _directory;
    private readonly EngineeringWorkspaceService _workspaceService;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public EngineeringCheckpointService(
        EngineeringWorkspaceService workspaceService,
        string? directory = null)
    {
        _workspaceService = workspaceService;
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "engineering-checkpoints");
    }

    public async Task<EngineeringCheckpoint?> CaptureAsync(
        string taskId,
        string phase,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await _workspaceService.InspectAsync(workspaceRoot, cancellationToken);
            var checkpoint = new EngineeringCheckpoint(
                taskId,
                phase,
                snapshot.WorkspaceRoot,
                snapshot.GitBranch,
                snapshot.ChangedFiles,
                snapshot.Additions,
                snapshot.Deletions,
                Hash(snapshot.Diff),
                DateTimeOffset.Now);
            await SaveAsync(checkpoint, cancellationToken);
            return checkpoint;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException)
        {
            return null;
        }
    }

    public IReadOnlyList<EngineeringCheckpoint> LoadForTask(string taskId)
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var safeTaskId = SafeName(taskId);
        var checkpoints = new List<EngineeringCheckpoint>();
        foreach (var path in Directory.EnumerateFiles(_directory, safeTaskId + "-*.json"))
        {
            try
            {
                var checkpoint = JsonSerializer.Deserialize<EngineeringCheckpoint>(
                    File.ReadAllText(path),
                    _jsonOptions);
                if (checkpoint is not null)
                {
                    checkpoints.Add(checkpoint);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                // Corrupt checkpoints remain isolated.
            }
        }
        return checkpoints.OrderBy(item => item.CapturedAt).ToArray();
    }

    private async Task SaveAsync(
        EngineeringCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            _directory,
            $"{SafeName(checkpoint.TaskId)}-{SafeName(checkpoint.Phase)}.json");
        var temporaryPath = path + ".tmp";
        var json = JsonSerializer.Serialize(checkpoint, _jsonOptions);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_directory);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string SafeName(string value)
    {
        var safe = string.Concat(value.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
        return string.IsNullOrWhiteSpace(safe) ? "checkpoint" : safe;
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
