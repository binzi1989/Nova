using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaDesktop.Services;

public sealed class WorkspaceToolHost
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".nova", ".vs", ".idea", "bin", "obj", "node_modules", ".dotnet-home",
        "dist", "build", "target", "coverage", ".venv", "venv", "__pycache__"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".xml", ".json", ".jsonl", ".md", ".txt", ".yml", ".yaml",
        ".toml", ".props", ".targets", ".sln", ".csproj", ".ts", ".tsx", ".js", ".jsx",
        ".css", ".scss", ".html", ".vue", ".svelte", ".astro", ".py", ".rb", ".php",
        ".rs", ".go", ".java", ".kt", ".kts", ".sql", ".ps1", ".sh", ".bat", ".cmd",
        ".wxml", ".wxss", ".wxs", ".axml", ".acss", ".swan", ".ttml", ".ttss",
        ".qml", ".qss"
    };

    private static readonly HashSet<string> TextFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dockerfile", "Makefile", "Procfile", "Gemfile", "Rakefile", ".editorconfig"
    };

    private readonly string _workspaceRoot;
    private readonly string _workspacePrefix;
    private readonly McpRegistryService _mcpRegistry;
    private readonly SkillRegistryService _skillRegistry;
    private readonly CapabilityCompassService _capabilityCompass;
    private readonly BackgroundWebResearchService _backgroundResearch = new();
    private readonly ProductivityInsightsService _productivityInsights;
    private readonly KnowledgeGraphService _knowledgeGraph;
    private readonly KnowledgeIndexService _knowledgeIndex;
    private readonly ArtifactRepositoryService _artifactRepository;
    private readonly DesktopControlService _desktopControl = new();
    private readonly Func<JsonObject, CancellationToken, Task<string>>? _parallelAgentHandler;
    private readonly Func<JsonObject, CancellationToken, Task<string>>? _scheduleTaskHandler;
    private readonly AgentScheduleService _scheduleService;
    private readonly EngineeringEvidenceLedgerService? _evidenceLedger;
    private readonly SideEffectReceiptService _sideEffectReceipts;
    private readonly string _taskId;
    private readonly IReadOnlyList<string>? _allowedWriteScopes;
    private readonly TextPatchPreviewService _patchPreview = new();

    public WorkspaceToolHost(
        string workspaceRoot,
        McpRegistryService? mcpRegistry = null,
        SkillRegistryService? skillRegistry = null,
        ProductivityInsightsService? productivityInsights = null,
        KnowledgeGraphService? knowledgeGraph = null,
        KnowledgeIndexService? knowledgeIndex = null,
        ArtifactRepositoryService? artifactRepository = null,
        Func<JsonObject, CancellationToken, Task<string>>? parallelAgentHandler = null,
        AgentScheduleService? scheduleService = null,
        Func<JsonObject, CancellationToken, Task<string>>? scheduleTaskHandler = null,
        EngineeringEvidenceLedgerService? evidenceLedger = null,
        string? taskId = null,
        IReadOnlyList<string>? allowedWriteScopes = null,
        SideEffectReceiptService? sideEffectReceipts = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _workspacePrefix = _workspaceRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _mcpRegistry = mcpRegistry ?? new McpRegistryService();
        _skillRegistry = skillRegistry ?? new SkillRegistryService();
        _capabilityCompass = new CapabilityCompassService(_mcpRegistry, _skillRegistry);
        _productivityInsights = productivityInsights ?? new ProductivityInsightsService();
        _knowledgeGraph = knowledgeGraph ?? new KnowledgeGraphService();
        _knowledgeIndex = knowledgeIndex ?? new KnowledgeIndexService();
        _artifactRepository = artifactRepository ?? new ArtifactRepositoryService();
        _parallelAgentHandler = parallelAgentHandler;
        _scheduleService = scheduleService ?? new AgentScheduleService();
        _scheduleTaskHandler = scheduleTaskHandler;
        _evidenceLedger = evidenceLedger;
        _taskId = string.IsNullOrWhiteSpace(taskId) ? "workspace" : taskId;
        _allowedWriteScopes = allowedWriteScopes?.Select(NormalizeWriteScope).ToArray();
        _sideEffectReceipts = sideEffectReceipts ?? new SideEffectReceiptService(
            Path.Combine(_workspaceRoot, ".nova", "side-effects"));
        Definitions =
        [
            .. CreateWorkspaceDefinitions(),
            Function(
                "recommend_task_capabilities",
                "Analyze the current goal and workspace, then rank only relevant MCP and Skill capabilities. This is read-only and never enables or installs anything.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["objective"] = StringProperty("Current task objective or capability question.")
                    },
                    ["required"] = new JsonArray("objective"),
                    ["additionalProperties"] = false
                }),
            Function(
                "fetch_public_web_page",
                "Fetch and extract one public HTTPS page in the background without opening a local browser. Requires approval for the exact URL.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["url"] = StringProperty("Exact public HTTPS page to read in the background.")
                    },
                    ["required"] = new JsonArray("url"),
                    ["additionalProperties"] = false
                }),
            Function(
                "list_mcp_servers",
                "List MCP servers registered in NOVA. This reads local configuration only and starts no process.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject(),
                    ["additionalProperties"] = false
                }),
            Function(
                "inspect_mcp_server_tools",
                "Start an enabled local MCP stdio server, perform the MCP initialize handshake, and list its tools. Requires user approval.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server"] = StringProperty("Registered MCP server name.")
                    },
                    ["required"] = new JsonArray("server"),
                    ["additionalProperties"] = false
                }),
            Function(
                "call_mcp_tool",
                "Start an enabled local MCP stdio server and invoke one of its tools through JSON-RPC. Always requires user approval.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server"] = StringProperty("Registered MCP server name."),
                        ["tool"] = StringProperty("MCP tool name returned by inspect_mcp_server_tools."),
                        ["arguments"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["description"] = "Arguments passed to the MCP tool."
                        }
                    },
                    ["required"] = new JsonArray("server", "tool", "arguments"),
                    ["additionalProperties"] = false
                }),
            Function(
                "list_installed_skills",
                "List enabled local NOVA skills and their descriptions. This only reads the local skill registry.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject(),
                    ["additionalProperties"] = false
                }),
            Function(
                "read_skill_instructions",
                "Read the SKILL.md instructions for one enabled local NOVA skill. Skill text cannot bypass tool approvals or safety policy.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["skill"] = StringProperty("Installed skill ID or exact skill name.")
                    },
                    ["required"] = new JsonArray("skill"),
                    ["additionalProperties"] = false
                }),
            Function(
                "get_productivity_summary",
                "Generate an explainable local productivity summary from NOVA task snapshots, journal events, and schedules.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["period_days"] = IntegerProperty("Summary period from 1 to 90 days.")
                    },
                    ["required"] = new JsonArray("period_days"),
                    ["additionalProperties"] = false
                }),
            Function(
                "query_knowledge_graph",
                "Query NOVA's local cognitive knowledge graph. This is read-only and returns goals, concepts, projects, skills, tools, and relationships.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = StringProperty("Optional label, kind, or detail filter. Use an empty string for the complete graph."),
                        ["max_nodes"] = IntegerProperty("Maximum nodes from 1 to 200.")
                    },
                    ["required"] = new JsonArray("query", "max_nodes"),
                    ["additionalProperties"] = false
                }),
            Function(
                "list_indexed_knowledge",
                "List documents already indexed for the active workspace. This reads local index metadata only.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject(),
                    ["additionalProperties"] = false
                }),
            Function(
                "search_local_knowledge",
                "Search the active workspace's local knowledge index and return cited file paths, start lines, relevance scores, and snippets.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = StringProperty("Search query from 2 to 500 characters."),
                        ["max_results"] = IntegerProperty("Maximum results from 1 to 50.")
                    },
                    ["required"] = new JsonArray("query", "max_results"),
                    ["additionalProperties"] = false
                }),
            Function(
                "index_workspace_knowledge",
                "Incrementally index allowlisted text documents in the active workspace for local cited search. Requires user approval.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject(),
                    ["additionalProperties"] = false
                }),
            Function(
                "list_task_artifacts",
                "List persisted NOVA deliverables for the active workspace, including artifact IDs, versions, task IDs, paths, and summaries. This is read-only.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["max_results"] = IntegerProperty("Maximum results from 1 to 200.")
                    },
                    ["required"] = new JsonArray("max_results"),
                    ["additionalProperties"] = false
                }),
            Function(
                "read_task_artifact",
                "Read one persisted NOVA deliverable by artifact ID and optional version. This returns repository content only and does not execute or modify the artifact.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["artifact_id"] = StringProperty("Artifact ID returned by list_task_artifacts."),
                        ["version"] = IntegerProperty("Optional artifact version. Use 0 for the latest version.")
                    },
                    ["required"] = new JsonArray("artifact_id", "version"),
                    ["additionalProperties"] = false
                }),
            Function(
                "list_desktop_windows",
                "List visible top-level Windows desktop windows with opaque window IDs. This is observation-only.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject(),
                    ["additionalProperties"] = false
                }),
            Function(
                "activate_desktop_window",
                "Bring one visible desktop window to the foreground. Requires user approval.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["window_id"] = StringProperty("Opaque window ID returned by list_desktop_windows.")
                    },
                    ["required"] = new JsonArray("window_id"),
                    ["additionalProperties"] = false
                }),
            Function(
                "open_browser_url",
                "Open an absolute HTTPS URL in the user's default browser. Requires user approval.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["url"] = StringProperty("Absolute HTTPS URL without embedded credentials.")
                    },
                    ["required"] = new JsonArray("url"),
                    ["additionalProperties"] = false
                }),
            Function(
                "type_text_to_window",
                "Type literal text into a visible, non-protected desktop window. No control characters or shortcuts. Requires approval.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["window_id"] = StringProperty("Opaque window ID returned by list_desktop_windows."),
                        ["text"] = StringProperty("Literal text from 1 to 1000 characters. It will be shown in the approval preview.")
                    },
                    ["required"] = new JsonArray("window_id", "text"),
                    ["additionalProperties"] = false
                }),
            Function(
                "send_window_key",
                "Send one allowlisted navigation key to a visible, non-protected desktop window. No modifiers or shortcuts. Requires approval.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["window_id"] = StringProperty("Opaque window ID returned by list_desktop_windows."),
                        ["key"] = StringProperty("One of ENTER, TAB, ESCAPE, BACKSPACE, LEFT, UP, RIGHT, DOWN, HOME, END, PAGEUP, PAGEDOWN, DELETE.")
                    },
                    ["required"] = new JsonArray("window_id", "key"),
                    ["additionalProperties"] = false
                }),
            Function(
                "click_window_point",
                "Click one bounded point inside a visible, non-protected desktop window using relative coordinates. Use only after list_desktop_windows and when a structured API is unavailable. Requires approval.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["window_id"] = StringProperty("Opaque window ID returned by list_desktop_windows."),
                        ["x_ratio"] = NumberProperty("Horizontal position from 0.01 to 0.99 inside the current window bounds."),
                        ["y_ratio"] = NumberProperty("Vertical position from 0.01 to 0.99 inside the current window bounds."),
                        ["target_description"] = StringProperty("Human-readable description of the control expected at this point.")
                    },
                    ["required"] = new JsonArray("window_id", "x_ratio", "y_ratio", "target_description"),
                    ["additionalProperties"] = false
                }),
            Function(
                "delegate_parallel_tasks",
                "Delegate two to four independent analysis subtasks to parallel model workers and merge their results. Requires user approval because it creates additional model requests.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["tasks"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["minItems"] = 2,
                            ["maxItems"] = 4,
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["title"] = StringProperty("Short worker role or subtask title."),
                                    ["instruction"] = StringProperty("Self-contained subtask instruction.")
                                },
                                ["required"] = new JsonArray("title", "instruction"),
                                ["additionalProperties"] = false
                            }
                        }
                    },
                    ["required"] = new JsonArray("tasks"),
                    ["additionalProperties"] = false
                }),
            Function(
                "list_scheduled_tasks",
                "List persistent NOVA scheduled tasks. This reads local schedule metadata only.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject(),
                    ["additionalProperties"] = false
                }),
            Function(
                "schedule_agent_task",
                $"Create a persistent one-time or recurring NOVA task. Current local time: {DateTimeOffset.Now:O}. Requires approval.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["name"] = StringProperty("Short schedule name."),
                        ["prompt"] = StringProperty("Self-contained agent goal to run."),
                        ["run_at"] = StringProperty("ISO 8601 timestamp with time-zone offset for a one-time run. Omit for recurring tasks."),
                        ["interval_minutes"] = IntegerProperty("Recurring interval from 5 to 10080 minutes. Omit for one-time tasks.")
                    },
                    ["required"] = new JsonArray("name", "prompt"),
                    ["additionalProperties"] = false
                }),
            Function(
                "disable_scheduled_task",
                "Disable a persistent NOVA scheduled task by ID. Requires approval.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = StringProperty("Schedule ID returned by list_scheduled_tasks.")
                    },
                    ["required"] = new JsonArray("id"),
                    ["additionalProperties"] = false
                })
        ];
    }

    public IReadOnlyList<JsonObject> Definitions { get; }

    private static IReadOnlyList<JsonObject> CreateWorkspaceDefinitions()
        =>
    [
        Function(
            "list_workspace_files",
            "List files beneath a directory in the active local workspace. Skips build outputs and hidden dependency folders.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["directory"] = StringProperty("Workspace-relative directory, or an empty string for the workspace root."),
                    ["max_depth"] = IntegerProperty("Maximum directory depth from 1 to 6.")
                },
                ["required"] = new JsonArray("directory", "max_depth"),
                ["additionalProperties"] = false
            }),
        Function(
            "read_text_file",
            "Read a UTF-8 text file from the active local workspace.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = StringProperty("Workspace-relative file path."),
                    ["max_chars"] = IntegerProperty("Maximum characters to return, from 256 to 120000.")
                },
                ["required"] = new JsonArray("path", "max_chars"),
                ["additionalProperties"] = false
            }),
        Function(
            "search_workspace_text",
            "Search text files in the active workspace for a literal or regular-expression query.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["query"] = StringProperty("Text or regular expression to find."),
                    ["file_pattern"] = StringProperty("File suffix filter such as .cs, .xaml, or * for all text files."),
                    ["use_regex"] = BooleanProperty("Whether query is a .NET regular expression.")
                },
                ["required"] = new JsonArray("query", "file_pattern", "use_regex"),
                ["additionalProperties"] = false
            }),
        Function(
            "write_text_file",
            "Create or replace a UTF-8 text file in the workspace. Existing content is backed up before replacement. Requires user approval.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = StringProperty("Workspace-relative destination path."),
                    ["content"] = StringProperty("Complete UTF-8 text content to write.")
                },
                ["required"] = new JsonArray("path", "content"),
                ["additionalProperties"] = false
            }),
        Function(
            "replace_text_in_file",
            "Replace one exact text block inside an existing UTF-8 workspace file. Prefer this over rewriting a complete existing file. The old text must match exactly. Requires user approval with a unified diff.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = StringProperty("Workspace-relative existing file path."),
                    ["old_text"] = StringProperty("Exact existing text block to replace, including whitespace."),
                    ["new_text"] = StringProperty("Replacement text."),
                    ["replace_all"] = BooleanProperty("Replace every exact occurrence. Use false unless the user clearly wants all occurrences.")
                },
                ["required"] = new JsonArray("path", "old_text", "new_text", "replace_all"),
                ["additionalProperties"] = false
            }),
        Function(
            "run_workspace_command",
            "Run an allowlisted development command in the workspace. Supported commands are dotnet build/test, Python pytest/compileall, cargo build/check/test, go build/test, git status/diff/log, and rg. Requires user approval.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["executable"] = StringProperty("One of: dotnet, python, cargo, go, git, rg."),
                    ["arguments"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Argument list. Do not include a shell, redirects, pipes, or command separators.",
                        ["items"] = new JsonObject { ["type"] = "string" }
                    }
                },
                ["required"] = new JsonArray("executable", "arguments"),
                ["additionalProperties"] = false
            })
    ];

    public bool RequiresApproval(string toolName)
        => toolName is "write_text_file"
            or "replace_text_in_file"
            or "run_workspace_command"
            or "fetch_public_web_page"
            or "inspect_mcp_server_tools"
            or "call_mcp_tool"
            or "activate_desktop_window"
            or "open_browser_url"
            or "type_text_to_window"
            or "send_window_key"
            or "click_window_point"
            or "delegate_parallel_tasks"
            or "index_workspace_knowledge"
            or "schedule_agent_task"
            or "disable_scheduled_task";

    public ToolApprovalRequest CreateApprovalRequest(string toolName, JsonObject arguments)
    {
        var preview = arguments.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (preview.Length > 700)
        {
            preview = preview[..700] + "\n…";
        }

        return toolName switch
        {
            "write_text_file" => CreateWriteApproval(arguments),
            "replace_text_in_file" => CreateTextEditApproval(arguments),
            "run_workspace_command" => new ToolApprovalRequest(
                toolName,
                $"允许运行 {arguments["executable"]?.GetValue<string>() ?? "本机命令"}？",
                "命令不会通过 shell 执行，只允许预设的开发命令，并被限制在当前工作区。",
                preview),
            "fetch_public_web_page" => new ToolApprovalRequest(
                toolName,
                $"允许后台读取 {GetSafeWebHost(arguments)}？",
                "NOVA 将在后台请求这一条公开 HTTPS 页面并抽取正文，不会打开本地浏览器、读取登录态、提交表单或访问内网。",
                preview),
            "inspect_mcp_server_tools" => new ToolApprovalRequest(
                toolName,
                $"允许启动 MCP Server {arguments["server"]?.GetValue<string>() ?? "未知"}？",
                "NOVA 将启动已注册的本地 stdio 进程，完成 MCP 初始化并读取其工具清单。",
                preview),
            "call_mcp_tool" => new ToolApprovalRequest(
                toolName,
                $"允许调用 MCP 工具 {arguments["tool"]?.GetValue<string>() ?? "未知"}？",
                "MCP 工具由外部本地进程执行。NOVA 会显示服务器、工具名和参数，本次授权不会复用于后续调用。",
                preview),
            "activate_desktop_window" => new ToolApprovalRequest(
                toolName,
                "允许切换当前桌面窗口？",
                "NOVA 将把指定的可见窗口带到前台，不会发送按键、点击或读取窗口内容。",
                preview),
            "open_browser_url" => new ToolApprovalRequest(
                toolName,
                $"允许在浏览器打开 {arguments["url"]?.GetValue<string>() ?? "HTTPS 地址"}？",
                "打开网址会向目标站点建立网络连接。NOVA 不会自动登录、提交表单或上传文件。",
                preview),
            "type_text_to_window" => new ToolApprovalRequest(
                toolName,
                "允许向指定窗口输入这段文字？",
                "文字将发送到当前窗口焦点。请检查目标窗口和完整文本；不会自动按 Enter，也不会使用快捷键。",
                preview),
            "send_window_key" => new ToolApprovalRequest(
                toolName,
                $"允许向指定窗口发送 {arguments["key"]?.GetValue<string>() ?? "按键"}？",
                "Enter 可能提交表单或发送消息。NOVA 不支持组合键，并禁止向终端、安全软件和密码管理器注入输入。",
                preview),
            "click_window_point" => new ToolApprovalRequest(
                toolName,
                $"允许点击“{arguments["target_description"]?.GetValue<string>() ?? "指定控件"}”？",
                "NOVA 将按当前窗口尺寸计算一次点击位置。窗口变化、弹窗或目标不明确时应停止；终端、安全软件、密码管理器和 NOVA 自身禁止注入。",
                preview),
            "delegate_parallel_tasks" => new ToolApprovalRequest(
                toolName,
                "允许启动并行模型工作组？",
                "NOVA 将向当前模型提供商额外发送 2–4 个独立请求，可能产生额外 Token 费用。",
                preview),
            "index_workspace_knowledge" => new ToolApprovalRequest(
                toolName,
                "允许为当前工作区建立本地知识索引？",
                "NOVA 将读取允许的文本文件并把分块索引保存到本机。不会上传文件、执行脚本或读取 .git、依赖目录和构建输出。",
                preview),
            "schedule_agent_task" => new ToolApprovalRequest(
                toolName,
                $"允许创建计划任务 {arguments["name"]?.GetValue<string>() ?? "未命名"}？",
                "计划任务会在未来自动创建模型请求，周期任务可能持续产生 Token 费用。NOVA 必须保持运行并可访问对应模型密钥。",
                preview),
            "disable_scheduled_task" => new ToolApprovalRequest(
                toolName,
                $"允许停用计划任务 {arguments["id"]?.GetValue<string>() ?? "未知"}？",
                "NOVA 将阻止该计划任务的后续自动运行；已有任务记录不会删除。",
                preview),
            _ => new ToolApprovalRequest(toolName, "允许执行本机工具？", "此工具需要你的确认。", preview)
        };
    }

    private static string GetSafeWebHost(JsonObject arguments)
    {
        try
        {
            return BackgroundWebResearchService.ParsePublicHttpsUri(
                arguments["url"]?.GetValue<string>() ?? string.Empty).Host;
        }
        catch
        {
            return "指定公开页面";
        }
    }

    private ToolApprovalRequest CreateWriteApproval(JsonObject arguments)
    {
        var relativePath = arguments["path"]?.GetValue<string>() ?? "文件";
        var proposed = arguments["content"]?.GetValue<string>() ?? string.Empty;
        var safeArguments = JsonSerializer.Serialize(new
        {
            path = relativePath,
            proposed_characters = proposed.Length
        });

        try
        {
            var fullPath = ResolvePath(relativePath, mustExist: false);
            EnsureTextFile(fullPath);
            var original = string.Empty;
            var originalExists = File.Exists(fullPath);
            arguments["_nova_original_exists"] = originalExists;
            arguments["_nova_original_sha256"] = originalExists
                ? ComputeFileHash(fullPath)
                : string.Empty;
            if (originalExists)
            {
                var fileInfo = new FileInfo(fullPath);
                if (fileInfo.Length > 2_000_000)
                {
                    return new ToolApprovalRequest(
                        "write_text_file",
                        $"大型文件写入需要审查：{relativePath}",
                        $"当前文件为 {fileInfo.Length:N0} 字节，超过 Patch 预览上限。NOVA 不会伪造不完整 Diff；建议拒绝并让 Agent 缩小修改范围。",
                        safeArguments,
                        "unified-diff",
                        $"PATCH PREVIEW UNAVAILABLE{Environment.NewLine}{Environment.NewLine}"
                        + $"当前文件：{fileInfo.Length:N0} bytes{Environment.NewLine}"
                        + $"拟写入：{proposed.Length:N0} characters{Environment.NewLine}{Environment.NewLine}"
                        + "为了避免不完整预览造成误判，NOVA 没有生成近似 Diff。");
                }
                original = File.ReadAllText(fullPath);
            }

            var patch = _patchPreview.Create(relativePath, original, proposed, originalExists);
            return new ToolApprovalRequest(
                "write_text_file",
                $"审查并写入 {relativePath}？",
                $"{patch.Summary}。批准后 NOVA 才会写入；现有文件会先保存到 .nova/recovery。",
                safeArguments,
                "unified-diff",
                patch.UnifiedDiff,
                patch.Additions,
                patch.Deletions);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or NotSupportedException)
        {
            return new ToolApprovalRequest(
                "write_text_file",
                $"无法生成 {relativePath} 的 Patch",
                $"预审失败：{exception.Message}。建议拒绝本次写入并让 Agent 检查路径或文件类型。",
                safeArguments);
        }
    }

    private ToolApprovalRequest CreateTextEditApproval(JsonObject arguments)
    {
        var relativePath = arguments["path"]?.GetValue<string>() ?? "文件";
        var safeArguments = JsonSerializer.Serialize(new
        {
            path = relativePath,
            operation = "exact text replacement",
            replace_all = arguments["replace_all"]?.GetValue<bool>() ?? false
        });

        try
        {
            var fullPath = ResolvePath(relativePath, mustExist: true);
            EnsureTextFile(fullPath);
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > 2_000_000)
            {
                throw new InvalidOperationException("文件超过 2 MB，请缩小目标或使用专用工具。");
            }

            var original = File.ReadAllText(fullPath);
            var oldText = RequireString(arguments, "old_text");
            var newText = arguments["new_text"]?.GetValue<string>() ?? string.Empty;
            if (oldText.Length > 200_000 || newText.Length > 200_000)
            {
                throw new InvalidOperationException("单次精确编辑不能超过 200,000 个字符。");
            }

            var replaceAll = arguments["replace_all"]?.GetValue<bool>() ?? false;
            var occurrences = CountOccurrences(original, oldText);
            if (occurrences == 0)
            {
                throw new InvalidOperationException("old_text 与当前文件不完全匹配，请重新读取文件后再编辑。");
            }
            if (occurrences > 1 && !replaceAll)
            {
                throw new InvalidOperationException(
                    $"old_text 在文件中出现 {occurrences} 次。请提供更长的唯一上下文，或明确使用 replace_all。");
            }

            arguments["_nova_original_exists"] = true;
            arguments["_nova_original_sha256"] = ComputeFileHash(fullPath);
            var proposed = replaceAll
                ? original.Replace(oldText, newText, StringComparison.Ordinal)
                : ReplaceFirst(original, oldText, newText);
            var patch = _patchPreview.Create(relativePath, original, proposed, originalExists: true);
            return new ToolApprovalRequest(
                "replace_text_in_file",
                $"审查并编辑 {relativePath}？",
                $"{patch.Summary} · 精确匹配 {occurrences} 处。批准后才会写入，并先保存恢复副本。",
                safeArguments,
                "unified-diff",
                patch.UnifiedDiff,
                patch.Additions,
                patch.Deletions);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or NotSupportedException)
        {
            return new ToolApprovalRequest(
                "replace_text_in_file",
                $"无法生成 {relativePath} 的精确编辑",
                $"预审失败：{exception.Message}。建议拒绝并让 Agent 重新读取目标文件。",
                safeArguments);
        }
    }

    public Task RecordApprovalDecisionAsync(
        string toolName,
        JsonObject arguments,
        bool approved,
        CancellationToken cancellationToken = default)
        => RecordApprovalDecisionAsync(
            toolName,
            arguments,
            approved,
            operationId: null,
            cancellationToken);

    public async Task RecordApprovalDecisionAsync(
        string toolName,
        JsonObject arguments,
        bool approved,
        string? operationId,
        CancellationToken cancellationToken = default)
    {
        if (_evidenceLedger is null)
        {
            return;
        }

        try
        {
            await _evidenceLedger.AppendAsync(
                _taskId,
                _workspaceRoot,
                "approval",
                toolName,
                DescribeEvidenceTarget(toolName, arguments),
                approved ? "approved" : "denied",
                IsMutatingTool(toolName, arguments),
                null,
                TimeSpan.Zero,
                null,
                approved
                    ? "当前用户授权策略允许此操作；权限范围与有效期由 NOVA 权限管家记录。"
                      + FormatOperationReference(operationId)
                    : "用户拒绝本次操作；工具没有执行。"
                      + FormatOperationReference(operationId),
                cancellationToken);
        }
        catch (Exception exception) when (IsEvidencePersistenceFailure(exception))
        {
            // Evidence persistence must not silently turn an approved tool into a failed tool.
        }
    }

    private static string FormatOperationReference(string? operationId)
        => string.IsNullOrWhiteSpace(operationId)
            ? string.Empty
            : $" Operation ID: {operationId}.";

    public Task<string> ExecuteAsync(
        string toolName,
        JsonObject arguments,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            toolName,
            arguments,
            operationId: null,
            approvalReference: null,
            cancellationToken);

    public async Task<string> ExecuteAsync(
        string toolName,
        JsonObject arguments,
        string? operationId,
        string? approvalReference,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        SideEffectBeginResult? sideEffect = null;
        var actionReturned = false;
        try
        {
            EnsureWriteOwnership(toolName, arguments);
            if (IsMutatingTool(toolName, arguments))
            {
                var resolvedOperationId = string.IsNullOrWhiteSpace(operationId)
                    ? Guid.NewGuid().ToString("N")
                    : operationId;
                sideEffect = await _sideEffectReceipts.BeginAsync(
                    _taskId,
                    resolvedOperationId,
                    toolName,
                    DescribeEvidenceTarget(toolName, arguments),
                    arguments.ToJsonString(),
                    approvalReference,
                    CaptureSideEffectFingerprint(toolName, arguments),
                    cancellationToken);
                if (sideEffect.IsCommittedReplay)
                {
                    var replayOutput = sideEffect.Receipt.Output
                                       ?? JsonSerializer.Serialize(new
                                       {
                                           status = "replayed",
                                           operation_id = sideEffect.Receipt.OperationId,
                                           message = "The committed side effect was not executed again."
                                       });
                    await RecordExecutionAsync(
                        toolName,
                        arguments,
                        "replayed",
                        null,
                        Stopwatch.GetElapsedTime(startedAt),
                        replayOutput,
                        $"已命中 Commit 收据 {sideEffect.Receipt.IdempotencyKey[..12]}，未重复执行。",
                        cancellationToken);
                    return replayOutput;
                }
            }

            var result = toolName switch
            {
                "list_workspace_files" => ListWorkspaceFiles(arguments),
                "read_text_file" => await ReadTextFileAsync(arguments, cancellationToken),
                "search_workspace_text" => await SearchWorkspaceTextAsync(arguments, cancellationToken),
                "write_text_file" => await WriteTextFileAsync(arguments, cancellationToken),
                "replace_text_in_file" => await ReplaceTextInFileAsync(arguments, cancellationToken),
                "run_workspace_command" => await RunWorkspaceCommandAsync(arguments, cancellationToken),
                "recommend_task_capabilities" => JsonSerializer.Serialize(
                    _capabilityCompass.Analyze(
                        RequireString(arguments, "objective"),
                        _workspaceRoot)),
                "fetch_public_web_page" => await _backgroundResearch.FetchPublicPageAsync(
                    RequireString(arguments, "url"),
                    cancellationToken),
                "list_mcp_servers" => _mcpRegistry.ListServers(),
                "inspect_mcp_server_tools" => await _mcpRegistry.InspectToolsAsync(
                    RequireString(arguments, "server"),
                    _workspaceRoot,
                    cancellationToken),
                "call_mcp_tool" => await _mcpRegistry.CallToolAsync(
                    RequireString(arguments, "server"),
                    RequireString(arguments, "tool"),
                    arguments["arguments"]?.AsObject() ?? new JsonObject(),
                    _workspaceRoot,
                    cancellationToken),
                "list_installed_skills" => _skillRegistry.ListForModel(),
                "read_skill_instructions" => _skillRegistry.ReadInstructions(
                    RequireString(arguments, "skill")),
                "get_productivity_summary" => _productivityInsights.GenerateJson(
                    Math.Clamp(arguments["period_days"]?.GetValue<int>() ?? 7, 1, 90)),
                "query_knowledge_graph" => _knowledgeGraph.QueryJson(
                    arguments["query"]?.GetValue<string>(),
                    Math.Clamp(arguments["max_nodes"]?.GetValue<int>() ?? 80, 1, 200)),
                "list_indexed_knowledge" => _knowledgeIndex.ListDocumentsJson(_workspaceRoot),
                "search_local_knowledge" => _knowledgeIndex.SearchJson(
                    RequireString(arguments, "query"),
                    _workspaceRoot,
                    Math.Clamp(arguments["max_results"]?.GetValue<int>() ?? 12, 1, 50)),
                "index_workspace_knowledge" => JsonSerializer.Serialize(
                    await _knowledgeIndex.IndexWorkspaceAsync(_workspaceRoot, cancellationToken)),
                "list_task_artifacts" => _artifactRepository.ListJson(
                    _workspaceRoot,
                    Math.Clamp(arguments["max_results"]?.GetValue<int>() ?? 50, 1, 200)),
                "read_task_artifact" => _artifactRepository.ReadJson(
                    RequireString(arguments, "artifact_id"),
                    (arguments["version"]?.GetValue<int>() ?? 0) is > 0 and var artifactVersion
                        ? artifactVersion
                        : null),
                "list_desktop_windows" => _desktopControl.ListWindows(),
                "activate_desktop_window" => _desktopControl.ActivateWindow(arguments),
                "open_browser_url" => _desktopControl.OpenBrowserUrl(arguments),
                "type_text_to_window" => await _desktopControl.TypeTextAsync(arguments, cancellationToken),
                "send_window_key" => await _desktopControl.SendKeyAsync(arguments, cancellationToken),
                "click_window_point" => await _desktopControl.ClickWindowPointAsync(arguments, cancellationToken),
                "delegate_parallel_tasks" => _parallelAgentHandler is null
                    ? JsonSerializer.Serialize(new { status = "unavailable", message = "Parallel model workers are not configured." })
                    : await _parallelAgentHandler(arguments, cancellationToken),
                "list_scheduled_tasks" => _scheduleService.ListSchedules(),
                "schedule_agent_task" => _scheduleTaskHandler is null
                    ? JsonSerializer.Serialize(new { status = "unavailable", message = "Scheduled task context is not configured." })
                    : await _scheduleTaskHandler(arguments, cancellationToken),
                "disable_scheduled_task" => await _scheduleService.DisableAsync(
                    RequireString(arguments, "id"),
                    cancellationToken),
                _ => JsonSerializer.Serialize(new { error = $"Unknown tool: {toolName}" })
            };
            actionReturned = true;
            if (sideEffect is not null)
            {
                await _sideEffectReceipts.CommitAsync(
                    sideEffect.Receipt,
                    CaptureSideEffectFingerprint(toolName, arguments),
                    result,
                    cancellationToken);
            }
            var exitCode = TryReadExitCode(toolName, result);
            await RecordExecutionAsync(
                toolName,
                arguments,
                exitCode is null or 0 ? "completed" : "failed",
                exitCode,
                Stopwatch.GetElapsedTime(startedAt),
                result,
                exitCode is null
                    ? $"工具成功返回 {result.Length:N0} 个字符。"
                    : $"受控命令退出码：{exitCode}。",
                cancellationToken);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (sideEffect is not null && !actionReturned)
            {
                try
                {
                    await _sideEffectReceipts.FailAsync(
                        sideEffect.Receipt,
                        exception.Message,
                        CancellationToken.None);
                }
                catch (Exception receiptException) when (IsEvidencePersistenceFailure(receiptException)
                                                        || receiptException is JsonException)
                {
                    // The original tool error remains primary; a missing terminal receipt is
                    // intentionally treated as uncertain on the next replay.
                }
            }
            await RecordExecutionAsync(
                toolName,
                arguments,
                "error",
                null,
                Stopwatch.GetElapsedTime(startedAt),
                exception.Message,
                exception.Message,
                cancellationToken);
            throw;
        }
    }

    private string? CaptureSideEffectFingerprint(
        string toolName,
        JsonObject arguments)
    {
        if (toolName is not ("write_text_file" or "replace_text_in_file"))
        {
            return null;
        }

        var relativePath = arguments["path"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var path = ResolvePath(relativePath, mustExist: false);
        return SideEffectReceiptService.ComputeFingerprint(path);
    }

    private string ListWorkspaceFiles(JsonObject arguments)
    {
        var directory = arguments["directory"]?.GetValue<string>() ?? string.Empty;
        var maxDepth = Math.Clamp(arguments["max_depth"]?.GetValue<int>() ?? 3, 1, 6);
        var root = ResolvePath(directory, mustExist: true);
        if (!Directory.Exists(root))
        {
            throw new InvalidOperationException("The requested directory does not exist.");
        }

        var files = EnumerateFiles(root, maxDepth)
            .Take(500)
            .Select(path => Path.GetRelativePath(_workspaceRoot, path))
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            workspace = _workspaceRoot,
            directory,
            count = files.Length,
            truncated = files.Length >= 500,
            files
        });
    }

    private async Task<string> ReadTextFileAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var relativePath = RequireString(arguments, "path");
        var maxChars = Math.Clamp(arguments["max_chars"]?.GetValue<int>() ?? 30000, 256, 120000);
        var path = ResolvePath(relativePath, mustExist: true);
        EnsureTextFile(path);

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var truncated = text.Length > maxChars;
        if (truncated)
        {
            text = text[..maxChars];
        }

        return JsonSerializer.Serialize(new
        {
            path = relativePath,
            chars = text.Length,
            truncated,
            content = text
        });
    }

    private async Task<string> SearchWorkspaceTextAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var query = RequireString(arguments, "query");
        var pattern = arguments["file_pattern"]?.GetValue<string>() ?? "*";
        var useRegex = arguments["use_regex"]?.GetValue<bool>() ?? false;
        Regex? regex = null;
        if (useRegex)
        {
            regex = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }

        var results = new List<object>();
        foreach (var file in EnumerateFiles(_workspaceRoot, 12))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pattern != "*" && !file.EndsWith(pattern, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TextExtensions.Contains(Path.GetExtension(file)))
            {
                continue;
            }

            var lineNumber = 0;
            var fileMatchCount = 0;
            foreach (var line in await File.ReadAllLinesAsync(file, cancellationToken))
            {
                lineNumber++;
                var matches = regex?.IsMatch(line) ?? line.Contains(query, StringComparison.OrdinalIgnoreCase);
                if (!matches)
                {
                    continue;
                }

                results.Add(new
                {
                    path = Path.GetRelativePath(_workspaceRoot, file),
                    line = lineNumber,
                    text = line.Length > 360 ? line[..360] + "…" : line
                });
                fileMatchCount++;
                if (results.Count >= 80 || fileMatchCount >= 12)
                {
                    break;
                }
            }

            if (results.Count >= 80)
            {
                break;
            }
        }

        return JsonSerializer.Serialize(new { query, count = results.Count, truncated = results.Count >= 80, results });
    }

    private async Task<string> WriteTextFileAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var relativePath = RequireString(arguments, "path");
        var content = arguments["content"]?.GetValue<string>() ?? string.Empty;
        if (content.Length > 1_500_000)
        {
            throw new InvalidOperationException("The requested write exceeds the 1.5 MB safety limit.");
        }

        var path = ResolvePath(relativePath, mustExist: false);
        EnsureTextFile(path);
        ValidateApprovedBaseline(arguments, path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string? backup = null;
        if (File.Exists(path))
        {
            var recoveryRoot = Path.Combine(_workspaceRoot, ".nova", "recovery", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var backupPath = Path.Combine(recoveryRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(path, backupPath, overwrite: true);
            backup = Path.GetRelativePath(_workspaceRoot, backupPath);
        }

        await File.WriteAllTextAsync(path, content, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            path = relativePath,
            chars = content.Length,
            backup,
            status = "written"
        });
    }

    private async Task<string> ReplaceTextInFileAsync(
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var relativePath = RequireString(arguments, "path");
        var oldText = RequireString(arguments, "old_text");
        var newText = arguments["new_text"]?.GetValue<string>() ?? string.Empty;
        var replaceAll = arguments["replace_all"]?.GetValue<bool>() ?? false;
        var path = ResolvePath(relativePath, mustExist: true);
        EnsureTextFile(path);
        ValidateApprovedBaseline(arguments, path);

        var original = await File.ReadAllTextAsync(path, cancellationToken);
        var occurrences = CountOccurrences(original, oldText);
        if (occurrences == 0 || (occurrences > 1 && !replaceAll))
        {
            throw new InvalidOperationException(
                occurrences == 0
                    ? "目标文本已变化，NOVA 拒绝应用过期编辑。"
                    : "目标文本不唯一，NOVA 拒绝可能作用于错误位置的编辑。");
        }

        var proposed = replaceAll
            ? original.Replace(oldText, newText, StringComparison.Ordinal)
            : ReplaceFirst(original, oldText, newText);
        var recoveryRoot = Path.Combine(
            _workspaceRoot,
            ".nova",
            "recovery",
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        var backupPath = Path.Combine(recoveryRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.Copy(path, backupPath, overwrite: true);
        await File.WriteAllTextAsync(path, proposed, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            path = relativePath,
            replacements = replaceAll ? occurrences : 1,
            backup = Path.GetRelativePath(_workspaceRoot, backupPath),
            status = "edited"
        });
    }

    private async Task<string> RunWorkspaceCommandAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var executable = RequireString(arguments, "executable").ToLowerInvariant();
        var args = arguments["arguments"]?.AsArray().Select(item => item?.GetValue<string>() ?? string.Empty).ToArray()
                   ?? [];
        ValidateCommand(executable, args);

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveExecutable(executable),
            WorkingDirectory = _workspaceRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The command exceeded the 3 minute execution limit.");
        }

        var output = await outputTask;
        var error = await errorTask;
        return JsonSerializer.Serialize(new
        {
            executable,
            arguments = args,
            exit_code = process.ExitCode,
            stdout = Limit(output, 40000),
            stderr = Limit(error, 20000)
        });
    }

    private string ResolvePath(string relativePath, bool mustExist)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Only workspace-relative paths are allowed.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(_workspaceRoot, relativePath));
        if (!fullPath.Equals(_workspaceRoot, StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(_workspacePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The requested path escapes the active workspace.");
        }

        if (fullPath.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || fullPath.EndsWith($"{Path.DirectorySeparatorChar}.git", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Direct access to .git is not allowed.");
        }

        EnsureNoReparsePoint(fullPath);

        if (mustExist && !File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException("The requested workspace path does not exist.");
        }

        return fullPath;
    }

    private void EnsureWriteOwnership(string toolName, JsonObject arguments)
    {
        if (_allowedWriteScopes is null
            || toolName is not ("write_text_file" or "replace_text_in_file"))
        {
            return;
        }
        var relativePath = RequireString(arguments, "path");
        var fullPath = ResolvePath(relativePath, mustExist: false);
        var normalized = Path.GetRelativePath(_workspaceRoot, fullPath)
            .Replace('\\', '/');
        var allowed = _allowedWriteScopes.Any(scope =>
            scope.EndsWith("/", StringComparison.Ordinal)
                ? normalized.StartsWith(scope, StringComparison.OrdinalIgnoreCase)
                : normalized.Equals(scope, StringComparison.OrdinalIgnoreCase));
        if (!allowed)
        {
            throw new InvalidOperationException(
                $"Agent Mesh ownership violation: '{normalized}' is outside this worker's write scopes "
                + $"[{string.Join(", ", _allowedWriteScopes)}].");
        }
    }

    private static string NormalizeWriteScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || Path.IsPathRooted(scope))
        {
            throw new InvalidOperationException(
                "Agent Mesh write scopes must be non-empty workspace-relative paths.");
        }
        var normalized = scope.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }
        if (normalized.Length == 0
            || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or "..")
            || normalized.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains('*')
            || normalized.Contains('?'))
        {
            throw new InvalidOperationException(
                $"Agent Mesh write scope is unsafe or ambiguous: '{scope}'.");
        }
        return normalized;
    }

    private void EnsureNoReparsePoint(string fullPath)
    {
        var relative = Path.GetRelativePath(_workspaceRoot, fullPath);
        if (relative == ".")
        {
            return;
        }

        var current = _workspaceRoot;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                continue;
            }

            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    "The requested path crosses a symbolic link or reparse point. NOVA requires a physical workspace path.");
            }
        }
    }

    private static void ValidateApprovedBaseline(JsonObject arguments, string path)
    {
        var expectedExists = arguments["_nova_original_exists"]?.GetValue<bool>();
        var expectedHash = arguments["_nova_original_sha256"]?.GetValue<string>();
        if (expectedExists is null || expectedHash is null)
        {
            return;
        }

        var existsNow = File.Exists(path);
        if (existsNow != expectedExists.Value)
        {
            throw new InvalidOperationException(
                "The target file changed after Patch approval. NOVA refused the write; generate a fresh Patch preview.");
        }

        if (existsNow
            && !ComputeFileHash(path).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The target file changed after Patch approval. NOVA refused the write; generate a fresh Patch preview.");
        }
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private IEnumerable<string> EnumerateFiles(string root, int maxDepth)
    {
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((root, 0));

        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory)
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(directory)
                    .OrderByDescending(
                        Path.GetFileName,
                        StringComparer.OrdinalIgnoreCase);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in directories)
            {
                if (!IgnoredDirectories.Contains(Path.GetFileName(child)))
                {
                    pending.Push((child, depth + 1));
                }
            }
        }
    }

    private static void ValidateCommand(string executable, IReadOnlyList<string> arguments)
    {
        if (arguments.Any(value => value.Contains('\n') || value.Contains('\r')))
        {
            throw new InvalidOperationException("Command arguments may not contain newlines.");
        }

        if (arguments.Any(value => Path.IsPathRooted(value)
                                   || value.Split('/', '\\').Any(segment => segment == "..")))
        {
            throw new InvalidOperationException("Command arguments may not reference paths outside the workspace.");
        }

        var first = arguments.FirstOrDefault()?.ToLowerInvariant() ?? string.Empty;
        var blockedFlags = executable switch
        {
            "rg" => new[] { "--pre", "--hostname-bin" },
            "git" => new[] { "--ext-diff", "--textconv", "--no-index", "--output", "-c", "--config-env" },
            "dotnet" => new[] { "--interactive" },
            "python" => new[] { "-c", "-i" },
            "cargo" => new[] { "--config" },
            "go" => new[] { "-exec" },
            _ => []
        };
        if (arguments.Any(argument => blockedFlags.Any(flag =>
                argument.Equals(flag, StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException("The command contains a flag that can escape the controlled execution boundary.");
        }

        var allowed = executable switch
        {
            "dotnet" => first is "build" or "test",
            "git" => first is "status" or "diff" or "log",
            "rg" => arguments.Count > 0,
            "python" => first == "-m"
                        && arguments.Count >= 2
                        && (arguments[1].Equals("pytest", StringComparison.OrdinalIgnoreCase)
                            || arguments[1].Equals("compileall", StringComparison.OrdinalIgnoreCase)),
            "cargo" => first is "build" or "check" or "test",
            "go" => first is "build" or "test",
            _ => false
        };

        if (!allowed)
        {
            throw new InvalidOperationException("The command is outside NOVA's development-command allowlist.");
        }
    }

    private string ResolveExecutable(string executable)
    {
        var executableName = executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? executable
            : executable + ".exe";
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var entry in pathEntries)
        {
            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(entry, executableName));
            }
            catch
            {
                continue;
            }

            if (candidate.StartsWith(_workspacePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"The allowlisted executable '{executable}' was not found on PATH.");
    }

    private static void EnsureTextFile(string path)
    {
        var extension = Path.GetExtension(path);
        var fileName = Path.GetFileName(path);
        if (!TextExtensions.Contains(extension)
            && !TextFileNames.Contains(fileName)
            && !fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Files with the '{extension}' extension are not enabled for text tools.");
        }
    }

    private static string RequireString(JsonObject arguments, string name)
    {
        var value = arguments[name]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing required argument: {name}");
        }

        return value;
    }

    private async Task RecordExecutionAsync(
        string toolName,
        JsonObject arguments,
        string outcome,
        int? exitCode,
        TimeSpan duration,
        string output,
        string summary,
        CancellationToken cancellationToken)
    {
        if (_evidenceLedger is null)
        {
            return;
        }

        try
        {
            await _evidenceLedger.AppendAsync(
                _taskId,
                _workspaceRoot,
                "tool",
                toolName,
                DescribeEvidenceTarget(toolName, arguments),
                outcome,
                IsMutatingTool(toolName, arguments),
                exitCode,
                duration,
                output,
                summary,
                cancellationToken);
        }
        catch (Exception exception) when (IsEvidencePersistenceFailure(exception))
        {
            // The primary tool result remains authoritative when local evidence storage is unavailable.
        }
    }

    private static bool IsEvidencePersistenceFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException;

    private static string DescribeEvidenceTarget(string toolName, JsonObject arguments)
    {
        if (toolName == "run_workspace_command")
        {
            var executable = arguments["executable"]?.GetValue<string>() ?? "command";
            var commandArguments = arguments["arguments"]?.AsArray()
                .Select(item => item?.GetValue<string>() ?? string.Empty)
                .ToArray() ?? [];
            return Limit($"{executable} {string.Join(' ', commandArguments)}".Trim(), 500);
        }

        var target = arguments["path"]?.GetValue<string>()
                     ?? arguments["directory"]?.GetValue<string>()
                     ?? arguments["server"]?.GetValue<string>()
                     ?? arguments["tool"]?.GetValue<string>()
                     ?? arguments["artifact_id"]?.GetValue<string>()
                     ?? arguments["window_id"]?.GetValue<string>()
                     ?? arguments["url"]?.GetValue<string>()
                     ?? arguments["name"]?.GetValue<string>()
                     ?? arguments["id"]?.GetValue<string>()
                     ?? arguments["query"]?.GetValue<string>()
                     ?? "workspace";
        return Limit(target, 500);
    }

    private static bool IsMutatingTool(string toolName, JsonObject arguments)
    {
        if (toolName == "run_workspace_command")
        {
            return arguments["executable"]?.GetValue<string>()
                ?.Equals("dotnet", StringComparison.OrdinalIgnoreCase) == true;
        }

        return toolName is "write_text_file"
            or "replace_text_in_file"
            or "call_mcp_tool"
            or "activate_desktop_window"
            or "open_browser_url"
            or "type_text_to_window"
            or "send_window_key"
            or "delegate_parallel_tasks"
            or "index_workspace_knowledge"
            or "schedule_agent_task"
            or "disable_scheduled_task";
    }

    private static int? TryReadExitCode(string toolName, string output)
    {
        if (toolName != "run_workspace_command")
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(output)?["exit_code"]?.GetValue<int>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Limit(string value, int max)
        => value.Length <= max ? value : value[..max] + "\n… output truncated …";

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string source, string oldText, string newText)
    {
        var index = source.IndexOf(oldText, StringComparison.Ordinal);
        return index < 0
            ? source
            : string.Concat(
                source.AsSpan(0, index),
                newText,
                source.AsSpan(index + oldText.Length));
    }

    private static JsonObject Function(string name, string description, JsonObject parameters)
        => new()
        {
            ["type"] = "function",
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = parameters,
            ["strict"] = true
        };

    private static JsonObject StringProperty(string description)
        => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject IntegerProperty(string description)
        => new() { ["type"] = "integer", ["description"] = description };

    private static JsonObject NumberProperty(string description)
        => new() { ["type"] = "number", ["description"] = description };

    private static JsonObject BooleanProperty(string description)
        => new() { ["type"] = "boolean", ["description"] = description };
}
