using System.IO;

namespace Nova.Core;

public sealed class CrossPlatformMcpProbe
{
    public IReadOnlyList<McpConfigLocation> GetKnownLocations(string workspaceRoot)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var applicationSupport = OperatingSystem.IsMacOS()
            ? Path.Combine(profile, "Library", "Application Support")
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return
        [
            Build("当前工作区", Path.Combine(workspaceRoot, ".vscode", "mcp.json")),
            Build("当前工作区", Path.Combine(workspaceRoot, ".mcp.json")),
            Build("Claude Desktop", Path.Combine(applicationSupport, "Claude", "claude_desktop_config.json")),
            Build("Claude Code", Path.Combine(profile, ".claude.json")),
            Build("Cursor", Path.Combine(profile, ".cursor", "mcp.json")),
            Build("Codex", Path.Combine(profile, ".codex", "config.toml"))
        ];
    }

    private static McpConfigLocation Build(string product, string path)
        => new(product, path, File.Exists(path));
}
