using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaDesktop.Services;

public sealed record McpDiscoverySource(
    string Product,
    string Path,
    string Format);

public sealed record McpDiscoveryCandidate(
    string Id,
    string Name,
    string SourceProduct,
    string SourcePath,
    McpServerRegistration Registration,
    bool IsCompatible,
    bool IsAlreadyRegistered,
    bool MayAcquireSoftware,
    int OmittedSecretCount,
    string RiskLabel,
    string Summary,
    string Notes)
{
    public bool CanImport => IsCompatible && !IsAlreadyRegistered;
}

public sealed record McpDiscoveryResult(
    IReadOnlyList<McpDiscoveryCandidate> Candidates,
    IReadOnlyList<string> ScannedPaths,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Read-only importer for MCP configurations created by other local clients.
/// Discovery never starts a process, performs network access, or copies literal secrets.
/// </summary>
public sealed class McpDiscoveryService
{
    private static readonly Regex SafeName = new(
        "^[A-Za-z0-9_.-]{1,64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex EnvironmentName = new(
        "^[A-Za-z_][A-Za-z0-9_]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TomlSection = new(
        @"^\s*\[(?<section>[^\]]+)\]\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TomlAssignment = new(
        @"^\s*(?<key>[A-Za-z0-9_.-]+)\s*=\s*(?<value>.+?)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _workspaceRoot;

    public McpDiscoveryService(string workspaceRoot)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public IReadOnlyList<McpDiscoverySource> GetDefaultSources()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return
        [
            new("当前工作区", Path.Combine(_workspaceRoot, ".vscode", "mcp.json"), "json"),
            new("当前工作区", Path.Combine(_workspaceRoot, ".mcp.json"), "json"),
            new("Claude Desktop", Path.Combine(appData, "Claude", "claude_desktop_config.json"), "json"),
            new("Claude Code", Path.Combine(profile, ".claude.json"), "json"),
            new("Cursor", Path.Combine(profile, ".cursor", "mcp.json"), "json"),
            new("Windsurf", Path.Combine(profile, ".codeium", "windsurf", "mcp_config.json"), "json"),
            new("Codex", Path.Combine(profile, ".codex", "config.toml"), "toml")
        ];
    }

    public IReadOnlyList<McpDiscoverySource> GetAvailableDefaultSources()
        => GetDefaultSources()
            .Where(source => File.Exists(source.Path))
            .GroupBy(source => source.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

    public async Task<McpDiscoveryResult> DiscoverAsync(
        IEnumerable<McpDiscoverySource> sources,
        IReadOnlyCollection<McpServerRegistration> registeredServers,
        CancellationToken cancellationToken)
    {
        var candidates = new List<McpDiscoveryCandidate>();
        var scannedPaths = new List<string>();
        var warnings = new List<string>();
        var existingNames = registeredServers
            .Select(server => server.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources
                     .GroupBy(item => Path.GetFullPath(item.Path), StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(source.Path))
            {
                continue;
            }

            scannedPaths.Add(source.Path);
            try
            {
                var text = await File.ReadAllTextAsync(source.Path, cancellationToken);
                var discovered = source.Format.Equals("toml", StringComparison.OrdinalIgnoreCase)
                    ? ParseCodexToml(source, text, existingNames)
                    : ParseJson(source, text, existingNames);
                candidates.AddRange(discovered);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or JsonException
                    or InvalidOperationException)
            {
                warnings.Add($"{source.Product}：无法读取 {source.Path}（{exception.Message}）");
            }
        }

        return new McpDiscoveryResult(
            candidates
                .GroupBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(candidate => candidate.CanImport)
                .ThenBy(candidate => candidate.SourceProduct, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            scannedPaths,
            warnings);
    }

    private IReadOnlyList<McpDiscoveryCandidate> ParseJson(
        McpDiscoverySource source,
        string text,
        IReadOnlySet<string> existingNames)
    {
        var root = JsonNode.Parse(text)?.AsObject()
                   ?? throw new JsonException("配置根节点不是 JSON 对象。");
        var serverNode = root["mcpServers"] as JsonObject
                         ?? root["servers"] as JsonObject
                         ?? root["mcp"]?["servers"] as JsonObject;
        if (serverNode is null)
        {
            return [];
        }

        var result = new List<McpDiscoveryCandidate>();
        foreach (var pair in serverNode)
        {
            if (pair.Value is not JsonObject definition)
            {
                continue;
            }

            var command = ReadString(definition["command"]);
            var url = ReadString(definition["url"])
                      ?? ReadString(definition["serverUrl"]);
            var transport = !string.IsNullOrWhiteSpace(url)
                || ReadString(definition["type"])?.Contains("http", StringComparison.OrdinalIgnoreCase) == true
                    ? "http"
                    : "stdio";
            var arguments = ReadStringArray(definition["args"] ?? definition["arguments"]);
            var environment = SanitizeMappings(definition["env"] as JsonObject
                                               ?? definition["environment"] as JsonObject
                                               ?? definition["environmentVariables"] as JsonObject);
            var headers = SanitizeMappings(definition["headers"] as JsonObject);
            result.Add(BuildCandidate(
                source,
                pair.Key,
                command,
                arguments,
                transport,
                url,
                environment.Mappings,
                headers.Mappings,
                environment.Omitted + headers.Omitted,
                existingNames));
        }
        return result;
    }

    private IReadOnlyList<McpDiscoveryCandidate> ParseCodexToml(
        McpDiscoverySource source,
        string text,
        IReadOnlySet<string> existingNames)
    {
        var definitions = new Dictionary<string, TomlServerBuilder>(StringComparer.OrdinalIgnoreCase);
        string? currentName = null;
        var inEnvironment = false;
        var inHeaders = false;

        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = StripTomlComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }
            var sectionMatch = TomlSection.Match(line);
            if (sectionMatch.Success)
            {
                var section = sectionMatch.Groups["section"].Value.Trim();
                if (!TryReadCodexServerSection(section, out currentName, out inEnvironment, out inHeaders))
                {
                    currentName = null;
                }
                else if (!definitions.ContainsKey(currentName!))
                {
                    definitions[currentName!] = new TomlServerBuilder();
                }
                continue;
            }
            if (currentName is null)
            {
                continue;
            }
            var assignment = TomlAssignment.Match(line);
            if (!assignment.Success)
            {
                continue;
            }

            var key = assignment.Groups["key"].Value;
            var value = assignment.Groups["value"].Value;
            var builder = definitions[currentName];
            if (inEnvironment)
            {
                builder.Environment[key] = ParseTomlString(value) ?? value;
            }
            else if (inHeaders)
            {
                builder.Headers[key] = ParseTomlString(value) ?? value;
            }
            else
            {
                switch (key.ToLowerInvariant())
                {
                    case "command":
                        builder.Command = ParseTomlString(value);
                        break;
                    case "url":
                        builder.Url = ParseTomlString(value);
                        break;
                    case "args":
                    case "arguments":
                        builder.Arguments = ParseTomlArray(value);
                        break;
                    case "env":
                        foreach (var mapping in ParseTomlInlineTable(value))
                        {
                            builder.Environment[mapping.Key] = mapping.Value;
                        }
                        break;
                    case "http_headers":
                    case "headers":
                        foreach (var mapping in ParseTomlInlineTable(value))
                        {
                            builder.Headers[mapping.Key] = mapping.Value;
                        }
                        break;
                }
            }
        }

        return definitions.Select(pair =>
        {
            var environment = SanitizeMappings(pair.Value.Environment);
            var headers = SanitizeMappings(pair.Value.Headers);
            return BuildCandidate(
                source,
                pair.Key,
                pair.Value.Command,
                pair.Value.Arguments,
                string.IsNullOrWhiteSpace(pair.Value.Url) ? "stdio" : "http",
                pair.Value.Url,
                environment.Mappings,
                headers.Mappings,
                environment.Omitted + headers.Omitted,
                existingNames);
        }).ToArray();
    }

    private McpDiscoveryCandidate BuildCandidate(
        McpDiscoverySource source,
        string name,
        string? command,
        IReadOnlyList<string> arguments,
        string transport,
        string? url,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyDictionary<string, string> headers,
        int omittedSecretCount,
        IReadOnlySet<string> existingNames)
    {
        name = name.Trim();
        command = command?.Trim() ?? string.Empty;
        url = url?.Trim();
        var notes = new List<string>();
        var compatible = SafeName.IsMatch(name);
        if (!compatible)
        {
            notes.Add("名称不符合 NOVA 的安全命名规则");
        }

        if (transport == "stdio")
        {
            if (command.Length == 0)
            {
                compatible = false;
                notes.Add("缺少启动命令");
            }
            else if (command.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            {
                compatible = false;
                notes.Add("启动命令包含路径，需要手动审查");
            }
        }
        else if (!IsSafeHttpEndpoint(url))
        {
            compatible = false;
            notes.Add("HTTP 地址必须为 HTTPS 或本机回环地址");
        }

        if (arguments.Any(argument => argument.IndexOfAny(['\0', '\r', '\n']) >= 0))
        {
            compatible = false;
            notes.Add("参数包含控制字符");
        }
        if (omittedSecretCount > 0)
        {
            notes.Add($"已忽略 {omittedSecretCount} 个明文或无法识别的密钥值");
        }

        var mayAcquireSoftware = transport == "stdio"
                                 && (command.Equals("npx", StringComparison.OrdinalIgnoreCase)
                                     || command.Equals("uvx", StringComparison.OrdinalIgnoreCase)
                                     || command.Equals("docker", StringComparison.OrdinalIgnoreCase)
                                     || command.Equals("docker.exe", StringComparison.OrdinalIgnoreCase));
        if (mayAcquireSoftware)
        {
            notes.Add("以后测试或启用时可能下载或启动外部软件");
        }

        var alreadyRegistered = existingNames.Contains(name);
        if (alreadyRegistered)
        {
            notes.Add("同名绑定已存在");
        }
        if (notes.Count == 0)
        {
            notes.Add("未发现高风险配置；导入后仍保持停用");
        }

        var registration = new McpServerRegistration(
            name,
            command,
            arguments,
            _workspaceRoot,
            false,
            environment,
            transport,
            url,
            headers);
        var risk = !compatible
            ? "需手动处理"
            : mayAcquireSoftware || omittedSecretCount > 0
                ? "需复核"
                : "低风险";
        var summary = transport == "http"
            ? url ?? "未提供 URL"
            : $"{command} {string.Join(" ", arguments.Take(3))}".Trim();
        return new McpDiscoveryCandidate(
            $"{source.Product}|{source.Path}|{name}",
            name,
            source.Product,
            source.Path,
            registration,
            compatible,
            alreadyRegistered,
            mayAcquireSoftware,
            omittedSecretCount,
            risk,
            summary,
            string.Join("；", notes));
    }

    private static bool TryReadCodexServerSection(
        string section,
        out string? name,
        out bool inEnvironment,
        out bool inHeaders)
    {
        name = null;
        inEnvironment = false;
        inHeaders = false;
        var prefix = section.StartsWith("mcp_servers.", StringComparison.OrdinalIgnoreCase)
            ? "mcp_servers."
            : section.StartsWith("mcp.servers.", StringComparison.OrdinalIgnoreCase)
                ? "mcp.servers."
                : null;
        if (prefix is null)
        {
            return false;
        }

        var remainder = section[prefix.Length..].Trim();
        var suffix = remainder.EndsWith(".env", StringComparison.OrdinalIgnoreCase)
            ? ".env"
            : remainder.EndsWith(".environment", StringComparison.OrdinalIgnoreCase)
                ? ".environment"
                : remainder.EndsWith(".http_headers", StringComparison.OrdinalIgnoreCase)
                    ? ".http_headers"
                    : remainder.EndsWith(".headers", StringComparison.OrdinalIgnoreCase)
                        ? ".headers"
                        : null;
        if (suffix is not null)
        {
            remainder = remainder[..^suffix.Length];
            inEnvironment = suffix is ".env" or ".environment";
            inHeaders = !inEnvironment;
        }
        name = remainder.Trim().Trim('"', '\'');
        return name.Length > 0;
    }

    private static (Dictionary<string, string> Mappings, int Omitted) SanitizeMappings(
        JsonObject? source)
    {
        var values = source?.ToDictionary(
                         pair => pair.Key,
                         pair => ReadString(pair.Value) ?? string.Empty,
                         StringComparer.OrdinalIgnoreCase)
                     ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return SanitizeMappings(values);
    }

    private static (Dictionary<string, string> Mappings, int Omitted) SanitizeMappings(
        IReadOnlyDictionary<string, string> source)
    {
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var omitted = 0;
        foreach (var pair in source)
        {
            var environmentName = ExtractEnvironmentName(pair.Value);
            if (environmentName is null)
            {
                omitted++;
                continue;
            }
            mappings[pair.Key] = environmentName;
        }
        return (mappings, omitted);
    }

    private static string? ExtractEnvironmentName(string value)
    {
        value = value.Trim();
        string candidate;
        if (value.StartsWith("${env:", StringComparison.OrdinalIgnoreCase) && value.EndsWith('}'))
        {
            candidate = value[6..^1];
        }
        else if (value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}'))
        {
            candidate = value[2..^1];
        }
        else if (value.StartsWith('%') && value.EndsWith('%') && value.Length > 2)
        {
            candidate = value[1..^1];
        }
        else if (value.StartsWith('$') && value.Length > 1)
        {
            candidate = value[1..];
        }
        else
        {
            return null;
        }
        return EnvironmentName.IsMatch(candidate) ? candidate : null;
    }

    private static bool IsSafeHttpEndpoint(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
           && string.IsNullOrEmpty(endpoint.UserInfo)
           && (endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
               || endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               && endpoint.IsLoopback);

    private static string? ReadString(JsonNode? node)
    {
        try
        {
            return node?.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string[] ReadStringArray(JsonNode? node)
        => node is JsonArray array
            ? array.Select(ReadString).Where(value => value is not null).Cast<string>().ToArray()
            : [];

    private static string StripTomlComment(string line)
    {
        var inString = false;
        var quote = '\0';
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if ((character is '"' or '\'') && (index == 0 || line[index - 1] != '\\'))
            {
                if (!inString)
                {
                    inString = true;
                    quote = character;
                }
                else if (quote == character)
                {
                    inString = false;
                }
            }
            else if (character == '#' && !inString)
            {
                return line[..index];
            }
        }
        return line;
    }

    private static string? ParseTomlString(string value)
    {
        value = value.Trim();
        if (value.Length < 2 || value[0] != value[^1] || value[0] is not ('"' or '\''))
        {
            return null;
        }
        if (value[0] == '\'')
        {
            return value[1..^1];
        }
        try
        {
            return JsonSerializer.Deserialize<string>(value);
        }
        catch (JsonException)
        {
            return value[1..^1];
        }
    }

    private static string[] ParseTomlArray(string value)
    {
        value = value.Trim();
        if (!value.StartsWith('[') || !value.EndsWith(']'))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<string[]>(value) ?? [];
        }
        catch (JsonException)
        {
            return value[1..^1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => ParseTomlString(item))
                .Where(item => item is not null)
                .Cast<string>()
                .ToArray();
        }
    }

    private static Dictionary<string, string> ParseTomlInlineTable(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        value = value.Trim();
        if (!value.StartsWith('{') || !value.EndsWith('}'))
        {
            return result;
        }
        foreach (var entry in value[1..^1]
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            var key = entry[..separator].Trim().Trim('"', '\'');
            var parsedValue = ParseTomlString(entry[(separator + 1)..]) ?? string.Empty;
            if (key.Length > 0)
            {
                result[key] = parsedValue;
            }
        }
        return result;
    }

    private sealed class TomlServerBuilder
    {
        public string? Command { get; set; }
        public string? Url { get; set; }
        public IReadOnlyList<string> Arguments { get; set; } = [];
        public Dictionary<string, string> Environment { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
