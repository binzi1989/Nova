using System.IO;
using System.Text;

namespace NovaDesktop.Services;

public sealed record DeliveryManifest(
    string ArtifactPath,
    IReadOnlyList<EngineeringChangedFile> ChangedFiles,
    string ResultStatus,
    string VerificationStatus,
    string Summary,
    string Preview);

public sealed class DeliveryManifestService
{
    private const int MaximumDisplayedFiles = 80;

    public async Task<DeliveryManifest> CreateAsync(
        string taskId,
        string taskTitle,
        EngineeringWorkspaceSnapshot before,
        EngineeringWorkspaceSnapshot after,
        string resultStatus,
        int proofScore,
        bool verificationAttempted,
        bool verificationPassed,
        string verificationSummary,
        CancellationToken cancellationToken = default)
    {
        var changedFiles = GetChangedFiles(before, after)
            .Where(item => !IsNovaInternal(item.Path))
            .Take(MaximumDisplayedFiles)
            .ToArray();
        var safeStatus = string.IsNullOrWhiteSpace(resultStatus)
            ? "DELIVERED"
            : resultStatus.Trim().ToUpperInvariant();
        var verificationStatus = verificationAttempted
            ? verificationPassed ? "验证通过" : "验证未通过"
            : "未运行自动验证";
        var summary =
            $"{safeStatus} · {changedFiles.Length} 个实际文件 · "
            + $"{verificationStatus}";
        var preview = BuildPreview(
            taskTitle,
            after,
            changedFiles,
            safeStatus,
            proofScore,
            verificationStatus,
            verificationSummary);
        var directory = Path.Combine(
            after.WorkspaceRoot,
            ".nova",
            "deliveries");
        Directory.CreateDirectory(directory);
        var artifactPath = Path.Combine(
            directory,
            NormalizeTaskId(taskId) + "-delivery.md");
        var temporaryPath = artifactPath
                            + "."
                            + Guid.NewGuid().ToString("N")
                            + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                preview,
                new UTF8Encoding(false),
                cancellationToken);
            File.Move(temporaryPath, artifactPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // The committed manifest remains authoritative.
                }
            }
        }

        return new DeliveryManifest(
            artifactPath,
            changedFiles,
            safeStatus,
            verificationStatus,
            summary,
            preview);
    }

    public static IReadOnlyList<EngineeringChangedFile> GetChangedFiles(
        EngineeringWorkspaceSnapshot before,
        EngineeringWorkspaceSnapshot after)
    {
        if (after.IsGitRepository)
        {
            return after.ChangedFiles
                .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var beforeEntries = ParseInventory(before.WorkspaceInventoryEntries);
        var afterEntries = ParseInventory(after.WorkspaceInventoryEntries);
        return afterEntries
            .Where(item => !beforeEntries.TryGetValue(item.Key, out var oldSignature)
                           || !oldSignature.Equals(
                               item.Value,
                               StringComparison.Ordinal))
            .Select(item => new EngineeringChangedFile(
                beforeEntries.ContainsKey(item.Key) ? "M" : "A",
                item.Key))
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Dictionary<string, string> ParseInventory(
        IReadOnlyList<string> entries)
    {
        var result = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var separator = entry.IndexOf('|');
            if (separator <= 0)
            {
                continue;
            }
            result[entry[..separator]] = entry[(separator + 1)..];
        }
        return result;
    }

    private static string BuildPreview(
        string taskTitle,
        EngineeringWorkspaceSnapshot snapshot,
        IReadOnlyList<EngineeringChangedFile> changedFiles,
        string resultStatus,
        int proofScore,
        string verificationStatus,
        string verificationSummary)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 本轮交付");
        builder.AppendLine();
        builder.AppendLine($"**任务**：{taskTitle}");
        builder.AppendLine($"**结果**：{resultStatus} · Proof {proofScore}/100");
        builder.AppendLine($"**验证**：{verificationStatus}");
        builder.AppendLine($"**工作区**：`{snapshot.WorkspaceRoot}`");
        builder.AppendLine();
        builder.AppendLine("## 实际变更");
        builder.AppendLine();
        if (changedFiles.Count == 0)
        {
            builder.AppendLine("- 未检测到可单独列出的工作区文件；请以结果与证据记录为准。");
        }
        else
        {
            foreach (var file in changedFiles)
            {
                builder.Append("- `")
                    .Append(file.Status)
                    .Append("` ")
                    .AppendLine(file.Path);
            }
        }
        builder.AppendLine();
        builder.AppendLine("## 如何接手");
        builder.AppendLine();
        builder.AppendLine("1. 在当前工作区直接打开上方文件。");
        builder.AppendLine($"2. 需要复验时运行：`{snapshot.VerificationCommand}`");
        builder.AppendLine("3. 需要继续时回到 NOVA，选择“继续完善”。");
        builder.AppendLine();
        builder.AppendLine("## 验证摘要");
        builder.AppendLine();
        builder.AppendLine(string.IsNullOrWhiteSpace(verificationSummary)
            ? verificationStatus
            : verificationSummary.Trim());
        return builder.ToString();
    }

    private static bool IsNovaInternal(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        return normalized.Equals(".nova", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith(
                   ".nova/",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTaskId(string taskId)
    {
        var safe = string.Concat(taskId
            .Where(character => char.IsAsciiLetterOrDigit(character)
                                || character is '-' or '_')
            .Take(64));
        return string.IsNullOrWhiteSpace(safe) ? "task" : safe;
    }
}
