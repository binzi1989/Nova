using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovaDesktop.Services;

public sealed record InstalledSkill(
    string Id,
    string Name,
    string Description,
    string DirectoryPath,
    bool Enabled,
    DateTimeOffset InstalledAt,
    int FileCount,
    long SizeBytes);

public sealed class SkillRegistryService
{
    private const int MaximumFiles = 500;
    private const long MaximumBytes = 25L * 1024L * 1024L;
    private const int MaximumInstructionCharacters = 120_000;
    private const string InstructionsFileName = "SKILL.md";
    private const string MetadataFileName = ".nova-skill.json";

    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".msi", ".bat", ".cmd", ".com", ".scr", ".sys", ".lnk"
    };

    private readonly string _skillsRoot;
    private readonly string _skillsPrefix;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SkillRegistryService(string? skillsRoot = null)
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA");
        _skillsRoot = Path.GetFullPath(skillsRoot ?? Path.Combine(dataDirectory, "skills"));
        _skillsPrefix = _skillsRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    public string SkillsRoot => _skillsRoot;

    public IReadOnlyList<InstalledSkill> GetSkills()
    {
        if (!Directory.Exists(_skillsRoot))
        {
            return [];
        }
        var skills = new List<InstalledSkill>();
        foreach (var directory in Directory.EnumerateDirectories(_skillsRoot))
        {
            try
            {
                var fullPath = Path.GetFullPath(directory);
                if (!IsContained(fullPath)
                    || new DirectoryInfo(fullPath).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                var instructionsPath = Path.Combine(fullPath, InstructionsFileName);
                if (!File.Exists(instructionsPath))
                {
                    continue;
                }

                var metadata = ReadMetadata(fullPath);
                var frontmatter = ParseFrontmatter(File.ReadAllText(instructionsPath));
                var files = Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories)
                    .Where(path => !Path.GetFileName(path).Equals(MetadataFileName, StringComparison.OrdinalIgnoreCase))
                    .Select(path => new FileInfo(path))
                    .ToArray();
                skills.Add(new InstalledSkill(
                    Path.GetFileName(fullPath),
                    frontmatter.Name,
                    frontmatter.Description,
                    fullPath,
                    metadata.Enabled,
                    metadata.InstalledAt,
                    files.Length,
                    files.Sum(file => file.Length)));
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or InvalidOperationException)
            {
                // A malformed skill is isolated from the rest of the registry.
            }
        }

        return skills.OrderBy(skill => skill.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public string ListForModel()
    {
        var skills = GetSkills()
            .Where(skill => skill.Enabled)
            .Select(skill => new
            {
                skill.Id,
                skill.Name,
                skill.Description
            })
            .ToArray();
        return JsonSerializer.Serialize(new
        {
            root = _skillsRoot,
            count = skills.Length,
            skills
        });
    }

    public string ReadInstructions(string id)
    {
        var skill = GetSkills().FirstOrDefault(item =>
            item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
            || item.Name.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Installed skill '{id}' was not found.");
        if (!skill.Enabled)
        {
            throw new InvalidOperationException($"Skill '{skill.Name}' is disabled.");
        }

        var content = File.ReadAllText(Path.Combine(skill.DirectoryPath, InstructionsFileName));
        var truncated = content.Length > MaximumInstructionCharacters;
        if (truncated)
        {
            content = content[..MaximumInstructionCharacters];
        }
        return JsonSerializer.Serialize(new
        {
            skill.Id,
            skill.Name,
            skill.Description,
            truncated,
            content
        });
    }

    public async Task<InstalledSkill> InstallFromFolderAsync(
        string sourceFolder,
        CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(sourceFolder);
        if (!Directory.Exists(source))
        {
            throw new InvalidOperationException("Skill source folder does not exist.");
        }
        if (new DirectoryInfo(source).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Skill source folder cannot be a symbolic link or reparse point.");
        }

        var instructionsPath = Path.Combine(source, InstructionsFileName);
        if (!File.Exists(instructionsPath))
        {
            throw new InvalidOperationException("The selected folder must contain SKILL.md.");
        }

        var instructions = await File.ReadAllTextAsync(instructionsPath, cancellationToken);
        var frontmatter = ParseFrontmatter(instructions);
        var id = CreateSkillId(frontmatter.Name, source);
        var destination = ResolveSkillDirectory(id);
        var temporary = ResolveSkillDirectory("install-" + Guid.NewGuid().ToString("N"));

        var sourceFiles = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .ToArray();
        if (sourceFiles.Length == 0 || sourceFiles.Length > MaximumFiles)
        {
            throw new InvalidOperationException($"A skill must contain 1-{MaximumFiles} files.");
        }
        if (sourceFiles.Any(file =>
                file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || BlockedExtensions.Contains(file.Extension)))
        {
            throw new InvalidOperationException("Skill contains a symbolic link or blocked executable file type.");
        }
        var totalBytes = sourceFiles.Sum(file => file.Length);
        if (totalBytes > MaximumBytes)
        {
            throw new InvalidOperationException("Skill exceeds the 25 MB installation limit.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_skillsRoot);
            if (Directory.Exists(destination))
            {
                throw new InvalidOperationException($"Skill '{frontmatter.Name}' is already installed.");
            }

            Directory.CreateDirectory(temporary);
            foreach (var sourceFile in sourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(source, sourceFile.FullName);
                if (relativePath.StartsWith("..", StringComparison.Ordinal)
                    || Path.IsPathRooted(relativePath))
                {
                    throw new InvalidOperationException("Skill file escapes the selected source folder.");
                }
                var destinationFile = Path.Combine(temporary, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
                await using var input = File.OpenRead(sourceFile.FullName);
                await using var output = new FileStream(
                    destinationFile,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    useAsync: true);
                await input.CopyToAsync(output, cancellationToken);
            }

            var installedAt = DateTimeOffset.UtcNow;
            await WriteMetadataAsync(temporary, true, installedAt, cancellationToken);
            Directory.Move(temporary, destination);
            return new InstalledSkill(
                id,
                frontmatter.Name,
                frontmatter.Description,
                destination,
                true,
                installedAt,
                sourceFiles.Length,
                totalBytes);
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
            _gate.Release();
        }
    }

    public async Task<InstalledSkill> InstallBundledAsync(
        string id,
        string instructions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instructions)
            || instructions.Length > MaximumInstructionCharacters)
        {
            throw new InvalidOperationException(
                $"Bundled Skill instructions must contain 1-{MaximumInstructionCharacters} characters.");
        }

        var frontmatter = ParseFrontmatter(instructions);
        var destination = ResolveSkillDirectory(id);
        var temporary = ResolveSkillDirectory("install-" + Guid.NewGuid().ToString("N"));
        var contentBytes = Encoding.UTF8.GetByteCount(instructions);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_skillsRoot);
            if (Directory.Exists(destination))
            {
                throw new InvalidOperationException($"Skill '{frontmatter.Name}' is already installed.");
            }

            Directory.CreateDirectory(temporary);
            await File.WriteAllTextAsync(
                Path.Combine(temporary, InstructionsFileName),
                instructions,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            var installedAt = DateTimeOffset.UtcNow;
            await WriteMetadataAsync(temporary, true, installedAt, cancellationToken);
            Directory.Move(temporary, destination);
            return new InstalledSkill(
                id,
                frontmatter.Name,
                frontmatter.Description,
                destination,
                true,
                installedAt,
                1,
                contentBytes);
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
            _gate.Release();
        }
    }

    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken)
    {
        var directory = ResolveExistingSkillDirectory(id);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var metadata = ReadMetadata(directory);
            await WriteMetadataAsync(directory, enabled, metadata.InstalledAt, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UninstallAsync(string id, CancellationToken cancellationToken)
    {
        var directory = ResolveExistingSkillDirectory(id);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(directory, recursive: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ResolveExistingSkillDirectory(string id)
    {
        var directory = ResolveSkillDirectory(id);
        if (!Directory.Exists(directory)
            || !File.Exists(Path.Combine(directory, InstructionsFileName)))
        {
            throw new InvalidOperationException($"Installed skill '{id}' was not found.");
        }
        return directory;
    }

    private string ResolveSkillDirectory(string id)
    {
        if (!Regex.IsMatch(id, "^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException("Skill ID is invalid.");
        }
        var path = Path.GetFullPath(Path.Combine(_skillsRoot, id));
        if (!IsContained(path))
        {
            throw new InvalidOperationException("Skill path escapes the NOVA skills directory.");
        }
        return path;
    }

    private bool IsContained(string path)
        => path.StartsWith(_skillsPrefix, StringComparison.OrdinalIgnoreCase)
           && !path.Equals(_skillsRoot, StringComparison.OrdinalIgnoreCase);

    private static string CreateSkillId(string name, string source)
    {
        var normalized = name.Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, "[^a-z0-9_-]+", "-").Trim('-');
        if (normalized.Length == 0)
        {
            var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..10].ToLowerInvariant();
            normalized = "skill-" + hash;
        }
        return normalized[..Math.Min(normalized.Length, 64)];
    }

    private static (string Name, string Description) ParseFrontmatter(string text)
    {
        var fallback = "Untitled skill";
        using var reader = new StringReader(text);
        if (!string.Equals(reader.ReadLine()?.Trim(), "---", StringComparison.Ordinal))
        {
            return (fallback, "Local NOVA skill");
        }

        string? name = null;
        string? description = null;
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim() == "---")
            {
                break;
            }
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"', '\'');
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                name = value;
            }
            else if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
            {
                description = value;
            }
        }

        var safeName = string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
        if (safeName.Length > 100)
        {
            safeName = safeName[..100];
        }
        var safeDescription = string.IsNullOrWhiteSpace(description)
            ? "Local NOVA skill"
            : description.Trim();
        if (safeDescription.Length > 500)
        {
            safeDescription = safeDescription[..500];
        }
        return (safeName, safeDescription);
    }

    private static (bool Enabled, DateTimeOffset InstalledAt) ReadMetadata(string directory)
    {
        var path = Path.Combine(directory, MetadataFileName);
        if (!File.Exists(path))
        {
            return (true, new DirectoryInfo(directory).CreationTimeUtc);
        }
        var metadata = JsonSerializer.Deserialize<SkillMetadata>(File.ReadAllText(path));
        return (metadata?.Enabled ?? true, metadata?.InstalledAt ?? DateTimeOffset.UtcNow);
    }

    private static async Task WriteMetadataAsync(
        string directory,
        bool enabled,
        DateTimeOffset installedAt,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, MetadataFileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(
                    new SkillMetadata(enabled, installedAt),
                    new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed record SkillMetadata(bool Enabled, DateTimeOffset InstalledAt);
}
