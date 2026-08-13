using System.Text.RegularExpressions;

namespace NovaDesktop.Services;

public sealed record WorkspaceShellAssessment(
    string Shell,
    string Risk,
    string PermissionKey,
    bool CanPersistForWorkspace,
    bool RequiresExplicitApproval,
    string Summary);

public static partial class WorkspaceShellPolicy
{
    public static WorkspaceShellAssessment Assess(string requestedShell, string command)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("Shell command cannot be empty.");
        if (command.Length > 12000 || command.IndexOf('\0') >= 0)
            throw new InvalidOperationException("Shell command exceeds the safe request boundary.");

        var shell = NormalizeShell(requestedShell);
        var elevated = HighRiskPattern().IsMatch(command)
                       || PackageManagerMutationPattern().IsMatch(command)
                       || AbsoluteSystemPathPattern().IsMatch(command)
                       || DownloadAndExecutePattern().IsMatch(command);
        return elevated
            ? new WorkspaceShellAssessment(
                shell, "high", $"shell:{shell}:workspace", false, true,
                "命令涉及提权、软件安装、系统位置、批量删除或下载后执行，必须逐次确认，不能永久授权。")
            : new WorkspaceShellAssessment(
                shell, "workspace", $"shell:{shell}:workspace", true, false,
                "命令以当前工作区为启动目录运行；终端仍具备当前用户权限，长期授权等同于信任该工作区中的自动化脚本。");
    }

    public static string NormalizeShell(string requestedShell)
    {
        var shell = (requestedShell ?? "auto").Trim().ToLowerInvariant();
        if (shell == "auto") shell = OperatingSystem.IsWindows() ? "powershell" : "zsh";
        if (OperatingSystem.IsWindows() && shell is not ("cmd" or "powershell" or "pwsh"))
            throw new InvalidOperationException("Windows only supports cmd, powershell, or pwsh terminal execution.");
        if (!OperatingSystem.IsWindows() && shell is not ("zsh" or "bash"))
            throw new InvalidOperationException("macOS/Linux only supports zsh or bash terminal execution.");
        return shell;
    }

    [GeneratedRegex(@"(?ix)(\bsudo\b|\bdoas\b|\brunas\b|start-process.{0,160}-verb\s+runas|-encodedcommand\b|\biex\b|invoke-expression|remove-item.{0,160}-recurse|\brm\s+-[^\r\n]*r[^\r\n]*f|\brmdir\s+/s|\bdel\s+/[sq]|\bformat\b|\bdiskpart\b|\bdiskutil\b|\bbcdedit\b|\breg(?:\.exe)?\s+(?:add|delete)|\bsc(?:\.exe)?\s+(?:create|delete|config)|\blaunchctl\b|\bdefaults\s+write|\bshutdown\b|\breboot\b)")]
    private static partial Regex HighRiskPattern();

    [GeneratedRegex(@"(?ix)(\bwinget\s+(?:install|uninstall|upgrade)\b|\bchoco\s+(?:install|uninstall|upgrade)\b|\bbrew\s+(?:install|uninstall|upgrade)\b|\b(?:apt|apt-get|dnf|yum|pacman)\s+(?:install|remove|upgrade|update)\b|\bnpm\s+(?:install|uninstall)\s+(?:--global|-g)\b|\bpip(?:3)?\s+(?:install|uninstall)\b)")]
    private static partial Regex PackageManagerMutationPattern();

    [GeneratedRegex(@"(?ix)([a-z]:\\(?:windows|program\s*files|users\\[^\\]+\\appdata)\b|/(?:etc|usr|bin|sbin|library|system|private)(?:/|\b)|(?:\$home|~)/(?:\.ssh|\.config|library)(?:/|\b))")]
    private static partial Regex AbsoluteSystemPathPattern();

    [GeneratedRegex(@"(?ix)(curl|wget|invoke-webrequest|iwr).{0,500}(\|\s*(?:sh|bash|zsh|powershell|pwsh)|invoke-expression|\biex\b|start-process|chmod\s+\+x)")]
    private static partial Regex DownloadAndExecutePattern();
}
