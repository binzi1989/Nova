using System.IO;

namespace Nova.Core;

public sealed class WorkspaceContextService
{
    private static readonly HashSet<string> IgnoredDirectories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".idea", ".vs", "bin", "obj", "node_modules",
            "dist", "artifacts", "packages", "coverage"
        };

    public WorkspaceContext Analyze(string root)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            return WorkspaceContext.Empty(root);
        }

        var signals = new List<string>();
        var fileCount = 0;
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && fileCount < 5000)
        {
            var current = stack.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current.Path))
                {
                    fileCount++;
                    AddSignal(signals, Path.GetFileName(file));
                    if (fileCount >= 5000)
                    {
                        break;
                    }
                }
                if (current.Depth >= 4)
                {
                    continue;
                }
                foreach (var directory in Directory.EnumerateDirectories(current.Path))
                {
                    if (!IgnoredDirectories.Contains(Path.GetFileName(directory)))
                    {
                        stack.Push((directory, current.Depth + 1));
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A partial, bounded workspace summary is still useful.
            }
        }

        var technology = DetectTechnology(signals);
        return new WorkspaceContext(
            root,
            Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))
                is { Length: > 0 } name ? name : root,
            technology,
            fileCount,
            signals.Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray());
    }

    private static void AddSignal(List<string> signals, string name)
    {
        var normalized = name.ToLowerInvariant();
        if (normalized is "package.json" or "vite.config.ts" or "vite.config.js"
            or "pyproject.toml" or "requirements.txt" or "cargo.toml"
            or "go.mod" or "pom.xml" or "build.gradle" or "build.gradle.kts"
            or "pubspec.yaml" or "composer.json"
            || normalized.EndsWith(".csproj", StringComparison.Ordinal)
            || normalized.EndsWith(".sln", StringComparison.Ordinal)
            || normalized.EndsWith(".xcodeproj", StringComparison.Ordinal))
        {
            signals.Add(name);
        }
    }

    private static string DetectTechnology(IReadOnlyCollection<string> signals)
    {
        if (signals.Any(item => item.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            return ".NET";
        }
        if (signals.Contains("package.json", StringComparer.OrdinalIgnoreCase))
        {
            return "Node.js / Web";
        }
        if (signals.Contains("pyproject.toml", StringComparer.OrdinalIgnoreCase)
            || signals.Contains("requirements.txt", StringComparer.OrdinalIgnoreCase))
        {
            return "Python";
        }
        if (signals.Contains("Cargo.toml", StringComparer.OrdinalIgnoreCase))
        {
            return "Rust";
        }
        if (signals.Contains("go.mod", StringComparer.OrdinalIgnoreCase))
        {
            return "Go";
        }
        if (signals.Any(item => item.EndsWith(".xcodeproj", StringComparison.OrdinalIgnoreCase)))
        {
            return "Apple / Xcode";
        }
        return "通用文件工作区";
    }
}
