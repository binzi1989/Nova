namespace Nova.Core;

public sealed record AgentMessage(string Role, string Content);

public sealed record AgentChatRequest(
    string Provider,
    string Model,
    string ApiKey,
    IReadOnlyList<AgentMessage> Messages,
    WorkspaceContext Workspace);

public sealed record AgentChatResult(
    string Text,
    string Provider,
    string Model,
    TimeSpan Duration);

public sealed record WorkspaceContext(
    string Root,
    string Name,
    string Technology,
    int FileCount,
    IReadOnlyList<string> Signals)
{
    public static WorkspaceContext Empty(string root)
        => new(root, Path.GetFileName(root), "通用文件工作区", 0, []);
}

public sealed record McpConfigLocation(
    string Product,
    string Path,
    bool Exists);
