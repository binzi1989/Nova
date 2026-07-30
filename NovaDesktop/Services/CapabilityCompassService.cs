using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NovaDesktop.Services;

public enum CapabilityKind
{
    Mcp,
    Skill,
    Gap
}

public enum CapabilityAction
{
    Ready,
    EnableMcp,
    EnableSkill,
    OpenMarketplace,
    DiscoverMcp,
    InstallSkill
}

public sealed record CapabilityRecommendation(
    string Id,
    CapabilityKind Kind,
    string SourceLabel,
    string Name,
    string Reason,
    string PermissionSummary,
    string RiskLabel,
    int Confidence,
    bool IsReady,
    CapabilityAction Action,
    string ActionLabel,
    string Accent);

public sealed record CapabilityCompassReport(
    string Intent,
    string WorkspaceSignal,
    int ReadyCount,
    int SuggestedCount,
    IReadOnlyList<CapabilityRecommendation> Recommendations)
{
    public string Summary
        => Recommendations.Count == 0
            ? "当前任务可以先使用 NOVA 内建能力；没有必要额外挂载扩展。"
            : $"找到 {ReadyCount} 项可直接使用的能力，另有 {SuggestedCount} 项需要你确认。";
}

public sealed class CapabilityCompassService
{
    private static readonly Regex WordPattern = new(
        @"[A-Za-z][A-Za-z0-9_.-]{2,}|[\p{IsCJKUnifiedIdeographs}]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly CapabilityDomain[] Domains =
    [
        new(
            "code",
            "代码协作",
            ["代码", "编程", "开发", "修复", "项目", "仓库", "github", "git", "issue", "pull request", "pr"],
            ["github", "git", "code", "repo", "issue", "pull", "开发", "代码"],
            "访问代码托管或读取专项工程指令",
            CapabilityAction.OpenMarketplace),
        new(
            "browser",
            "网页与浏览器",
            ["网页", "网站", "浏览器", "搜索", "调研", "登录", "chrome", "browser", "web", "playwright"],
            ["browser", "chrome", "playwright", "web", "search", "网页", "浏览器"],
            "可能读取网页内容或操作浏览器会话",
            CapabilityAction.OpenMarketplace),
        new(
            "docs",
            "文档与表格",
            ["文档", "word", "docx", "pdf", "表格", "excel", "xlsx", "ppt", "幻灯片", "spreadsheet"],
            ["document", "word", "pdf", "excel", "sheet", "slide", "文档", "表格"],
            "读取专项格式说明；写入仍经过任务授权",
            CapabilityAction.OpenMarketplace),
        new(
            "data",
            "数据与数据库",
            ["数据库", "数据仓库", "sql", "postgres", "mysql", "sqlite", "redis", "database"],
            ["database", "postgres", "mysql", "sqlite", "sql", "redis", "数据"],
            "可能连接数据库或读取数据结构",
            CapabilityAction.OpenMarketplace),
        new(
            "collaboration",
            "协作与日程",
            ["邮件", "日历", "会议", "消息", "slack", "teams", "gmail", "outlook", "notion", "jira"],
            ["mail", "calendar", "slack", "teams", "gmail", "outlook", "notion", "jira", "邮件", "日历"],
            "可能访问外部账号；每次真实操作仍单独授权",
            CapabilityAction.OpenMarketplace),
        new(
            "design",
            "设计与视觉",
            ["设计", "界面", "ui", "ux", "图片", "图标", "figma", "视觉", "原型"],
            ["figma", "design", "image", "ui", "ux", "设计", "视觉"],
            "读取设计说明或连接设计工具",
            CapabilityAction.OpenMarketplace)
    ];

    private readonly McpRegistryService _mcpRegistry;
    private readonly SkillRegistryService _skillRegistry;

    public CapabilityCompassService(
        McpRegistryService mcpRegistry,
        SkillRegistryService skillRegistry)
    {
        _mcpRegistry = mcpRegistry;
        _skillRegistry = skillRegistry;
    }

    public CapabilityCompassReport Analyze(string? intent, string workspaceRoot)
    {
        var normalizedIntent = (intent ?? string.Empty).Trim();
        var workspace = InspectWorkspace(workspaceRoot);
        var analysisText = $"{normalizedIntent}\n{workspace.SearchText}";
        var matchedDomains = Domains
            .Where(domain => ContainsAny(analysisText, domain.IntentSignals))
            .ToArray();
        var recommendations = new List<CapabilityRecommendation>();
        var mcpServers = SafeGetMcpServers();
        var skills = SafeGetSkills();

        foreach (var server in mcpServers)
        {
            var descriptor = string.Join(
                " ",
                server.Name,
                server.Transport,
                server.Command,
                server.Url,
                string.Join(" ", server.Arguments));
            var score = ScoreCapability(normalizedIntent, descriptor, matchedDomains);
            if (score < 22 && normalizedIntent.Length > 0)
            {
                continue;
            }

            var risk = server.Transport == "http"
                ? "外部网络"
                : server.Command is "npx" or "uvx"
                    ? "可能获取软件"
                    : "本机进程";
            recommendations.Add(new CapabilityRecommendation(
                server.Name,
                CapabilityKind.Mcp,
                "MCP",
                server.Name,
                BuildReason(descriptor, matchedDomains, server.Enabled),
                server.Enabled
                    ? "已授权启用；真正连接或调用时仍会展示目标和参数。"
                    : "启用只允许 Agent 看见该连接；测试与每次调用仍需确认。",
                risk,
                score,
                server.Enabled,
                server.Enabled ? CapabilityAction.Ready : CapabilityAction.EnableMcp,
                server.Enabled ? "查看连接" : "审阅并启用",
                server.Enabled ? "#78C8B6" : "#D7B36B"));
        }

        foreach (var skill in skills)
        {
            var descriptor = $"{skill.Name} {skill.Description} {skill.Id}";
            var score = ScoreCapability(normalizedIntent, descriptor, matchedDomains);
            if (score < 22 && normalizedIntent.Length > 0)
            {
                continue;
            }

            recommendations.Add(new CapabilityRecommendation(
                skill.Id,
                CapabilityKind.Skill,
                "SKILL",
                skill.Name,
                BuildReason(descriptor, matchedDomains, skill.Enabled),
                skill.Enabled
                    ? "只读取相关 SKILL.md；指令不能绕过工作区与工具审批。"
                    : "启用后仅允许 Agent 按需读取说明，不会执行 Skill 内脚本。",
                "本地指令",
                score,
                skill.Enabled,
                skill.Enabled ? CapabilityAction.Ready : CapabilityAction.EnableSkill,
                skill.Enabled ? "查看 Skill" : "审阅并启用",
                skill.Enabled ? "#78C8B6" : "#D7B36B"));
        }

        foreach (var domain in matchedDomains)
        {
            var hasMatch = recommendations.Any(item =>
                item.Kind != CapabilityKind.Gap
                && CapabilityMatchesDomain(item, domain));
            if (hasMatch)
            {
                continue;
            }
            recommendations.Add(new CapabilityRecommendation(
                $"gap-{domain.Id}",
                CapabilityKind.Gap,
                "能力缺口",
                domain.Label,
                $"任务涉及{domain.Label}，但当前没有匹配度足够的已安装能力。",
                domain.Permission,
                "等待选择",
                72,
                false,
                domain.DefaultAction,
                "去能力集市",
                "#C45A45"));
        }

        if (recommendations.Count == 0 && normalizedIntent.Length > 0)
        {
            recommendations.Add(new CapabilityRecommendation(
                "gap-discovery",
                CapabilityKind.Gap,
                "能力司南",
                "尚未发现必要扩展",
                "目前可以先使用 NOVA 的工作区、代码、知识与验证能力；若任务需要外部系统，再进行安全扫描。",
                "扫描前会列出准确配置路径；不会静默读取或启动外部程序。",
                "低风险",
                48,
                true,
                CapabilityAction.Ready,
                "内建能力足够",
                "#78C8B6"));
        }

        var ranked = recommendations
            .OrderByDescending(item => item.IsReady)
            .ThenByDescending(item => item.Confidence)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        return new CapabilityCompassReport(
            normalizedIntent,
            workspace.Label,
            ranked.Count(item => item.IsReady),
            ranked.Count(item => !item.IsReady),
            ranked);
    }

    public static string FormatForPrompt(CapabilityCompassReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[NOVA CAPABILITY COMPASS]");
        builder.AppendLine($"Workspace signals: {report.WorkspaceSignal}");
        var relevant = report.Recommendations
            .Where(item => item.IsReady && item.Kind is CapabilityKind.Mcp or CapabilityKind.Skill)
            .ToArray();
        if (relevant.Length == 0)
        {
            builder.AppendLine("No task-relevant extension is currently mounted. Use built-in tools when sufficient.");
        }
        else
        {
            builder.AppendLine("Task-relevant capabilities already approved by the user:");
            foreach (var item in relevant)
            {
                builder.AppendLine($"- {item.SourceLabel} {item.Name}: {item.Reason}");
            }
        }
        if (report.Recommendations.Any(item => !item.IsReady))
        {
            builder.AppendLine(
                "Some potentially useful capabilities are disabled or missing. Do not claim they are available and do not activate them silently.");
        }
        return builder.ToString().TrimEnd();
    }

    private static int ScoreCapability(
        string intent,
        string descriptor,
        IReadOnlyList<CapabilityDomain> matchedDomains)
    {
        var score = intent.Length == 0 ? 28 : 0;
        foreach (var domain in matchedDomains)
        {
            if (ContainsAny(descriptor, domain.CapabilityHints))
            {
                score += 58;
            }
        }
        var intentWords = WordPattern.Matches(intent)
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24);
        score += intentWords.Count(word =>
            descriptor.Contains(word, StringComparison.OrdinalIgnoreCase)) * 12;
        return Math.Clamp(score, 0, 96);
    }

    private static string BuildReason(
        string descriptor,
        IReadOnlyList<CapabilityDomain> domains,
        bool enabled)
    {
        var domain = domains.FirstOrDefault(item =>
            ContainsAny(descriptor, item.CapabilityHints));
        var relevance = domain is null
            ? "与当前任务描述存在直接关键词关联"
            : $"可补足当前任务的“{domain.Label}”能力";
        return enabled ? $"{relevance}，且已经处于可用状态。" : $"{relevance}，目前尚未启用。";
    }

    private static bool CapabilityMatchesDomain(
        CapabilityRecommendation recommendation,
        CapabilityDomain domain)
        => ContainsAny(
            $"{recommendation.Name} {recommendation.Reason}",
            domain.CapabilityHints);

    private IReadOnlyList<McpServerRegistration> SafeGetMcpServers()
    {
        try
        {
            return _mcpRegistry.GetServers();
        }
        catch
        {
            return [];
        }
    }

    private IReadOnlyList<InstalledSkill> SafeGetSkills()
    {
        try
        {
            return _skillRegistry.GetSkills();
        }
        catch
        {
            return [];
        }
    }

    private static WorkspaceInspection InspectWorkspace(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
        {
            return new WorkspaceInspection("未选择有效工作区", string.Empty);
        }
        try
        {
            var names = Directory.EnumerateFiles(workspaceRoot, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Take(80)
                .ToArray();
            var signals = new List<string>();
            AddSignal(names, signals, "package.json", "Node/前端工程");
            AddSignal(names, signals, "project.config.json", "微信小程序");
            AddSignal(names, signals, "*.sln", ".NET 工程", wildcard: true);
            AddSignal(names, signals, "*.csproj", ".NET 工程", wildcard: true);
            AddSignal(names, signals, "pyproject.toml", "Python 工程");
            AddSignal(names, signals, "Cargo.toml", "Rust 工程");
            AddSignal(names, signals, "go.mod", "Go 工程");
            var label = signals.Count == 0
                ? "通用工作区"
                : string.Join(" · ", signals.Distinct());
            return new WorkspaceInspection(label, string.Join(" ", names) + " " + label);
        }
        catch
        {
            return new WorkspaceInspection("工作区信号暂不可读", string.Empty);
        }
    }

    private static void AddSignal(
        IReadOnlyList<string?> names,
        ICollection<string> signals,
        string pattern,
        string label,
        bool wildcard = false)
    {
        var found = wildcard
            ? names.Any(name => name?.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) == true)
            : names.Any(name => name?.Equals(pattern, StringComparison.OrdinalIgnoreCase) == true);
        if (found)
        {
            signals.Add(label);
        }
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> signals)
        => signals.Any(signal => text.Contains(signal, StringComparison.OrdinalIgnoreCase));

    private sealed record CapabilityDomain(
        string Id,
        string Label,
        IReadOnlyList<string> IntentSignals,
        IReadOnlyList<string> CapabilityHints,
        string Permission,
        CapabilityAction DefaultAction);

    private sealed record WorkspaceInspection(string Label, string SearchText);
}
