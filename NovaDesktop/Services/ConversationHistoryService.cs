using System.IO;
using System.Text;
using System.Text.Json;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public sealed class ConversationHistoryService
{
    private const int MaximumStoredTurns = 80;
    private const int MaximumContextTurns = 16;
    private const int MaximumContextCharacters = 36_000;
    private readonly string _root;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConversationHistoryService(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "conversations");
    }

    public string Root => _root;

    public IReadOnlyList<ConversationTurn> Load(string taskId)
    {
        var path = GetPath(taskId);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<ConversationTurn[]>(
                       File.ReadAllText(path),
                       _options)
                   ?.OrderBy(turn => turn.CreatedAt)
                   .ToArray()
                   ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return [];
        }
    }

    public async Task<ConversationTurn> AppendAsync(
        string taskId,
        string role,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("对话内容不能为空。", nameof(content));
        }
        role = role.Trim().ToLowerInvariant();
        if (role is not ("user" or "assistant" or "system"))
        {
            throw new ArgumentOutOfRangeException(nameof(role), "不支持的对话角色。");
        }

        var turn = new ConversationTurn(
            Guid.NewGuid().ToString("N"),
            taskId,
            role,
            content.Trim(),
            DateTimeOffset.Now);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var turns = Load(taskId)
                .Append(turn)
                .TakeLast(MaximumStoredTurns)
                .ToArray();
            await SaveAsync(taskId, turns, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
        return turn;
    }

    public string BuildContextPrompt(string taskId, string currentPrompt)
        => BuildContextPrompt(
            taskId,
            currentPrompt,
            supplementalTurns: null,
            includeCurrentPrompt: true);

    public string BuildContextPrompt(
        string taskId,
        string currentPrompt,
        IReadOnlyList<ConversationTurn>? supplementalTurns,
        bool includeCurrentPrompt)
    {
        var durableTurns = Load(taskId).ToList();
        var transientTurns = supplementalTurns?
            .Where(turn => !string.IsNullOrWhiteSpace(turn.Content))
            .OrderBy(turn => turn.CreatedAt)
            .ToList()
            ?? [];
        // The durable history is the source of truth after start_task has
        // appended the current user turn. A longer transient history is used
        // only for tasks created by older builds that did not persist every
        // conversation turn.
        var turns = (transientTurns.Count > durableTurns.Count
                ? transientTurns
                : durableTurns)
            .TakeLast(MaximumStoredTurns)
            .ToList();
        var currentIsAlreadyLast = turns.LastOrDefault() is { Role: "user" } last
                                   && last.Content.Equals(
                                       currentPrompt.Trim(),
                                       StringComparison.Ordinal);
        if (currentIsAlreadyLast)
        {
            turns.RemoveAt(turns.Count - 1);
        }
        if (turns.Count == 0)
        {
            return includeCurrentPrompt ? currentPrompt : string.Empty;
        }

        var originalGoal = turns.FirstOrDefault(turn =>
            turn.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
        var userDirections = turns
            .Where(turn => turn.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            .TakeLast(12)
            .ToArray();
        var assistantStates = turns
            .Where(turn => turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            .TakeLast(3)
            .ToArray();
        var recentTurns = turns
            .TakeLast(MaximumContextTurns)
            .TakeLast(8)
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine("[NOVA THREAD MEMORY v2]");
        builder.AppendLine("这是同一个 NOVA 任务中的连续对话，不是新任务。");
        builder.AppendLine("即使本任务没有产生任何本地文件，对话中用户给出的目标、事实、术语、选择、偏好和纠正仍是有效任务状态。");
        builder.AppendLine("解释“继续、这个、刚才、上一版、方向一”等指代时，优先结合最近对话，不要求用户重复已经提供的信息。");
        builder.AppendLine("优先级：当前用户指令 > 较新的用户纠正/选择 > 较早用户目标 > NOVA 先前的推断。");
        builder.AppendLine("用户没有确认的 NOVA 推测不得升级为事实；若历史与工具读取到的当前工作区冲突，以工具证据为准。");
        builder.AppendLine();

        if (originalGoal is not null)
        {
            builder.AppendLine("[ORIGINAL USER GOAL]");
            builder.AppendLine(BoundForMemory(originalGoal.Content, 3_200));
            builder.AppendLine();
        }

        if (userDirections.Length > 0)
        {
            builder.AppendLine("[USER-PROVIDED FACTS, DECISIONS AND DIRECTIONS]");
            builder.AppendLine("以下条目按时间排列；如有冲突，后面的用户条目覆盖前面的条目。");
            foreach (var (turn, index) in userDirections.Select((turn, index) => (turn, index)))
            {
                builder.AppendLine($"U{index + 1}: {BoundForMemory(turn.Content, 1_400)}");
            }
            builder.AppendLine();
        }

        if (assistantStates.Length > 0)
        {
            builder.AppendLine("[RECENT NOVA STATE — conclusions may be revised by the user]");
            foreach (var (turn, index) in assistantStates.Select((turn, index) => (turn, index)))
            {
                builder.AppendLine($"A{index + 1}:");
                builder.AppendLine(BoundForMemory(StripDeliveryPassport(turn.Content), 2_400));
            }
            builder.AppendLine();
        }

        builder.AppendLine("[RECENT DIALOGUE]");
        foreach (var turn in recentTurns)
        {
            var role = turn.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                ? "USER"
                : turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                    ? "ASSISTANT"
                    : "SYSTEM";
            builder.AppendLine($"<{role}>");
            builder.AppendLine(BoundForMemory(
                role == "ASSISTANT" ? StripDeliveryPassport(turn.Content) : turn.Content,
                role == "ASSISTANT" ? 3_200 : 2_200));
            builder.AppendLine($"</{role}>");
        }

        if (includeCurrentPrompt && !string.IsNullOrWhiteSpace(currentPrompt))
        {
            builder.AppendLine();
            builder.AppendLine("[CURRENT USER REQUEST]");
            builder.AppendLine(BoundForMemory(currentPrompt, 8_000));
        }
        else
        {
            builder.AppendLine();
            builder.AppendLine("[CURRENT USER REQUEST FOLLOWS]");
        }

        var context = builder.ToString().Trim();
        if (context.Length <= MaximumContextCharacters)
        {
            return context;
        }
        const int preservedHead = 8_000;
        var preservedTail = MaximumContextCharacters - preservedHead - 80;
        return context[..preservedHead]
               + Environment.NewLine
               + "… 中段较早对话已按上下文预算压缩省略 …"
               + Environment.NewLine
               + context[^preservedTail..];
    }

    public int GetRoundCount(string taskId)
        => Load(taskId).Count(turn => turn.Role.Equals("user", StringComparison.OrdinalIgnoreCase));

    public int GetResponseCount(string taskId)
        => Load(taskId).Count(turn =>
            turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase));

    public async Task<bool> DeleteAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(taskId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var deleted = false;
            if (File.Exists(path))
            {
                File.Delete(path);
                deleted = true;
            }
            var temporaryPath = path + ".tmp";
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
            return deleted;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string StripDeliveryPassport(string content)
    {
        var marker = content.IndexOf(
            "\n---\n### NOVA 交付护照",
            StringComparison.OrdinalIgnoreCase);
        return marker >= 0 ? content[..marker].Trim() : content.Trim();
    }

    private static string BoundForMemory(string content, int maximumCharacters)
    {
        var normalized = (content ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (normalized.Length <= maximumCharacters)
        {
            return normalized;
        }

        var head = Math.Max(1, maximumCharacters / 2);
        var tail = Math.Max(1, maximumCharacters - head - 24);
        return normalized[..head] + "\n… 内容中段已压缩 …\n" + normalized[^tail..];
    }

    private async Task SaveAsync(
        string taskId,
        IReadOnlyList<ConversationTurn> turns,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var path = GetPath(taskId);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(turns, _options),
            Encoding.UTF8,
            cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    private string GetPath(string taskId)
    {
        var safe = string.Concat(taskId.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
        if (safe.Length == 0)
        {
            throw new InvalidOperationException("任务 ID 无法转换为安全文件名。");
        }
        return Path.Combine(_root, safe + ".json");
    }
}
