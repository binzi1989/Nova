using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace NovaDesktop.Services;

public sealed class McpStreamableHttpClient : IMcpClientSession
{
    private const string ProtocolVersion = "2025-11-25";
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private string? _sessionId;
    private int _nextRequestId;

    private McpStreamableHttpClient(
        HttpClient httpClient,
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers)
    {
        _httpClient = httpClient;
        _endpoint = endpoint;
        _headers = headers;
    }

    public static async Task<McpStreamableHttpClient> ConnectAsync(
        McpServerRegistration server,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var endpoint = ValidateEndpoint(server.Url);
        var headers = ResolveHeaders(server.HttpHeaders);
        var client = new McpStreamableHttpClient(httpClient, endpoint, headers);
        await client.InitializeAsync(cancellationToken);
        return client;
    }

    public Task<JsonObject> ListToolsAsync(CancellationToken cancellationToken)
        => RequestAsync("tools/list", new JsonObject(), cancellationToken);

    public Task<JsonObject> CallToolAsync(
        string toolName,
        JsonObject arguments,
        CancellationToken cancellationToken)
        => RequestAsync(
            "tools/call",
            new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = arguments.DeepClone()
            },
            cancellationToken);

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RequestAsync(
            "initialize",
            new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "nova-desktop",
                    ["title"] = "NOVA Desktop",
                    ["version"] = NovaProductVersion.Current
                }
            },
            cancellationToken,
            includeProtocolHeader: false);
        await SendNotificationAsync(
            "notifications/initialized",
            new JsonObject(),
            cancellationToken);
    }

    private async Task<JsonObject> RequestAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken,
        bool includeProtocolHeader = true)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        };
        var responseMessage = await SendMessageAsync(
            message,
            id,
            includeProtocolHeader,
            cancellationToken);
        if (responseMessage?["error"] is JsonObject error)
        {
            throw new InvalidOperationException(
                $"MCP {method} failed: {error["message"]?.GetValue<string>() ?? "Unknown error"}");
        }
        return responseMessage?["result"]?.AsObject()
               ?? throw new InvalidOperationException($"MCP {method} returned no result.");
    }

    private async Task SendNotificationAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters
        };
        await SendMessageAsync(message, null, includeProtocolHeader: true, cancellationToken);
    }

    private async Task<JsonObject?> SendMessageAsync(
        JsonObject message,
        int? expectedId,
        bool includeProtocolHeader,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (includeProtocolHeader)
        {
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
        }
        if (!string.IsNullOrWhiteSpace(_sessionId))
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        }
        foreach (var (name, value) in _headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        request.Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues))
        {
            _sessionId = sessionValues.FirstOrDefault();
        }
        if (expectedId is null && response.StatusCode == HttpStatusCode.Accepted)
        {
            return null;
        }
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"MCP HTTP {(int)response.StatusCode}: {body}");
        }
        if (expectedId is null || string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in body.Split('\n', StringSplitOptions.TrimEntries))
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }
                var payload = line[5..].Trim();
                if (payload.Length == 0)
                {
                    continue;
                }
                var candidate = JsonNode.Parse(payload)?.AsObject();
                if (ReadId(candidate) == expectedId)
                {
                    return candidate;
                }
            }
            throw new InvalidOperationException("MCP HTTP SSE response did not contain the expected JSON-RPC result.");
        }

        var json = JsonNode.Parse(body)?.AsObject()
                   ?? throw new InvalidOperationException("MCP HTTP returned invalid JSON.");
        if (ReadId(json) != expectedId)
        {
            throw new InvalidOperationException("MCP HTTP returned a mismatched JSON-RPC response ID.");
        }
        return json;
    }

    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;

    private static int? ReadId(JsonObject? message)
    {
        if (message?["id"] is not JsonValue id)
        {
            return null;
        }
        return id.TryGetValue<int>(out var value) ? value : null;
    }

    private static Uri ValidateEndpoint(string? rawUrl)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || (uri.Scheme != Uri.UriSchemeHttps
                && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new InvalidOperationException(
                "Streamable HTTP MCP endpoints must use HTTPS, except loopback addresses may use HTTP.");
        }
        return uri;
    }

    private static IReadOnlyDictionary<string, string> ResolveHeaders(
        IReadOnlyDictionary<string, string>? mappings)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, sourceVariable) in mappings ?? new Dictionary<string, string>())
        {
            if (name.Length is 0 or > 128
                || name.Any(character => char.IsControl(character) || character is ':' or ' ')
                || sourceVariable.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            {
                throw new InvalidOperationException("Invalid MCP HTTP header environment mapping.");
            }
            var value = Environment.GetEnvironmentVariable(sourceVariable);
            if (!string.IsNullOrWhiteSpace(value)
                && !value.Contains('\r')
                && !value.Contains('\n'))
            {
                headers[name] = value;
            }
        }
        return headers;
    }
}
