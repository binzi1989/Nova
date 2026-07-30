using System.IO;

namespace NovaDesktop.Services;

public enum MarketplaceCapabilityKind
{
    Mcp,
    Skill
}

public sealed record BundledSkillDefinition(
    string Id,
    string Instructions);

public sealed record MarketplaceCatalogItem(
    string Id,
    MarketplaceCapabilityKind Kind,
    string Category,
    string Name,
    string Publisher,
    string Description,
    string TrustLabel,
    string RiskLabel,
    string PermissionSummary,
    string Requirements,
    string SourceUrl,
    IReadOnlyList<string> RequiredCommands,
    IReadOnlyList<string> RequiredEnvironmentVariables,
    bool MayAcquireSoftware,
    McpServerRegistration? McpRegistration,
    BundledSkillDefinition? SkillDefinition,
    bool IsInstalled,
    bool IsEnabled,
    string StateLabel,
    string ActionLabel,
    string Accent)
{
    public string KindLabel => Kind == MarketplaceCapabilityKind.Mcp ? "MCP" : "SKILL";
}

public sealed class CapabilityMarketplaceService
{
    private readonly McpRegistryService _mcpRegistry;
    private readonly SkillRegistryService _skillRegistry;
    private readonly string _workspaceRoot;

    public CapabilityMarketplaceService(
        McpRegistryService mcpRegistry,
        SkillRegistryService skillRegistry,
        string workspaceRoot)
    {
        _mcpRegistry = mcpRegistry;
        _skillRegistry = skillRegistry;
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public IReadOnlyList<MarketplaceCatalogItem> GetCatalog()
    {
        var servers = _mcpRegistry.GetServers();
        var skills = _skillRegistry.GetSkills();
        return CreateDefinitions()
            .Select(item =>
            {
                var installedMcp = item.McpRegistration is null
                    ? null
                    : servers.FirstOrDefault(server =>
                        server.Name.Equals(
                            item.McpRegistration.Name,
                            StringComparison.OrdinalIgnoreCase));
                var installedSkill = item.SkillDefinition is null
                    ? null
                    : skills.FirstOrDefault(skill =>
                        skill.Id.Equals(
                            item.SkillDefinition.Id,
                            StringComparison.OrdinalIgnoreCase));
                var installed = installedMcp is not null || installedSkill is not null;
                var enabled = installedMcp?.Enabled == true || installedSkill?.Enabled == true;
                var missing = GetMissingPrerequisites(item);
                var state = enabled
                    ? "已加载"
                    : installed
                        ? "已登记 · 未启用"
                        : missing.Count > 0
                            ? $"缺少 {string.Join(" / ", missing)}"
                            : "可安全加载";
                var action = enabled
                    ? "查看能力"
                    : installed
                        ? "审阅并启用"
                        : missing.Count > 0 && item.Kind == MarketplaceCapabilityKind.Mcp
                            ? "登记待配置"
                            : "审阅并加载";
                return item with
                {
                    IsInstalled = installed,
                    IsEnabled = enabled,
                    StateLabel = state,
                    ActionLabel = action
                };
            })
            .ToArray();
    }

    public IReadOnlyList<string> GetMissingPrerequisites(MarketplaceCatalogItem item)
    {
        var missing = item.RequiredCommands
            .Where(command => !CommandExists(command))
            .Select(command => command.Equals("npx", StringComparison.OrdinalIgnoreCase)
                ? "Node.js/npm"
                : command)
            .ToList();
        missing.AddRange(
            item.RequiredEnvironmentVariables
                .Where(name => string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(name)))
                .Select(name => $"环境变量 {name}"));
        return missing;
    }

    private IReadOnlyList<MarketplaceCatalogItem> CreateDefinitions()
        =>
        [
            CreateMcp(
                "github-official",
                "编程协作",
                "GitHub 官方 MCP（只读）",
                "GitHub",
                "读取仓库、Issue 与 Pull Request；默认以只读模式登记，写操作不进入工具面。",
                "官方源",
                "外部账号",
                "需要 Docker 与最小权限 GitHub PAT。加载只登记连接，真实访问仍逐次授权。",
                "Docker Desktop · GITHUB_PAT",
                "https://github.com/github/github-mcp-server",
                ["docker"],
                ["GITHUB_PAT"],
                mayAcquireSoftware: true,
                new McpServerRegistration(
                    "github-official",
                    "docker",
                    [
                        "run", "-i", "--rm",
                        "-e", "GITHUB_PERSONAL_ACCESS_TOKEN",
                        "-e", "GITHUB_READ_ONLY=1",
                        "ghcr.io/github/github-mcp-server"
                    ],
                    _workspaceRoot,
                    false,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "GITHUB_PAT"
                    })),
            CreateMcp(
                "playwright-official",
                "浏览器",
                "Playwright MCP（隔离无头）",
                "Microsoft",
                "在隔离、无头浏览器中完成网页交互。普通资料读取优先使用 NOVA 内置后台研究，无需启动浏览器。",
                "官方源",
                "网页交互",
                "首次连接可能由 npx 下载官方包；访问站点和具体交互仍逐次授权。",
                "Node.js 18+ · npm/npx",
                "https://github.com/microsoft/playwright-mcp",
                ["npx"],
                [],
                mayAcquireSoftware: true,
                new McpServerRegistration(
                    "playwright-official",
                    "npx",
                    ["-y", "@playwright/mcp@latest", "--headless", "--isolated"],
                    _workspaceRoot,
                    false,
                    new Dictionary<string, string>())),
            CreateMcp(
                "filesystem-workspace",
                "本地工程",
                "Filesystem MCP（仅当前工作区）",
                "Model Context Protocol",
                "把文件能力限制在当前任务工作区。NOVA 自带工程工具已足够时，无需重复加载。",
                "参考实现",
                "工作区读写",
                $"只允许访问 {_workspaceRoot}；每次写入仍由 NOVA 工具审批层确认。",
                "Node.js · npm/npx",
                "https://github.com/modelcontextprotocol/servers/tree/main/src/filesystem",
                ["npx"],
                [],
                mayAcquireSoftware: true,
                new McpServerRegistration(
                    "filesystem-workspace",
                    "npx",
                    ["-y", "@modelcontextprotocol/server-filesystem", _workspaceRoot],
                    _workspaceRoot,
                    false,
                    new Dictionary<string, string>())),
            CreateSkill(
                "engineering-closure",
                "编程协作",
                "工程闭环",
                "NOVA",
                "让编码任务从理解、实现、验证走到可运行交付，拒绝只写样例或口头宣称完成。",
                "内置审阅",
                "本地指令",
                "只读取一份 SKILL.md；不包含脚本，不扩大文件与命令权限。",
                EngineeringClosureSkill),
            CreateSkill(
                "goal-expedition",
                "目标模式",
                "目标远征",
                "NOVA",
                "适合只给目标、线索很少的任务：主动探索约束、拆解路径、持续验证并以结果收口。",
                "内置审阅",
                "本地指令",
                "允许自主推演，不允许越过外部访问、危险写入或账号操作审批。",
                GoalExpeditionSkill),
            CreateSkill(
                "research-synthesis",
                "研究分析",
                "后台研策",
                "NOVA",
                "公开资料默认后台读取、交叉验证并给出来源；只有登录、表单或可视操作才请求打开浏览器。",
                "内置审阅",
                "公开网络",
                "Skill 本身不联网；调用后台研究工具前会展示域名和用途并由你授权。",
                ResearchSynthesisSkill),
            CreateSkill(
                "huaxia-warm-ux",
                "设计体验",
                "华夏温度体验",
                "NOVA",
                "用克制的中国色、礼貌而真实的反馈、可选择的提问和清晰交付，减少冰冷工具感。",
                "内置审阅",
                "本地指令",
                "只影响体验评审与表达方式，不写文件、不安装字体或素材。",
                HuaxiaWarmUxSkill)
        ];

    private static MarketplaceCatalogItem CreateMcp(
        string id,
        string category,
        string name,
        string publisher,
        string description,
        string trust,
        string risk,
        string permission,
        string requirements,
        string source,
        IReadOnlyList<string> commands,
        IReadOnlyList<string> environment,
        bool mayAcquireSoftware,
        McpServerRegistration registration)
        => new(
            id,
            MarketplaceCapabilityKind.Mcp,
            category,
            name,
            publisher,
            description,
            trust,
            risk,
            permission,
            requirements,
            source,
            commands,
            environment,
            mayAcquireSoftware,
            registration,
            null,
            false,
            false,
            string.Empty,
            string.Empty,
            "#C45A45");

    private static MarketplaceCatalogItem CreateSkill(
        string id,
        string category,
        string name,
        string publisher,
        string description,
        string trust,
        string risk,
        string permission,
        string instructions)
        => new(
            id,
            MarketplaceCapabilityKind.Skill,
            category,
            name,
            publisher,
            description,
            trust,
            risk,
            permission,
            "无需额外运行时",
            "内置于 NOVA AgentOS",
            [],
            [],
            false,
            null,
            new BundledSkillDefinition(id, instructions),
            false,
            false,
            string.Empty,
            string.Empty,
            "#78C8B6");

    private static bool CommandExists(string command)
    {
        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", string.Empty }
            : new[] { string.Empty };
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    if (File.Exists(Path.Combine(path, command + extension)))
                    {
                        return true;
                    }
                }
                catch
                {
                    // A malformed PATH entry should not hide the remaining prerequisites.
                }
            }
        }
        return false;
    }

    private const string EngineeringClosureSkill = """
        ---
        name: 工程闭环
        description: 将编码任务推进到真实、完整、可验证、可交付的结果。
        ---
        # 工程闭环

        先读取真实工程状态和已有约束，再形成最小可行计划。实现必须覆盖主路径、失败路径和与现有代码的衔接；不得用演示代码、伪数据或“应该可以”代替交付。

        完成前按项目风险运行构建、测试或启动验证，并核对产物确实存在。若验证失败，继续定位修正；若受外部条件阻塞，明确给出证据、影响和最短解除路径。最终回答先报告结果，再列关键文件、验证证据和仍存在的真实限制。

        此 Skill 不能绕过工具审批、工作区边界、外部写入确认或用户明确约束。
        """;

    private const string GoalExpeditionSkill = """
        ---
        name: 目标远征
        description: 在线索稀少时自主探索约束、寻找解法并以可验证结果收口。
        ---
        # 目标远征

        把用户目标视为成功条件，而不是一句待改写的提示词。先从工作区、现有产品状态和只读信息中寻找线索，记录假设并优先验证高风险假设。将路线拆成可逆的小步，每一步都应缩短与目标之间的距离。

        遇到失败时先尝试安全替代路径，并用证据更新计划。只有缺少会实质改变结果的用户选择、需要新权限或外部状态无法推进时才暂停。不能把自主性解释为无限权限。
        """;

    private const string ResearchSynthesisSkill = """
        ---
        name: 后台研策
        description: 对公开资料执行不打扰用户的后台研究、交叉验证与来源整理。
        ---
        # 后台研策

        对公开 HTTPS 资料，优先请求 NOVA 后台研究能力；获批后在后台读取正文，不打开本地浏览器。比较发布日期与事件日期，技术事实优先采用官方文档、代码仓库或原始论文，并把来源贴近结论。

        只有需要用户登录态、可视页面确认、表单、点击交互或用户明确要求“打开给我看”时，才请求浏览器控制。禁止后台读取 localhost、内网地址、嵌入凭据的 URL，禁止用研究授权执行写操作。
        """;

    private const string HuaxiaWarmUxSkill = """
        ---
        name: 华夏温度体验
        description: 用现代东方气质、真实反馈和低学习成本审视产品体验。
        ---
        # 华夏温度体验

        视觉采用克制的墨、玉、朱砂与暖纸色，重层级、留白和触感，不堆砌传统纹样。语言像可靠的同行：说明正在发生什么、为什么需要选择，以及选择后的影响。

        当问题可以结构化时，优先提供少量清晰选项，同时保留自由输入。运行中持续显示真实进度、当前动作、可暂停点和恢复方式；交付后让成果自然进入对话，不用突兀页面切换。
        """;
}
