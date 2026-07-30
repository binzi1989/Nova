using System.Text.Json;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public sealed record ProductivityDay(
    DateOnly Date,
    int Activities,
    int CompletedTasks,
    double FocusMinutes);

public sealed record ProductivitySummary(
    int PeriodDays,
    DateTimeOffset GeneratedAt,
    int TotalTasks,
    int CompletedTasks,
    int ActiveTasks,
    int BlockedTasks,
    int ActivityCount,
    int ActiveDays,
    double CompletionRate,
    double FocusMinutes,
    double AverageCycleMinutes,
    int EnabledSchedules,
    int ProductivityScore,
    string PeakDay,
    IReadOnlyList<string> Insights,
    IReadOnlyList<ProductivityDay> DailyTrend);

public sealed class ProductivityInsightsService
{
    private readonly TaskSnapshotService _snapshots;
    private readonly TaskJournalService _journal;
    private readonly AgentScheduleService _schedules;

    public ProductivityInsightsService(
        TaskSnapshotService? snapshots = null,
        TaskJournalService? journal = null,
        AgentScheduleService? schedules = null)
    {
        _snapshots = snapshots ?? new TaskSnapshotService();
        _journal = journal ?? new TaskJournalService();
        _schedules = schedules ?? new AgentScheduleService();
    }

    public ProductivitySummary Generate(int periodDays = 7)
    {
        periodDays = Math.Clamp(periodDays, 1, 90);
        var now = DateTimeOffset.Now;
        var since = now.AddDays(-periodDays);
        var snapshots = _snapshots.LoadAll()
            .Where(item => item.UpdatedAt >= since || item.CreatedAt >= since)
            .ToArray();
        var entries = _journal.ReadRecent(since, 20_000);

        var completed = snapshots.Count(item => item.State == TaskState.Completed);
        var active = snapshots.Count(item => item.State is TaskState.Running or TaskState.Queued);
        var blocked = snapshots.Count(item =>
            item.State is TaskState.Failed
                or TaskState.Waiting
                or TaskState.Paused
                or TaskState.BudgetExhausted);
        var completionRate = snapshots.Length == 0
            ? 0
            : completed * 100d / snapshots.Length;
        var cycleDurations = snapshots
            .Where(item => item.State == TaskState.Completed && item.UpdatedAt >= item.CreatedAt)
            .Select(item => Math.Min((item.UpdatedAt - item.CreatedAt).TotalMinutes, 24 * 60))
            .ToArray();
        var averageCycle = cycleDurations.Length == 0 ? 0 : cycleDurations.Average();
        var focusMinutes = EstimateFocusMinutes(entries);
        var activeDays = entries
            .Select(entry => DateOnly.FromDateTime(entry.Timestamp.LocalDateTime))
            .Distinct()
            .Count();

        var daily = Enumerable.Range(0, periodDays)
            .Select(offset =>
            {
                var date = DateOnly.FromDateTime(now.AddDays(-(periodDays - 1 - offset)).LocalDateTime);
                var dayEntries = entries.Where(entry =>
                    DateOnly.FromDateTime(entry.Timestamp.LocalDateTime) == date).ToArray();
                return new ProductivityDay(
                    date,
                    dayEntries.Length,
                    snapshots.Count(item =>
                        item.State == TaskState.Completed
                        && DateOnly.FromDateTime(item.UpdatedAt.LocalDateTime) == date),
                    EstimateFocusMinutes(dayEntries));
            })
            .ToArray();
        var peak = daily
            .OrderByDescending(day => day.FocusMinutes)
            .ThenByDescending(day => day.Activities)
            .FirstOrDefault();
        var peakDay = peak is null || (peak.Activities == 0 && peak.CompletedTasks == 0)
            ? "暂无"
            : peak.Date.ToString("MM-dd");
        var enabledSchedules = _schedules.GetEnabledCount();

        var score = CalculateScore(
            completionRate,
            activeDays,
            periodDays,
            focusMinutes,
            blocked,
            snapshots.Length);
        var insights = BuildInsights(
            snapshots,
            entries,
            completionRate,
            focusMinutes,
            peakDay,
            enabledSchedules);

        return new ProductivitySummary(
            periodDays,
            now,
            snapshots.Length,
            completed,
            active,
            blocked,
            entries.Count,
            activeDays,
            Math.Round(completionRate, 1),
            Math.Round(focusMinutes, 1),
            Math.Round(averageCycle, 1),
            enabledSchedules,
            score,
            peakDay,
            insights,
            daily);
    }

    public string GenerateJson(int periodDays = 7)
        => JsonSerializer.Serialize(Generate(periodDays));

    private static double EstimateFocusMinutes(IEnumerable<TaskJournalEntry> entries)
    {
        var total = 0d;
        foreach (var group in entries
                     .Where(entry => !entry.TaskId.Equals("system", StringComparison.OrdinalIgnoreCase))
                     .GroupBy(entry => entry.TaskId))
        {
            var ordered = group.OrderBy(entry => entry.Timestamp).ToArray();
            if (ordered.Length == 1)
            {
                total += 1;
                continue;
            }
            for (var index = 1; index < ordered.Length; index++)
            {
                var gap = (ordered[index].Timestamp - ordered[index - 1].Timestamp).TotalMinutes;
                if (gap is > 0 and <= 15)
                {
                    total += gap;
                }
            }
        }
        return total;
    }

    private static int CalculateScore(
        double completionRate,
        int activeDays,
        int periodDays,
        double focusMinutes,
        int blocked,
        int totalTasks)
    {
        if (totalTasks == 0 && focusMinutes <= 0)
        {
            return 0;
        }
        var completion = completionRate * .5;
        var consistency = Math.Min(20, activeDays * 20d / Math.Min(periodDays, 7));
        var focus = Math.Min(20, focusMinutes / Math.Max(1, periodDays) * 2);
        var reliability = totalTasks == 0 ? 10 : Math.Max(0, 10 - blocked * 10d / totalTasks);
        return (int)Math.Round(Math.Clamp(completion + consistency + focus + reliability, 0, 100));
    }

    private static IReadOnlyList<string> BuildInsights(
        IReadOnlyList<TaskSnapshot> snapshots,
        IReadOnlyList<TaskJournalEntry> entries,
        double completionRate,
        double focusMinutes,
        string peakDay,
        int enabledSchedules)
    {
        if (snapshots.Count == 0 && entries.Count == 0)
        {
            return
            [
                "还没有足够的真实任务数据。完成一次 NOVA 任务后，这里会形成个人效率基线。",
                "建议先建立一个可在 30–90 分钟内验证的目标，以获得更准确的周期数据。"
            ];
        }

        var insights = new List<string>
        {
            $"本周期任务完成率为 {completionRate:0.#}%，估算有效专注时间 {focusMinutes:0.#} 分钟。"
        };
        if (peakDay != "暂无")
        {
            insights.Add($"你的高产日期是 {peakDay}；可以把高认知负荷任务优先放到相似时段。");
        }

        var blocked = snapshots
            .Where(item => item.State is TaskState.Failed
                or TaskState.Waiting
                or TaskState.Paused
                or TaskState.BudgetExhausted)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault();
        if (blocked is not null)
        {
            insights.Add($"当前主要阻塞目标是“{blocked.Title}”，建议先明确下一步可验证动作。");
        }
        else
        {
            insights.Add("当前没有持久化阻塞任务，执行流保持畅通。");
        }

        var busiestAgent = entries
            .Where(entry => !entry.Agent.Equals("系统", StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => entry.Agent)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault();
        if (busiestAgent is not null)
        {
            insights.Add($"最活跃执行单元是“{busiestAgent.Key}”，共记录 {busiestAgent.Count()} 次活动。");
        }
        if (enabledSchedules > 0)
        {
            insights.Add($"有 {enabledSchedules} 个自动计划已启用；建议定期检查其产出是否仍然值得 Token 成本。");
        }
        return insights.Take(5).ToArray();
    }
}
