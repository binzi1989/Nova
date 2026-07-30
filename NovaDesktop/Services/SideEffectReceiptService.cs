using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaDesktop.Services;

public enum SideEffectReceiptState
{
    Intent,
    Committed,
    Failed
}

public sealed record SideEffectReceipt(
    int SchemaVersion,
    string TaskId,
    string OperationId,
    string IdempotencyKey,
    string ToolName,
    string Target,
    string ArgumentsHash,
    string? ApprovalReference,
    SideEffectReceiptState State,
    string? BeforeFingerprint,
    string? AfterFingerprint,
    string? Output,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SideEffectBeginResult(
    SideEffectReceipt Receipt,
    bool IsCommittedReplay);

public sealed class SideEffectReceiptService
{
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SideEffectReceiptService(string? storageRoot = null)
    {
        _root = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "agent-os",
            "side-effects");
    }

    public async Task<SideEffectBeginResult> BeginAsync(
        string taskId,
        string operationId,
        string toolName,
        string target,
        string argumentsJson,
        string? approvalReference,
        string? beforeFingerprint,
        CancellationToken cancellationToken = default)
    {
        var normalizedTaskId = NormalizeId(taskId, "task");
        var normalizedOperationId = NormalizeId(operationId, "operation");
        var argumentsHash = ComputeHash(argumentsJson);
        var idempotencyKey = ComputeHash(
            $"{normalizedTaskId}\n{normalizedOperationId}\n{toolName}\n{argumentsHash}");
        var taskRoot = Path.Combine(_root, normalizedTaskId);
        var receiptPath = Path.Combine(taskRoot, idempotencyKey + ".json");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(taskRoot);
            if (File.Exists(receiptPath))
            {
                var existing = await LoadAsync(receiptPath, cancellationToken)
                               ?? throw new InvalidDataException(
                                   "副作用收据已存在但无法读取，NOVA 已停止自动重放。");
                if (existing.State == SideEffectReceiptState.Committed)
                {
                    return new SideEffectBeginResult(existing, true);
                }

                throw new UncertainSideEffectException(existing);
            }

            var now = DateTimeOffset.Now;
            var receipt = new SideEffectReceipt(
                1,
                normalizedTaskId,
                normalizedOperationId,
                idempotencyKey,
                toolName,
                Limit(target, 1000),
                argumentsHash,
                approvalReference,
                SideEffectReceiptState.Intent,
                beforeFingerprint,
                null,
                null,
                null,
                now,
                now);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, _jsonOptions);
            try
            {
                await using var stream = new FileStream(
                    receiptPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            catch (IOException) when (File.Exists(receiptPath))
            {
                var concurrent = await LoadAsync(receiptPath, cancellationToken)
                                 ?? throw new InvalidDataException(
                                     "并发副作用收据已创建但无法读取，NOVA 已停止自动重放。");
                if (concurrent.State == SideEffectReceiptState.Committed)
                {
                    return new SideEffectBeginResult(concurrent, true);
                }
                throw new UncertainSideEffectException(concurrent);
            }
            return new SideEffectBeginResult(receipt, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task CommitAsync(
        SideEffectReceipt receipt,
        string? afterFingerprint,
        string output,
        CancellationToken cancellationToken = default)
        => UpdateAsync(
            receipt,
            SideEffectReceiptState.Committed,
            afterFingerprint,
            Limit(output, 200_000),
            null,
            cancellationToken);

    public Task FailAsync(
        SideEffectReceipt receipt,
        string error,
        CancellationToken cancellationToken = default)
        => UpdateAsync(
            receipt,
            SideEffectReceiptState.Failed,
            receipt.AfterFingerprint,
            null,
            Limit(error, 4000),
            cancellationToken);

    public IReadOnlyList<SideEffectReceipt> LoadForTask(string taskId)
    {
        var taskRoot = Path.Combine(_root, NormalizeId(taskId, "task"));
        if (!Directory.Exists(taskRoot))
        {
            return [];
        }

        return Directory.EnumerateFiles(taskRoot, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path =>
            {
                try
                {
                    return JsonSerializer.Deserialize<SideEffectReceipt>(
                        File.ReadAllText(path),
                        _jsonOptions);
                }
                catch (Exception exception) when (exception is IOException or JsonException)
                {
                    return null;
                }
            })
            .Where(receipt => receipt is not null)
            .Cast<SideEffectReceipt>()
            .OrderBy(receipt => receipt.CreatedAt)
            .ToArray();
    }

    public static string ComputeFingerprint(string path)
    {
        if (!File.Exists(path))
        {
            return "missing";
        }
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private async Task UpdateAsync(
        SideEffectReceipt receipt,
        SideEffectReceiptState state,
        string? afterFingerprint,
        string? output,
        string? error,
        CancellationToken cancellationToken)
    {
        var taskRoot = Path.Combine(_root, NormalizeId(receipt.TaskId, "task"));
        var receiptPath = Path.Combine(taskRoot, receipt.IdempotencyKey + ".json");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadAsync(receiptPath, cancellationToken)
                          ?? throw new InvalidDataException(
                              "副作用 Intent 收据丢失，NOVA 无法安全确认动作终态。");
            if (current.State == SideEffectReceiptState.Committed)
            {
                return;
            }

            var updated = current with
            {
                State = state,
                AfterFingerprint = afterFingerprint,
                Output = output,
                Error = error,
                UpdatedAt = DateTimeOffset.Now
            };
            var temporaryPath = receiptPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(updated, _jsonOptions),
                cancellationToken);
            File.Move(temporaryPath, receiptPath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SideEffectReceipt?> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<SideEffectReceipt>(json, _jsonOptions);
    }

    private static string NormalizeId(string value, string fallback)
    {
        var normalized = new string(value
            .Where(character => char.IsLetterOrDigit(character)
                                || character is '-' or '_')
            .Take(80)
            .ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string ComputeHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string Limit(string value, int maxCharacters)
        => value.Length <= maxCharacters
            ? value
            : value[..maxCharacters] + "\n…";
}

public sealed class UncertainSideEffectException : InvalidOperationException
{
    public UncertainSideEffectException(SideEffectReceipt receipt)
        : base(
            receipt.State == SideEffectReceiptState.Intent
                ? $"动作 {receipt.ToolName} 已写入 Intent，但没有 Commit 收据。"
                  + "NOVA 已阻止自动重放，请先检查目标状态后再决定。"
                : $"动作 {receipt.ToolName} 的同一操作 ID 已失败。"
                  + "NOVA 已阻止静默重试，请创建新的显式尝试。")
    {
        Receipt = receipt;
    }

    public SideEffectReceipt Receipt { get; }
}
