using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nova.Maimai.Connector;

internal sealed class NativeHost
{
    public const int MaxMessageBytes = 1024 * 1024;
    private readonly ProfileService _profiles;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public NativeHost(ProfileService profiles)
    {
        _profiles = profiles;
    }

    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReadMessageAsync(input, cancellationToken);
            if (message is null)
            {
                break;
            }

            JsonObject response;
            try
            {
                response = Handle(message);
            }
            catch (Exception exception)
            {
                response = new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = exception.Message
                };
            }
            await WriteMessageAsync(output, response, cancellationToken);
        }
    }

    public JsonObject Handle(JsonObject message)
    {
        var action = message["action"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        return action switch
        {
            "ping" => new JsonObject
            {
                ["ok"] = true,
                ["version"] = "1.0.0",
                ["mode"] = "visible-page-user-click",
                ["encryptedAtRest"] = true
            },
            "capture" => Capture(message),
            "stats" => JsonSerializer.SerializeToNode(new { ok = true, stats = _profiles.Stats() }, _json)!.AsObject(),
            _ => throw new InvalidOperationException("不支持的本地连接器操作。")
        };
    }

    private JsonObject Capture(JsonObject message)
    {
        var payload = message["payload"]?.Deserialize<CaptureInput>(_json)
                      ?? throw new InvalidOperationException("缺少 capture payload。");
        var record = _profiles.Capture(payload);
        return JsonSerializer.SerializeToNode(new
        {
            ok = true,
            profile = new
            {
                record.Id,
                record.DisplayName,
                record.CurrentTitle,
                record.CurrentCompany,
                record.ContactStatus,
                record.UpdatedAt,
                record.RetentionUntil
            }
        }, _json)!.AsObject();
    }

    internal static async Task<JsonObject?> ReadMessageAsync(Stream input, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var first = await input.ReadAsync(header.AsMemory(0, 1), cancellationToken);
        if (first == 0)
        {
            return null;
        }
        await ReadExactlyAsync(input, header.AsMemory(1), cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageBytes)
        {
            throw new InvalidDataException("Native Messaging 消息大小无效。");
        }
        var payload = new byte[length];
        await ReadExactlyAsync(input, payload, cancellationToken);
        return JsonNode.Parse(payload)?.AsObject()
               ?? throw new JsonException("Native Messaging 消息必须是 JSON 对象。");
    }

    internal static async Task WriteMessageAsync(
        Stream output,
        JsonObject message,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > MaxMessageBytes)
        {
            throw new InvalidDataException("Native Messaging 响应过大。");
        }
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await output.WriteAsync(header, cancellationToken);
        await output.WriteAsync(payload, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static async Task ReadExactlyAsync(
        Stream input,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await input.ReadAsync(destination[offset..], cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Native Messaging 消息被截断。");
            }
            offset += read;
        }
    }
}
