using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NovaDesktop.Services;

public sealed record EngineeringChangedFile(string Status, string Path);

public sealed record EngineeringWorkspaceSnapshot(
    string WorkspaceRoot,
    string WorkspaceName,
    CodexRuntimeProbe Codex,
    bool IsGitRepository,
    string GitBranch,
    IReadOnlyList<EngineeringChangedFile> ChangedFiles,
    int Additions,
    int Deletions,
    string Diff,
    IReadOnlyList<string> Projects,
    string VerificationCommand,
    string HealthStatus,
    DateTimeOffset CapturedAt)
{
    public int WorkspaceFileCount { get; init; }
    public string WorkspaceFingerprint { get; init; } = string.Empty;
    public IReadOnlyList<string> WorkspaceInventoryEntries { get; init; } = [];
}

public sealed record EngineeringVerificationResult(
    bool Started,
    bool Passed,
    string Command,
    int ExitCode,
    string Output,
    TimeSpan Duration,
    DateTimeOffset CompletedAt);

public sealed class EngineeringWorkspaceService
{
    private const int MaxDiffCharacters = 120_000;
    private const string InternalWeChatValidator = "__nova_validate_wechat_miniprogram__";
    private readonly CodexRuntimeProbeService _codexProbe;
    private readonly EngineeringEvidenceLedgerService _evidenceLedger;
    private readonly GitWorktreeService _worktrees;
    private readonly GitHunkReviewService _hunkReview;
    private readonly EngineeringCodeReviewService _codeReview;
    private readonly ConcurrentDictionary<string, ProjectDiscoveryCache> _projectCache =
        new(StringComparer.OrdinalIgnoreCase);

    public EngineeringWorkspaceService(
        CodexRuntimeProbeService? codexProbe = null,
        EngineeringEvidenceLedgerService? evidenceLedger = null,
        GitWorktreeService? worktrees = null,
        GitHunkReviewService? hunkReview = null,
        EngineeringCodeReviewService? codeReview = null)
    {
        _codexProbe = codexProbe ?? new CodexRuntimeProbeService();
        _evidenceLedger = evidenceLedger ?? new EngineeringEvidenceLedgerService();
        _worktrees = worktrees ?? new GitWorktreeService(evidenceLedger: _evidenceLedger);
        _hunkReview = hunkReview ?? new GitHunkReviewService(_evidenceLedger);
        _codeReview = codeReview ?? new EngineeringCodeReviewService();
    }

    public Task<CodexRuntimeProbe> ProbeCodexExecutableAsync(
        CancellationToken cancellationToken = default)
        => _codexProbe.ProbeExecutableAsync(cancellationToken);

    public IReadOnlyList<EngineeringEvidenceEntry> ReadRecentEvidence(
        string workspaceRoot,
        int maximumEntries = 80)
        => _evidenceLedger.ReadRecent(workspaceRoot, maximumEntries: maximumEntries);

    public Task<GitWorktreeSession> CreateIsolatedWorktreeAsync(
        string workspaceRoot,
        string? sessionLabel = null,
        CancellationToken cancellationToken = default)
        => _worktrees.CreateAsync(workspaceRoot, sessionLabel, cancellationToken);

    public bool IsManagedWorktree(string workspaceRoot)
        => _worktrees.IsManagedWorktree(workspaceRoot);

    public Task<GitWorktreeRecycleResult> RecycleWorktreeAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
        => _worktrees.RecycleAsync(workspaceRoot, cancellationToken);

    public Task<IReadOnlyList<EngineeringDiffHunk>> GetUnstagedHunksAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
        => _hunkReview.GetUnstagedHunksAsync(workspaceRoot, cancellationToken);

    public Task<GitHunkOperationResult> StageHunksAsync(
        string workspaceRoot,
        IReadOnlyCollection<string> hunkIds,
        CancellationToken cancellationToken = default)
        => _hunkReview.StageAsync(workspaceRoot, hunkIds, cancellationToken);

    public Task<GitHunkOperationResult> RevertHunksAsync(
        string workspaceRoot,
        IReadOnlyCollection<string> hunkIds,
        CancellationToken cancellationToken = default)
        => _hunkReview.RevertAsync(workspaceRoot, hunkIds, cancellationToken);

    public async Task<EngineeringCodeReviewResult> RunLocalCodeReviewAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await InspectAsync(workspaceRoot, cancellationToken);
        var review = _codeReview.Review(snapshot);
        var formatted = EngineeringCodeReviewService.Format(review);
        try
        {
            await _evidenceLedger.AppendAsync(
                "manual-engineering",
                workspaceRoot,
                "code-review",
                "local-static-review",
                snapshot.GitBranch,
                review.Findings.Any(item => item.Severity == "HIGH") ? "attention" : "completed",
                false,
                null,
                TimeSpan.Zero,
                formatted,
                review.Summary,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException
                                          or System.Security.SecurityException)
        {
            // Review remains available in the active window.
        }
        return review;
    }

    public async Task<CodexReadOnlyReviewResult> RunCodexReadOnlyReviewAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var result = await _codexProbe.RunReadOnlyReviewAsync(workspaceRoot, cancellationToken);
        try
        {
            await _evidenceLedger.AppendAsync(
                "manual-engineering",
                workspaceRoot,
                "code-review",
                "codex-read-only-review",
                workspaceRoot,
                result.Succeeded ? "completed" : "failed",
                false,
                result.ExitCode,
                result.Duration,
                result.Review,
                result.Detail,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException
                                          or System.Security.SecurityException)
        {
            // Review result remains authoritative.
        }
        return result;
    }

    public async Task<EngineeringWorkspaceSnapshot> InspectAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var root = ValidateWorkspace(workspaceRoot);
        var workspaceInventory = CaptureWorkspaceInventory(root);
        var projects = DiscoverProjects(root);
        var verification = SelectVerification(root, projects);
        var codexTask = _codexProbe.ProbeAsync(cancellationToken);
        var gitRootResult = await RunProcessAsync(
            root,
            "git",
            ["rev-parse", "--show-toplevel"],
            TimeSpan.FromSeconds(8),
            cancellationToken);
        var isGitRepository = gitRootResult.Started && gitRootResult.ExitCode == 0;

        var branch = "NO REPOSITORY";
        IReadOnlyList<EngineeringChangedFile> changedFiles = [];
        var additions = 0;
        var deletions = 0;
        var diff = "当前工作区不是 Git 仓库；工程中心仍可运行构建与测试。";

        if (isGitRepository)
        {
            var branchTask = RunProcessAsync(
                root,
                "git",
                ["branch", "--show-current"],
                TimeSpan.FromSeconds(8),
                cancellationToken);
            var statusTask = RunProcessAsync(
                root,
                "git",
                ["status", "--short"],
                TimeSpan.FromSeconds(8),
                cancellationToken);
            var numStatTask = RunProcessAsync(
                root,
                "git",
                ["diff", "--numstat"],
                TimeSpan.FromSeconds(12),
                cancellationToken);
            var stagedNumStatTask = RunProcessAsync(
                root,
                "git",
                ["diff", "--cached", "--numstat"],
                TimeSpan.FromSeconds(12),
                cancellationToken);
            var diffTask = RunProcessAsync(
                root,
                "git",
                ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "--unified=3"],
                TimeSpan.FromSeconds(15),
                cancellationToken);
            var stagedDiffTask = RunProcessAsync(
                root,
                "git",
                ["diff", "--cached", "--no-ext-diff", "--no-textconv", "--no-color", "--unified=3"],
                TimeSpan.FromSeconds(15),
                cancellationToken);

            await Task.WhenAll(
                branchTask,
                statusTask,
                numStatTask,
                stagedNumStatTask,
                diffTask,
                stagedDiffTask);
            branch = FirstLine(branchTask.Result.Output) ?? "DETACHED";
            changedFiles = ParseChangedFiles(statusTask.Result.Output);
            var unstagedStats = ParseNumStat(numStatTask.Result.Output);
            var stagedStats = ParseNumStat(stagedNumStatTask.Result.Output);
            additions = unstagedStats.Additions + stagedStats.Additions;
            deletions = unstagedStats.Deletions + stagedStats.Deletions;
            diff = BuildLayeredDiff(
                root,
                stagedDiffTask.Result.Output,
                diffTask.Result.Output,
                changedFiles);
            if (string.IsNullOrWhiteSpace(diff))
            {
                diff = changedFiles.Count == 0
                    ? "工作区干净，没有未提交变更。"
                    : "存在未跟踪或暂存文件；当前 unstaged diff 为空。";
            }
        }

        var codex = await codexTask;
        var health = projects.Count == 0
            ? "未识别到工程清单"
            : changedFiles.Count == 0
                ? "工程已识别 · 工作区干净"
                : $"工程已识别 · {changedFiles.Count} 个变更待审查";

        return new EngineeringWorkspaceSnapshot(
            root,
            new DirectoryInfo(root).Name,
            codex,
            isGitRepository,
            branch,
            changedFiles,
            additions,
            deletions,
            diff,
            projects,
            verification.DisplayCommand,
            health,
            DateTimeOffset.Now)
        {
            WorkspaceFileCount = workspaceInventory.FileCount,
            WorkspaceFingerprint = workspaceInventory.Fingerprint,
            WorkspaceInventoryEntries = workspaceInventory.Entries
        };
    }

    public async Task<EngineeringVerificationResult> VerifyAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var root = ValidateWorkspace(workspaceRoot);
        var projects = DiscoverProjects(root);
        var verification = SelectVerification(root, projects);
        if (verification.Executable is null)
        {
            return new EngineeringVerificationResult(
                false,
                false,
                verification.DisplayCommand,
                -1,
                "当前仅自动运行 .NET 工程验证；其他技术栈请通过 NOVA 的受控命令工具执行。",
                TimeSpan.Zero,
                DateTimeOffset.Now);
        }

        var startedAt = Stopwatch.GetTimestamp();
        EngineeringVerificationResult verificationResult;
        if (verification.Executable == InternalWeChatValidator)
        {
            var validation = ValidateWeChatMiniProgram(
                root,
                verification.Arguments.Single());
            verificationResult = new EngineeringVerificationResult(
                true,
                validation.Passed,
                verification.DisplayCommand,
                validation.Passed ? 0 : 1,
                validation.Output,
                Stopwatch.GetElapsedTime(startedAt),
                DateTimeOffset.Now);
        }
        else
        {
            var result = await RunProcessAsync(
                root,
                verification.Executable,
                verification.Arguments,
                TimeSpan.FromMinutes(3),
                cancellationToken);
            var duration = Stopwatch.GetElapsedTime(startedAt);
            var output = JoinOutput(result.Output, result.Error);
            verificationResult = new EngineeringVerificationResult(
                result.Started,
                result.Started && result.ExitCode == 0,
                verification.DisplayCommand,
                result.ExitCode,
                Limit(output, 80_000),
                duration,
                DateTimeOffset.Now);
        }
        try
        {
            await _evidenceLedger.AppendAsync(
                "manual-engineering",
                root,
                "verification",
                "workspace-verification",
                verification.DisplayCommand,
                verificationResult.Passed ? "passed" : "failed",
                true,
                verificationResult.ExitCode,
                verificationResult.Duration,
                verificationResult.Output,
                verificationResult.Passed
                    ? "工程验证通过。"
                    : "工程验证未通过；完整输出保留在当前工程会话中。",
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException
                                          or System.Security.SecurityException)
        {
            // Verification remains valid even if the optional local ledger is unavailable.
        }
        return verificationResult;
    }

    private static string ValidateWorkspace(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("工作区路径不能为空。", nameof(workspaceRoot));
        }

        var root = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"工作区不存在：{root}");
        }

        return root;
    }

    private IReadOnlyList<string> DiscoverProjects(string root)
    {
        if (_projectCache.TryGetValue(root, out var cached)
            && DateTimeOffset.Now - cached.CapturedAt < TimeSpan.FromSeconds(4))
        {
            return cached.Projects;
        }

        var candidates = new List<string>();
        var targetNames = new HashSet<string>(
            ["package.json", "pyproject.toml", "Cargo.toml", "go.mod", "project.config.json"],
            StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateWorkspaceFiles(root))
        {
            var fileName = Path.GetFileName(path);
            if (!fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                && !fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                && !targetNames.Contains(fileName))
            {
                continue;
            }

            candidates.Add(Path.GetRelativePath(root, path));
            if (candidates.Count >= 20)
            {
                break;
            }
        }

        var projects = candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
        _projectCache[root] = new ProjectDiscoveryCache(DateTimeOffset.Now, projects);
        return projects;
    }

    private static IEnumerable<string> EnumerateWorkspaceFiles(string root)
    {
        const int maximumDirectories = 2500;
        const int maximumDepth = 8;
        var visited = 0;
        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((root, 0));
        while (pending.Count > 0 && visited < maximumDirectories)
        {
            var (directory, depth) = pending.Dequeue();
            visited++;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            if (depth >= maximumDepth)
            {
                continue;
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(directory);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var child in directories)
            {
                try
                {
                    var info = new DirectoryInfo(child);
                    if (IsIgnoredDirectory(info.Name)
                        || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }
                    pending.Enqueue((child, depth + 1));
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    // Skip protected or invalid directory entries.
                }
            }
        }
    }

    private static WorkspaceInventory CaptureWorkspaceInventory(string root)
    {
        var entries = new List<string>();
        foreach (var path in EnumerateWorkspaceFiles(root).Take(5000))
        {
            try
            {
                var info = new FileInfo(path);
                entries.Add(
                    $"{Path.GetRelativePath(root, path).Replace('\\', '/')}|"
                    + $"{info.Length}|{info.LastWriteTimeUtc.Ticks}");
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or NotSupportedException)
            {
                // Protected files do not participate in the bounded fingerprint.
            }
        }
        entries.Sort(StringComparer.OrdinalIgnoreCase);
        var payload = Encoding.UTF8.GetBytes(string.Join('\n', entries));
        return new WorkspaceInventory(
            entries.Count,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            entries);
    }

    private static VerificationCommand SelectVerification(string root, IReadOnlyList<string> projects)
    {
        var wechatProject = projects.FirstOrDefault(path =>
            path.EndsWith("project.config.json", StringComparison.OrdinalIgnoreCase));
        if (wechatProject is not null)
        {
            return new VerificationCommand(
                InternalWeChatValidator,
                [wechatProject],
                $"NOVA validate WeChat Mini Program {Quote(wechatProject)}");
        }

        var skipRestore = CanSkipDotnetRestore(root, projects);
        var restoreArguments = skipRestore ? new[] { "--no-restore" } : [];
        var solution = projects.FirstOrDefault(path =>
            path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
        if (solution is not null)
        {
            return new VerificationCommand(
                "dotnet",
                ["test", solution, "--nologo", .. restoreArguments],
                $"dotnet test {Quote(solution)} --nologo"
                + (skipRestore ? " --no-restore" : string.Empty));
        }

        var testProject = projects.FirstOrDefault(path =>
            path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            && (path.Contains("test", StringComparison.OrdinalIgnoreCase)
                || path.Contains("spec", StringComparison.OrdinalIgnoreCase)));
        if (testProject is not null)
        {
            return new VerificationCommand(
                "dotnet",
                ["test", testProject, "--nologo", .. restoreArguments],
                $"dotnet test {Quote(testProject)} --nologo"
                + (skipRestore ? " --no-restore" : string.Empty));
        }

        var project = projects.FirstOrDefault(path =>
            path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        if (project is not null)
        {
            return new VerificationCommand(
                "dotnet",
                ["build", project, "--nologo", .. restoreArguments],
                $"dotnet build {Quote(project)} --nologo"
                + (skipRestore ? " --no-restore" : string.Empty));
        }

        var packageJson = projects.FirstOrDefault(path =>
            path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase));
        if (packageJson is not null)
        {
            var script = SelectNodeScript(Path.Combine(root, packageJson));
            if (script is not null)
            {
                var directory = Path.GetDirectoryName(packageJson) ?? ".";
                var prefixArguments = directory is "." or ""
                    ? Array.Empty<string>()
                    : new[] { "--prefix", directory };
                return new VerificationCommand(
                    "npm.cmd",
                    [.. prefixArguments, "run", "--if-present", script],
                    $"npm {string.Join(" ", prefixArguments.Select(Quote))} run --if-present {script}".Trim());
            }
        }

        var pyproject = projects.FirstOrDefault(path =>
            path.EndsWith("pyproject.toml", StringComparison.OrdinalIgnoreCase));
        if (pyproject is not null)
        {
            var directory = Path.GetDirectoryName(pyproject);
            var target = string.IsNullOrWhiteSpace(directory) ? "." : directory;
            return new VerificationCommand(
                "python",
                ["-m", "pytest", target, "-q"],
                $"python -m pytest {Quote(target)} -q");
        }

        var cargo = projects.FirstOrDefault(path =>
            path.EndsWith("Cargo.toml", StringComparison.OrdinalIgnoreCase));
        if (cargo is not null)
        {
            return new VerificationCommand(
                "cargo",
                ["test", "--manifest-path", cargo, "--quiet"],
                $"cargo test --manifest-path {Quote(cargo)} --quiet");
        }

        var goModule = projects.FirstOrDefault(path =>
            path.EndsWith("go.mod", StringComparison.OrdinalIgnoreCase));
        if (goModule is not null)
        {
            var directory = Path.GetDirectoryName(goModule);
            var target = string.IsNullOrWhiteSpace(directory)
                ? "./..."
                : $"./{directory.Replace('\\', '/')}/...";
            return new VerificationCommand(
                "go",
                ["test", target],
                $"go test {Quote(target)}");
        }

        return new VerificationCommand(
            null,
            [],
            projects.Count == 0 ? "NO VERIFICATION TARGET" : "MANUAL VERIFICATION REQUIRED");
    }

    private static (bool Passed, string Output) ValidateWeChatMiniProgram(
        string workspaceRoot,
        string projectConfigPath)
    {
        var errors = new List<string>();
        var configFullPath = Path.Combine(workspaceRoot, projectConfigPath);
        var projectDirectory = Path.GetDirectoryName(configFullPath) ?? workspaceRoot;
        string miniProgramRoot = projectDirectory;
        try
        {
            using var config = JsonDocument.Parse(File.ReadAllText(configFullPath));
            if (config.RootElement.TryGetProperty("miniprogramRoot", out var rootProperty)
                && rootProperty.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(rootProperty.GetString()))
            {
                miniProgramRoot = Path.GetFullPath(
                    Path.Combine(projectDirectory, rootProperty.GetString()!));
                var projectPrefix = projectDirectory.TrimEnd(
                                        Path.DirectorySeparatorChar,
                                        Path.AltDirectorySeparatorChar)
                                    + Path.DirectorySeparatorChar;
                if (!miniProgramRoot.Equals(
                        projectDirectory,
                        StringComparison.OrdinalIgnoreCase)
                    && !miniProgramRoot.StartsWith(
                        projectPrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("project.config.json 的 miniprogramRoot 越出项目目录。");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            errors.Add($"project.config.json 无法解析：{exception.Message}");
        }

        foreach (var required in new[] { "app.json", "app.js", "app.wxss" })
        {
            if (!File.Exists(Path.Combine(miniProgramRoot, required)))
            {
                errors.Add($"缺少根文件：{required}");
            }
        }

        var appJsonPath = Path.Combine(miniProgramRoot, "app.json");
        var pages = new List<string>();
        if (File.Exists(appJsonPath))
        {
            try
            {
                using var app = JsonDocument.Parse(File.ReadAllText(appJsonPath));
                if (!app.RootElement.TryGetProperty("pages", out var pagesElement)
                    || pagesElement.ValueKind != JsonValueKind.Array)
                {
                    errors.Add("app.json 缺少 pages 数组。");
                }
                else
                {
                    pages.AddRange(pagesElement
                        .EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString())
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Select(item => item!));
                    if (pages.Count == 0)
                    {
                        errors.Add("app.json 的 pages 数组为空。");
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                errors.Add($"app.json 无法解析：{exception.Message}");
            }
        }

        foreach (var page in pages)
        {
            if (Path.IsPathRooted(page) || page.Contains("..", StringComparison.Ordinal))
            {
                errors.Add($"页面路径不安全：{page}");
                continue;
            }
            foreach (var extension in new[] { ".js", ".wxml", ".wxss" })
            {
                var pageFile = Path.Combine(
                    miniProgramRoot,
                    page.Replace('/', Path.DirectorySeparatorChar) + extension);
                if (!File.Exists(pageFile))
                {
                    errors.Add($"页面 {page} 缺少 {extension} 文件。");
                }
            }
        }

        var passed = errors.Count == 0;
        var output = passed
            ? $"微信小程序结构验证通过：{pages.Count} 个页面，根配置和页面三件套完整。"
            : "微信小程序结构验证失败："
              + Environment.NewLine
              + string.Join(Environment.NewLine, errors.Select(item => "- " + item));
        return (passed, output);
    }

    private static bool CanSkipDotnetRestore(
        string root,
        IReadOnlyList<string> projects)
    {
        var csProjects = projects
            .Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return csProjects.Length > 0
               && csProjects.All(path =>
               {
                   var directory = Path.GetDirectoryName(Path.Combine(root, path)) ?? root;
                   return File.Exists(Path.Combine(directory, "obj", "project.assets.json"));
               });
    }

    private static string? SelectNodeScript(string packageJsonPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (!document.RootElement.TryGetProperty("scripts", out var scripts)
                || scripts.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (scripts.TryGetProperty("test", out var test)
                && test.ValueKind == JsonValueKind.String
                && !test.GetString()!.Contains("no test specified", StringComparison.OrdinalIgnoreCase))
            {
                return "test";
            }
            return scripts.TryGetProperty("build", out var build)
                   && build.ValueKind == JsonValueKind.String
                ? "build"
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string workingDirectory,
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new ProcessResult(false, -1, string.Empty, "进程未能启动。");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return new ProcessResult(true, -1, await outputTask, "命令执行超时，已终止进程树。");
            }

            return new ProcessResult(true, process.ExitCode, await outputTask, await errorTask);
        }
        catch (Exception exception)
        {
            return new ProcessResult(false, -1, string.Empty, exception.Message);
        }
    }

    private static IReadOnlyList<EngineeringChangedFile> ParseChangedFiles(string output)
        => output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Length >= 3
                ? new EngineeringChangedFile(line[..2].Trim(), line[3..].Trim())
                : new EngineeringChangedFile("?", line.Trim()))
            .Where(file => !file.Path.Equals(".nova", StringComparison.OrdinalIgnoreCase)
                           && !file.Path.StartsWith(".nova/", StringComparison.OrdinalIgnoreCase)
                           && !file.Path.StartsWith(".nova\\", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static (int Additions, int Deletions) ParseNumStat(string output)
    {
        var additions = 0;
        var deletions = 0;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2)
            {
                continue;
            }

            if (int.TryParse(parts[0], out var added))
            {
                additions += added;
            }
            if (int.TryParse(parts[1], out var removed))
            {
                deletions += removed;
            }
        }
        return (additions, deletions);
    }

    private static string BuildLayeredDiff(
        string workspaceRoot,
        string stagedDiff,
        string unstagedDiff,
        IReadOnlyList<EngineeringChangedFile> changedFiles)
    {
        var builder = new StringBuilder();
        builder.AppendLine("══ STAGED DIFF ══════════════════════════════════════════════");
        builder.AppendLine(string.IsNullOrWhiteSpace(stagedDiff)
            ? "（没有已暂存变更）"
            : stagedDiff.TrimEnd());
        builder.AppendLine();
        builder.AppendLine("══ UNSTAGED DIFF ════════════════════════════════════════════");
        builder.AppendLine(string.IsNullOrWhiteSpace(unstagedDiff)
            ? "（没有未暂存文本变更）"
            : unstagedDiff.TrimEnd());

        var untracked = changedFiles
            .Where(file => file.Status == "??")
            .Select(file => file.Path)
            .ToArray();
        if (untracked.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("══ UNTRACKED FILES ══════════════════════════════════════════");
            foreach (var path in untracked)
            {
                builder.AppendLine(path);
                AppendUntrackedTextDiff(builder, workspaceRoot, path);
                if (builder.Length >= MaxDiffCharacters)
                {
                    break;
                }
            }
        }

        return Limit(builder.ToString(), MaxDiffCharacters);
    }

    private static void AppendUntrackedTextDiff(
        StringBuilder builder,
        string workspaceRoot,
        string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        var textFile = new[]
        {
            ".cs", ".xaml", ".xml", ".json", ".md", ".txt", ".yml", ".yaml",
            ".toml", ".props", ".targets", ".csproj", ".sln", ".ts", ".tsx",
            ".js", ".jsx", ".css", ".html", ".py", ".rs", ".go", ".java",
            ".wxml", ".wxss", ".wxs", ".axml", ".acss", ".swan", ".ttml", ".ttss"
        }.Contains(extension, StringComparer.OrdinalIgnoreCase);
        if (!textFile)
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath));
            var rootPrefix = workspaceRoot.TrimEnd(Path.DirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length > 160_000)
            {
                return;
            }

            builder.AppendLine($"--- /dev/null");
            builder.AppendLine($"+++ b/{relativePath.Replace('\\', '/')}");
            builder.AppendLine("@@ -0,0 +1 @@");
            foreach (var line in File.ReadLines(fullPath).Take(2500))
            {
                builder.Append('+').AppendLine(line);
                if (builder.Length >= MaxDiffCharacters)
                {
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            builder.AppendLine("（无法读取未跟踪文本文件进行审查）");
        }
    }

    private static bool IsIgnoredDirectory(string name)
        => name is ".git" or ".nova" or "bin" or "obj" or "node_modules" or "dist"
           || name.StartsWith(".", StringComparison.Ordinal);

    private static string Quote(string value)
        => value.Contains(' ') ? $"\"{value}\"" : value;

    private static string? FirstLine(string value)
        => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private static string JoinOutput(string output, string error)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(output))
        {
            builder.AppendLine(output.TrimEnd());
        }
        if (!string.IsNullOrWhiteSpace(error))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }
            builder.AppendLine(error.TrimEnd());
        }
        return builder.Length == 0 ? "命令没有返回输出。" : builder.ToString();
    }

    private static string Limit(string value, int max)
        => value.Length <= max ? value : value[..max] + Environment.NewLine + "… OUTPUT TRUNCATED …";

    private sealed record VerificationCommand(
        string? Executable,
        IReadOnlyList<string> Arguments,
        string DisplayCommand);

    private sealed record ProcessResult(bool Started, int ExitCode, string Output, string Error);
    private sealed record ProjectDiscoveryCache(
        DateTimeOffset CapturedAt,
        IReadOnlyList<string> Projects);
    private sealed record WorkspaceInventory(
        int FileCount,
        string Fingerprint,
        IReadOnlyList<string> Entries);
}
