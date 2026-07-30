using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovaDesktop.Services;

public sealed record EngineeringCompletenessFinding(
    string Severity,
    string Code,
    string Title,
    string Evidence,
    string RepairHint);

public sealed record EngineeringCompletenessAssessment(
    string TaskId,
    int Score,
    bool ReadyForDelivery,
    int ChangedFileCount,
    int NewChangedFileCount,
    IReadOnlyList<EngineeringCompletenessFinding> Findings,
    string Summary,
    string ArtifactPath,
    DateTimeOffset AssessedAt);

public sealed class EngineeringCompletenessService
{
    private static readonly Regex StrongPlaceholderPattern = new(
        @"(?i)\b(NotImplementedException|TODO:\s*(implement|finish)|FIXME:\s*(implement|finish)|coming\s+soon|placeholder\s+implementation)\b|待实现|尚未实现|占位实现",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<EngineeringCompletenessAssessment> AssessAndPersistAsync(
        string taskId,
        string objective,
        EngineeringWorkspaceSnapshot before,
        EngineeringWorkspaceSnapshot after,
        bool verificationAttempted,
        bool verificationPassed,
        EngineeringCodeReviewResult? review,
        CancellationToken cancellationToken = default)
    {
        var findings = new List<EngineeringCompletenessFinding>();
        var beforePaths = before.ChangedFiles
            .Select(item => item.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var afterPaths = after.ChangedFiles
            .Select(item => item.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newPaths = afterPaths
            .Where(path => !beforePaths.Contains(path))
            .ToArray();
        var workspaceFingerprintChanged = !string.IsNullOrWhiteSpace(
                                              after.WorkspaceFingerprint)
                                          && !string.Equals(
                                              before.WorkspaceFingerprint,
                                              after.WorkspaceFingerprint,
                                              StringComparison.Ordinal);
        var materialDelta = !string.Equals(
                                NormalizeDiff(before.Diff),
                                NormalizeDiff(after.Diff),
                                StringComparison.Ordinal)
                            || before.Additions != after.Additions
                            || before.Deletions != after.Deletions
                            || !beforePaths.SetEquals(afterPaths)
                            || workspaceFingerprintChanged;
        var effectiveChangedFileCount = after.IsGitRepository
            ? after.ChangedFiles.Count
            : Math.Max(
                materialDelta ? 1 : 0,
                after.WorkspaceFileCount - before.WorkspaceFileCount);
        var effectiveNewFileCount = after.IsGitRepository
            ? newPaths.Length
            : Math.Max(0, after.WorkspaceFileCount - before.WorkspaceFileCount);

        if (after.Projects.Count == 0)
        {
            findings.Add(Blocker(
                "project-manifest",
                "没有可识别的工程清单",
                "未发现 .sln、.csproj、package.json、project.config.json、pyproject.toml、Cargo.toml 或 go.mod。",
                "创建或修复真实工程清单，并保证源码属于该工程。"));
        }
        if (!materialDelta)
        {
            findings.Add(Blocker(
                "material-delta",
                "没有检测到本轮真实工程增量",
                "任务前后的 Git Diff、文件集合和行数统计一致。",
                "重新检查目标并落盘必要实现；不要只返回说明。"));
        }
        if (!verificationAttempted
            || after.VerificationCommand is "NO VERIFICATION TARGET" or "MANUAL VERIFICATION REQUIRED")
        {
            findings.Add(Blocker(
                "verification-target",
                "缺少可重复运行的工程验证",
                after.VerificationCommand,
                "补齐构建或测试入口，使 NOVA 能在交付前重复验证。"));
        }
        else if (!verificationPassed)
        {
            findings.Add(Blocker(
                "verification-failed",
                "构建或测试没有通过",
                after.VerificationCommand,
                "根据真实退出码修复根因，然后重新运行同一验证。"));
        }

        var placeholderLines = AddedLines(after.Diff)
            .Concat(after.IsGitRepository
                ? []
                : ScanWorkspacePlaceholders(after.WorkspaceRoot))
            .Distinct(StringComparer.Ordinal)
            .Where(line => StrongPlaceholderPattern.IsMatch(line))
            .Take(8)
            .ToArray();
        if (placeholderLines.Length > 0)
        {
            findings.Add(Blocker(
                "placeholder-implementation",
                "交付中仍存在强占位实现",
                string.Join(Environment.NewLine, placeholderLines),
                "实现真实行为，或把无法完成的外部依赖明确标为 BLOCKED。"));
        }

        if (IsProjectCreationGoal(objective)
            && before.ChangedFiles.Count == 0
            && before.WorkspaceFileCount <= 1
            && effectiveChangedFileCount < 3)
        {
            findings.Add(Blocker(
                "implausibly-small-project",
                "项目型目标的交付范围异常狭窄",
                $"只检测到 {effectiveChangedFileCount} 个任务增量文件。",
                "检查入口、核心逻辑、配置/清单、错误处理和验证是否都已真实落盘。"));
        }

        if (review is null)
        {
            findings.Add(Warning(
                "review-missing",
                "没有本地变更审查",
                "无法检查新增风险、测试覆盖和占位代码。",
                "在交付前运行本地规则审查。"));
        }
        else
        {
            var highFindings = review.Findings
                .Where(item => item.Severity == "HIGH")
                .ToArray();
            if (highFindings.Length > 0 || review.Score < 80)
            {
                findings.Add(Blocker(
                    "review-gate",
                    "本地审查没有达到交付线",
                    $"score {review.Score}/100 · HIGH {highFindings.Length}",
                    "逐项处理 HIGH/MEDIUM 风险并重新审查，不要仅解释风险。"));
            }

            if (review.Findings.Any(item => item.Rule == "test-coverage"))
            {
                findings.Add(Warning(
                    "test-coverage",
                    "代码变化没有对应测试变化",
                    "本地审查检测到源码变化，但 Diff 中没有测试文件变化。",
                    "补充最接近本次行为的自动测试，或证明现有测试已覆盖该路径。"));
            }
        }

        if (effectiveChangedFileCount > 30)
        {
            findings.Add(Warning(
                "change-breadth",
                "一次性交付范围过大",
                $"{effectiveChangedFileCount} 个文件发生变化。",
                "检查是否包含生成物、依赖目录或与目标无关的修改。"));
        }

        var blockers = findings.Count(item => item.Severity == "BLOCKER");
        var warnings = findings.Count(item => item.Severity == "WARNING");
        var score = Math.Clamp(100 - blockers * 25 - warnings * 8, 0, 100);
        var ready = blockers == 0 && score >= 80;
        var root = Path.Combine(
            after.WorkspaceRoot,
            ".nova",
            "engineering-completeness");
        Directory.CreateDirectory(root);
        var artifactPath = Path.Combine(root, NormalizeTaskId(taskId) + ".json");
        var summary = ready
            ? $"READY · {effectiveChangedFileCount} files · verification PASS · review {review?.Score ?? 0}/100"
            : $"NOT READY · score {score}/100 · {blockers} blockers · {warnings} warnings";
        var assessment = new EngineeringCompletenessAssessment(
            taskId,
            score,
            ready,
            effectiveChangedFileCount,
            effectiveNewFileCount,
            findings,
            summary,
            artifactPath,
            DateTimeOffset.Now);
        var temporaryPath = artifactPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(
                assessment,
                new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        File.Move(temporaryPath, artifactPath, overwrite: true);
        return assessment;
    }

    public static string Format(EngineeringCompletenessAssessment assessment)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            $"ENGINEERING COMPLETENESS · {(assessment.ReadyForDelivery ? "READY" : "NOT READY")}");
        builder.AppendLine($"Score: {assessment.Score}/100");
        builder.AppendLine(
            $"Changed files: {assessment.ChangedFileCount} · New in task: {assessment.NewChangedFileCount}");
        builder.AppendLine();
        if (assessment.Findings.Count == 0)
        {
            builder.AppendLine("构建/测试、审查、工程清单、真实增量和占位扫描均通过。");
        }
        foreach (var finding in assessment.Findings)
        {
            builder.Append('[').Append(finding.Severity).Append("] ")
                .Append(finding.Title).Append(" · ").AppendLine(finding.Code);
            builder.AppendLine("  证据：" + finding.Evidence);
            builder.AppendLine("  修复：" + finding.RepairHint);
        }
        return builder.ToString();
    }

    public static string BuildRepairPrompt(EngineeringCompletenessAssessment assessment)
        => string.Join(
            Environment.NewLine,
            assessment.Findings
                .Where(item => item.Severity == "BLOCKER")
                .Select((item, index) =>
                    $"{index + 1}. {item.Title}\n证据：{item.Evidence}\n必须修复：{item.RepairHint}"));

    private static EngineeringCompletenessFinding Blocker(
        string code,
        string title,
        string evidence,
        string repairHint)
        => new("BLOCKER", code, title, evidence, repairHint);

    private static EngineeringCompletenessFinding Warning(
        string code,
        string title,
        string evidence,
        string repairHint)
        => new("WARNING", code, title, evidence, repairHint);

    private static IEnumerable<string> AddedLines(string diff)
        => diff.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Where(line => line.StartsWith('+')
                           && !line.StartsWith("+++", StringComparison.Ordinal))
            .Select(line => line[1..].Trim())
            .Where(line => line.Length > 0);

    private static IReadOnlyList<string> ScanWorkspacePlaceholders(string workspaceRoot)
    {
        var matches = new List<string>();
        var supportedExtensions = new HashSet<string>(
            [
                ".cs", ".csx", ".fs", ".vb", ".ts", ".tsx", ".js", ".jsx",
                ".mjs", ".cjs", ".py", ".rs", ".go", ".java", ".kt", ".swift",
                ".cpp", ".cc", ".c", ".h", ".hpp", ".html", ".css", ".scss",
                ".vue", ".svelte", ".json", ".yaml", ".yml", ".toml",
                ".wxml", ".wxss", ".wxs", ".axml", ".acss", ".swan", ".ttml", ".ttss"
            ],
            StringComparer.OrdinalIgnoreCase);
        var skippedSegments = new[]
        {
            $"{Path.DirectorySeparatorChar}.nova{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}"
        };
        var scanned = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         workspaceRoot,
                         "*",
                         SearchOption.AllDirectories))
            {
                if (scanned >= 240)
                {
                    break;
                }
                if (!supportedExtensions.Contains(Path.GetExtension(path))
                    || skippedSegments.Any(segment =>
                        path.Contains(segment, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                scanned++;
                try
                {
                    var info = new FileInfo(path);
                    if (info.Length > 512 * 1024)
                    {
                        continue;
                    }
                    foreach (var line in File.ReadLines(path).Take(3000))
                    {
                        if (StrongPlaceholderPattern.IsMatch(line))
                        {
                            matches.Add(
                                $"{Path.GetRelativePath(workspaceRoot, path)}: {line.Trim()}");
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException
                                                  or NotSupportedException)
                {
                    // Ignore protected or transient files in the bounded scan.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Return the bounded evidence collected before traversal failed.
        }
        return matches;
    }

    private static string NormalizeDiff(string diff)
        => diff.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static bool IsProjectCreationGoal(string objective)
    {
        var creationSignals = new[]
        {
            "开发一个", "创建一个", "构建一个", "生成一个", "从零", "完整项目",
            "build an", "create an", "create a", "new app", "new project",
            "小游戏", "应用程序", "agentos"
        };
        return creationSignals.Any(signal =>
            objective.Contains(signal, StringComparison.OrdinalIgnoreCase));
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
}
