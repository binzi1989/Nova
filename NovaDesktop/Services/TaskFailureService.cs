using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public static class TaskFailureClassifier
{
    public static TaskFailureRecord Classify(
        string taskId,
        Exception exception,
        string? stage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(exception);
        var normalizedStage = string.IsNullOrWhiteSpace(stage)
            ? "unknown"
            : stage.Trim();
        var message = TaskFailureLedgerService.Redact(exception.Message);

        if (exception is AgentBudgetExceededException budget)
        {
            return Create(
                taskId,
                TaskFailureKind.Budget,
                $"BUDGET_{NormalizeCode(budget.Resource)}",
                "任务预算停在安全点",
                message,
                FailureRecoveryAction.Resume,
                "继续任务并建立新一轮弹性预算",
                retryable: true,
                blocksReplay: false,
                normalizedStage,
                exception);
        }
        if (exception is UncertainSideEffectException)
        {
            return Create(
                taskId,
                TaskFailureKind.SideEffectUncertain,
                "SIDE_EFFECT_UNCERTAIN",
                "动作结果需要你确认",
                message,
                FailureRecoveryAction.ReviewSideEffect,
                "先检查目标状态，再显式决定是否重试",
                retryable: false,
                blocksReplay: true,
                normalizedStage,
                exception);
        }
        if (exception is AgentLeaseConflictException)
        {
            return Create(
                taskId,
                TaskFailureKind.HostInterruption,
                "LEASE_CONFLICT",
                "任务正在另一处执行",
                message,
                FailureRecoveryAction.Resume,
                "等待原宿主释放任务后继续",
                retryable: true,
                blocksReplay: true,
                normalizedStage,
                exception);
        }
        if (exception is UnauthorizedAccessException)
        {
            return Create(
                taskId,
                TaskFailureKind.Permission,
                "WORKSPACE_ACCESS_DENIED",
                "工作区权限不足",
                message,
                FailureRecoveryAction.ReviewPermission,
                "检查文件权限或重新选择可写工作区",
                retryable: true,
                blocksReplay: false,
                normalizedStage,
                exception);
        }
        if (exception is HttpRequestException http
            && http.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden)
        {
            return Create(
                taskId,
                TaskFailureKind.Model,
                "MODEL_AUTH_REJECTED",
                "模型凭据未通过验证",
                message,
                FailureRecoveryAction.ReconnectModel,
                "重新连接模型并检查密钥或账户权限",
                retryable: true,
                blocksReplay: false,
                normalizedStage,
                exception);
        }
        if (exception is HttpRequestException)
        {
            return Create(
                taskId,
                TaskFailureKind.Network,
                "NETWORK_UNAVAILABLE",
                "模型网络连接中断",
                message,
                FailureRecoveryAction.Retry,
                "网络恢复后从当前对话继续",
                retryable: true,
                blocksReplay: false,
                normalizedStage,
                exception);
        }
        if (exception is TaskCanceledException or TimeoutException)
        {
            return Create(
                taskId,
                TaskFailureKind.Network,
                "REQUEST_TIMEOUT",
                "外部请求响应超时",
                message,
                FailureRecoveryAction.Retry,
                "保留现有成果并重试未完成步骤",
                retryable: true,
                blocksReplay: false,
                normalizedStage,
                exception);
        }
        if (exception is JsonException)
        {
            return Create(
                taskId,
                TaskFailureKind.Model,
                "MODEL_RESPONSE_INVALID",
                "模型返回格式异常",
                message,
                FailureRecoveryAction.Retry,
                "携带原上下文重新请求，不重复已提交动作",
                retryable: true,
                blocksReplay: false,
                normalizedStage,
                exception);
        }

        var searchable = $"{normalizedStage} {message}";
        if (ContainsAny(searchable, "verify", "verification", "test", "proof", "council", "验证", "测试", "证明"))
        {
            return Create(
                taskId,
                TaskFailureKind.Verification,
                "VERIFICATION_FAILED",
                "结果验证未通过",
                message,
                FailureRecoveryAction.Reverify,
                "查看失败证据并只修复未通过项",
                retryable: true,
                blocksReplay: false,
                normalizedStage,
                exception);
        }
        if (ContainsAny(searchable, "build", "compile", "restore", "构建", "编译"))
        {
            return Create(
                taskId,
                TaskFailureKind.Build,
                "BUILD_FAILED",
                "工程构建未通过",
                message,
                FailureRecoveryAction.FixBuild,
                "查看构建输出并定向修复失败项",
                retryable: true,
                blocksReplay: false,
                normalizedStage,
                exception);
        }
        if (ContainsAny(searchable, "permission", "approval", "denied", "unauthorized", "权限", "授权", "拒绝"))
        {
            return Create(
                taskId,
                TaskFailureKind.Permission,
                "ACTION_NOT_AUTHORIZED",
                "当前动作没有获得授权",
                message,
                FailureRecoveryAction.ReviewPermission,
                "确认权限范围后继续，已完成步骤不会重放",
                retryable: true,
                blocksReplay: false,
                normalizedStage,
                exception);
        }
        if (exception is DirectoryNotFoundException or FileNotFoundException)
        {
            return Create(
                taskId,
                TaskFailureKind.Configuration,
                "WORKSPACE_NOT_FOUND",
                "任务依赖的路径不存在",
                message,
                FailureRecoveryAction.RestoreWorkspace,
                "恢复原工作区或重新选择任务根目录",
                retryable: true,
                blocksReplay: true,
                normalizedStage,
                exception);
        }
        if (exception is IOException
            || ContainsAny(searchable, "tool", "command", "mcp", "workspace", "extension", "工具", "命令", "工作区"))
        {
            return Create(
                taskId,
                TaskFailureKind.Tool,
                "TOOL_EXECUTION_FAILED",
                "本地工具执行中断",
                message,
                FailureRecoveryAction.Retry,
                "检查工具输出和工作区状态后继续",
                retryable: true,
                blocksReplay: false,
                normalizedStage,
                exception);
        }
        if (ContainsAny(searchable, "model", "provider", "api", "response", "模型"))
        {
            return Create(
                taskId,
                TaskFailureKind.Model,
                "MODEL_RUNTIME_FAILED",
                "模型执行链中断",
                message,
                FailureRecoveryAction.ReconnectModel,
                "检查模型状态后从当前上下文继续",
                retryable: true,
                blocksReplay: false,
                normalizedStage,
                exception);
        }
        return Create(
            taskId,
            TaskFailureKind.Unknown,
            "UNCLASSIFIED_FAILURE",
            "执行链异常中断",
            message,
            FailureRecoveryAction.InspectDiagnostics,
            "查看诊断信息后从安全点继续",
            retryable: true,
            blocksReplay: false,
            normalizedStage,
            exception);
    }

    public static TaskFailureRecord CreateHostInterruption(
        string taskId,
        string detail,
        string stage)
        => Create(
            taskId,
            TaskFailureKind.HostInterruption,
            "HOST_INTERRUPTED",
            "任务在完成前停止",
            TaskFailureLedgerService.Redact(detail),
            FailureRecoveryAction.Resume,
            "从最近安全点继续",
            retryable: true,
            blocksReplay: false,
            stage,
            new OperationCanceledException(detail));

    private static TaskFailureRecord Create(
        string taskId,
        TaskFailureKind kind,
        string code,
        string title,
        string message,
        FailureRecoveryAction recoveryAction,
        string recoveryLabel,
        bool retryable,
        bool blocksReplay,
        string stage,
        Exception exception)
        => new(
            Guid.NewGuid().ToString("N"),
            taskId,
            kind,
            code,
            title,
            message,
            recoveryAction,
            recoveryLabel,
            retryable,
            blocksReplay,
            stage,
            exception.GetType().Name,
            DateTimeOffset.Now);

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(needle => value.Contains(
            needle,
            StringComparison.OrdinalIgnoreCase));

    private static string NormalizeCode(string value)
    {
        var normalized = new string(value
            .Where(character => char.IsAsciiLetterOrDigit(character))
            .Take(32)
            .ToArray());
        return string.IsNullOrWhiteSpace(normalized)
            ? "LIMIT"
            : normalized.ToUpperInvariant();
    }
}

public sealed class TaskFailureLedgerService
{
    private const int MaximumRecordsPerTask = 100;
    private const int MaximumMessageLength = 2400;
    private static readonly Regex ApiKeyPattern = new(
        @"\bsk-[A-Za-z0-9_-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BearerPattern = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string _storageRoot;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public TaskFailureLedgerService(string? storageRoot = null)
    {
        _storageRoot = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "failures");
    }

    public async Task RecordAsync(
        TaskFailureRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var records = Load(record.TaskId).ToList();
            records.Add(record with
            {
                UserMessage = Redact(record.UserMessage),
                Stage = Redact(record.Stage)
            });
            if (records.Count > MaximumRecordsPerTask)
            {
                records = records
                    .Skip(records.Count - MaximumRecordsPerTask)
                    .ToList();
            }
            Directory.CreateDirectory(_storageRoot);
            var path = GetPath(record.TaskId);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(
                    temporary,
                    JsonSerializer.Serialize(records, _jsonOptions),
                    new UTF8Encoding(false),
                    cancellationToken);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    try
                    {
                        File.Delete(temporary);
                    }
                    catch (IOException)
                    {
                        // The committed ledger remains authoritative.
                    }
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public IReadOnlyList<TaskFailureRecord> Load(string taskId)
    {
        var path = GetPath(taskId);
        if (!File.Exists(path))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<TaskFailureRecord>>(
                       File.ReadAllText(path),
                       _jsonOptions)
                   ?? [];
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException)
        {
            return [];
        }
    }

    public TaskFailureRecord? LoadLatest(string taskId)
        => Load(taskId).MaxBy(record => record.OccurredAt);

    internal static string Redact(string? value)
    {
        var redacted = BearerPattern.Replace(
            ApiKeyPattern.Replace(value ?? string.Empty, "[REDACTED_API_KEY]"),
            "Bearer [REDACTED]");
        redacted = redacted.Replace('\0', ' ').Trim();
        return redacted.Length <= MaximumMessageLength
            ? redacted
            : redacted[..MaximumMessageLength];
    }

    private string GetPath(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)
            || taskId.Length > 256
            || taskId.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Task ID is empty or invalid.", nameof(taskId));
        }
        var stem = string.Concat(taskId
            .Where(character => char.IsAsciiLetterOrDigit(character)
                                || character is '-' or '_')
            .Take(64));
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "task";
        }
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(taskId)))[..12]
            .ToLowerInvariant();
        return Path.Combine(_storageRoot, $"{stem}-{hash}.json");
    }
}
