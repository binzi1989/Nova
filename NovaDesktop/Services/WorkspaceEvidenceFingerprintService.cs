using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public sealed class WorkspaceEvidenceFingerprintService
{
    private const int MaximumFiles = 12_000;
    private const long MaximumHashedBytes = 384L * 1024L * 1024L;
    private static readonly HashSet<string> ExcludedDirectories = new(
        [
            ".git",
            ".idea",
            ".nova",
            ".vs",
            ".vscode",
            "bin",
            "coverage",
            "dist",
            "node_modules",
            "obj",
            "packages",
            "target"
        ],
        StringComparer.OrdinalIgnoreCase);

    public Task<WorkspaceEvidenceFingerprint> CaptureAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => Capture(workspaceRoot, cancellationToken),
            cancellationToken);

    public WorkspaceEvidenceFingerprint Capture(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)
            || !Directory.Exists(workspaceRoot))
        {
            throw new DirectoryNotFoundException(
                $"Workspace does not exist: {workspaceRoot}");
        }

        var root = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var enumerationComplete = true;
        var paths = EnumerateEvidenceFiles(
                root,
                cancellationToken,
                () => enumerationComplete = false)
            .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal)
            .ToArray();
        var complete = enumerationComplete && paths.Length <= MaximumFiles;
        var selected = paths.Take(MaximumFiles);
        var fileCount = 0;
        long hashedBytes = 0;
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendUtf8(aggregate, "NOVA-WORKSPACE-EVIDENCE-V1\n");
        foreach (var path in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists || info.Length < 0)
                {
                    complete = false;
                    continue;
                }
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException)
            {
                complete = false;
                continue;
            }

            if (hashedBytes + info.Length > MaximumHashedBytes)
            {
                complete = false;
                break;
            }

            var relativePath = Path.GetRelativePath(root, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            AppendUtf8(aggregate, relativePath);
            AppendUtf8(aggregate, "\0");
            AppendUtf8(aggregate, info.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            AppendUtf8(aggregate, "\0");

            try
            {
                using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024,
                    FileOptions.SequentialScan);
                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        fileHash.AppendData(buffer, 0, read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                aggregate.AppendData(fileHash.GetHashAndReset());
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException)
            {
                complete = false;
                AppendUtf8(aggregate, "[UNREADABLE]");
            }
            AppendUtf8(aggregate, "\n");
            fileCount++;
            hashedBytes += info.Length;
        }

        return new WorkspaceEvidenceFingerprint(
            root,
            Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant(),
            fileCount,
            hashedBytes,
            complete,
            DateTimeOffset.Now,
            complete
                ? $"Captured {fileCount} files ({hashedBytes:N0} bytes)."
                : $"Capture reached its safety boundary after {fileCount} files "
                  + $"({hashedBytes:N0} bytes); proof cannot be considered fresh.");
    }

    private static IEnumerable<string> EnumerateEvidenceFiles(
        string root,
        CancellationToken cancellationToken,
        Action markIncomplete)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException)
            {
                markIncomplete();
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException)
                {
                    markIncomplete();
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!ExcludedDirectories.Contains(Path.GetFileName(entry)))
                    {
                        pending.Push(entry);
                    }
                    continue;
                }
                if ((attributes & (FileAttributes.Device | FileAttributes.Offline)) == 0)
                {
                    yield return entry;
                }
            }
        }
    }

    private static void AppendUtf8(IncrementalHash hash, string value)
        => hash.AppendData(Encoding.UTF8.GetBytes(value));
}
