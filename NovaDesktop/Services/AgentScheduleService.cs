using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public enum AgentScheduleMode
{
    Once,
    Interval
}

public sealed record AgentScheduleItem(
    string Id,
    string Name,
    string Prompt,
    string WorkspaceRoot,
    string Provider,
    string Model,
    AgentScheduleMode Mode,
    DateTimeOffset NextRunAt,
    int? IntervalMinutes,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastClaimedAt,
    AgentExecutionMode ExecutionMode = AgentExecutionMode.Build);

public sealed record AgentScheduleDraft(
    string Name,
    string Prompt,
    string WorkspaceRoot,
    string Provider,
    string Model,
    AgentScheduleMode Mode,
    DateTimeOffset? RunAt = null,
    int? IntervalMinutes = null,
    AgentExecutionMode ExecutionMode = AgentExecutionMode.Build);

public sealed record AgentScheduleCreationContext(
    string WorkspaceRoot,
    string Provider,
    string Model,
    bool HasProviderKey,
    AgentExecutionMode ExecutionMode);

public sealed class AgentScheduleService
{
    private static readonly SemaphoreSlim FileLock = new(1, 1);
    private readonly string _schedulePath;
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AgentScheduleService(string? schedulePath = null)
    {
        _schedulePath = schedulePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "schedules.json");
    }

    public int GetEnabledCount()
    {
        try
        {
            return Load().Count(item => item.Enabled);
        }
        catch
        {
            return 0;
        }
    }

    public IReadOnlyList<AgentScheduleItem> GetSchedules()
        => Load()
            .OrderByDescending(item => item.Enabled)
            .ThenBy(item => item.NextRunAt)
            .ToArray();

    public string ListSchedules()
    {
        var schedules = GetSchedules()
            .Select(item => new
            {
                item.Id,
                item.Name,
                item.Prompt,
                item.WorkspaceRoot,
                item.Provider,
                item.Model,
                mode = item.Mode.ToString().ToLowerInvariant(),
                item.NextRunAt,
                item.IntervalMinutes,
                item.Enabled,
                item.LastClaimedAt
            });
        return JsonSerializer.Serialize(new
        {
            schedule_path = _schedulePath,
            schedules
        });
    }

    public async Task<string> CreateAsync(
        JsonObject arguments,
        AgentRunRequest request,
        CancellationToken cancellationToken)
    {
        var prompt = RequireString(arguments, "prompt");
        if (prompt.Length > 12000)
        {
            throw new InvalidOperationException("Scheduled task prompt exceeds 12,000 characters.");
        }

        var hasRunAt = !string.IsNullOrWhiteSpace(arguments["run_at"]?.GetValue<string>());
        var hasInterval = arguments["interval_minutes"] is not null;
        if (hasRunAt == hasInterval)
        {
            throw new InvalidOperationException("Specify exactly one of run_at or interval_minutes.");
        }

        AgentScheduleMode mode;
        DateTimeOffset nextRunAt;
        int? intervalMinutes = null;
        if (hasRunAt)
        {
            if (!DateTimeOffset.TryParse(
                    arguments["run_at"]?.GetValue<string>(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out nextRunAt))
            {
                throw new InvalidOperationException("run_at must be an ISO 8601 timestamp with a time-zone offset.");
            }
            if (nextRunAt < DateTimeOffset.Now.AddMinutes(-1)
                || nextRunAt > DateTimeOffset.Now.AddYears(1))
            {
                throw new InvalidOperationException("One-time schedules must be between now and one year from now.");
            }
            mode = AgentScheduleMode.Once;
        }
        else
        {
            intervalMinutes = arguments["interval_minutes"]?.GetValue<int>()
                              ?? throw new InvalidOperationException("interval_minutes is required.");
            if (intervalMinutes is < 5 or > 10080)
            {
                throw new InvalidOperationException("Recurring intervals must be between 5 minutes and 7 days.");
            }
            mode = AgentScheduleMode.Interval;
            nextRunAt = DateTimeOffset.Now.AddMinutes(intervalMinutes.Value);
        }

        var name = arguments["name"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = prompt.Length <= 24 ? prompt : prompt[..24] + "…";
        }
        if (name.Length > 80)
        {
            throw new InvalidOperationException("Scheduled task name exceeds 80 characters.");
        }

        var item = await CreateAsync(
            new AgentScheduleDraft(
                name,
                prompt,
                request.WorkspaceRoot,
                request.Provider,
                request.Model,
                mode,
                mode == AgentScheduleMode.Once ? nextRunAt : null,
                intervalMinutes,
                request.ExecutionMode),
            cancellationToken);
        return JsonSerializer.Serialize(new
        {
            status = "scheduled",
            item.Id,
            item.Name,
            mode = item.Mode.ToString().ToLowerInvariant(),
            item.NextRunAt,
            item.IntervalMinutes,
            note = "NOVA must be running and the selected provider key must be available when the task becomes due."
        });
    }

    public async Task<AgentScheduleItem> CreateAsync(
        AgentScheduleDraft draft,
        CancellationToken cancellationToken)
    {
        var prompt = draft.Prompt.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException("请输入计划任务要完成的目标。");
        }
        if (prompt.Length > 12000)
        {
            throw new InvalidOperationException("计划目标不能超过 12,000 个字符。");
        }
        if (string.IsNullOrWhiteSpace(draft.WorkspaceRoot)
            || !Directory.Exists(draft.WorkspaceRoot))
        {
            throw new InvalidOperationException("计划任务的工作区不存在，请先返回主窗口选择有效目录。");
        }

        var name = draft.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = prompt.Length <= 24 ? prompt : prompt[..24] + "…";
        }
        if (name.Length > 80)
        {
            throw new InvalidOperationException("计划名称不能超过 80 个字符。");
        }

        DateTimeOffset nextRunAt;
        int? intervalMinutes = null;
        if (draft.Mode == AgentScheduleMode.Once)
        {
            nextRunAt = draft.RunAt
                        ?? throw new InvalidOperationException("请选择一次性计划的运行日期和时间。");
            if (nextRunAt < DateTimeOffset.Now.AddMinutes(-1)
                || nextRunAt > DateTimeOffset.Now.AddYears(1))
            {
                throw new InvalidOperationException("一次性计划必须安排在现在至一年内。");
            }
        }
        else
        {
            intervalMinutes = draft.IntervalMinutes
                              ?? throw new InvalidOperationException("请选择周期执行间隔。");
            if (intervalMinutes is < 5 or > 10080)
            {
                throw new InvalidOperationException("周期执行间隔必须在 5 分钟至 7 天之间。");
            }
            nextRunAt = DateTimeOffset.Now.AddMinutes(intervalMinutes.Value);
        }

        var item = new AgentScheduleItem(
            Guid.NewGuid().ToString("N")[..12],
            name,
            prompt,
            draft.WorkspaceRoot,
            draft.Provider,
            draft.Model,
            draft.Mode,
            nextRunAt,
            intervalMinutes,
            true,
            DateTimeOffset.Now,
            null,
            draft.ExecutionMode);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var schedules = Load().ToList();
            schedules.Add(item);
            await SaveAsync(schedules, cancellationToken);
        }
        finally
        {
            FileLock.Release();
        }
        return item;
    }

    public async Task<string> DisableAsync(
        string id,
        CancellationToken cancellationToken)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var schedules = Load().ToList();
            var index = schedules.FindIndex(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new InvalidOperationException($"Scheduled task '{id}' was not found.");
            }
            schedules[index] = schedules[index] with { Enabled = false };
            await SaveAsync(schedules, cancellationToken);
            return JsonSerializer.Serialize(new { status = "disabled", id });
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentScheduleItem?> TryClaimNextDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var schedules = Load().ToList();
            var due = schedules
                .Where(item => item.Enabled && item.NextRunAt <= now)
                .OrderBy(item => item.NextRunAt)
                .FirstOrDefault();
            if (due is null)
            {
                return null;
            }

            var claimed = due with
            {
                Enabled = due.Mode == AgentScheduleMode.Interval,
                NextRunAt = due.Mode == AgentScheduleMode.Interval
                    ? now.AddMinutes(due.IntervalMinutes!.Value)
                    : due.NextRunAt,
                LastClaimedAt = now
            };
            schedules[schedules.IndexOf(due)] = claimed;
            await SaveAsync(schedules, cancellationToken);
            return due;
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task RequeueAsync(
        AgentScheduleItem item,
        DateTimeOffset nextRunAt,
        CancellationToken cancellationToken)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var schedules = Load().ToList();
            var index = schedules.FindIndex(schedule => schedule.Id == item.Id);
            if (index < 0)
            {
                return;
            }
            schedules[index] = schedules[index] with
            {
                Enabled = true,
                NextRunAt = nextRunAt
            };
            await SaveAsync(schedules, cancellationToken);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private IReadOnlyList<AgentScheduleItem> Load()
    {
        EnsureFileExists();
        try
        {
            return JsonSerializer.Deserialize<List<AgentScheduleItem>>(
                       File.ReadAllText(_schedulePath),
                       _options)
                   ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new InvalidOperationException($"Unable to read scheduled tasks from '{_schedulePath}'.", exception);
        }
    }

    private async Task SaveAsync(
        IReadOnlyList<AgentScheduleItem> schedules,
        CancellationToken cancellationToken)
    {
        EnsureFileExists();
        var temporaryPath = _schedulePath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(schedules, _options),
            cancellationToken);
        File.Move(temporaryPath, _schedulePath, overwrite: true);
    }

    private void EnsureFileExists()
    {
        var directory = Path.GetDirectoryName(_schedulePath)
                        ?? throw new InvalidOperationException("Schedule path has no parent directory.");
        Directory.CreateDirectory(directory);
        if (!File.Exists(_schedulePath))
        {
            File.WriteAllText(_schedulePath, "[]");
        }
    }

    private static string RequireString(JsonObject arguments, string name)
    {
        var value = arguments[name]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing required argument: {name}");
        }
        return value;
    }
}
