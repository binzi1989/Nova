using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public sealed record ArtifactRepositorySnapshot(
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ArtifactItem> Artifacts);

public sealed class ArtifactRepositoryService
{
    private const int MaximumArtifacts = 1000;
    private const int MaximumVersionsPerArtifact = 10;
    private const int MaximumPreviewCharacters = 200_000;
    private readonly string _repositoryPath;
    private readonly string _outputRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public ArtifactRepositoryService(
        string? repositoryPath = null,
        string? outputRoot = null)
    {
        var novaRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA");
        _repositoryPath = repositoryPath ?? Path.Combine(novaRoot, "artifacts.json");
        _outputRoot = outputRoot ?? Path.Combine(novaRoot, "outputs");
    }

    public string RepositoryPath => _repositoryPath;
    public string OutputRoot => _outputRoot;

    public ArtifactRepositorySnapshot GetSnapshot()
    {
        if (!File.Exists(_repositoryPath))
        {
            return new ArtifactRepositorySnapshot(DateTimeOffset.MinValue, []);
        }

        try
        {
            return JsonSerializer.Deserialize<ArtifactRepositorySnapshot>(
                       File.ReadAllText(_repositoryPath),
                       _options)
                   ?? new ArtifactRepositorySnapshot(DateTimeOffset.MinValue, []);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new InvalidOperationException(
                $"Unable to read artifact repository '{_repositoryPath}'.",
                exception);
        }
    }

    public IReadOnlyList<ArtifactItem> GetForTask(string taskId)
        => GetSnapshot().Artifacts
            .Where(item => item.TaskId.Equals(taskId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.Version)
                .ThenByDescending(item => item.CreatedAt)
                .First())
            .OrderByDescending(item => item.CreatedAt)
            .ToArray();

    public IReadOnlyList<ArtifactItem> GetVersions(string artifactId)
        => GetSnapshot().Artifacts
            .Where(item => item.Id.Equals(artifactId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Version)
            .ThenByDescending(item => item.CreatedAt)
            .ToArray();

    public IReadOnlyList<ArtifactItem> GetLatest(string? workspaceRoot = null)
    {
        var normalizedWorkspace = string.IsNullOrWhiteSpace(workspaceRoot)
            ? null
            : Path.GetFullPath(workspaceRoot);
        return GetSnapshot().Artifacts
            .Where(item => normalizedWorkspace is null
                           || item.WorkspaceRoot.Equals(
                               normalizedWorkspace,
                               StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.Version)
                .ThenByDescending(item => item.CreatedAt)
                .First())
            .OrderByDescending(item => item.CreatedAt)
            .ToArray();
    }

    public string ListJson(string? workspaceRoot = null, int maximumResults = 50)
    {
        var artifacts = GetLatest(workspaceRoot)
            .Take(Math.Clamp(maximumResults, 1, 200))
            .Select(ToModelRecord)
            .ToArray();
        return JsonSerializer.Serialize(new
        {
            repository_path = _repositoryPath,
            count = artifacts.Length,
            artifacts
        });
    }

    public string ReadJson(string artifactId, int? version = null)
    {
        artifactId = artifactId.Trim();
        var artifacts = GetVersions(artifactId);
        var artifact = version is null
            ? artifacts.FirstOrDefault()
            : artifacts.FirstOrDefault(item => item.Version == version);
        return artifact is null
            ? JsonSerializer.Serialize(new { error = "Artifact was not found.", artifact_id = artifactId, version })
            : JsonSerializer.Serialize(ToModelRecord(artifact));
    }

    public async Task<IReadOnlyList<ArtifactItem>> PersistAsync(
        TaskItem task,
        IReadOnlyList<ArtifactItem> drafts,
        CancellationToken cancellationToken = default)
    {
        if (drafts.Count == 0)
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = GetSnapshot();
            var stored = snapshot.Artifacts.ToList();
            var persisted = new List<ArtifactItem>(drafts.Count);
            foreach (var draft in drafts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateDraft(draft);
                var seriesId = string.IsNullOrWhiteSpace(draft.Id)
                    ? CreateSeriesId(task.Id, draft.Type, draft.Title)
                    : draft.Id;
                var previousVersions = stored
                    .Where(item => item.Id.Equals(seriesId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.Version)
                    .ToArray();
                var previous = previousVersions.FirstOrDefault();
                var preview = NormalizePreview(draft.Preview);
                var sourceLocation = NormalizeExistingLocation(draft.Location);

                if (previous is not null
                    && previous.Preview.Equals(preview, StringComparison.Ordinal)
                    && previous.Subtitle.Equals(draft.Subtitle, StringComparison.Ordinal)
                    && previous.Type.Equals(draft.Type, StringComparison.Ordinal)
                    && File.Exists(previous.Location))
                {
                    persisted.Add(previous);
                    continue;
                }

                var version = previous is null ? 1 : previous.Version + 1;
                var location = sourceLocation
                               ?? await WriteArtifactFileAsync(
                                   task,
                                   draft,
                                   preview,
                                   version,
                                   cancellationToken);
                var item = draft with
                {
                    Preview = preview,
                    Location = location,
                    Id = seriesId,
                    TaskId = task.Id,
                    WorkspaceRoot = Path.GetFullPath(task.WorkspaceRoot),
                    Version = version,
                    CreatedAt = DateTimeOffset.Now
                };
                stored.Add(item);
                persisted.Add(item);
            }

            stored = stored
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .SelectMany(group => group
                    .OrderByDescending(item => item.Version)
                    .Take(MaximumVersionsPerArtifact))
                .OrderByDescending(item => item.CreatedAt)
                .Take(MaximumArtifacts)
                .ToList();
            await SaveAsync(
                new ArtifactRepositorySnapshot(DateTimeOffset.Now, stored),
                cancellationToken);
            return persisted;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> WriteArtifactFileAsync(
        TaskItem task,
        ArtifactItem draft,
        string preview,
        int version,
        CancellationToken cancellationToken)
    {
        var taskDirectory = Path.Combine(_outputRoot, SafeSegment(task.Id));
        Directory.CreateDirectory(taskDirectory);
        var stem = SafeSegment(BuildFileStem(draft));
        if (stem.Length > 64)
        {
            stem = stem[..64];
        }
        var suffix = version > 1 ? $"-v{version}" : string.Empty;
        var path = Path.Combine(taskDirectory, stem + suffix + ".md");
        var content = $"""
                       # {draft.Title}

                       - 类型：{draft.Type}
                       - 任务：{task.Title}
                       - 工作区：{task.WorkspaceRoot}
                       - 版本：v{version}
                       - 生成时间：{DateTimeOffset.Now:O}

                       {draft.Subtitle}

                       ---

                       {preview}
                       """;
        await File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken);
        return Path.GetFullPath(path);
    }

    private string? NormalizeExistingLocation(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }
        try
        {
            var fullPath = Path.GetFullPath(location);
            if (!File.Exists(fullPath))
            {
                return null;
            }
            var allowedRoot = Path.GetFullPath(_outputRoot)
                                  .TrimEnd(Path.DirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private async Task SaveAsync(
        ArtifactRepositorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_repositoryPath)
                        ?? throw new InvalidOperationException("Artifact repository path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _repositoryPath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(snapshot, _options),
            Encoding.UTF8,
            cancellationToken);
        File.Move(temporaryPath, _repositoryPath, overwrite: true);
    }

    private static void ValidateDraft(ArtifactItem draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 160)
        {
            throw new InvalidOperationException("Artifact title must contain 1-160 characters.");
        }
        if (string.IsNullOrWhiteSpace(draft.Type) || draft.Type.Length > 40)
        {
            throw new InvalidOperationException("Artifact type must contain 1-40 characters.");
        }
        if (draft.Preview.Length > MaximumPreviewCharacters)
        {
            throw new InvalidOperationException(
                $"Artifact preview exceeds {MaximumPreviewCharacters:N0} characters.");
        }
    }

    private static string NormalizePreview(string preview)
        => preview.Replace("\0", string.Empty).Trim();

    private static string BuildFileStem(ArtifactItem draft)
    {
        var type = draft.Type.Trim().ToLowerInvariant();
        if (type is "code" or "source" or "patch" or "build" or "test"
            or "engineering" or "project")
        {
            return $"{type}-{draft.Title}";
        }

        var prefix = type switch
        {
            "answer" => "成果",
            "report" => "报告",
            "analysis" => "分析",
            "research" => "调研报告",
            "plan" => "执行方案",
            "evidence" => "证据清单",
            "record" => "任务记录",
            "context" => "上下文摘要",
            "mission" => "目标契约",
            "decision" => "决策报告",
            "brief" => "简报",
            _ => "交付物"
        };
        var title = draft.Title.Trim();
        return title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? title
            : $"{prefix}-{title}";
    }

    private static string CreateSeriesId(string taskId, string type, string title)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{taskId}\n{type}\n{title}"));
        return "artifact-" + Convert.ToHexString(bytes)[..20].ToLowerInvariant();
    }

    private static string SafeSegment(string value)
    {
        var safe = string.Concat(value.Select(character =>
            char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.'
                || character is >= '\u4e00' and <= '\u9fff'
                ? character
                : '-')).Trim('-', '.', ' ');
        return string.IsNullOrWhiteSpace(safe) ? "artifact" : safe;
    }

    private static object ToModelRecord(ArtifactItem artifact)
        => new
        {
            artifact.Id,
            artifact.TaskId,
            artifact.Type,
            artifact.Title,
            artifact.Subtitle,
            artifact.Preview,
            artifact.Location,
            artifact.WorkspaceRoot,
            artifact.Version,
            artifact.CreatedAt
        };
}
