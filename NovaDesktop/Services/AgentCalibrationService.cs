using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovaDesktop.Services;

public sealed record AgentCalibrationPatch(
    string Id,
    string PackId,
    int Version,
    string Scope,
    string ScopeKey,
    string ScopeLabel,
    string Category,
    string Instruction,
    string? SourceTaskId,
    string? SourceTitle,
    string? SourcePath,
    string State,
    string RegressionStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AgentCalibrationSnapshot(
    string PackId,
    int Version,
    int ActiveCount,
    IReadOnlyList<AgentCalibrationPatch> Patches);

public sealed record CreateAgentCalibrationRequest(
    string PackId,
    string Scope,
    string Category,
    string Instruction,
    string? TaskId,
    string? WorkspaceRoot,
    string? SourceTitle,
    string? SourcePath);

/// <summary>
/// Stores user-approved Agent corrections as versioned overlays. Original Agent
/// Pack files are never edited, so every calibration remains inspectable and
/// reversible.
/// </summary>
public sealed class AgentCalibrationService
{
    private static readonly Regex SafePackId = new(
        "^[a-z0-9][a-z0-9.-]{2,79}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Scopes =
        ["turn", "project", "agent", "organization"];
    private static readonly HashSet<string> Categories =
        ["fact", "judgment", "workflow", "format", "evidence", "permission", "tone", "other"];
    private readonly string _statePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public AgentCalibrationService(string? statePath = null)
    {
        _statePath = statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "agent-packs",
            "calibrations.json");
    }

    public AgentCalibrationSnapshot GetSnapshot(string packId)
    {
        ValidatePackId(packId);
        var patches = ReadState().Patches
            .Where(patch => patch.PackId.Equals(packId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(patch => patch.Version)
            .ToArray();
        return new AgentCalibrationSnapshot(
            packId,
            patches.Select(patch => patch.Version).DefaultIfEmpty(0).Max(),
            patches.Count(patch => patch.State == "active"),
            patches);
    }

    public async Task<AgentCalibrationSnapshot> CreateAsync(
        CreateAgentCalibrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = ReadState();
            var version = state.Patches
                .Where(patch => patch.PackId.Equals(request.PackId, StringComparison.OrdinalIgnoreCase))
                .Select(patch => patch.Version)
                .DefaultIfEmpty(0)
                .Max() + 1;
            var (scopeKey, scopeLabel) = ResolveScope(request);
            var now = DateTimeOffset.UtcNow;
            state.Patches.Add(new AgentCalibrationPatch(
                $"cal-{Guid.NewGuid():N}",
                request.PackId,
                version,
                request.Scope,
                scopeKey,
                scopeLabel,
                request.Category,
                request.Instruction.Trim(),
                CleanOptional(request.TaskId, 96),
                CleanOptional(request.SourceTitle, 160),
                CleanOptional(request.SourcePath, 500),
                "active",
                "pending",
                now,
                now));
            await WriteStateAsync(state, cancellationToken);
            return ProjectSnapshot(request.PackId, state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AgentCalibrationSnapshot> RollbackAsync(
        string packId,
        string patchId,
        CancellationToken cancellationToken = default)
    {
        ValidatePackId(packId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = ReadState();
            var index = state.Patches.FindIndex(patch =>
                patch.Id.Equals(patchId, StringComparison.OrdinalIgnoreCase)
                && patch.PackId.Equals(packId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new InvalidOperationException("没有找到这条 Agent 校准记录。");
            }
            var current = state.Patches[index];
            state.Patches[index] = current with
            {
                State = current.State == "active" ? "rolled-back" : "active",
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await WriteStateAsync(state, cancellationToken);
            return ProjectSnapshot(packId, state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public string BuildRuntimeContext(
        string? packId,
        string? taskId,
        string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(packId))
        {
            return string.Empty;
        }
        var projectKey = ProjectKey(workspaceRoot);
        var applicable = ReadState().Patches
            .Where(patch => patch.State == "active"
                            && patch.PackId.Equals(packId, StringComparison.OrdinalIgnoreCase)
                            && ScopeMatches(patch, taskId, projectKey))
            .OrderBy(patch => ScopeWeight(patch.Scope))
            .ThenBy(patch => patch.Version)
            .ToArray();
        if (applicable.Length == 0)
        {
            return string.Empty;
        }
        var builder = new StringBuilder();
        builder.AppendLine("[NOVA AGENT CALIBRATION OVERLAY]");
        builder.AppendLine("These are explicit, versioned user corrections. They override conflicting Pack defaults but never override AgentOS safety, permission, workspace, budget or Proof-of-Done rules.");
        builder.AppendLine("Apply only where relevant. Preserve confirmed facts and do not reinterpret a correction as broader authority.");
        foreach (var patch in applicable)
        {
            builder.AppendLine($"- v{patch.Version} · {patch.Scope} · {patch.Category}: {patch.Instruction}");
        }
        return builder.ToString();
    }

    private static bool ScopeMatches(AgentCalibrationPatch patch, string? taskId, string projectKey)
        => patch.Scope switch
        {
            "turn" => !string.IsNullOrWhiteSpace(taskId)
                      && patch.ScopeKey.Equals(taskId, StringComparison.OrdinalIgnoreCase),
            "project" => patch.ScopeKey.Equals(projectKey, StringComparison.Ordinal),
            "agent" or "organization" => true,
            _ => false
        };

    private static int ScopeWeight(string scope) => scope switch
    {
        "organization" => 0,
        "agent" => 1,
        "project" => 2,
        "turn" => 3,
        _ => 0
    };

    private static (string Key, string Label) ResolveScope(CreateAgentCalibrationRequest request)
        => request.Scope switch
        {
            "turn" => (Required(request.TaskId, "本轮校准需要当前任务。"), "当前任务"),
            "project" => (ProjectKey(Required(request.WorkspaceRoot, "项目校准需要当前工作区。")),
                Path.GetFileName(Path.TrimEndingDirectorySeparator(request.WorkspaceRoot!)) is { Length: > 0 } name ? name : "当前项目"),
            "agent" => (request.PackId, "该 Agent"),
            "organization" => ("local-organization", "组织版本（本机）"),
            _ => throw new InvalidOperationException("不支持的校准作用域。")
        };

    private static string ProjectKey(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return "no-workspace";
        }
        var normalized = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..20];
    }

    private static void ValidateRequest(CreateAgentCalibrationRequest request)
    {
        ValidatePackId(request.PackId);
        if (!Scopes.Contains(request.Scope))
            throw new InvalidOperationException("校准范围必须是本轮、项目、Agent 或组织版本。");
        if (!Categories.Contains(request.Category))
            throw new InvalidOperationException("请选择有效的校准类型。");
        if (string.IsNullOrWhiteSpace(request.Instruction)
            || request.Instruction.Trim().Length is < 4 or > 2000)
            throw new InvalidOperationException("请用 4-2000 字清楚描述希望 Agent 如何改变。");
        if (request.Scope == "turn" && string.IsNullOrWhiteSpace(request.TaskId))
            throw new InvalidOperationException("本轮校准需要当前任务。");
        if (request.Scope == "project" && string.IsNullOrWhiteSpace(request.WorkspaceRoot))
            throw new InvalidOperationException("项目校准需要当前工作区。");
    }

    private static void ValidatePackId(string packId)
    {
        if (!SafePackId.IsMatch(packId ?? string.Empty))
            throw new InvalidOperationException("Agent Pack ID 无效。");
    }

    private CalibrationState ReadState()
    {
        try
        {
            return File.Exists(_statePath)
                ? JsonSerializer.Deserialize<CalibrationState>(File.ReadAllText(_statePath), _json) ?? new CalibrationState()
                : new CalibrationState();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new CalibrationState();
        }
    }

    private async Task WriteStateAsync(CalibrationState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temporary = _statePath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, _json), cancellationToken);
        File.Move(temporary, _statePath, overwrite: true);
    }

    private static AgentCalibrationSnapshot ProjectSnapshot(string packId, CalibrationState state)
    {
        var patches = state.Patches
            .Where(patch => patch.PackId.Equals(packId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(patch => patch.Version)
            .ToArray();
        return new AgentCalibrationSnapshot(
            packId,
            patches.Select(patch => patch.Version).DefaultIfEmpty(0).Max(),
            patches.Count(patch => patch.State == "active"),
            patches);
    }

    private static string Required(string? value, string message)
        => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value.Trim();

    private static string? CleanOptional(string? value, int maxLength)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maxLength)];

    private sealed class CalibrationState
    {
        public List<AgentCalibrationPatch> Patches { get; init; } = [];
    }
}
