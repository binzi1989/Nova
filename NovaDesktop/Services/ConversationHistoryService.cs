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
    {
        var turns = Load(taskId).TakeLast(MaximumContextTurns).ToList();
        var currentIsAlreadyLast = turns.LastOrDefault() is { Role: "user" } last
                                   && last.Content.Equals(
                                       currentPrompt.Trim(),
                                       StringComparison.Ordinal);
        if (!currentIsAlreadyLast && !string.IsNullOrWhiteSpace(currentPrompt))
        {
            turns.Add(new ConversationTurn(
                "current",
                taskId,
                "user",
                currentPrompt.Trim(),
                DateTimeOffset.Now));
        }
        if (turns.Count <= 1)
        {
            return currentPrompt;
        }

        var builder = new StringBuilder();
        builder.AppendLine("这是同一个 NOVA 任务中的连续对话。请延续已经确认的目标、术语、工作区状态与交付物，不要把本轮误当成全新任务。");
        builder.AppendLine("如果历史与当前工作区实际内容冲突，以当前工具读取结果为准。");
        builder.AppendLine();
        builder.AppendLine("最近对话：");
        foreach (var turn in turns)
        {
            var role = turn.Role == "user" ? "用户" : turn.Role == "assistant" ? "NOVA" : "系统";
            builder.AppendLine($"[{role}]");
            builder.AppendLine(turn.Content);
            builder.AppendLine();
        }

        var context = builder.ToString();
        if (context.Length <= MaximumContextCharacters)
        {
            return context;
        }
        return "…较早对话已压缩省略…" + Environment.NewLine
               + context[^MaximumContextCharacters..];
    }

    public int GetRoundCount(string taskId)
        => Load(taskId).Count(turn => turn.Role.Equals("user", StringComparison.OrdinalIgnoreCase));

    public int GetResponseCount(string taskId)
        => Load(taskId).Count(turn =>
            turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase));

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
