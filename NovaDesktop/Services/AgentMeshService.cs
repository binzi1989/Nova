using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NovaDesktop.Services;

public sealed record AgentMeshPackageResult(
    AgentMeshWorkPackage Package,
    AgentRunResult? AgentResult,
    string Status,
    string Detail,
    string Patch,
    string PatchPath,
    string PatchSha256,
    int Additions,
    int Deletions);

public sealed record AgentMeshRunResult(
    string MeshId,
    string TaskId,
    string SourceRepository,
    string BaseHead,
    string IntegrationHead,
    GitWorktreeSession IntegrationSession,
    AgentMeshPlan Plan,
    IReadOnlyList<IReadOnlyList<string>> Waves,
    IReadOnlyList<AgentMeshPackageResult> Packages,
    EngineeringVerificationResult? Verification,
    EngineeringCodeReviewResult Review,
    string CombinedPatch,
    string CombinedPatchPath,
    string CombinedPatchSha256,
    int Additions,
    int Deletions,
    string ArtifactDirectory,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt)
{
    public bool IsEligible
        => Packages.All(item => item.Status == "READY")
           && CombinedPatch.Length > 0
           && (Verification is null || Verification.Passed);
}

public sealed record AgentMeshApplyResult(
    bool Applied,
    int ExitCode,
    string Detail);

public sealed class AgentMeshService
{
    private readonly GitWorktreeService _worktrees;
    private readonly EngineeringWorkspaceService _engineering;
    private readonly string _artifactRoot;

    public AgentMeshService(
        GitWorktreeService? worktrees = null,
        EngineeringWorkspaceService? engineering = null,
        string? artifactRoot = null)
    {
        _worktrees = worktrees ?? new GitWorktreeService();
        _engineering = engineering ?? new EngineeringWorkspaceService();
        _artifactRoot = artifactRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "agent-mesh");
    }

    public async Task<AgentMeshRunResult> RunAsync(
        string sourceRepository,
        string taskId,
        AgentMeshPlan plan,
        Func<AgentMeshWorkPackage, string, int, int, CancellationToken, Task<AgentRunResult>>
            runPackage,
        bool runVerification,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourceRepository);
        var sourceSnapshot = await _engineering.InspectAsync(source, cancellationToken);
        if (!sourceSnapshot.IsGitRepository)
        {
            throw new InvalidOperationException("Agent Mesh requires a Git repository.");
        }
        if (sourceSnapshot.ChangedFiles.Count > 0)
        {
            throw new InvalidOperationException(
                "主工作区存在未提交修改，Agent Mesh 不会构建可能覆盖用户工作的集成基线。");
        }

        var baseHead = await ReadHeadAsync(source, cancellationToken);
        var meshId = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss")
                     + "-"
                     + SafeName(taskId)
                     + "-"
                     + Guid.NewGuid().ToString("N")[..6];
        var artifactDirectory = Path.GetFullPath(Path.Combine(_artifactRoot, meshId));
        EnsureChildPath(_artifactRoot, artifactDirectory);
        Directory.CreateDirectory(artifactDirectory);
        var startedAt = DateTimeOffset.Now;
        var integration = await _worktrees.CreateAsync(
            source,
            $"mesh-integration-{SafeName(taskId)}",
            cancellationToken);
        if (!integration.Created || !integration.Head.Equals(
                baseHead,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Agent Mesh integration Worktree creation failed: {integration.Detail}");
        }

        var results = new List<AgentMeshPackageResult>();
        var waves = plan.BuildWaves();
        try
        {
            for (var waveIndex = 0; waveIndex < waves.Count; waveIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wave = waves[waveIndex];
                var waveHead = await ReadHeadAsync(
                    integration.WorkspaceRoot,
                    cancellationToken);
                var sessions = new List<(AgentMeshWorkPackage Package, GitWorktreeSession Session)>();
                try
                {
                    foreach (var package in wave)
                    {
                        var session = await _worktrees.CreateAsync(
                            integration.WorkspaceRoot,
                            $"mesh-{SafeName(package.Id)}",
                            cancellationToken);
                        if (!session.Created
                            || !session.Head.Equals(
                                waveHead,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                $"Mesh package {package.Id} did not start from wave HEAD.");
                        }
                        sessions.Add((package, session));
                    }

                    var packageTasks = sessions.Select(item => RunOnePackageAsync(
                        item.Package,
                        item.Session,
                        artifactDirectory,
                        waveIndex,
                        waves.Count,
                        runPackage,
                        cancellationToken));
                    var waveResults = await Task.WhenAll(packageTasks);
                    results.AddRange(waveResults);
                    if (waveResults.Any(item => item.Status != "READY"))
                    {
                        throw new InvalidOperationException(
                            $"Agent Mesh wave {waveIndex + 1} produced an invalid package: "
                            + string.Join(
                                "; ",
                                waveResults
                                    .Where(item => item.Status != "READY")
                                    .Select(item => $"{item.Package.Id}: {item.Detail}")));
                    }

                    foreach (var packageResult in waveResults.OrderBy(
                                 item => item.Package.Id,
                                 StringComparer.OrdinalIgnoreCase))
                    {
                        var apply = await ApplyPatchToIntegrationAsync(
                            integration.WorkspaceRoot,
                            waveHead,
                            packageResult.PatchPath,
                            packageResult.PatchSha256,
                            cancellationToken);
                        if (!apply.Applied)
                        {
                            throw new InvalidOperationException(
                                $"Mesh package {packageResult.Package.Id} could not integrate: "
                                + apply.Detail);
                        }
                    }
                    await CommitWaveAsync(
                        integration.WorkspaceRoot,
                        $"NOVA Agent Mesh wave {waveIndex + 1}",
                        cancellationToken);
                }
                finally
                {
                    foreach (var item in sessions)
                    {
                        await TryDiscardAsync(item.Session.WorkspaceRoot);
                    }
                }
            }

            var integrationHead = await ReadHeadAsync(
                integration.WorkspaceRoot,
                cancellationToken);
            var verification = runVerification
                ? await _engineering.VerifyAsync(
                    integration.WorkspaceRoot,
                    cancellationToken)
                : null;
            var combinedPatch = await ExportRangePatchAsync(
                integration.WorkspaceRoot,
                baseHead,
                integrationHead,
                cancellationToken);
            if (combinedPatch.Length == 0)
            {
                throw new InvalidOperationException(
                    "Agent Mesh completed without an exportable combined Patch.");
            }
            if (combinedPatch.Length > 3_000_000)
            {
                throw new InvalidOperationException(
                    "Agent Mesh combined Patch exceeds the 3 MB safety limit.");
            }
            var combinedPatchPath = Path.Combine(
                artifactDirectory,
                "combined.patch");
            await File.WriteAllTextAsync(
                combinedPatchPath,
                combinedPatch,
                new UTF8Encoding(false),
                cancellationToken);
            var combinedHash = ComputeHash(Encoding.UTF8.GetBytes(combinedPatch));
            var (additions, deletions) = CountPatchLines(combinedPatch);
            var cleanSnapshot = await _engineering.InspectAsync(
                integration.WorkspaceRoot,
                cancellationToken);
            var reviewSnapshot = cleanSnapshot with
            {
                ChangedFiles = ParsePatchFiles(combinedPatch),
                Additions = additions,
                Deletions = deletions,
                Diff = combinedPatch,
                HealthStatus = $"Agent Mesh combined change · +{additions} / -{deletions}"
            };
            var review = new EngineeringCodeReviewService().Review(reviewSnapshot);
            var result = new AgentMeshRunResult(
                meshId,
                taskId,
                source,
                baseHead,
                integrationHead,
                integration,
                plan,
                waves.Select(wave =>
                        (IReadOnlyList<string>)wave.Select(item => item.Id).ToArray())
                    .ToArray(),
                results,
                verification,
                review,
                combinedPatch,
                combinedPatchPath,
                combinedHash,
                additions,
                deletions,
                artifactDirectory,
                startedAt,
                DateTimeOffset.Now);
            await PersistManifestAsync(result, cancellationToken);
            return result;
        }
        catch
        {
            await TryRecycleAsync(integration.WorkspaceRoot);
            throw;
        }
    }

    public async Task<AgentMeshApplyResult> ApplyAsync(
        AgentMeshRunResult mesh,
        CancellationToken cancellationToken = default)
    {
        if (!mesh.IsEligible)
        {
            return new AgentMeshApplyResult(
                false,
                -1,
                "Agent Mesh did not satisfy the integration eligibility gate.");
        }
        var source = Path.GetFullPath(mesh.SourceRepository);
        var snapshot = await _engineering.InspectAsync(source, cancellationToken);
        if (!snapshot.IsGitRepository || snapshot.ChangedFiles.Count > 0)
        {
            return new AgentMeshApplyResult(
                false,
                -1,
                "主工作区在 Mesh 运行期间发生变化，Combined Patch 未应用。");
        }
        var currentHead = await ReadHeadAsync(source, cancellationToken);
        if (!currentHead.Equals(mesh.BaseHead, StringComparison.OrdinalIgnoreCase))
        {
            return new AgentMeshApplyResult(
                false,
                -1,
                "主工作区 HEAD 已改变，Combined Patch 未应用。");
        }
        var actualHash = ComputeHash(
            await File.ReadAllBytesAsync(mesh.CombinedPatchPath, cancellationToken));
        if (!actualHash.Equals(
                mesh.CombinedPatchSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return new AgentMeshApplyResult(
                false,
                -1,
                "Combined Patch 哈希校验失败。");
        }
        var check = await RunGitAsync(
            source,
            ["apply", "--check", "--binary", mesh.CombinedPatchPath],
            TimeSpan.FromSeconds(45),
            cancellationToken);
        if (!check.Started || check.ExitCode != 0)
        {
            return new AgentMeshApplyResult(
                false,
                check.ExitCode,
                FirstLine(check.Error) ?? "Combined Patch 与主工作区不兼容。");
        }
        var apply = await RunGitAsync(
            source,
            ["apply", "--binary", "--whitespace=nowarn", mesh.CombinedPatchPath],
            TimeSpan.FromMinutes(1),
            cancellationToken);
        return new AgentMeshApplyResult(
            apply.Started && apply.ExitCode == 0,
            apply.ExitCode,
            apply.Started && apply.ExitCode == 0
                ? "Agent Mesh Combined Patch 已应用到主工作区，尚未提交。"
                : FirstLine(apply.Error) ?? "Combined Patch 应用失败。");
    }

    public async Task<string> PersistDecisionAsync(
        AgentMeshRunResult mesh,
        AgentMeshCouncilDecision decision,
        bool applied,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(mesh.ArtifactDirectory, "decision.json");
        var json = JsonSerializer.Serialize(new
        {
            schema_version = 1,
            mesh_id = mesh.MeshId,
            decision.Provider,
            decision.Model,
            decision.Verdict,
            decision.Confidence,
            decision.Summary,
            applied,
            combined_patch_sha256 = mesh.CombinedPatchSha256,
            completed_at = decision.CompletedAt,
            raw_response = decision.RawResponse
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(
            path,
            json,
            new UTF8Encoding(false),
            cancellationToken);
        return path;
    }

    public Task CleanupAsync(
        AgentMeshRunResult mesh,
        CancellationToken cancellationToken = default)
        => _worktrees.DiscardAsync(
            mesh.IntegrationSession.WorkspaceRoot,
            cancellationToken);

    private async Task<AgentMeshPackageResult> RunOnePackageAsync(
        AgentMeshWorkPackage package,
        GitWorktreeSession session,
        string artifactDirectory,
        int waveIndex,
        int waveCount,
        Func<AgentMeshWorkPackage, string, int, int, CancellationToken, Task<AgentRunResult>>
            runPackage,
        CancellationToken cancellationToken)
    {
        AgentRunResult? agentResult = null;
        var status = "FAILED";
        var detail = "Package did not run.";
        try
        {
            agentResult = await runPackage(
                package,
                session.WorkspaceRoot,
                waveIndex,
                waveCount,
                cancellationToken);
            var snapshot = await _engineering.InspectAsync(
                session.WorkspaceRoot,
                cancellationToken);
            if (agentResult.MutatingToolCalls == 0 || snapshot.ChangedFiles.Count == 0)
            {
                detail = "工作包没有产生真实文件变更。";
            }
            else
            {
                EnsureChangedFilesOwned(package, snapshot.ChangedFiles);
                status = "READY";
                detail = $"工作包完成，{snapshot.ChangedFiles.Count} 个文件位于所有权范围内。";
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            detail = exception.Message;
        }

        var patch = await ExportWorkingPatchAsync(
            session.WorkspaceRoot,
            cancellationToken);
        var patchPath = Path.Combine(
            artifactDirectory,
            $"{SafeName(package.Id)}.patch");
        await File.WriteAllTextAsync(
            patchPath,
            patch,
            new UTF8Encoding(false),
            cancellationToken);
        var hash = ComputeHash(Encoding.UTF8.GetBytes(patch));
        var (additions, deletions) = CountPatchLines(patch);
        if (patch.Length == 0 && status == "READY")
        {
            status = "FAILED";
            detail = "工作包没有可导出的 Patch。";
        }
        return new AgentMeshPackageResult(
            package,
            agentResult,
            status,
            detail,
            patch,
            patchPath,
            hash,
            additions,
            deletions);
    }

    private static void EnsureChangedFilesOwned(
        AgentMeshWorkPackage package,
        IReadOnlyList<EngineeringChangedFile> changedFiles)
    {
        foreach (var changed in changedFiles)
        {
            var path = changed.Path.Replace('\\', '/').Trim('"');
            var owned = package.OwnedPaths.Any(scope =>
                scope.EndsWith("/", StringComparison.Ordinal)
                    ? path.StartsWith(scope, StringComparison.OrdinalIgnoreCase)
                    : path.Equals(scope, StringComparison.OrdinalIgnoreCase));
            if (!owned)
            {
                throw new InvalidOperationException(
                    $"Agent Mesh ownership audit rejected '{path}' from package {package.Id}.");
            }
        }
    }

    private static async Task<AgentMeshApplyResult> ApplyPatchToIntegrationAsync(
        string integrationRoot,
        string expectedHead,
        string patchPath,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        var currentHead = await ReadHeadAsync(integrationRoot, cancellationToken);
        if (!currentHead.Equals(expectedHead, StringComparison.OrdinalIgnoreCase))
        {
            return new AgentMeshApplyResult(
                false,
                -1,
                "Integration HEAD changed during the current wave.");
        }
        var actualHash = ComputeHash(
            await File.ReadAllBytesAsync(patchPath, cancellationToken));
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            return new AgentMeshApplyResult(false, -1, "Package Patch hash mismatch.");
        }
        var check = await RunGitAsync(
            integrationRoot,
            ["apply", "--check", "--binary", patchPath],
            TimeSpan.FromSeconds(30),
            cancellationToken);
        if (!check.Started || check.ExitCode != 0)
        {
            return new AgentMeshApplyResult(
                false,
                check.ExitCode,
                FirstLine(check.Error) ?? "Package Patch check failed.");
        }
        var apply = await RunGitAsync(
            integrationRoot,
            ["apply", "--binary", "--whitespace=nowarn", patchPath],
            TimeSpan.FromSeconds(45),
            cancellationToken);
        return new AgentMeshApplyResult(
            apply.Started && apply.ExitCode == 0,
            apply.ExitCode,
            apply.Started && apply.ExitCode == 0
                ? "Package Patch integrated."
                : FirstLine(apply.Error) ?? "Package Patch apply failed.");
    }

    private static async Task CommitWaveAsync(
        string integrationRoot,
        string message,
        CancellationToken cancellationToken)
    {
        var add = await RunGitAsync(
            integrationRoot,
            ["add", "-A", "--", "."],
            TimeSpan.FromSeconds(30),
            cancellationToken);
        EnsureGitSucceeded(add, "Unable to stage Agent Mesh wave.");
        var commit = await RunGitAsync(
            integrationRoot,
            [
                "-c", "user.name=NOVA Agent Mesh",
                "-c", "user.email=agent-mesh@nova.local",
                "commit", "-m", message
            ],
            TimeSpan.FromSeconds(45),
            cancellationToken);
        EnsureGitSucceeded(commit, "Unable to commit Agent Mesh wave.");
    }

    private static async Task<string> ExportWorkingPatchAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var addIntent = await RunGitAsync(
            workspaceRoot,
            ["add", "-N", "--", "."],
            TimeSpan.FromSeconds(30),
            cancellationToken);
        EnsureGitSucceeded(addIntent, "Unable to include new files in package Patch.");
        try
        {
            var diff = await RunGitAsync(
                workspaceRoot,
                ["diff", "--binary", "--full-index", "HEAD", "--"],
                TimeSpan.FromSeconds(45),
                cancellationToken);
            EnsureGitSucceeded(diff, "Unable to export package Patch.");
            if (diff.Output.Length > 1_500_000)
            {
                throw new InvalidOperationException(
                    "One Agent Mesh package Patch exceeds 1.5 MB.");
            }
            return diff.Output;
        }
        finally
        {
            await RunGitAsync(
                workspaceRoot,
                ["reset", "--mixed", "HEAD", "--"],
                TimeSpan.FromSeconds(20),
                CancellationToken.None);
        }
    }

    private static async Task<string> ExportRangePatchAsync(
        string workspaceRoot,
        string baseHead,
        string integrationHead,
        CancellationToken cancellationToken)
    {
        var diff = await RunGitAsync(
            workspaceRoot,
            [
                "diff",
                "--binary",
                "--full-index",
                baseHead,
                integrationHead,
                "--"
            ],
            TimeSpan.FromSeconds(60),
            cancellationToken);
        EnsureGitSucceeded(diff, "Unable to export Agent Mesh combined Patch.");
        return diff.Output;
    }

    private static async Task PersistManifestAsync(
        AgentMeshRunResult mesh,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(mesh.ArtifactDirectory, "mesh.json");
        var json = JsonSerializer.Serialize(new
        {
            schema_version = 1,
            mesh_id = mesh.MeshId,
            task_id = mesh.TaskId,
            source_repository = mesh.SourceRepository,
            base_head = mesh.BaseHead,
            integration_head = mesh.IntegrationHead,
            strategy = mesh.Plan.Strategy,
            waves = mesh.Waves,
            packages = mesh.Packages.Select(item => new
            {
                id = item.Package.Id,
                item.Package.Title,
                item.Package.OwnedPaths,
                item.Package.DependsOn,
                provider = item.AgentResult?.Provider,
                model = item.AgentResult?.Model,
                item.Status,
                item.Detail,
                patch_file = Path.GetFileName(item.PatchPath),
                patch_sha256 = item.PatchSha256,
                item.Additions,
                item.Deletions
            }),
            verification = mesh.Verification is null
                ? null
                : new
                {
                    mesh.Verification.Passed,
                    mesh.Verification.Command,
                    mesh.Verification.ExitCode,
                    duration_ms = mesh.Verification.Duration.TotalMilliseconds
                },
            review_score = mesh.Review.Score,
            combined_patch = Path.GetFileName(mesh.CombinedPatchPath),
            combined_patch_sha256 = mesh.CombinedPatchSha256,
            mesh.Additions,
            mesh.Deletions,
            started_at = mesh.StartedAt,
            completed_at = mesh.CompletedAt
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(
            path,
            json,
            new UTF8Encoding(false),
            cancellationToken);
    }

    private async Task TryDiscardAsync(string workspaceRoot)
    {
        try
        {
            await _worktrees.DiscardAsync(workspaceRoot, CancellationToken.None);
        }
        catch
        {
            // Managed leftovers remain visible to the Engineering Center.
        }
    }

    private async Task TryRecycleAsync(string workspaceRoot)
    {
        try
        {
            await _worktrees.RecycleAsync(workspaceRoot, CancellationToken.None);
        }
        catch
        {
            // Failure recovery is best effort.
        }
    }

    private static async Task<string> ReadHeadAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var head = await RunGitAsync(
            workspaceRoot,
            ["rev-parse", "HEAD"],
            TimeSpan.FromSeconds(10),
            cancellationToken);
        EnsureGitSucceeded(head, "Git did not return HEAD.");
        return FirstLine(head.Output)
               ?? throw new InvalidOperationException("Git returned an empty HEAD.");
    }

    private static (int Additions, int Deletions) CountPatchLines(string patch)
    {
        var additions = 0;
        var deletions = 0;
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("+++", StringComparison.Ordinal)
                || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }
            if (line.StartsWith('+'))
            {
                additions++;
            }
            else if (line.StartsWith('-'))
            {
                deletions++;
            }
        }
        return (additions, deletions);
    }

    private static IReadOnlyList<EngineeringChangedFile> ParsePatchFiles(string patch)
        => patch.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("diff --git a/", StringComparison.Ordinal))
            .Select(line =>
            {
                var marker = line.IndexOf(" b/", StringComparison.Ordinal);
                var path = marker >= 0 ? line[(marker + 3)..].Trim() : line[13..].Trim();
                return new EngineeringChangedFile("M", path);
            })
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string ComputeHash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void EnsureChildPath(string root, string target)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(target).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Agent Mesh artifact path escaped its root.");
        }
    }

    private static string SafeName(string value)
    {
        var safe = string.Concat(value
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-'))
            .Trim('-');
        return string.IsNullOrWhiteSpace(safe)
            ? "mesh"
            : safe[..Math.Min(40, safe.Length)];
    }

    private static string? FirstLine(string value)
        => value.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private static void EnsureGitSucceeded(GitResult result, string fallback)
    {
        if (!result.Started || result.ExitCode != 0)
        {
            throw new InvalidOperationException(FirstLine(result.Error) ?? fallback);
        }
    }

    private static async Task<GitResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
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
                return new GitResult(false, -1, string.Empty, "Git process did not start.");
            }
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return new GitResult(true, -1, await outputTask, "Git operation timed out.");
            }
            return new GitResult(
                true,
                process.ExitCode,
                await outputTask,
                await errorTask);
        }
        catch (Exception exception)
        {
            return new GitResult(false, -1, string.Empty, exception.Message);
        }
    }

    private sealed record GitResult(bool Started, int ExitCode, string Output, string Error);
}
