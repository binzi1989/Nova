using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace NovaDesktop.Services;

public sealed record RemoteCapabilityStoreItem(
    string Id,
    string Kind,
    string Source,
    string SourceLabel,
    string Name,
    string Publisher,
    string Description,
    string TrustLabel,
    string PermissionSummary,
    string Requirements,
    string SourceUrl,
    bool Installable,
    string ActionLabel);

public sealed class RemoteCapabilityStoreService
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private const int MaximumSkillCharacters = 120_000;
    private const string McpRegistryBase = "https://registry.modelcontextprotocol.io/v0.1/servers";
    private const string SkillMdSearchBase = "https://api.skillmd.com/v1/search";

    private readonly McpRegistryService _mcpRegistry;
    private readonly SkillRegistryService _skillRegistry;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, StoreInstallTarget> _targets =
        new(StringComparer.OrdinalIgnoreCase);

    public RemoteCapabilityStoreService(
        McpRegistryService mcpRegistry,
        SkillRegistryService skillRegistry,
        HttpClient? httpClient = null)
    {
        _mcpRegistry = mcpRegistry;
        _skillRegistry = skillRegistry;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("NOVA-AgentOS", NovaProductVersion.Current));
    }

    public object GetSources()
        => new[]
        {
            new
            {
                id = "mcp-official",
                kind = "mcp",
                name = "MCP 官方 Registry",
                publisher = "Model Context Protocol",
                description = "MCP 官方社区注册表；安装时只登记连接，默认保持停用。",
                trust = "官方目录",
                endpoint = McpRegistryBase
            },
            new
            {
                id = "skillmd",
                kind = "skill",
                name = "SkillMD",
                publisher = "SkillMD",
                description = "开放的 Agent Skills 目录；NOVA 只安装经过格式与体积校验的 SKILL.md。",
                trust = "社区目录 · 安装前审阅",
                endpoint = SkillMdSearchBase
            }
        };

    public async Task<IReadOnlyList<RemoteCapabilityStoreItem>> SearchAsync(
        string kind,
        string query,
        CancellationToken cancellationToken)
    {
        kind = kind.Trim().ToLowerInvariant();
        query = query.Trim();
        var items = kind switch
        {
            "mcp" => await SearchMcpAsync(query, cancellationToken),
            "skill" => await SearchSkillsAsync(query, cancellationToken),
            _ => (await SearchMcpAsync(query, cancellationToken))
                .Concat(await SearchSkillsAsync(query, cancellationToken))
                .ToArray()
        };
        foreach (var item in items)
        {
            if (_targets.TryGetValue(item.Id, out var target))
            {
                _targets[item.Id] = target with { PublicItem = item };
            }
        }
        return items;
    }

    public async Task<object> InstallAsync(string id, CancellationToken cancellationToken)
    {
        if (!_targets.TryGetValue(id, out var target))
        {
            throw new InvalidOperationException(
                "能力条目已过期，请重新搜索后再安装。NOVA 不接受未经目录校验的安装地址。");
        }
        if (!target.PublicItem.Installable)
        {
            throw new InvalidOperationException("这个条目需要额外配置，当前只能查看来源。");
        }

        if (target.McpRegistration is not null)
        {
            await _mcpRegistry.UpsertAsync(
                target.McpRegistration with { Enabled = false },
                cancellationToken);
            return new
            {
                id,
                kind = "mcp",
                installed = true,
                enabled = false,
                message = "已登记到 MCP，保持停用；请审阅权限并补齐凭证后再启用。"
            };
        }

        if (target.RawSkillUrl is null)
        {
            throw new InvalidOperationException("能力条目没有可安装内容。");
        }
        var rawUrl = ValidateSkillUrl(target.RawSkillUrl);
        var instructions = await GetBoundedStringAsync(rawUrl, cancellationToken);
        if (instructions.Length is 0 or > MaximumSkillCharacters
            || !instructions.TrimStart().StartsWith("---", StringComparison.Ordinal)
            || !instructions.Contains("\nname:", StringComparison.OrdinalIgnoreCase)
            || !instructions.Contains("\ndescription:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("远程 Skill 不符合 NOVA 的 SKILL.md 安全格式。");
        }
        var installed = await _skillRegistry.InstallBundledAsync(
            "store-" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(target.PublicItem.SourceUrl)))[..16].ToLowerInvariant(),
            instructions,
            cancellationToken);
        return new
        {
            id,
            kind = "skill",
            installed = true,
            enabled = installed.Enabled,
            installed.Id,
            installed.Name
        };
    }

    private async Task<IReadOnlyList<RemoteCapabilityStoreItem>> SearchMcpAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var url = McpRegistryBase + "?limit=40";
        if (query.Length > 0)
        {
            url += "&search=" + Uri.EscapeDataString(query);
        }
        var root = JsonNode.Parse(await GetBoundedStringAsync(new Uri(url), cancellationToken))
                   ?? throw new InvalidOperationException("MCP Registry 返回了空响应。");
        var array = FindArray(root, "servers");
        var result = new List<RemoteCapabilityStoreItem>();
        var seenServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in array.OfType<JsonObject>().Take(40))
        {
            var server = entry["server"] as JsonObject ?? entry;
            var registryMeta =
                entry["_meta"]?["io.modelcontextprotocol.registry/official"] as JsonObject
                ?? entry["x-io.modelcontextprotocol.registry"] as JsonObject;
            var registryName = Text(server, "name", "id");
            if (registryName.Length == 0)
            {
                continue;
            }
            if (!seenServers.Add(registryName))
            {
                continue;
            }
            var displayName = Text(server, "title", "displayName");
            if (displayName.Length == 0)
            {
                displayName = registryName.Split('/').Last();
            }
            var description = Text(server, "description");
            var repository = Text(
                server["repository"] as JsonObject,
                "url");
            if (repository.Length == 0)
            {
                repository = Text(
                    registryMeta?["repository"] as JsonObject,
                    "url");
            }
            var version = Text(server, "version");
            if (version.Length == 0)
            {
                version = Text(registryMeta, "version");
            }

            var registration = CreateMcpRegistration(server, registryName, version);
            var sourceUrl = repository.Length > 0
                ? repository
                : $"https://registry.modelcontextprotocol.io/v0.1/servers/{Uri.EscapeDataString(registryName)}/versions/latest";
            var id = "registry-mcp-" + ShortHash(registryName + "|" + version);
            var item = new RemoteCapabilityStoreItem(
                id,
                "mcp",
                "mcp-official",
                "MCP 官方 Registry",
                displayName,
                PublisherFromName(registryName),
                description.Length > 0 ? description : "已发布到 MCP 官方注册表的能力服务器。",
                "官方目录 · 运行方待审阅",
                "安装只登记连接并保持停用；启用前需审阅命令、网络、账号与工作区权限。",
                registration is null ? "需要按来源文档手动配置" : RegistrationRequirements(registration),
                sourceUrl,
                registration is not null,
                registration is null ? "查看来源" : "登记到 NOVA");
            _targets[id] = new StoreInstallTarget(item, registration, null);
            result.Add(item);
            if (result.Count >= 20)
            {
                break;
            }
        }
        return result;
    }

    private async Task<IReadOnlyList<RemoteCapabilityStoreItem>> SearchSkillsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var effectiveQuery = query.Length > 0 ? query : "coding";
        var url = $"{SkillMdSearchBase}?q={Uri.EscapeDataString(effectiveQuery)}&limit=40";
        var root = JsonNode.Parse(await GetBoundedStringAsync(new Uri(url), cancellationToken))
                   ?? throw new InvalidOperationException("Skills 目录返回了空响应。");
        var array = FindArray(root, "results", "skills", "items", "data");
        var result = new List<RemoteCapabilityStoreItem>();
        foreach (var entry in array.OfType<JsonObject>().Take(40))
        {
            var rawUrl = Text(entry, "raw_url", "rawUrl", "download_url");
            var slug = Text(entry, "slug", "id", "full_name", "name");
            if (slug.Length == 0 || rawUrl.Length == 0)
            {
                continue;
            }
            var name = Text(entry, "name", "title");
            var author = Text(entry, "author", "publisher", "owner");
            var description = Text(entry, "description", "summary");
            var verified = Bool(entry, "verified", "is_verified");
            var sourceUrl = Text(entry, "url", "html_url", "source_url");
            if (sourceUrl.Length == 0)
            {
                sourceUrl = rawUrl;
            }
            var id = "skillmd-" + ShortHash(slug + "|" + rawUrl);
            var item = new RemoteCapabilityStoreItem(
                id,
                "skill",
                "skillmd",
                "SkillMD",
                name.Length > 0 ? name : slug.Split('/').Last(),
                author.Length > 0 ? author : slug.Split('/').First(),
                description.Length > 0 ? description : "符合 Agent Skills 规范的 SKILL.md。",
                verified ? "目录已验证 · NOVA 再校验" : "社区条目 · 必须审阅",
                "NOVA 仅下载 SKILL.md 文本，执行文件、二进制和符号链接不会由商店安装。",
                "Agent Skills / SKILL.md",
                sourceUrl,
                true,
                "审阅并安装");
            _targets[id] = new StoreInstallTarget(item, null, rawUrl);
            result.Add(item);
        }
        return result;
    }

    private static McpServerRegistration? CreateMcpRegistration(
        JsonObject server,
        string registryName,
        string version)
    {
        foreach (var remote in (server["remotes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var url = Text(remote, "url");
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps)
            {
                return new McpServerRegistration(
                    SafeRegistrationName(registryName),
                    string.Empty,
                    [],
                    null,
                    false,
                    new Dictionary<string, string>(),
                    "http",
                    uri.ToString(),
                    new Dictionary<string, string>());
            }
        }

        foreach (var package in (server["packages"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var registryType = Text(package, "registryType", "registry_type", "registry").ToLowerInvariant();
            var identifier = Text(package, "identifier", "name", "package");
            if (registryType == "npm" && identifier.Length > 0)
            {
                var packageSpec = version.Length > 0 && identifier.IndexOf('@', 1) < 0
                    ? $"{identifier}@{version}"
                    : identifier;
                return new McpServerRegistration(
                    SafeRegistrationName(registryName),
                    "npx",
                    ["-y", packageSpec],
                    null,
                    false,
                    new Dictionary<string, string>());
            }
        }
        return null;
    }

    private async Task<string> GetBoundedStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidOperationException("能力目录响应超过 4 MB 安全上限。");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            if (memory.Length + read > MaximumResponseBytes)
            {
                throw new InvalidOperationException("能力目录响应超过 4 MB 安全上限。");
            }
            memory.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static Uri ValidateSkillUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host is not (
                "api.skillmd.com"
                or "skillmd.com"
                or "www.skillmd.com"
                or "raw.githubusercontent.com"))
        {
            throw new InvalidOperationException("Skill 下载地址不在受信目录允许列表中。");
        }
        return uri;
    }

    private static JsonArray FindArray(JsonNode root, params string[] names)
    {
        if (root is JsonArray direct)
        {
            return direct;
        }
        if (root is not JsonObject obj)
        {
            return [];
        }
        foreach (var name in names)
        {
            if (obj[name] is JsonArray array)
            {
                return array;
            }
            if (obj[name] is JsonObject nested)
            {
                foreach (var child in names)
                {
                    if (nested[child] is JsonArray nestedArray)
                    {
                        return nestedArray;
                    }
                }
            }
        }
        return [];
    }

    private static string Text(JsonObject? value, params string[] names)
    {
        if (value is null)
        {
            return string.Empty;
        }
        foreach (var name in names)
        {
            if (value[name] is JsonValue scalar
                && scalar.TryGetValue<string>(out var text)
                && !string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }
        return string.Empty;
    }

    private static bool Bool(JsonObject value, params string[] names)
    {
        foreach (var name in names)
        {
            if (value[name] is JsonValue scalar
                && scalar.TryGetValue<bool>(out var result))
            {
                return result;
            }
        }
        return false;
    }

    private static string SafeRegistrationName(string value)
    {
        var safe = new string(value
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-'
                ? character
                : '-')
            .ToArray()).Trim('-');
        return safe.Length > 64 ? safe[..64] : safe;
    }

    private static string PublisherFromName(string value)
    {
        var owner = value.Split('/').FirstOrDefault() ?? "Community";
        return owner.StartsWith("io.github.", StringComparison.OrdinalIgnoreCase)
            ? owner["io.github.".Length..]
            : owner;
    }

    private static string RegistrationRequirements(McpServerRegistration registration)
        => registration.Transport == "http"
            ? "HTTPS · 可能需要 OAuth 或 API Key"
            : "Node.js · npm/npx · 可能需要环境变量";

    private static string ShortHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..18].ToLowerInvariant();

    private sealed record StoreInstallTarget(
        RemoteCapabilityStoreItem PublicItem,
        McpServerRegistration? McpRegistration,
        string? RawSkillUrl);
}
