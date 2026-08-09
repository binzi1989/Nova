using System.Text.Json;
using System.Text.Json.Nodes;

namespace NovaDesktop.Services;

/// <summary>
/// Durable, renderer-independent delivery records. The model may describe a
/// result, but AgentOS owns the artifact manifest and review history.
/// </summary>
public sealed class DeliveryContractService
{
    private readonly string _root;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public DeliveryContractService(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "deliveries");
    }

    public JsonObject? Get(string taskId)
    {
        var path = GetPath(taskId);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    public async Task<JsonObject> SaveAsync(
        string taskId,
        JsonObject delivery,
        CancellationToken cancellationToken = default)
    {
        var previous = Get(taskId);
        var stored = delivery.DeepClone().AsObject();
        stored["schemaVersion"] = stored["schemaVersion"]?.GetValue<string>() ?? "1.0";
        stored["taskId"] = taskId;
        stored["deliveryId"] = stored["deliveryId"]?.GetValue<string>()
            ?? $"delivery-{Guid.NewGuid():N}";
        stored["revision"] = previous is null
            ? 1
            : (previous["revision"]?.GetValue<int>() ?? 1) + 1;
        if (previous is not null)
        {
            stored["parentDeliveryId"] = previous["deliveryId"]?.GetValue<string>();
            var history = previous["history"]?.DeepClone() as JsonArray ?? new JsonArray();
            var historical = previous.DeepClone().AsObject();
            historical.Remove("history");
            history.Add(historical);
            while (history.Count > 20) history.RemoveAt(0);
            stored["history"] = history;
        }
        stored["createdAt"] = stored["createdAt"]?.GetValue<string>()
            ?? DateTimeOffset.UtcNow.ToString("O");
        stored["reviewState"] = stored["reviewState"]?.GetValue<string>() ?? "unreviewed";
        stored["feedback"] ??= new JsonArray();
        await WriteAsync(taskId, stored, cancellationToken);
        return stored;
    }

    public async Task<JsonObject> AddFeedbackAsync(
        string taskId,
        string scope,
        string category,
        string note,
        string? artifactId,
        bool calibrateAgent,
        CancellationToken cancellationToken = default)
    {
        var stored = Get(taskId)
            ?? throw new InvalidOperationException("该任务还没有可审查的交付记录。");
        var feedback = stored["feedback"] as JsonArray ?? new JsonArray();
        feedback.Add(new JsonObject
        {
            ["id"] = $"feedback-{Guid.NewGuid():N}",
            ["scope"] = scope,
            ["category"] = category,
            ["note"] = note,
            ["artifactId"] = artifactId,
            ["calibrateAgent"] = calibrateAgent,
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O")
        });
        stored["feedback"] = feedback;
        stored["reviewState"] = "changes-requested";
        stored["updatedAt"] = DateTimeOffset.UtcNow.ToString("O");
        await WriteAsync(taskId, stored, cancellationToken);
        return stored;
    }

    public async Task<JsonObject> AcceptAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        var stored = Get(taskId)
            ?? throw new InvalidOperationException("该任务还没有可审查的交付记录。");
        stored["reviewState"] = "accepted";
        stored["updatedAt"] = DateTimeOffset.UtcNow.ToString("O");
        await WriteAsync(taskId, stored, cancellationToken);
        return stored;
    }

    private async Task WriteAsync(
        string taskId,
        JsonObject value,
        CancellationToken cancellationToken)
    {
        var path = GetPath(taskId);
        var temporaryPath = path + ".tmp";
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_root);
            await File.WriteAllTextAsync(
                temporaryPath,
                value.ToJsonString(_options),
                cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private string GetPath(string taskId)
    {
        var safeName = string.Concat(taskId.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
        if (safeName.Length == 0)
            throw new InvalidOperationException("Task ID cannot be converted to a safe delivery name.");
        return Path.Combine(_root, safeName + ".json");
    }
}
