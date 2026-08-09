using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nova.Maimai.Connector;

internal sealed class McpServer
{
    public const string ProtocolVersion = "2025-11-25";
    private readonly ProfileService _profiles;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public McpServer(ProfileService profiles)
    {
        _profiles = profiles;
    }

    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(input);
        await using var writer = new StreamWriter(output, new System.Text.UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        while (!cancellationToken.IsCancellationRequested
               && await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject? response;
            try
            {
                var request = JsonNode.Parse(line)?.AsObject()
                              ?? throw new JsonException("JSON-RPC 请求必须是对象。");
                response = Handle(request);
            }
            catch (Exception exception)
            {
                response = Error(null, -32700, exception.Message);
            }

            if (response is not null)
            {
                await writer.WriteLineAsync(response.ToJsonString());
            }
        }
    }

    public JsonObject? Handle(JsonObject request)
    {
        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>() ?? string.Empty;
        if (id is null)
        {
            return null;
        }

        try
        {
            var parameters = request["params"] as JsonObject ?? new JsonObject();
            var result = method switch
            {
                "initialize" => Initialize(parameters),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = ToolDefinitions() },
                "tools/call" => CallTool(parameters),
                _ => throw new McpMethodException(-32601, $"未知 MCP 方法：{method}")
            };
            return Success(id, result);
        }
        catch (McpMethodException exception)
        {
            return Error(id, exception.Code, exception.Message);
        }
        catch (Exception exception)
        {
            return Error(id, -32603, exception.Message);
        }
    }

    private static JsonObject Initialize(JsonObject parameters)
    {
        var requested = parameters["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = requested == ProtocolVersion ? requested : ProtocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false }
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "nova-maimai-safe-connector",
                ["title"] = "NOVA 脉脉安全连接器",
                ["version"] = "1.0.0"
            },
            ["instructions"] = "只读取用户通过 Chrome 扩展主动保存的当前可见页面信息；不自动访问脉脉、不发送消息。"
        };
    }

    private JsonObject CallTool(JsonObject parameters)
    {
        var name = parameters["name"]?.GetValue<string>() ?? string.Empty;
        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        object value = name switch
        {
            "maimai_list_profiles" => List(arguments),
            "maimai_search_profiles" => Search(arguments),
            "maimai_get_profile" => _profiles.Get(RequiredString(arguments, "id")),
            "maimai_record_contact_status" => _profiles.RecordContactStatus(
                RequiredString(arguments, "id"),
                RequiredString(arguments, "contact_status"),
                OptionalString(arguments, "note")),
            "maimai_delete_profile" => new
            {
                id = RequiredString(arguments, "id"),
                deleted = _profiles.Delete(RequiredString(arguments, "id"))
            },
            "maimai_purge_expired" => new { purged = _profiles.PurgeExpired() },
            "maimai_stats" => _profiles.Stats(),
            _ => throw new McpMethodException(-32602, $"未知工具：{name}")
        };

        var json = JsonSerializer.Serialize(value, _json);
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = json }
            },
            ["isError"] = false
        };
    }

    private object List(JsonObject arguments)
    {
        var limit = OptionalInt(arguments, "limit", 50);
        var status = OptionalString(arguments, "contact_status");
        var items = _profiles.List(limit, status).Select(Summary).ToArray();
        return new { count = items.Length, profiles = items };
    }

    private object Search(JsonObject arguments)
    {
        var items = _profiles.Search(
            RequiredString(arguments, "query"),
            OptionalInt(arguments, "limit", 30)).Select(Summary).ToArray();
        return new { count = items.Length, profiles = items };
    }

    private static object Summary(ProfileRecord profile) => new
    {
        profile.Id,
        profile.SourceType,
        profile.DisplayName,
        profile.CurrentTitle,
        profile.CurrentCompany,
        profile.Location,
        profile.VisibleSkills,
        profile.ContactStatus,
        profile.Purpose,
        profile.Notes,
        profile.SourceUrl,
        profile.UpdatedAt,
        profile.RetentionUntil
    };

    private static JsonArray ToolDefinitions() => new(
        Tool("maimai_list_profiles", "列出用户主动保存的脉脉公开档案。", new JsonObject
        {
            ["limit"] = Integer("最多返回 200 条。", 1, 200),
            ["contact_status"] = StatusProperty()
        }),
        Tool("maimai_search_profiles", "按姓名、职位、公司、地区、技能和备注检索已保存档案。", new JsonObject
        {
            ["query"] = String("空格分隔的检索词。"),
            ["limit"] = Integer("最多返回 100 条。", 1, 100)
        }, "query"),
        Tool("maimai_get_profile", "读取单个已保存档案及其可见页面证据。", new JsonObject
        {
            ["id"] = String("档案 ID。")
        }, "id"),
        Tool("maimai_record_contact_status", "记录人工联系进度；本工具不会发送任何消息。", new JsonObject
        {
            ["id"] = String("档案 ID。"),
            ["contact_status"] = StatusProperty(),
            ["note"] = String("可选的人工跟进备注。")
        }, "id", "contact_status"),
        Tool("maimai_delete_profile", "永久删除一个本地档案。", new JsonObject
        {
            ["id"] = String("档案 ID。")
        }, "id"),
        Tool("maimai_purge_expired", "删除所有已经超过保留期限的档案。", new JsonObject()),
        Tool("maimai_stats", "查看本地档案数量、状态和最早到期时间。", new JsonObject()));

    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required)
        => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(name => JsonValue.Create(name)).ToArray()),
                ["additionalProperties"] = false
            }
        };

    private static JsonObject String(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description
    };

    private static JsonObject Integer(string description, int minimum, int maximum) => new()
    {
        ["type"] = "integer",
        ["description"] = description,
        ["minimum"] = minimum,
        ["maximum"] = maximum
    };

    private static JsonObject StatusProperty() => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray("new", "shortlisted", "contacted_manual", "replied", "archived")
    };

    private static string RequiredString(JsonObject arguments, string name)
    {
        var value = OptionalString(arguments, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"缺少参数 {name}。")
            : value;
    }

    private static string? OptionalString(JsonObject arguments, string name)
        => arguments[name]?.GetValue<string>()?.Trim();

    private static int OptionalInt(JsonObject arguments, string name, int fallback)
        => arguments[name]?.GetValue<int>() ?? fallback;

    private static JsonObject Success(JsonNode id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };

    private sealed class McpMethodException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
