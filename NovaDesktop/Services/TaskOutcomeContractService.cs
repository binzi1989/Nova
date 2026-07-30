using System.Text;
using System.Text.Json;
using System.IO;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public sealed record TaskOutcomeCriterion(
    string Id,
    string Title,
    string RequiredEvidence,
    int Weight);

public sealed record TaskOutcomeContract(
    string TaskId,
    string Objective,
    AgentExecutionMode ExecutionMode,
    bool RequiresWorkspaceMutation,
    bool WorkspaceRecognized,
    string VerificationCommand,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<TaskOutcomeCriterion> Criteria,
    DateTimeOffset CreatedAt);

public sealed record TaskOutcomeCriterionResult(
    string Id,
    string Title,
    string Status,
    int Weight,
    string Evidence);

public sealed record TaskOutcomeAssessment(
    string TaskId,
    string Status,
    int ProofScore,
    IReadOnlyList<TaskOutcomeCriterionResult> Criteria,
    DateTimeOffset AssessedAt,
    string ArtifactPath);

public sealed class TaskOutcomeContractService
{
    private readonly EngineeringWorkspaceService _workspaceService;
    private readonly string _storageRoot;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public TaskOutcomeContractService(
        EngineeringWorkspaceService workspaceService,
        string? storageRoot = null)
    {
        _workspaceService = workspaceService;
        _storageRoot = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "outcome-contracts");
    }

    public async Task<TaskOutcomeContract> CreateAsync(
        string taskId,
        string objective,
        AgentExecutionMode executionMode,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _workspaceService.InspectAsync(workspaceRoot, cancellationToken);
        return await CreateAsync(
            taskId,
            objective,
            executionMode,
            snapshot,
            cancellationToken);
    }

    public async Task<TaskOutcomeContract> CreateAsync(
        string taskId,
        string objective,
        AgentExecutionMode executionMode,
        EngineeringWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var contract = Create(taskId, objective, executionMode, snapshot);
        await SaveAsync(
            ContractPath(taskId),
            contract,
            cancellationToken);
        return contract;
    }

    public async Task<TaskOutcomeContract> CreateGoalAsync(
        string taskId,
        GoalMissionCharter mission,
        EngineeringWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var contract = CreateGoal(taskId, mission, snapshot);
        await SaveAsync(
            ContractPath(taskId),
            contract,
            cancellationToken);
        return contract;
    }

    public static TaskOutcomeContract CreateGoal(
        string taskId,
        GoalMissionCharter mission,
        EngineeringWorkspaceSnapshot snapshot)
    {
        var contract = Create(
            taskId,
            mission.ObjectiveForContract,
            AgentExecutionMode.Goal,
            snapshot);
        var fixedCriteria = contract.RequiresWorkspaceMutation
            ?
            new TaskOutcomeCriterion[]
            {
                new("mission-charter", "目标已冻结为可验证 Mission",
                    "Mission Charter 已持久化且包含稳定成功信号。", 8),
                new("workspace-evidence", "工作区证据已建立",
                    "工作区边界和事实来源已经识别。", 7),
                new("material-change", "真实修改已经落盘",
                    "至少一次经过授权且成功的文件变更。", 10),
                new("verification", "工程验证通过",
                    $"验证命令成功退出：{snapshot.VerificationCommand}", 15),
                new("local-review", "本地审查达到交付线",
                    "审查分数不低于 80 且没有 HIGH 风险。", 8),
                new("independent-council", "独立验证 Agent 完成裁决",
                    "只读 Council 返回可解析裁决和逐信号证据矩阵。", 7),
                new("final-response", "交付说明存在",
                    "最终说明非空；它只证明交付格式，不证明目标已经达成。", 5)
            }
            :
            new TaskOutcomeCriterion[]
            {
                new("mission-charter", "目标已冻结为可验证 Mission",
                    "Mission Charter 已持久化且包含稳定成功信号。", 10),
                new("workspace-evidence", "工作区证据已建立",
                    "工作区边界和事实来源已经识别。", 10),
                new("independent-council", "独立验证 Agent 完成裁决",
                    "只读 Council 返回可解析裁决和逐信号证据矩阵。", 15),
                new("final-response", "交付说明存在",
                    "最终说明非空；它只证明交付格式，不证明目标已经达成。", 5)
            };
        var signalWeight = contract.RequiresWorkspaceMutation ? 40 : 60;
        var perSignal = signalWeight / mission.SuccessSignals.Count;
        var remainder = signalWeight % mission.SuccessSignals.Count;
        var signalCriteria = mission.SuccessSignals
            .Select((signal, index) => new TaskOutcomeCriterion(
                $"goal-signal-{index + 1:D2}",
                $"成功信号 {index + 1}：{signal}",
                $"独立 Council 必须返回 SIGNAL {index + 1}: PASS，"
                + "并给出可复核证据；缺失或只有模型声明一律视为 UNVERIFIED。",
                perSignal + (index < remainder ? 1 : 0)))
            .ToArray();
        return contract with
        {
            Criteria = fixedCriteria.Concat(signalCriteria).ToArray()
        };
    }

    public static TaskOutcomeContract Create(
        string taskId,
        string objective,
        AgentExecutionMode executionMode,
        EngineeringWorkspaceSnapshot snapshot)
    {
        var boundedObjective = string.IsNullOrWhiteSpace(objective)
            ? "完成当前工程任务"
            : objective.Trim()[..Math.Min(objective.Trim().Length, 6000)];
        var requiresMutation = AgentExecutionPolicy.CanMutateWorkspace(executionMode)
                               && EngineeringTaskRouter.RequiresWorkspaceMutation(boundedObjective);
        var workspaceRecognized = snapshot.Projects.Count > 0
                                  || executionMode == AgentExecutionMode.Goal
                                  && Directory.Exists(snapshot.WorkspaceRoot);
        IReadOnlyList<TaskOutcomeCriterion> criteria = requiresMutation
            ? executionMode == AgentExecutionMode.Goal
                ?
                new TaskOutcomeCriterion[]
                {
                    new TaskOutcomeCriterion(
                        "mission-charter",
                        "目标已冻结为可验证 Mission",
                        "包含可观察结果、成功信号、约束、未知项与停止条件",
                        15),
                    new TaskOutcomeCriterion(
                        "workspace-evidence",
                        "工作区证据已建立",
                        "识别到工程清单并以工作区内容为事实来源",
                        10),
                    new TaskOutcomeCriterion(
                        "material-change",
                        "真实修改已经落盘",
                        "至少一次经授权且成功的文件变更",
                        20),
                    new TaskOutcomeCriterion(
                        "verification",
                        "结果验证通过",
                        $"命令成功退出：{snapshot.VerificationCommand}",
                        25),
                    new TaskOutcomeCriterion(
                        "local-review",
                        "本地审查达到交付线",
                        "审查分数不低于 80 且没有 HIGH 风险",
                        10),
                    new TaskOutcomeCriterion(
                        "independent-council",
                        "独立验证 Agent 通过",
                        "后验只读 Council 返回结构化 PASS 裁决",
                        10),
                    new TaskOutcomeCriterion(
                        "final-response",
                        "结果与目标建立证据对应",
                        "最终说明逐项覆盖成功信号与剩余边界",
                        10)
                }
            : executionMode == AgentExecutionMode.Autopilot
                ?
                new TaskOutcomeCriterion[]
                {
                    new TaskOutcomeCriterion(
                        "workspace-evidence",
                        "工作区证据已建立",
                        "识别到工程清单并以工作区内容为事实来源",
                        10),
                    new TaskOutcomeCriterion(
                        "material-change",
                        "真实修改已经落盘",
                        "至少一次经审批且成功的文件变更工具调用",
                        25),
                    new TaskOutcomeCriterion(
                        "verification",
                        "工程验证通过",
                        $"命令成功退出：{snapshot.VerificationCommand}",
                        30),
                    new TaskOutcomeCriterion(
                        "local-review",
                        "本地审查达到交付线",
                        "审查分数不低于 80 且没有 HIGH 风险",
                        10),
                    new TaskOutcomeCriterion(
                        "independent-council",
                        "独立验证 Agent 通过",
                        "后验只读 Council 返回结构化 PASS 裁决",
                        15),
                    new TaskOutcomeCriterion(
                        "final-response",
                        "交付说明完整",
                        "主 Agent 返回非空最终说明与证据摘要",
                        10)
                }
                :
                new TaskOutcomeCriterion[]
                {
                    new TaskOutcomeCriterion(
                        "workspace-evidence",
                        "工作区证据已建立",
                        "识别到工程清单并以工作区内容为事实来源",
                        15),
                    new TaskOutcomeCriterion(
                        "material-change",
                        "真实修改已经落盘",
                        "至少一次经审批且成功的文件变更工具调用",
                        30),
                    new TaskOutcomeCriterion(
                        "verification",
                        "工程验证通过",
                        $"命令成功退出：{snapshot.VerificationCommand}",
                        35),
                    new TaskOutcomeCriterion(
                        "local-review",
                        "本地审查达到交付线",
                        "审查分数不低于 80 且没有 HIGH 风险",
                        10),
                    new TaskOutcomeCriterion(
                        "final-response",
                        "交付说明完整",
                        "主 Agent 返回非空最终说明与证据摘要",
                        10)
                }
            : executionMode == AgentExecutionMode.Goal
                ?
                new TaskOutcomeCriterion[]
                {
                    new TaskOutcomeCriterion(
                        "mission-charter",
                        "目标已冻结为可验证 Mission",
                        "包含可观察结果、成功信号、约束、未知项与停止条件",
                        35),
                    new TaskOutcomeCriterion(
                        "workspace-evidence",
                        "工作区证据已建立",
                        "工作区边界和事实来源已经识别",
                        20),
                    new TaskOutcomeCriterion(
                        "final-response",
                        "目标得到证据化结果",
                        "最终说明逐项覆盖成功信号并区分事实、推断和边界",
                        45)
                }
                :
            new TaskOutcomeCriterion[]
            {
                new TaskOutcomeCriterion(
                    "workspace-evidence",
                    "工作区证据已建立",
                    "识别到工程清单并以工作区内容为事实来源",
                    40),
                new TaskOutcomeCriterion(
                    "final-response",
                    "目标得到可验证回答",
                    "主 Agent 返回非空最终说明并区分事实与推断",
                    60)
            };

        return new TaskOutcomeContract(
            taskId,
            boundedObjective,
            executionMode,
            requiresMutation,
            workspaceRecognized,
            snapshot.VerificationCommand,
            ExtractConstraints(boundedObjective),
            criteria,
            DateTimeOffset.Now);
    }

    public async Task<TaskOutcomeAssessment> AssessAsync(
        TaskOutcomeContract contract,
        AgentRunResult result,
        bool verificationAttempted,
        bool verificationPassed,
        EngineeringCodeReviewResult? review,
        CancellationToken cancellationToken = default)
        => await AssessAsync(
            contract,
            result,
            verificationAttempted,
            verificationPassed,
            review,
            null,
            cancellationToken);

    public async Task<TaskOutcomeAssessment> AssessAsync(
        TaskOutcomeContract contract,
        AgentRunResult result,
        bool verificationAttempted,
        bool verificationPassed,
        EngineeringCodeReviewResult? review,
        VerificationCouncilResult? council,
        CancellationToken cancellationToken = default)
    {
        var artifactPath = AssessmentPath(contract.TaskId);
        var assessment = Assess(
            contract,
            result,
            verificationAttempted,
            verificationPassed,
            review,
            council,
            artifactPath);
        await SaveAsync(artifactPath, assessment, cancellationToken);
        return assessment;
    }

    public static TaskOutcomeAssessment Assess(
        TaskOutcomeContract contract,
        AgentRunResult result,
        bool verificationAttempted,
        bool verificationPassed,
        EngineeringCodeReviewResult? review,
        string artifactPath = "")
        => Assess(
            contract,
            result,
            verificationAttempted,
            verificationPassed,
            review,
            null,
            artifactPath);

    public static TaskOutcomeAssessment Assess(
        TaskOutcomeContract contract,
        AgentRunResult result,
        bool verificationAttempted,
        bool verificationPassed,
        EngineeringCodeReviewResult? review,
        VerificationCouncilResult? council,
        string artifactPath = "")
    {
        var results = contract.Criteria.Select(criterion =>
        {
            var (status, evidence) = criterion.Id.StartsWith(
                "goal-signal-",
                StringComparison.Ordinal)
                ? EvaluateGoalSignal(criterion.Id, council)
                : criterion.Id switch
            {
                "workspace-evidence" => contract.WorkspaceRecognized
                    ? ("PASS", "已识别工程清单，运行上下文绑定到真实工作区。")
                    : ("UNVERIFIED", "未识别到工程清单，无法证明工作区边界。"),
                "material-change" => result.MutatingToolCalls > 0
                    ? ("PASS", $"{result.MutatingToolCalls} 次文件变更工具调用已成功。")
                    : ("FAIL", "没有成功的文件变更工具调用。"),
                "verification" => EvaluateVerification(
                    contract.VerificationCommand,
                    verificationAttempted,
                    verificationPassed),
                "local-review" => EvaluateReview(review),
                "independent-council" => EvaluateCouncil(council),
                "mission-charter" => contract.ExecutionMode == AgentExecutionMode.Goal
                                     && contract.Objective.Contains(
                                         "Success signals:",
                                         StringComparison.Ordinal)
                    ? ("PASS", "Mission Charter 已持久化并编入完成契约。")
                    : ("UNVERIFIED", "没有结构化 Mission Charter 证据。"),
                "final-response" => string.IsNullOrWhiteSpace(result.FinalText)
                    ? ("FAIL", "主 Agent 没有返回最终说明。")
                    : ("PASS", $"最终说明包含 {result.FinalText.Length:N0} 个字符。"),
                _ => ("UNVERIFIED", "没有可用的证据判定器。")
            };
            return new TaskOutcomeCriterionResult(
                criterion.Id,
                criterion.Title,
                status,
                criterion.Weight,
                evidence);
        }).ToArray();

        var totalWeight = Math.Max(1, results.Sum(item => item.Weight));
        var passedWeight = results
            .Where(item => item.Status == "PASS")
            .Sum(item => item.Weight);
        var score = (int)Math.Round(passedWeight * 100d / totalWeight);
        var status = results.Any(item => item.Status == "FAIL")
            ? "FAILED"
            : results.Any(item => item.Status == "UNVERIFIED")
                ? "PARTIAL"
                : "PROVEN";
        return new TaskOutcomeAssessment(
            contract.TaskId,
            status,
            score,
            results,
            DateTimeOffset.Now,
            artifactPath);
    }

    public static string FormatForPrompt(TaskOutcomeContract contract)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[NOVA PROOF-OF-DONE CONTRACT]");
        builder.AppendLine($"Objective: {contract.Objective}");
        builder.AppendLine($"Mutation required: {contract.RequiresWorkspaceMutation}");
        builder.AppendLine("Completion criteria:");
        foreach (var criterion in contract.Criteria)
        {
            builder.AppendLine(
                $"- {criterion.Title} ({criterion.Weight}%): {criterion.RequiredEvidence}");
        }
        if (contract.Constraints.Count > 0)
        {
            builder.AppendLine("Explicit constraints:");
            foreach (var constraint in contract.Constraints)
            {
                builder.AppendLine($"- {constraint}");
            }
        }
        builder.AppendLine(
            "Do not declare completion unless tool evidence satisfies the applicable criteria.");
        return builder.ToString();
    }

    public static string FormatAssessment(TaskOutcomeAssessment assessment)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"## NOVA Proof-of-Done · {assessment.Status} · {assessment.ProofScore}/100");
        foreach (var criterion in assessment.Criteria)
        {
            var mark = criterion.Status switch
            {
                "PASS" => "✓",
                "FAIL" => "✕",
                _ => "○"
            };
            builder.AppendLine(
                $"- {mark} **{criterion.Title}** · {criterion.Status} · {criterion.Evidence}");
        }
        return builder.ToString().TrimEnd();
    }

    private static (string Status, string Evidence) EvaluateVerification(
        string command,
        bool attempted,
        bool passed)
    {
        if (command is "NO VERIFICATION TARGET" or "MANUAL VERIFICATION REQUIRED")
        {
            return ("UNVERIFIED", $"没有可自动执行的验证目标：{command}。");
        }
        if (!attempted)
        {
            return ("UNVERIFIED", $"验证命令未获授权或未运行：{command}。");
        }
        return passed
            ? ("PASS", $"验证命令成功退出：{command}。")
            : ("FAIL", $"验证命令执行后未通过：{command}。");
    }

    private static (string Status, string Evidence) EvaluateReview(
        EngineeringCodeReviewResult? review)
    {
        if (review is null)
        {
            return ("UNVERIFIED", "没有生成本地代码审查证据。");
        }
        var high = review.Findings.Count(item => item.Severity == "HIGH");
        return review.Score >= 80 && high == 0
            ? ("PASS", $"本地审查 {review.Score}/100，HIGH 0。")
            : ("FAIL", $"本地审查 {review.Score}/100，HIGH {high}。");
    }

    private static (string Status, string Evidence) EvaluateCouncil(
        VerificationCouncilResult? council)
    {
        if (council is null || council.Verdict is "SKIPPED" or "UNAVAILABLE")
        {
            return (
                "UNVERIFIED",
                council?.Summary ?? "独立验证 Council 没有运行。");
        }
        return council.Verdict == "PASS"
            ? (
                "PASS",
                $"独立 Council PASS · confidence {council.Confidence}% · "
                + $"{council.Provider}/{council.Model}。")
            : (
                "FAIL",
                $"独立 Council {council.Verdict} · confidence {council.Confidence}% · "
                + council.Summary);
    }

    private static (string Status, string Evidence) EvaluateGoalSignal(
        string criterionId,
        VerificationCouncilResult? council)
    {
        if (!int.TryParse(
                criterionId["goal-signal-".Length..],
                out var signalIndex))
        {
            return ("UNVERIFIED", "成功信号 ID 无法解析。");
        }
        if (council is null
            || council.Verdict is "SKIPPED" or "UNAVAILABLE"
            || string.IsNullOrWhiteSpace(council.RawResponse))
        {
            return (
                "UNVERIFIED",
                council?.Summary ?? "没有独立 Council 的逐信号证据。");
        }

        var pattern =
            $@"(?im)^\s*SIGNAL\s+{signalIndex}\s*:\s*"
            + @"(?<status>PASS|UNVERIFIED|FAIL|BLOCKED)\s*\|\s*"
            + @"(?<evidence>[^\r\n]+)\s*$";
        var match = System.Text.RegularExpressions.Regex.Match(
            council.RawResponse,
            pattern,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return (
                "UNVERIFIED",
                $"Council 未返回 SIGNAL {signalIndex} 的结构化证据。");
        }

        var verdict = match.Groups["status"].Value.ToUpperInvariant();
        var signalEvidence = match.Groups["evidence"].Value.Trim();
        return verdict switch
        {
            "PASS" => ("PASS", signalEvidence),
            "UNVERIFIED" => ("UNVERIFIED", signalEvidence),
            "BLOCKED" => ("FAIL", $"BLOCKED：{signalEvidence}"),
            _ => ("FAIL", signalEvidence)
        };
    }

    private static IReadOnlyList<string> ExtractConstraints(string objective)
    {
        var signals = new[] { "不要", "不能", "不得", "避免", "must not", "without", "do not" };
        return objective
            .Split(
                ['\r', '\n', '。', '；', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => signals.Any(signal =>
                part.Contains(signal, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
    }

    private async Task SaveAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        var json = JsonSerializer.Serialize(value, _jsonOptions);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_storageRoot);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private string ContractPath(string taskId)
        => Path.Combine(_storageRoot, $"{SafeName(taskId)}-contract.json");

    private string AssessmentPath(string taskId)
        => Path.Combine(_storageRoot, $"{SafeName(taskId)}-assessment.json");

    private static string SafeName(string value)
    {
        var safe = string.Concat(value.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
        return string.IsNullOrWhiteSpace(safe) ? "task" : safe;
    }
}
