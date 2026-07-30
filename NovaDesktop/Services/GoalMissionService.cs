using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaDesktop.Services;

public sealed record GoalMissionCharter(
    string TaskId,
    string Title,
    string Outcome,
    string ExecutionKind,
    IReadOnlyList<string> SuccessSignals,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> Unknowns,
    string Strategy,
    IReadOnlyList<string> StopConditions,
    int Confidence,
    string RawResponse,
    string ArtifactPath,
    DateTimeOffset CreatedAt,
    int MissionVersion = 1,
    string MissionHash = "")
{
    public bool RequiresWorkspaceChange
        => ExecutionKind is "BUILD" or "MIXED";

    public string ObjectiveForContract
        => $"""
           Execution requirement: {(RequiresWorkspaceChange ? "开发并实现当前工作区的真实变更。" : "以证据化结果达成目标，不要求工作区变更。")}
           Goal: {Outcome}
           Success signals:
           {string.Join(Environment.NewLine, SuccessSignals.Select(item => $"- {item}"))}
           Constraints:
           {string.Join(Environment.NewLine, Constraints.Select(item => $"- {item}"))}
           Stop conditions:
           {string.Join(Environment.NewLine, StopConditions.Select(item => $"- {item}"))}
           """;
}

public sealed class GoalMissionService
{
    private static readonly Regex ApiKeyPattern = new(
        @"\bsk-[A-Za-z0-9_-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BearerPattern = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AssignedSecretPattern = new(
        @"(?im)\b(api[_-]?key|token|secret|password)\s*[:=]\s*[""']?[^'""\s,;]{6,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ExecutionKinds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "BUILD",
            "RESEARCH",
            "OPERATE",
            "MIXED"
        };

    private readonly string _storageRoot;

    public GoalMissionService(string? storageRoot = null)
    {
        _storageRoot = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "goal-missions");
    }

    public static string BuildDiscoveryPrompt(
        string rawGoal,
        TaskOutcomeContract preliminaryContract,
        EngineeringWorkspaceSnapshot snapshot,
        AdaptiveContextPack? contextPack)
    {
        var paths = contextPack?.Selections
            .Select(item => item.RelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToArray() ?? [];
        return $$"""
                 You are NOVA Goal Explorer. The user intentionally supplied a strong desired outcome with limited clues.
                 Explore the local workspace using read-only tools when useful, distinguish evidence from assumptions,
                 and convert the goal into a measurable Mission Charter. Do not ask preference questions. Do not write
                 files, run commands, use external apps, call MCP, schedule work or delegate.

                 RAW USER GOAL:
                 {{rawGoal}}

                 PRELIMINARY CONTRACT:
                 {{TaskOutcomeContractService.FormatForPrompt(preliminaryContract)}}

                 WORKSPACE:
                 Name: {{snapshot.WorkspaceName}}
                 Git: {{snapshot.IsGitRepository}} · {{snapshot.GitBranch}}
                 Projects: {{string.Join(", ", snapshot.Projects)}}
                 Verification: {{snapshot.VerificationCommand}}
                 High-signal paths: {{string.Join(", ", paths)}}

                 Return JSON only:
                 {
                   "mission_title": "short title",
                   "outcome": "observable end state, not an activity",
                   "execution_kind": "BUILD | RESEARCH | OPERATE | MIXED",
                   "success_signals": [
                     "2 to 8 observable pieces of evidence that prove the outcome"
                   ],
                   "constraints": [
                     "hard constraints from the user, workspace, safety boundary or current evidence"
                   ],
                   "unknowns": [
                     "important unknowns NOVA can investigate without asking the user"
                   ],
                   "strategy": "chosen approach, alternatives rejected and why",
                   "stop_conditions": [
                     "conditions that require user authority, external state, or make further work wasteful"
                   ],
                   "confidence": 0
                 }

                 Rules:
                 - Do not invent business facts, credentials, deployment authority or external access.
                 - Success signals must be testable, inspectable or attributable to evidence.
                 - BUILD means workspace changes are central; RESEARCH means an evidence-backed answer is sufficient;
                   OPERATE means approved external/local app actions are central; MIXED combines material categories.
                 - Unknowns are a research queue, not excuses to stop.
                 - Stop conditions must not include ordinary implementation difficulty.
                 """;
    }

    public static GoalMissionCharter Parse(
        string taskId,
        string rawGoal,
        string response)
    {
        var json = ExtractJson(response);
        var root = JsonNode.Parse(json)?.AsObject()
                   ?? throw new InvalidOperationException(
                       "Goal Explorer returned an empty Mission Charter.");
        var title = Redact(Required(root, "mission_title", 3, 120));
        var outcome = Redact(Required(root, "outcome", 20, 3000));
        var executionKind = Required(root, "execution_kind", 4, 20).ToUpperInvariant();
        if (!ExecutionKinds.Contains(executionKind))
        {
            throw new InvalidOperationException(
                $"Goal Mission execution_kind is unsupported: {executionKind}.");
        }
        var success = ReadList(root["success_signals"], "success_signals", 2, 8, 500)
            .Select(Redact)
            .ToArray();
        var constraints = ReadList(root["constraints"], "constraints", 0, 12, 500)
            .Select(Redact)
            .ToArray();
        var unknowns = ReadList(root["unknowns"], "unknowns", 0, 12, 500)
            .Select(Redact)
            .ToArray();
        var strategy = Redact(Required(root, "strategy", 20, 4000));
        var stop = ReadList(root["stop_conditions"], "stop_conditions", 1, 8, 500)
            .Select(Redact)
            .ToArray();
        var confidence = Math.Clamp(root["confidence"]?.GetValue<int>() ?? 0, 0, 100);
        var charter = new GoalMissionCharter(
            taskId,
            title,
            outcome,
            executionKind,
            success,
            constraints,
            unknowns,
            strategy,
            stop,
            confidence,
            Redact(response),
            string.Empty,
            DateTimeOffset.Now);
        return charter with { MissionHash = ComputeHash(charter) };
    }

    public static GoalMissionCharter Fallback(
        string taskId,
        string rawGoal,
        EngineeringWorkspaceSnapshot snapshot,
        string reason)
    {
        var charter = new GoalMissionCharter(
            taskId,
            "结果导向任务",
            string.IsNullOrWhiteSpace(rawGoal)
                ? "在当前工作区形成一个可检查、可继续推进的真实结果。"
                : rawGoal.Trim()[..Math.Min(rawGoal.Trim().Length, 3000)],
            EngineeringTaskRouter.Classify(rawGoal).IsEngineeringTask
                ? "BUILD"
                : "RESEARCH",
            snapshot.VerificationCommand is
                "NO VERIFICATION TARGET" or "MANUAL VERIFICATION REQUIRED"
                ?
                [
                    "最终交付物能够直接检查，并明确事实、推断和未完成边界。",
                    "结果与用户目标建立清晰对应，不以过程活动代替完成。"
                ]
                :
                [
                    $"工程验证成功：{snapshot.VerificationCommand}",
                    "最终交付物能够直接检查，并与目标建立清晰对应。"
                ],
            ["所有变更与外部操作继续经过 NOVA 权限边界。"],
            [$"结构化 Goal Explorer 不可用：{reason}"],
            "从工作区证据出发，先验证最高风险假设，再选择最短可证明路径。",
            ["需要新的用户权限、外部凭据、不可逆操作或目标之间发生实质冲突。"],
            35,
            string.Empty,
            string.Empty,
            DateTimeOffset.Now);
        return charter with { MissionHash = ComputeHash(charter) };
    }

    public async Task<GoalMissionCharter> SaveAsync(
        GoalMissionCharter charter,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_storageRoot);
        if (!TryGetPath(charter.TaskId, out var path))
        {
            throw new InvalidOperationException(
                "Goal Mission task ID cannot be converted to a safe storage path.");
        }
        var version = Math.Max(1, charter.MissionVersion);
        var saved = charter with
        {
            ArtifactPath = path,
            MissionVersion = version,
            MissionHash = string.Empty
        };
        saved = saved with { MissionHash = ComputeHash(saved) };
        var json = JsonSerializer.Serialize(saved, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(
            temporary,
            json,
            new UTF8Encoding(false),
            cancellationToken);
        File.Move(temporary, path, overwrite: true);
        return saved;
    }

    public GoalMissionCharter? Load(string taskId)
        => TryLoad(taskId, out var charter) ? charter : null;

    public bool TryLoad(
        string taskId,
        out GoalMissionCharter? charter)
    {
        charter = null;
        if (!TryGetPath(taskId, out var path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            if (new FileInfo(path).Length > 1_000_000)
            {
                return false;
            }
            var loaded = JsonSerializer.Deserialize<GoalMissionCharter>(
                File.ReadAllText(path));
            if (!IsValidLoadedCharter(loaded, taskId))
            {
                return false;
            }

            var normalized = loaded! with
            {
                ArtifactPath = path,
                MissionVersion = Math.Max(1, loaded!.MissionVersion)
            };
            var computedHash = ComputeHash(normalized);
            if (!string.IsNullOrWhiteSpace(normalized.MissionHash)
                && (normalized.MissionHash.Trim().Length != 64
                    || !FixedTimeEquals(normalized.MissionHash, computedHash)))
            {
                return false;
            }

            charter = normalized with { MissionHash = computedHash };
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return false;
        }
    }

    public static string ComputeHash(GoalMissionCharter charter)
    {
        ArgumentNullException.ThrowIfNull(charter);
        var canonical = JsonSerializer.Serialize(new
        {
            TaskId = Normalize(charter.TaskId),
            Title = Normalize(charter.Title),
            Outcome = Normalize(charter.Outcome),
            ExecutionKind = Normalize(charter.ExecutionKind).ToUpperInvariant(),
            SuccessSignals = NormalizeList(charter.SuccessSignals),
            Constraints = NormalizeList(charter.Constraints),
            Unknowns = NormalizeList(charter.Unknowns),
            Strategy = Normalize(charter.Strategy),
            StopConditions = NormalizeList(charter.StopConditions),
            Confidence = Math.Clamp(charter.Confidence, 0, 100),
            CreatedAt = charter.CreatedAt.ToUniversalTime().ToString("O"),
            MissionVersion = Math.Max(1, charter.MissionVersion)
        });
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    public static string Format(GoalMissionCharter charter)
        => $"""
           # Goal Mission Charter

           **{charter.Title}**

           Outcome: {charter.Outcome}
           Execution kind: {charter.ExecutionKind}
           Confidence: {charter.Confidence}%

           Success signals:
           {string.Join(Environment.NewLine, charter.SuccessSignals.Select(item => $"- {item}"))}

           Constraints:
           {string.Join(Environment.NewLine, charter.Constraints.Select(item => $"- {item}"))}

           Unknowns NOVA will investigate:
           {string.Join(Environment.NewLine, charter.Unknowns.Select(item => $"- {item}"))}

           Strategy:
           {charter.Strategy}

           Stop conditions:
           {string.Join(Environment.NewLine, charter.StopConditions.Select(item => $"- {item}"))}
           """;

    private static string Required(
        JsonObject root,
        string name,
        int minimum,
        int maximum)
    {
        var value = root[name]?.GetValue<string>()?.Trim() ?? string.Empty;
        if (value.Length < minimum || value.Length > maximum)
        {
            throw new InvalidOperationException(
                $"Goal Mission field '{name}' is missing or outside {minimum}–{maximum} characters.");
        }
        return value;
    }

    private static IReadOnlyList<string> ReadList(
        JsonNode? node,
        string name,
        int minimum,
        int maximum,
        int maximumItemLength)
    {
        var values = node?.AsArray()
                         .Select(item => item?.GetValue<string>()?.Trim())
                         .Where(item => !string.IsNullOrWhiteSpace(item))
                         .Select(item => item!)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .ToArray()
                     ?? [];
        if (values.Length < minimum
            || values.Length > maximum
            || values.Any(item => item.Length > maximumItemLength))
        {
            throw new InvalidOperationException(
                $"Goal Mission list '{name}' violates its size limit.");
        }
        return values;
    }

    private static string ExtractJson(string response)
    {
        response ??= string.Empty;
        var trimmed = response.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine >= 0 && lastFence > firstNewLine)
            {
                trimmed = trimmed[(firstNewLine + 1)..lastFence].Trim();
            }
        }
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException(
                "Goal Explorer did not return a JSON object.");
        }
        var json = trimmed[start..(end + 1)];
        if (json.Length > 80_000)
        {
            throw new InvalidOperationException(
                "Goal Mission Charter exceeds the 80 KB safety limit.");
        }
        try
        {
            JsonDocument.Parse(json);
            return json;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Goal Mission Charter is not valid JSON: {exception.Message}");
        }
    }

    private static string Redact(string value)
        => AssignedSecretPattern.Replace(
            BearerPattern.Replace(
                ApiKeyPattern.Replace(value ?? string.Empty, "[REDACTED_API_KEY]"),
                "Bearer [REDACTED]"),
            "$1=[REDACTED]");

    private bool TryGetPath(string taskId, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return false;
        }

        var safeId = string.Concat(taskId.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-'));
        if (string.IsNullOrWhiteSpace(safeId))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(_storageRoot);
            var candidate = Path.GetFullPath(Path.Combine(root, safeId + ".json"));
            var boundary = root.TrimEnd(
                               Path.DirectorySeparatorChar,
                               Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            path = candidate;
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsValidLoadedCharter(
        GoalMissionCharter? charter,
        string requestedTaskId)
        => charter is not null
           && string.Equals(
               charter.TaskId,
               requestedTaskId,
               StringComparison.Ordinal)
           && !string.IsNullOrWhiteSpace(charter.Title)
           && charter.Title.Length <= 120
           && !string.IsNullOrWhiteSpace(charter.Outcome)
           && charter.Outcome.Length <= 3000
           && ExecutionKinds.Contains(charter.ExecutionKind)
           && charter.SuccessSignals is { Count: >= 2 and <= 8 }
           && charter.SuccessSignals.All(item =>
               !string.IsNullOrWhiteSpace(item) && item.Length <= 500)
           && charter.Constraints is { Count: <= 12 }
           && charter.Constraints.All(item =>
               !string.IsNullOrWhiteSpace(item) && item.Length <= 500)
           && charter.Unknowns is { Count: <= 12 }
           && charter.Unknowns.All(item =>
               !string.IsNullOrWhiteSpace(item) && item.Length <= 500)
           && !string.IsNullOrWhiteSpace(charter.Strategy)
           && charter.Strategy.Length <= 4000
           && charter.StopConditions is { Count: >= 1 and <= 8 }
           && charter.StopConditions.All(item =>
               !string.IsNullOrWhiteSpace(item) && item.Length <= 500)
           && charter.Confidence is >= 0 and <= 100
           && charter.MissionVersion >= 0;

    private static string Normalize(string? value)
        => (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    private static string[] NormalizeList(IReadOnlyList<string>? values)
        => values?.Select(Normalize).ToArray() ?? [];

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left.Trim().ToLowerInvariant());
        var rightBytes = Encoding.UTF8.GetBytes(right.Trim().ToLowerInvariant());
        return leftBytes.Length == rightBytes.Length
               && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
