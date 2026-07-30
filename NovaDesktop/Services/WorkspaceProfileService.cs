using System.IO;
using System.Text.Json;

namespace NovaDesktop.Services;

public sealed record WorkspaceProfile(
    string Root,
    string Name,
    string Kind,
    string PrimaryManifest,
    string BuildHint,
    bool IsGitRepository,
    bool Exists,
    DateTimeOffset LastUsed)
{
    public string KindLabel => string.IsNullOrWhiteSpace(PrimaryManifest)
        ? Kind
        : $"{Kind} · {PrimaryManifest}";

    public string StatusLabel => Exists ? BuildHint : "目录已不存在";
}

public sealed class WorkspaceProfileService
{
    private static readonly string[] ManifestNames =
    [
        "*.sln",
        "*.csproj",
        "package.json",
        "pyproject.toml",
        "Cargo.toml",
        "go.mod"
    ];

    private readonly string _historyPath;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };
    private readonly object _sync = new();

    public WorkspaceProfileService(string? historyPath = null)
    {
        _historyPath = historyPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "workspaces.json");
    }

    public string HistoryPath => _historyPath;

    public WorkspaceProfile Analyze(string selectedPath, bool resolveProjectRoot = true)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            throw new ArgumentException("工作区路径不能为空。", nameof(selectedPath));
        }

        var selected = Path.GetFullPath(selectedPath);
        if (!Directory.Exists(selected))
        {
            return new WorkspaceProfile(
                selected,
                Path.GetFileName(selected.TrimEnd(Path.DirectorySeparatorChar)),
                "不可用工作区",
                string.Empty,
                "目录已不存在",
                false,
                false,
                DateTimeOffset.Now);
        }

        var root = resolveProjectRoot ? ResolveProjectRoot(selected) : selected;
        var manifests = FindManifests(root, maximumDepth: 3, maximumResults: 16);
        var primary = SelectPrimaryManifest(manifests);
        var kind = GetKind(primary, manifests);
        var isGit = Directory.Exists(Path.Combine(root, ".git"))
                    || File.Exists(Path.Combine(root, ".git"));
        var buildHint = GetBuildHint(primary, manifests);
        return new WorkspaceProfile(
            root,
            new DirectoryInfo(root).Name,
            kind,
            primary,
            buildHint,
            isGit,
            true,
            DateTimeOffset.Now);
    }

    public WorkspaceProfile Remember(string selectedPath, bool resolveProjectRoot = true)
    {
        var profile = Analyze(selectedPath, resolveProjectRoot);
        if (!profile.Exists)
        {
            return profile;
        }

        lock (_sync)
        {
            var profiles = LoadCore()
                .Where(item => !item.Root.Equals(profile.Root, StringComparison.OrdinalIgnoreCase))
                .Prepend(profile)
                .Take(12)
                .ToArray();
            SaveCore(profiles);
        }
        return profile;
    }

    public IReadOnlyList<WorkspaceProfile> LoadRecent()
    {
        lock (_sync)
        {
            var profiles = new List<WorkspaceProfile>();
            foreach (var item in LoadCore())
            {
                if (!Directory.Exists(item.Root))
                {
                    profiles.Add(item with { Exists = false });
                    continue;
                }
                try
                {
                    profiles.Add(Analyze(item.Root, resolveProjectRoot: false) with
                    {
                        LastUsed = item.LastUsed
                    });
                }
                catch (Exception exception) when (
                    exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException)
                {
                    profiles.Add(item with
                    {
                        Exists = false,
                        BuildHint = "目录当前不可访问"
                    });
                }
            }
            return profiles
                .OrderByDescending(item => item.LastUsed)
                .Take(12)
                .ToArray();
        }
    }

    public string ResolveProjectRoot(string selectedPath)
    {
        var current = new DirectoryInfo(Path.GetFullPath(selectedPath));
        DirectoryInfo? nearestManifest = null;
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git"))
                || File.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }

            if (nearestManifest is null && HasDirectManifest(current.FullName))
            {
                nearestManifest = current;
            }

            current = current.Parent;
        }

        return nearestManifest?.FullName ?? Path.GetFullPath(selectedPath);
    }

    private IReadOnlyList<WorkspaceProfile> LoadCore()
    {
        if (!File.Exists(_historyPath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<WorkspaceProfile[]>(
                       File.ReadAllText(_historyPath),
                       _options)
                   ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return [];
        }
    }

    private void SaveCore(IReadOnlyList<WorkspaceProfile> profiles)
    {
        var directory = Path.GetDirectoryName(_historyPath)
                        ?? throw new InvalidOperationException("工作区历史路径没有父目录。");
        Directory.CreateDirectory(directory);
        var temporary = _historyPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(profiles, _options));
        File.Move(temporary, _historyPath, overwrite: true);
    }

    private static bool HasDirectManifest(string directory)
        => ManifestNames.Any(pattern => Directory.EnumerateFiles(
            directory,
            pattern,
            SearchOption.TopDirectoryOnly).Any());

    private static IReadOnlyList<string> FindManifests(
        string root,
        int maximumDepth,
        int maximumResults)
    {
        var results = new List<string>();
        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((root, 0));
        while (pending.Count > 0 && results.Count < maximumResults)
        {
            var (directory, depth) = pending.Dequeue();
            try
            {
                foreach (var pattern in ManifestNames)
                {
                    foreach (var path in Directory.EnumerateFiles(
                                 directory,
                                 pattern,
                                 SearchOption.TopDirectoryOnly))
                    {
                        results.Add(Path.GetRelativePath(root, path));
                        if (results.Count >= maximumResults)
                        {
                            break;
                        }
                    }
                    if (results.Count >= maximumResults)
                    {
                        break;
                    }
                }

                if (depth >= maximumDepth)
                {
                    continue;
                }

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var info = new DirectoryInfo(child);
                    if (IsIgnoredDirectory(info.Name)
                        || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }
                    pending.Enqueue((child, depth + 1));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A protected child does not invalidate the selected workspace.
            }
        }

        return results
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value.Count(character => character is '/' or '\\'))
            .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string SelectPrimaryManifest(IReadOnlyList<string> manifests)
        => manifests.FirstOrDefault(path => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
           ?? manifests.FirstOrDefault(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
           ?? manifests.FirstOrDefault(path => path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase))
           ?? manifests.FirstOrDefault(path => path.EndsWith("pyproject.toml", StringComparison.OrdinalIgnoreCase))
           ?? manifests.FirstOrDefault(path => path.EndsWith("Cargo.toml", StringComparison.OrdinalIgnoreCase))
           ?? manifests.FirstOrDefault(path => path.EndsWith("go.mod", StringComparison.OrdinalIgnoreCase))
           ?? string.Empty;

    private static string GetKind(string primary, IReadOnlyList<string> manifests)
    {
        if (primary.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || primary.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return ".NET 工程";
        }
        if (primary.EndsWith("package.json", StringComparison.OrdinalIgnoreCase))
        {
            return "Node 工程";
        }
        if (primary.EndsWith("pyproject.toml", StringComparison.OrdinalIgnoreCase))
        {
            return "Python 工程";
        }
        if (primary.EndsWith("Cargo.toml", StringComparison.OrdinalIgnoreCase))
        {
            return "Rust 工程";
        }
        if (primary.EndsWith("go.mod", StringComparison.OrdinalIgnoreCase))
        {
            return "Go 工程";
        }
        return manifests.Count > 1 ? "多工程工作区" : "通用文件工作区";
    }

    private static string GetBuildHint(string primary, IReadOnlyList<string> manifests)
    {
        if (string.IsNullOrWhiteSpace(primary))
        {
            return "未发现构建清单 · 仍可进行文件任务";
        }
        var additional = Math.Max(0, manifests.Count - 1);
        return additional == 0
            ? $"智能增量构建 · {primary}"
            : $"智能增量构建 · 主清单 {primary} · 另有 {additional} 个工程";
    }

    private static bool IsIgnoredDirectory(string name)
        => name is ".git" or ".nova" or "bin" or "obj" or "node_modules"
            or "dist" or "build" or "target" or ".venv" or "venv"
           || name.StartsWith(".", StringComparison.Ordinal);
}
