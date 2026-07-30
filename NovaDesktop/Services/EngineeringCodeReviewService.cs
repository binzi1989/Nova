using System.Text;
using System.Text.RegularExpressions;
using System.IO;

namespace NovaDesktop.Services;

public sealed record EngineeringReviewFinding(
    string Severity,
    string FilePath,
    int Line,
    string Rule,
    string Message)
{
    public string LocationLabel => Line > 0 ? $"{FilePath}:{Line}" : FilePath;
}

public sealed record EngineeringCodeReviewResult(
    int Score,
    IReadOnlyList<EngineeringReviewFinding> Findings,
    string Summary,
    DateTimeOffset ReviewedAt);

public sealed class EngineeringCodeReviewService
{
    private static readonly Regex HunkHeader = new(
        @"^@@\s+-\d+(?:,\d+)?\s+\+(?<line>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SecretPattern = new(
        @"(?i)(sk-[A-Za-z0-9_-]{8,}|Bearer\s+[A-Za-z0-9._~+/=-]{8,}|(api[_-]?key|token|secret|password)\s*[:=]\s*[""']?[^'""\s]{6,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public EngineeringCodeReviewResult Review(EngineeringWorkspaceSnapshot snapshot)
    {
        var findings = new List<EngineeringReviewFinding>();
        var currentFile = "workspace";
        var currentLine = 0;
        var changedCodeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changedTestFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in snapshot.Diff
                     .Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Replace('\r', '\n')
                     .Split('\n'))
        {
            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                currentFile = line[6..];
                if (IsCodeFile(currentFile))
                {
                    changedCodeFiles.Add(currentFile);
                    if (IsTestFile(currentFile))
                    {
                        changedTestFiles.Add(currentFile);
                    }
                }
                continue;
            }

            var hunkMatch = HunkHeader.Match(line);
            if (hunkMatch.Success)
            {
                currentLine = int.Parse(hunkMatch.Groups["line"].Value);
                continue;
            }

            if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                var content = line[1..];
                InspectAddedLine(findings, currentFile, currentLine, content);
                currentLine++;
            }
            else if (!line.StartsWith('-'))
            {
                currentLine++;
            }
        }

        if (snapshot.Additions + snapshot.Deletions > 800)
        {
            findings.Add(new EngineeringReviewFinding(
                "MEDIUM",
                "workspace",
                0,
                "change-size",
                "本次变更超过 800 行；建议拆分为更小的可验证单元。"));
        }
        if (changedCodeFiles.Count > 0 && changedTestFiles.Count == 0)
        {
            findings.Add(new EngineeringReviewFinding(
                "MEDIUM",
                "workspace",
                0,
                "test-coverage",
                "检测到代码变更，但 Diff 中没有对应测试文件变更。请确认现有测试是否足够。"));
        }

        var high = findings.Count(item => item.Severity == "HIGH");
        var medium = findings.Count(item => item.Severity == "MEDIUM");
        var low = findings.Count(item => item.Severity == "LOW");
        var score = Math.Clamp(100 - high * 30 - medium * 12 - low * 4, 0, 100);
        var summary = findings.Count == 0
            ? "本地规则审查未发现明显风险；这不替代编译、测试或人工业务审查。"
            : $"发现 {findings.Count} 项：HIGH {high} · MEDIUM {medium} · LOW {low}。";
        return new EngineeringCodeReviewResult(score, findings, summary, DateTimeOffset.Now);
    }

    public static string Format(EngineeringCodeReviewResult review)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"LOCAL CODE REVIEW · SCORE {review.Score}");
        builder.AppendLine(review.Summary);
        builder.AppendLine();
        foreach (var finding in review.Findings)
        {
            builder.Append('[')
                .Append(finding.Severity)
                .Append("] ")
                .Append(finding.LocationLabel)
                .Append(" · ")
                .Append(finding.Rule)
                .AppendLine();
            builder.AppendLine("  " + finding.Message);
        }
        return builder.ToString();
    }

    private static void InspectAddedLine(
        ICollection<EngineeringReviewFinding> findings,
        string file,
        int line,
        string content)
    {
        if (SecretPattern.IsMatch(content))
        {
            findings.Add(new EngineeringReviewFinding(
                "HIGH", file, line, "credential-exposure",
                "新增内容疑似包含密钥、Token 或密码。请移出源码并立即轮换已暴露凭据。"));
        }
        if (content.Contains("catch {", StringComparison.Ordinal)
            || content.Contains("catch{}", StringComparison.Ordinal))
        {
            findings.Add(new EngineeringReviewFinding(
                "MEDIUM", file, line, "swallowed-exception",
                "新增空 catch 可能隐藏真实失败；至少记录、转换或明确解释异常。"));
        }
        if (content.Contains("Thread.Sleep(", StringComparison.Ordinal)
            || content.Contains(".Result", StringComparison.Ordinal)
            || content.Contains(".Wait()", StringComparison.Ordinal))
        {
            findings.Add(new EngineeringReviewFinding(
                "MEDIUM", file, line, "blocking-call",
                "新增同步阻塞调用可能造成 UI 卡顿或线程池饥饿，请审查异步边界。"));
        }
        if (content.Contains("[Fact(Skip", StringComparison.OrdinalIgnoreCase)
            || content.Contains(".skip(", StringComparison.OrdinalIgnoreCase)
            || content.Contains("@pytest.mark.skip", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new EngineeringReviewFinding(
                "MEDIUM", file, line, "disabled-test",
                "变更中出现被跳过的测试，请确认不是为了绕过失败验证。"));
        }
        if (content.Contains("TODO", StringComparison.OrdinalIgnoreCase)
            || content.Contains("FIXME", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new EngineeringReviewFinding(
                "LOW", file, line, "unfinished-work",
                "新增 TODO/FIXME；若属于交付范围，应在完成前处理或登记。"));
        }
    }

    private static bool IsCodeFile(string path)
        => new[]
        {
            ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".rs", ".go", ".java", ".cpp", ".c", ".h"
        }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool IsTestFile(string path)
        => path.Contains("test", StringComparison.OrdinalIgnoreCase)
           || path.Contains("spec", StringComparison.OrdinalIgnoreCase);
}
