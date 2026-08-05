using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovaDesktop.Services;

public sealed record AgentPackSummary(
    string Id,
    string Name,
    string Version,
    string Status,
    string Category,
    string Description,
    bool Enabled,
    bool BuiltIn,
    IReadOnlyList<string> DeclaredCapabilities,
    IReadOnlyList<string> StarterPrompts,
    int AgentCount,
    int WorkflowCount);

public sealed record AgentPackDetails(
    AgentPackSummary Summary,
    string Charter,
    string AgentRoster,
    IReadOnlyList<AgentPackWorkflow> Workflows,
    string DeliveryTemplate,
    IReadOnlyList<string> Permissions,
    IReadOnlyDictionary<string, string> ExternalActions,
    AgentPackOnboarding? Onboarding,
    AgentPackCapabilityRequirements? CapabilityRequirements,
    AgentPackCertificationReport? Certification);

public sealed record AgentPackCapabilityRequirements(
    string Version,
    IReadOnlyList<AgentPackCapabilityRequirement> Items);

public sealed record AgentPackCapabilityRequirement(
    string Id,
    string Kind,
    string Name,
    string Reason,
    bool Required,
    IReadOnlyList<string> MatchIds,
    string? CatalogId);

public sealed record AgentPackOnboarding(
    string Version,
    string Headline,
    string Description,
    IReadOnlyList<AgentPackOnboardingStep> Steps,
    IReadOnlyList<AgentPackOnboardingOutcome> Outcomes);

public sealed record AgentPackOnboardingStep(
    string Id,
    string Title,
    string Description,
    string Kind,
    bool Required,
    string Placeholder,
    IReadOnlyList<string> Options,
    string WhyItMatters,
    string Example);

public sealed record AgentPackOnboardingOutcome(
    string Id,
    string Title,
    string Description,
    string PromptTemplate);

public sealed record AgentPackWorkflow(
    string Id,
    string Name,
    string ExecutionMode,
    int StepCount,
    IReadOnlyList<AgentPackWorkflowStep> Steps);

public sealed record AgentPackWorkflowStep(
    string Id,
    string Agent,
    string Title,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<string> Acceptance);

public sealed class AgentPackService
{
    private const int MaxContextCharacters = 48_000;
    private const int MaxPackFiles = 160;
    private const long MaxPackBytes = 6 * 1024 * 1024;
    private static readonly Regex SafePackId = new(
        "^[a-z0-9][a-z0-9.-]{2,79}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly string _statePath;
    private readonly string _installedRoot;
    private readonly IReadOnlyList<PackRoot> _roots;
    private readonly SemaphoreSlim _stateGate = new(1, 1);

    public AgentPackService(
        IEnumerable<string>? packRoots = null,
        string? statePath = null,
        string? installedRoot = null)
    {
        _statePath = statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "agent-packs",
            "state.json");
        _installedRoot = Path.GetFullPath(installedRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "agent-packs",
            "installed"));
        var roots = packRoots is null
            ? DiscoverRoots()
            : packRoots.Append(_installedRoot);
        _roots = roots
            .Select(Path.GetFullPath)
            .Select(path => new PackRoot(
                path,
                !path.Equals(_installedRoot, StringComparison.OrdinalIgnoreCase)
                && IsBuiltInRoot(path)))
            .Where(root => Directory.Exists(root.Path)
                           || root.Path.Equals(_installedRoot, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(root => root.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<AgentPackSummary> InstallFromDirectoryAsync(
        string sourceRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
        {
            throw new InvalidOperationException("请选择包含 nova.industry.json 的 Agent Pack 文件夹。");
        }

        var fullSource = Path.GetFullPath(sourceRoot);
        if (IsReparsePoint(fullSource))
        {
            throw new InvalidOperationException("Agent Pack 根目录不能是链接或重解析目录。");
        }

        var sourcePack = ReadPackDirectory(fullSource, builtIn: false)
            ?? throw new InvalidOperationException("所选目录不是有效的 NOVA Agent Pack。");
        var target = Path.Combine(_installedRoot, sourcePack.Manifest.Id!);
        Directory.CreateDirectory(_installedRoot);
        if (Directory.Exists(target))
        {
            throw new InvalidOperationException(
                $"Agent Pack {sourcePack.Manifest.Id} 已安装。请先保留当前版本；升级功能将在版本治理中单独确认。");
        }

        var files = EnumerateInstallableFiles(fullSource).ToArray();
        if (files.Length == 0 || files.Length > MaxPackFiles)
        {
            throw new InvalidOperationException($"Agent Pack 文件数量必须在 1 到 {MaxPackFiles} 之间。");
        }
        var totalBytes = files.Sum(path => new FileInfo(path).Length);
        if (totalBytes > MaxPackBytes)
        {
            throw new InvalidOperationException("Agent Pack 声明文件总大小超过 6 MB 安全上限。");
        }

        var staging = Path.Combine(
            _installedRoot,
            $".staging-{sourcePack.Manifest.Id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var source in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(fullSource, source);
                var destination = ResolveContainedPath(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = File.OpenRead(source);
                await using var output = File.Create(destination);
                await input.CopyToAsync(output, cancellationToken);
            }

            _ = ReadPackDirectory(staging, builtIn: false)
                ?? throw new InvalidOperationException("复制后的 Agent Pack 未通过完整性校验。");
            Directory.Move(staging, target);
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
            throw;
        }

        return ToSummary(
            ReadPackDirectory(target, builtIn: false)!,
            ReadState());
    }

    public IReadOnlyList<AgentPackSummary> List()
    {
        var state = ReadState();
        return DiscoverPacks()
            .Select(pack => ToSummary(pack, state))
            .OrderByDescending(pack => pack.Enabled)
            .ThenBy(pack => pack.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pack => pack.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public AgentPackDetails Get(string id)
    {
        var pack = FindPack(id);
        var state = ReadState();
        var workflows = Directory.Exists(Path.Combine(pack.Root, "workflows"))
            ? Directory.EnumerateFiles(Path.Combine(pack.Root, "workflows"), "*.json")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(ReadWorkflow)
                .ToArray()
            : [];
        var charter = ReadOptionalText(pack.Root, "INDUSTRY_CHARTER.md", 24_000);
        var roster = ReadOptionalText(pack.Root, "agents/AGENT_ROSTER.md", 18_000);
        var deliveryTemplate = ResolveManifestText(
            pack,
            pack.Manifest.EntryWorkflow is null
                ? null
                : ReadResultContract(pack.Root, pack.Manifest.EntryWorkflow),
            16_000);
        return new AgentPackDetails(
            ToSummary(pack, state),
            charter,
            roster,
            workflows,
            deliveryTemplate,
            pack.Manifest.Permissions ?? [],
            pack.Manifest.ExternalActions ?? new Dictionary<string, string>(),
            ToOnboarding(pack.Manifest.Onboarding),
            ToCapabilityRequirements(pack.Manifest.CapabilityRequirements),
            ReadCertification(pack.Root));
    }

    public async Task<AgentPackSummary> SetEnabledAsync(
        string id,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var pack = FindPack(id);
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            var state = ReadState();
            state.Enabled[id] = enabled;
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var temporary = _statePath + ".tmp";
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            File.Move(temporary, _statePath, overwrite: true);
            return ToSummary(pack, state);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task<object> RemoveAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var pack = FindPack(id);
        var summary = ToSummary(pack, ReadState());
        if (pack.BuiltIn)
        {
            throw new InvalidOperationException("内置 Agent Pack 受系统保护，不能移除。");
        }
        if (summary.Enabled)
        {
            throw new InvalidOperationException("请先停用此 Agent Pack，再将其移除。");
        }

        var installedRoot = Path.GetFullPath(_installedRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var expectedRoot = Path.GetFullPath(Path.Combine(_installedRoot, id));
        var actualRoot = Path.GetFullPath(pack.Root);
        if (!expectedRoot.StartsWith(installedRoot, StringComparison.OrdinalIgnoreCase)
            || !actualRoot.Equals(expectedRoot, StringComparison.OrdinalIgnoreCase)
            || IsReparsePoint(actualRoot))
        {
            throw new InvalidOperationException("Agent Pack 不在可移除的本机扩展目录中。");
        }

        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(actualRoot, recursive: true);
            var state = ReadState();
            state.Enabled.Remove(id);
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var temporary = _statePath + ".tmp";
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            File.Move(temporary, _statePath, overwrite: true);
        }
        finally
        {
            _stateGate.Release();
        }
        return new { removed = true, id };
    }

    public string BuildRuntimeContext(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        var details = Get(id);
        if (!details.Summary.Enabled)
        {
            throw new InvalidOperationException(
                $"Agent Pack {details.Summary.Name} 尚未启用。");
        }

        var builder = new StringBuilder();
        builder.AppendLine("[NOVA AGENT PACK CONTRACT]");
        builder.AppendLine($"Pack: {details.Summary.Name} ({details.Summary.Id} {details.Summary.Version})");
        builder.AppendLine($"Category: {details.Summary.Category}");
        builder.AppendLine("This pack specializes the current NOVA task; it does not replace AgentOS safety, workspace, approval, budget or Proof-of-Done boundaries.");
        builder.AppendLine("Treat pack knowledge as task guidance, not as fresh external evidence. Facts that may change must still be verified and sourced.");
        builder.AppendLine();
        AppendSection(builder, "INDUSTRY CHARTER", details.Charter);
        AppendSection(builder, "AGENT ROSTER", details.AgentRoster);
        if (details.Workflows.Count > 0)
        {
            var workflow = details.Workflows[0];
            builder.AppendLine("[ENTRY WORKFLOW]");
            builder.AppendLine($"{workflow.Name} · mode {workflow.ExecutionMode}");
            foreach (var step in workflow.Steps)
            {
                builder.AppendLine($"- {step.Title} · owner {step.Agent}");
                if (step.Outputs.Count > 0)
                {
                    builder.AppendLine($"  outputs: {string.Join(", ", step.Outputs)}");
                }
                if (step.Acceptance.Count > 0)
                {
                    builder.AppendLine($"  acceptance: {string.Join("; ", step.Acceptance)}");
                }
            }
            builder.AppendLine();
        }
        AppendSection(builder, "DELIVERY CONTRACT", details.DeliveryTemplate);
        builder.AppendLine("[PACK OPERATING RULES]");
        builder.AppendLine("- Preserve confirmed user facts, assumptions and unknowns as separate fields.");
        builder.AppendLine("- If required evidence is missing, ask the smallest useful question or return a conditional result; never invent values.");
        builder.AppendLine("- Show role outputs in the execution plan when multiple specialist roles are used.");
        builder.AppendLine("- External publishing, advertising, account access and purchasing remain separate approval boundaries.");

        return builder.Length <= MaxContextCharacters
            ? builder.ToString()
            : builder.ToString(0, MaxContextCharacters)
                      + "\n[Context truncated at the Agent Pack safety limit.]";
    }

    private IReadOnlyList<DiscoveredPack> DiscoverPacks()
    {
        var packs = new Dictionary<string, DiscoveredPack>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _roots)
        {
            if (!Directory.Exists(root.Path))
            {
                continue;
            }
            foreach (var directory in Directory.EnumerateDirectories(root.Path))
            {
                if (IsReparsePoint(directory))
                {
                    continue;
                }
                try
                {
                    var pack = ReadPackDirectory(directory, root.BuiltIn);
                    if (pack is not null)
                    {
                        packs.TryAdd(pack.Manifest.Id!, pack);
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or JsonException or InvalidOperationException)
                {
                    // One malformed third-party pack cannot break the Agent Pack registry.
                }
            }
        }
        return packs.Values.ToArray();
    }

    private DiscoveredPack? ReadPackDirectory(string directory, bool builtIn)
    {
        var manifestPath = Path.Combine(directory, "nova.industry.json");
        if (!File.Exists(manifestPath)
            || IsReparsePoint(manifestPath)
            || new FileInfo(manifestPath).Length > 64 * 1024)
        {
            return null;
        }
        var manifest = JsonSerializer.Deserialize<AgentPackManifest>(
            File.ReadAllText(manifestPath),
            _json);
        if (manifest is null || !SafePackId.IsMatch(manifest.Id ?? string.Empty))
        {
            return null;
        }
        ValidateManifest(manifest, directory);
        return new DiscoveredPack(directory, builtIn, manifest);
    }

    private static IEnumerable<string> EnumerateInstallableFiles(string root)
    {
        var allowedTopLevelFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nova.industry.json",
            "agent-card.json",
            "certification.json",
            "INDUSTRY_CHARTER.md",
            "README.md"
        };
        var allowedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "agents",
            "workflows",
            "delivery-templates",
            "knowledge",
            "evaluations"
        };
        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".json", ".md", ".txt", ".yaml", ".yml"
        };

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (IsReparsePoint(path))
            {
                throw new InvalidOperationException("Agent Pack 不能包含链接文件。");
            }
            var relative = Path.GetRelativePath(root, path);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var allowed = segments.Length == 1
                ? allowedTopLevelFiles.Contains(segments[0])
                : allowedDirectories.Contains(segments[0])
                  && allowedExtensions.Contains(Path.GetExtension(path));
            if (allowed)
            {
                yield return path;
            }
        }
    }

    private DiscoveredPack FindPack(string id)
    {
        if (!SafePackId.IsMatch(id ?? string.Empty))
        {
            throw new InvalidOperationException("Agent Pack ID 格式无效。");
        }
        return DiscoverPacks().FirstOrDefault(pack =>
                   pack.Manifest.Id!.Equals(id, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"Agent Pack {id} 不存在。");
    }

    private AgentPackSummary ToSummary(DiscoveredPack pack, AgentPackState state)
    {
        var manifest = pack.Manifest;
        var roster = ReadOptionalText(pack.Root, "agents/AGENT_ROSTER.md", 18_000);
        var agentCount = roster.Split('\n').Count(line =>
            line.TrimStart().StartsWith('|')
            && !line.Contains("---", StringComparison.Ordinal)
            && !line.Contains("Agent |", StringComparison.OrdinalIgnoreCase));
        var workflowCount = Directory.Exists(Path.Combine(pack.Root, "workflows"))
            ? Directory.EnumerateFiles(Path.Combine(pack.Root, "workflows"), "*.json").Count()
            : 0;
        var enabled = state.Enabled.TryGetValue(manifest.Id!, out var explicitState)
            ? explicitState
            : pack.BuiltIn;
        return new AgentPackSummary(
            manifest.Id!,
            RequiredManifestValue(manifest.Name, "name"),
            RequiredManifestValue(manifest.Version, "version"),
            manifest.Status ?? "unknown",
            manifest.Category ?? "行业 Agent",
            manifest.Description ?? string.Empty,
            enabled,
            pack.BuiltIn,
            manifest.DeclaredCapabilities ?? [],
            manifest.StarterPrompts ?? [],
            agentCount,
            workflowCount);
    }

    private AgentPackWorkflow ReadWorkflow(string workflowPath)
    {
        if (IsReparsePoint(workflowPath) || new FileInfo(workflowPath).Length > 128 * 1024)
        {
            throw new InvalidOperationException("Agent Pack 工作流文件无效。");
        }
        using var document = JsonDocument.Parse(File.ReadAllText(workflowPath));
        var root = document.RootElement;
        var steps = root.TryGetProperty("steps", out var stepsElement)
            ? stepsElement.EnumerateArray().Take(48).Select(step =>
                new AgentPackWorkflowStep(
                    JsonString(step, "id"),
                    JsonString(step, "agent"),
                    JsonString(step, "title"),
                    JsonStringArray(step, "outputs"),
                    JsonStringArray(step, "acceptance"))).ToArray()
            : [];
        return new AgentPackWorkflow(
            JsonString(root, "id"),
            JsonString(root, "name"),
            JsonString(root, "executionMode"),
            steps.Length,
            steps);
    }

    private static string? ReadResultContract(string root, string workflowRelativePath)
    {
        var workflowPath = ResolveContainedPath(root, workflowRelativePath);
        if (!File.Exists(workflowPath) || new FileInfo(workflowPath).Length > 128 * 1024)
        {
            return null;
        }
        using var document = JsonDocument.Parse(File.ReadAllText(workflowPath));
        return document.RootElement.TryGetProperty("resultContract", out var value)
            ? value.GetString()
            : null;
    }

    private AgentPackCertificationReport? ReadCertification(string root)
    {
        var path = Path.Combine(root, "certification.json");
        if (!File.Exists(path) || IsReparsePoint(path) || new FileInfo(path).Length > 64 * 1024)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<AgentPackCertificationReport>(
                File.ReadAllText(path),
                _json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ResolveManifestText(
        DiscoveredPack pack,
        string? relativePath,
        int maxCharacters)
        => string.IsNullOrWhiteSpace(relativePath)
            ? string.Empty
            : ReadOptionalText(pack.Root, relativePath, maxCharacters);

    private static string ReadOptionalText(string root, string relativePath, int maxCharacters)
    {
        var path = ResolveContainedPath(root, relativePath);
        if (!File.Exists(path) || IsReparsePoint(path))
        {
            return string.Empty;
        }
        var info = new FileInfo(path);
        if (info.Length > 256 * 1024)
        {
            return string.Empty;
        }
        var text = File.ReadAllText(path);
        return text.Length <= maxCharacters ? text : text[..maxCharacters];
    }

    private static void ValidateManifest(AgentPackManifest manifest, string root)
    {
        _ = RequiredManifestValue(manifest.Name, "name");
        _ = RequiredManifestValue(manifest.Version, "version");
        if (manifest.Permissions?.Any(value => value.Length > 100) == true)
        {
            throw new InvalidOperationException("Agent Pack 权限声明过长。");
        }
        if (!string.IsNullOrWhiteSpace(manifest.EntryWorkflow))
        {
            _ = ResolveContainedPath(root, manifest.EntryWorkflow);
        }
        ValidateOnboarding(manifest.Onboarding);
        ValidateCapabilityRequirements(manifest.CapabilityRequirements);
    }

    private static AgentPackCapabilityRequirements? ToCapabilityRequirements(
        AgentPackCapabilityRequirementsManifest? requirements)
        => requirements is null
            ? null
            : new AgentPackCapabilityRequirements(
                requirements.Version?.Trim() ?? "1.0",
                (requirements.Items ?? []).Take(16).Select(item =>
                    new AgentPackCapabilityRequirement(
                        item.Id?.Trim() ?? string.Empty,
                        item.Kind?.Trim().ToLowerInvariant() ?? string.Empty,
                        item.Name?.Trim() ?? string.Empty,
                        item.Reason?.Trim() ?? string.Empty,
                        item.Required,
                        item.MatchIds?.Take(12)
                            .Select(value => value.Trim())
                            .Where(value => value.Length > 0)
                            .ToArray() ?? [],
                        string.IsNullOrWhiteSpace(item.CatalogId)
                            ? null
                            : item.CatalogId.Trim())).ToArray());

    private static void ValidateCapabilityRequirements(
        AgentPackCapabilityRequirementsManifest? requirements)
    {
        if (requirements is null)
        {
            return;
        }
        if (!string.Equals(requirements.Version?.Trim(), "1.0", StringComparison.Ordinal)
            || (requirements.Items?.Count ?? 0) is < 1 or > 16)
        {
            throw new InvalidOperationException(
                "Agent Pack capabilityRequirements must use version 1.0 and contain 1-16 items.");
        }
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in requirements.Items!)
        {
            var id = item.Id?.Trim() ?? string.Empty;
            var kind = item.Kind?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!Regex.IsMatch(id, "^[a-z][a-z0-9-]{1,39}$") || !ids.Add(id))
            {
                throw new InvalidOperationException(
                    "Agent Pack capability requirement IDs must be unique kebab-case values.");
            }
            if (kind is not ("mcp" or "skill")
                || string.IsNullOrWhiteSpace(item.Name)
                || string.IsNullOrWhiteSpace(item.Reason)
                || (item.MatchIds?.Count ?? 0) is < 1 or > 12)
            {
                throw new InvalidOperationException(
                    $"Agent Pack capability requirement '{id}' is incomplete.");
            }
            if (item.MatchIds!.Any(value =>
                    string.IsNullOrWhiteSpace(value) || value.Length > 100)
                || item.CatalogId?.Length > 100)
            {
                throw new InvalidOperationException(
                    $"Agent Pack capability requirement '{id}' contains an invalid match ID.");
            }
        }
    }

    private static AgentPackOnboarding? ToOnboarding(AgentPackOnboardingManifest? onboarding)
        => onboarding is null
            ? null
            : new AgentPackOnboarding(
                onboarding.Version?.Trim() ?? "1.0",
                onboarding.Headline?.Trim() ?? "从这里开始",
                onboarding.Description?.Trim() ?? string.Empty,
                (onboarding.Steps ?? []).Take(8).Select(step =>
                    new AgentPackOnboardingStep(
                        step.Id?.Trim() ?? string.Empty,
                        step.Title?.Trim() ?? string.Empty,
                        step.Description?.Trim() ?? string.Empty,
                        step.Kind?.Trim().ToLowerInvariant() ?? "text",
                        step.Required,
                        step.Placeholder?.Trim() ?? string.Empty,
                        step.Options?.Take(12).Select(value => value.Trim()).Where(value => value.Length > 0).ToArray() ?? [],
                        step.WhyItMatters?.Trim() ?? string.Empty,
                        step.Example?.Trim() ?? string.Empty)).ToArray(),
                (onboarding.Outcomes ?? []).Take(8).Select(outcome =>
                    new AgentPackOnboardingOutcome(
                        outcome.Id?.Trim() ?? string.Empty,
                        outcome.Title?.Trim() ?? string.Empty,
                        outcome.Description?.Trim() ?? string.Empty,
                        outcome.PromptTemplate?.Trim() ?? string.Empty)).ToArray());

    private static void ValidateOnboarding(AgentPackOnboardingManifest? onboarding)
    {
        if (onboarding is null)
        {
            return;
        }
        if ((onboarding.Steps?.Count ?? 0) is < 1 or > 8
            || (onboarding.Outcomes?.Count ?? 0) is < 1 or > 8)
        {
            throw new InvalidOperationException("Agent Pack 启动引导必须包含 1-8 个输入步骤和 1-8 个结果目标。");
        }
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in onboarding.Steps!)
        {
            var id = step.Id?.Trim() ?? string.Empty;
            var kind = step.Kind?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!Regex.IsMatch(id, "^[a-z][a-z0-9-]{1,39}$") || !ids.Add(id))
            {
                throw new InvalidOperationException("Agent Pack 启动引导的步骤 ID 无效或重复。");
            }
            if (kind is not ("text" or "select" or "attachment"))
            {
                throw new InvalidOperationException($"Agent Pack 启动引导不支持输入类型 {kind}。");
            }
            if (kind == "select" && (step.Options?.Count ?? 0) == 0)
            {
                throw new InvalidOperationException($"Agent Pack 启动引导步骤 {id} 缺少可选项。");
            }
        }
        foreach (var outcome in onboarding.Outcomes!)
        {
            if (string.IsNullOrWhiteSpace(outcome.Id)
                || string.IsNullOrWhiteSpace(outcome.Title)
                || string.IsNullOrWhiteSpace(outcome.PromptTemplate))
            {
                throw new InvalidOperationException("Agent Pack 启动引导的结果目标不完整。");
            }
        }
    }

    private AgentPackState ReadState()
    {
        try
        {
            return JsonSerializer.Deserialize<AgentPackState>(
                       File.ReadAllText(_statePath),
                       _json)
                   ?? new AgentPackState();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new AgentPackState();
        }
    }

    private static IEnumerable<string> DiscoverRoots()
    {
        var configured = Environment.GetEnvironmentVariable("NOVA_AGENT_PACKS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            foreach (var value in configured.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value.Trim();
                }
            }
        }

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "agent-packs",
            "installed");
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "agent-packs"));

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 9; depth++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "industry-packs");
            if (Directory.Exists(candidate))
            {
                yield return candidate;
                break;
            }
        }
    }

    private static bool IsBuiltInRoot(string path)
        => !path.Contains(
            Path.Combine("NOVA", "agent-packs", "installed"),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Agent Pack 文件路径必须是相对路径。");
        }
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Agent Pack 文件路径超出包目录。");
        }
        return fullPath;
    }

    private static string RequiredManifestValue(string? value, string name)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Agent Pack 缺少 {name}。")
            : value.Trim();

    private static string JsonString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static string[] JsonStringArray(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Take(32)
                .Select(item => item!)
                .ToArray()
            : [];

    private static void AppendSection(StringBuilder builder, string title, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }
        builder.AppendLine($"[{title}]");
        builder.AppendLine(body.Trim());
        builder.AppendLine();
    }

    private sealed record PackRoot(string Path, bool BuiltIn);
    private sealed record DiscoveredPack(string Root, bool BuiltIn, AgentPackManifest Manifest);

    private sealed class AgentPackState
    {
        public Dictionary<string, bool> Enabled { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class AgentPackManifest
    {
        public string? Id { get; init; }
        public string? Name { get; init; }
        public string? Version { get; init; }
        public string? Status { get; init; }
        public string? Category { get; init; }
        public string? Description { get; init; }
        public string? EntryWorkflow { get; init; }
        public IReadOnlyList<string>? DeclaredCapabilities { get; init; }
        public IReadOnlyList<string>? Permissions { get; init; }
        public IReadOnlyList<string>? StarterPrompts { get; init; }
        public Dictionary<string, string>? ExternalActions { get; init; }
        public AgentPackOnboardingManifest? Onboarding { get; init; }
        public AgentPackCapabilityRequirementsManifest? CapabilityRequirements { get; init; }
    }

    private sealed class AgentPackCapabilityRequirementsManifest
    {
        public string? Version { get; init; }
        public IReadOnlyList<AgentPackCapabilityRequirementManifest>? Items { get; init; }
    }

    private sealed class AgentPackCapabilityRequirementManifest
    {
        public string? Id { get; init; }
        public string? Kind { get; init; }
        public string? Name { get; init; }
        public string? Reason { get; init; }
        public bool Required { get; init; }
        public IReadOnlyList<string>? MatchIds { get; init; }
        public string? CatalogId { get; init; }
    }

    private sealed class AgentPackOnboardingManifest
    {
        public string? Version { get; init; }
        public string? Headline { get; init; }
        public string? Description { get; init; }
        public IReadOnlyList<AgentPackOnboardingStepManifest>? Steps { get; init; }
        public IReadOnlyList<AgentPackOnboardingOutcomeManifest>? Outcomes { get; init; }
    }

    private sealed class AgentPackOnboardingStepManifest
    {
        public string? Id { get; init; }
        public string? Title { get; init; }
        public string? Description { get; init; }
        public string? Kind { get; init; }
        public bool Required { get; init; }
        public string? Placeholder { get; init; }
        public IReadOnlyList<string>? Options { get; init; }
        public string? WhyItMatters { get; init; }
        public string? Example { get; init; }
    }

    private sealed class AgentPackOnboardingOutcomeManifest
    {
        public string? Id { get; init; }
        public string? Title { get; init; }
        public string? Description { get; init; }
        public string? PromptTemplate { get; init; }
    }
}
