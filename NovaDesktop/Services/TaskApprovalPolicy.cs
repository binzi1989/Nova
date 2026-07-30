namespace NovaDesktop.Services;

public sealed record ApprovalScope(
    string Key,
    string Label,
    string TrustActionLabel,
    string SafetyNote,
    bool CanTrustForRun);

/// <summary>
/// Keeps deliberately granted, bounded approval scopes in memory for one execution run.
/// It never persists grants and never widens the underlying workspace, command, network,
/// MCP, or desktop safety checks.
/// </summary>
public sealed class TaskApprovalPolicy
{
    private readonly HashSet<string> _grantedScopes = new(StringComparer.Ordinal);
    private string? _runId;

    public void BeginRun(string taskId)
    {
        _runId = taskId;
        _grantedScopes.Clear();
    }

    public void EndRun()
    {
        _runId = null;
        _grantedScopes.Clear();
    }

    public bool IsGranted(string taskId, ApprovalScope scope)
        => scope.CanTrustForRun
           && string.Equals(_runId, taskId, StringComparison.Ordinal)
           && _grantedScopes.Contains(scope.Key);

    public bool GrantForRun(string taskId, ApprovalScope scope)
    {
        if (!scope.CanTrustForRun
            || !string.Equals(_runId, taskId, StringComparison.Ordinal))
        {
            return false;
        }

        return _grantedScopes.Add(scope.Key);
    }

    public ApprovalScope Describe(ToolApprovalRequest request)
    {
        var commonSafety =
            "只在当前这轮任务里有效；任务结束后自动收回。路径、命令与网络边界仍会逐次校验，风险升级时我会再来问你。";

        return request.ToolName switch
        {
            "write_text_file" or "replace_text_in_file"
                when IsReviewablePatch(request) => new ApprovalScope(
                    "workspace-safe-write",
                    "工作区内安全文件修改",
                    "本轮信任安全修改",
                    commonSafety,
                    true),
            "run_workspace_command" => new ApprovalScope(
                "allowlisted-development-command",
                "受限开发命令",
                "本轮信任开发命令",
                commonSafety,
                true),
            "fetch_public_web_page" => new ApprovalScope(
                "public-https-research",
                "公开 HTTPS 资料读取",
                "本轮信任公开资料读取",
                commonSafety,
                true),
            "index_workspace_knowledge" => new ApprovalScope(
                "local-knowledge-index",
                "本地只读知识索引",
                "本轮信任索引更新",
                commonSafety,
                true),
            _ => new ApprovalScope(
                string.Empty,
                "需要单独确认的操作",
                string.Empty,
                "这类操作会连接外部进程、影响桌面、创建计划或扩大成本，因此每次都会单独等你确认。",
                false)
        };
    }

    private static bool IsReviewablePatch(ToolApprovalRequest request)
        => request.PreviewKind.Equals("unified-diff", StringComparison.Ordinal)
           && !string.IsNullOrWhiteSpace(request.ChangePreview)
           && request.Additions + request.Deletions <= 600
           && !request.Title.Contains("无法", StringComparison.Ordinal)
           && !request.Title.Contains("大型", StringComparison.Ordinal);
}
