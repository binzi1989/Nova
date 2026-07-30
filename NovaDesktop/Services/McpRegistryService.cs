using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaDesktop.Services;

public sealed record McpServerRegistration(
    string Name,
    string Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    bool Enabled,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    string Transport = "stdio",
    string? Url = null,
    IReadOnlyDictionary<string, string>? HttpHeaders = null);

public sealed class McpRegistryService
{
    private static readonly Regex SafeName = new(
        "^[A-Za-z0-9_.-]{1,64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex EnvironmentName = new(
        "^[A-Za-z_][A-Za-z0-9_]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HeaderName = new(
        "^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,128}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _configPath;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public McpRegistryService(string? configPath = null, HttpClient? httpClient = null)
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA");
        _configPath = configPath ?? Path.Combine(dataDirectory, "mcp-servers.json");
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    }

    public string ConfigPath => _configPath;

    public IReadOnlyList<McpServerRegistration> GetServers()
        => LoadServers();

    public IReadOnlyList<McpServerRegistration> GetEnabledServers()
        => LoadServers().Where(server => server.Enabled).ToArray();

    public async Task UpsertAsync(
        McpServerRegistration registration,
        CancellationToken cancellationToken)
    {
        registration = NormalizeAndValidate(registration);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var servers = LoadServers().ToList();
            var index = servers.FindIndex(server =>
                server.Name.Equals(registration.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                servers[index] = registration;
            }
            else
            {
                servers.Add(registration);
            }

            await SaveServersAsync(servers, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var servers = LoadServers().ToList();
            var index = servers.FindIndex(server =>
                server.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new InvalidOperationException($"MCP server '{name}' is not registered.");
            }

            servers[index] = servers[index] with { Enabled = enabled };
            await SaveServersAsync(servers, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string name, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var servers = LoadServers().ToList();
            var removed = servers.RemoveAll(server =>
                server.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                throw new InvalidOperationException($"MCP server '{name}' is not registered.");
            }

            await SaveServersAsync(servers, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public string ListServers()
    {
        var servers = LoadServers()
            .Select(server => new
            {
                server.Name,
                server.Transport,
                server.Command,
                server.Url,
                arguments = server.Arguments,
                server.WorkingDirectory,
                server.Enabled,
                environment = server.EnvironmentVariables.Keys,
                headers = server.HttpHeaders?.Keys.ToArray() ?? []
            })
            .ToArray();
        return JsonSerializer.Serialize(new
        {
            config_path = _configPath,
            count = servers.Length,
            enabled = servers.Count(server => server.Enabled),
            servers
        });
    }

    public async Task<string> InspectToolsAsync(
        string serverName,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var server = FindEnabledServer(serverName);
        await using var client = await ConnectAsync(server, workspaceRoot, cancellationToken);
        var result = await client.ListToolsAsync(cancellationToken);
        return JsonSerializer.Serialize(new
        {
            server = server.Name,
            tools = result["tools"]?.DeepClone() ?? new JsonArray(),
            next_cursor = result["nextCursor"]?.DeepClone()
        });
    }

    public async Task<string> CallToolAsync(
        string serverName,
        string toolName,
        JsonObject arguments,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            throw new InvalidOperationException("MCP tool name is required.");
        }

        var server = FindEnabledServer(serverName);
        await using var client = await ConnectAsync(server, workspaceRoot, cancellationToken);
        var result = await client.CallToolAsync(toolName, arguments, cancellationToken);
        return new JsonObject
        {
            ["server"] = server.Name,
            ["tool"] = toolName,
            ["result"] = result.DeepClone()
        }.ToJsonString();
    }

    private McpServerRegistration FindEnabledServer(string name)
        => GetEnabledServers().FirstOrDefault(
               server => server.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
           ?? throw new InvalidOperationException($"Enabled MCP server '{name}' is not registered.");

    private IReadOnlyList<McpServerRegistration> LoadServers()
    {
        EnsureConfigExists();
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_configPath))?.AsObject();
            var registrations = new List<McpServerRegistration>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in root?["servers"]?.AsArray() ?? [])
            {
                if (node is not JsonObject server)
                {
                    continue;
                }

                var name = server["name"]?.GetValue<string>()?.Trim() ?? string.Empty;
                var transport = server["transport"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "stdio";
                var command = server["command"]?.GetValue<string>()?.Trim() ?? string.Empty;
                var url = server["url"]?.GetValue<string>()?.Trim();
                if (name.Length == 0
                    || transport is not ("stdio" or "http")
                    || (transport == "stdio" && command.Length == 0)
                    || (transport == "http" && string.IsNullOrWhiteSpace(url))
                    || !seenNames.Add(name))
                {
                    continue;
                }

                var arguments = server["arguments"]?.AsArray()
                    .Select(item => item?.GetValue<string>() ?? string.Empty)
                    .ToArray() ?? [];
                var environment = ReadStringMap(server["environmentVariables"] as JsonObject);
                var headers = ReadStringMap(server["headers"] as JsonObject);

                registrations.Add(new McpServerRegistration(
                    name,
                    command,
                    arguments,
                    server["workingDirectory"]?.GetValue<string>(),
                    server["enabled"]?.GetValue<bool>() ?? false,
                    environment,
                    transport,
                    url,
                    headers));
            }
            return registrations;
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Unable to read MCP registry '{_configPath}'.", exception);
        }
    }

    private async Task<IMcpClientSession> ConnectAsync(
        McpServerRegistration server,
        string workspaceRoot,
        CancellationToken cancellationToken)
        => server.Transport == "http"
            ? await McpStreamableHttpClient.ConnectAsync(server, _httpClient, cancellationToken)
            : await McpStdioClient.ConnectAsync(server, workspaceRoot, cancellationToken);

    private async Task SaveServersAsync(
        IReadOnlyCollection<McpServerRegistration> servers,
        CancellationToken cancellationToken)
    {
        var array = new JsonArray();
        foreach (var server in servers.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var environment = new JsonObject();
            foreach (var pair in server.EnvironmentVariables)
            {
                environment[pair.Key] = pair.Value;
            }
            var headers = new JsonObject();
            foreach (var pair in server.HttpHeaders ?? new Dictionary<string, string>())
            {
                headers[pair.Key] = pair.Value;
            }

            array.Add(new JsonObject
            {
                ["name"] = server.Name,
                ["transport"] = server.Transport,
                ["command"] = server.Command,
                ["arguments"] = new JsonArray(
                    server.Arguments.Select(argument => JsonValue.Create(argument)).ToArray()),
                ["workingDirectory"] = server.WorkingDirectory,
                ["enabled"] = server.Enabled,
                ["environmentVariables"] = environment,
                ["url"] = server.Url,
                ["headers"] = headers
            });
        }

        var directory = Path.GetDirectoryName(_configPath)
                        ?? throw new InvalidOperationException("MCP registry path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                new JsonObject
                {
                    ["version"] = 1,
                    ["servers"] = array
                }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            File.Move(temporaryPath, _configPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static McpServerRegistration NormalizeAndValidate(McpServerRegistration registration)
    {
        var name = registration.Name.Trim();
        var transport = registration.Transport.Trim().ToLowerInvariant();
        var command = registration.Command.Trim();
        var url = registration.Url?.Trim();
        if (!SafeName.IsMatch(name))
        {
            throw new InvalidOperationException("Server name must use 1-64 letters, numbers, dots, dashes, or underscores.");
        }
        if (transport is not ("stdio" or "http"))
        {
            throw new InvalidOperationException("Transport must be stdio or http.");
        }
        if (transport == "stdio")
        {
            if (command.Length == 0 || command.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            {
                throw new InvalidOperationException("Stdio command must be a simple executable name available on PATH.");
            }
        }
        else
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint)
                || (!endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    && !(endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                         && endpoint.IsLoopback))
                || !string.IsNullOrEmpty(endpoint.UserInfo))
            {
                throw new InvalidOperationException("HTTP MCP URL must use HTTPS, or HTTP on a loopback address, with no embedded credentials.");
            }
        }

        foreach (var argument in registration.Arguments)
        {
            if (argument.IndexOfAny(['\0', '\r', '\n']) >= 0)
            {
                throw new InvalidOperationException("MCP arguments cannot contain control characters.");
            }
        }
        ValidateMappings(registration.EnvironmentVariables, EnvironmentName, "environment");
        ValidateMappings(registration.HttpHeaders ?? new Dictionary<string, string>(), HeaderName, "header");

        return registration with
        {
            Name = name,
            Transport = transport,
            Command = transport == "stdio" ? command : string.Empty,
            Url = transport == "http" ? url : null,
            WorkingDirectory = string.IsNullOrWhiteSpace(registration.WorkingDirectory)
                ? null
                : Path.GetFullPath(registration.WorkingDirectory.Trim()),
            Arguments = registration.Arguments.Select(argument => argument.Trim()).ToArray(),
            EnvironmentVariables = new Dictionary<string, string>(
                registration.EnvironmentVariables,
                StringComparer.OrdinalIgnoreCase),
            HttpHeaders = new Dictionary<string, string>(
                registration.HttpHeaders ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private static void ValidateMappings(
        IReadOnlyDictionary<string, string> mappings,
        Regex targetPattern,
        string kind)
    {
        foreach (var pair in mappings)
        {
            if (!targetPattern.IsMatch(pair.Key) || !EnvironmentName.IsMatch(pair.Value))
            {
                throw new InvalidOperationException(
                    $"Invalid {kind} mapping '{pair.Key}'. Values must name environment variables, never literal secrets.");
            }
        }
    }

    private static Dictionary<string, string> ReadStringMap(JsonObject? source)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return result;
        }
        foreach (var pair in source)
        {
            var value = pair.Value?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(value))
            {
                result[pair.Key] = value;
            }
        }
        return result;
    }

    private void EnsureConfigExists()
    {
        var directory = Path.GetDirectoryName(_configPath)
                        ?? throw new InvalidOperationException("MCP registry path has no parent directory.");
        Directory.CreateDirectory(directory);
        if (File.Exists(_configPath))
        {
            return;
        }

        File.WriteAllText(
            _configPath,
            new JsonObject
            {
                ["version"] = 1,
                ["servers"] = new JsonArray()
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
