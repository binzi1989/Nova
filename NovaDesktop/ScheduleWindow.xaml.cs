using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NovaDesktop.Services;

namespace NovaDesktop;

public partial class ScheduleWindow : Window
{
    private static readonly SolidColorBrush EnabledBrush = new(
        Color.FromRgb(86, 214, 169));
    private static readonly SolidColorBrush DisabledBrush = new(
        Color.FromRgb(123, 137, 160));

    private readonly AgentScheduleService _scheduleService;
    private readonly AgentScheduleCreationContext _context;
    private bool _creating;

    public ScheduleWindow(
        AgentScheduleService scheduleService,
        AgentScheduleCreationContext context)
    {
        InitializeComponent();
        _scheduleService = scheduleService;
        _context = context;

        var defaultRun = DateTime.Now.AddHours(1);
        RunDatePicker.SelectedDate = defaultRun.Date;
        RunTimeBox.Text = defaultRun.ToString("HH:mm", CultureInfo.InvariantCulture);
        WorkspaceText.Text = $"工作区 · {_context.WorkspaceRoot}";
        RuntimeText.Text =
            $"模型 · {ProviderLabel(_context.Provider)} / {_context.Model} · {_context.ExecutionMode}";
        KeyStatusText.Text = _context.HasProviderKey
            ? "模型密钥已可用"
            : "模型密钥当前不可用；计划仍可保存，但到期时会安全延期。";
        KeyStatusText.Foreground = _context.HasProviderKey
            ? EnabledBrush
            : new SolidColorBrush(Color.FromRgb(244, 200, 106));

        Loaded += (_, _) =>
        {
            RefreshSchedules();
            PromptBox.Focus();
        };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();

    private void Refresh_Click(object sender, RoutedEventArgs e)
        => RefreshSchedules();

    private void FocusCreate_Click(object sender, RoutedEventArgs e)
    {
        PromptBox.Focus();
        PromptBox.CaretIndex = PromptBox.Text.Length;
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (OncePanel is null || IntervalBox is null || CreateButton is null)
        {
            return;
        }

        var isInterval = IntervalRadio.IsChecked == true;
        OncePanel.Visibility = isInterval ? Visibility.Collapsed : Visibility.Visible;
        IntervalBox.Visibility = isInterval ? Visibility.Visible : Visibility.Collapsed;
        CreateButton.Content = isInterval ? "创建周期计划" : "创建一次性计划";
        FormErrorText.Text = string.Empty;
    }

    private void ScheduleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DisableButton.IsEnabled =
            ScheduleList.SelectedItem is ScheduleDisplayItem { Item.Enabled: true };
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_creating)
        {
            return;
        }

        FormErrorText.Text = string.Empty;
        AgentScheduleMode mode;
        DateTimeOffset? runAt = null;
        int? intervalMinutes = null;
        try
        {
            mode = IntervalRadio.IsChecked == true
                ? AgentScheduleMode.Interval
                : AgentScheduleMode.Once;
            if (mode == AgentScheduleMode.Once)
            {
                runAt = ParseRunAt();
            }
            else
            {
                intervalMinutes = ParseIntervalMinutes();
            }

            var draft = new AgentScheduleDraft(
                NameBox.Text,
                PromptBox.Text,
                _context.WorkspaceRoot,
                _context.Provider,
                _context.Model,
                mode,
                runAt,
                intervalMinutes,
                _context.ExecutionMode);

            _creating = true;
            CreateButton.IsEnabled = false;
            CreateButton.Content = "正在创建…";
            var created = await _scheduleService.CreateAsync(
                draft,
                CancellationToken.None);

            StatusText.Text =
                $"已创建：{created.Name} · 下次运行 {created.NextRunAt.ToLocalTime():yyyy-MM-dd HH:mm}";
            NameBox.Text = string.Empty;
            PromptBox.Text = string.Empty;
            RefreshSchedules(created.Id);
            PromptBox.Focus();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            FormErrorText.Text = exception.Message;
            StatusText.Text = "计划尚未创建，请检查右侧信息。";
        }
        finally
        {
            _creating = false;
            CreateButton.IsEnabled = true;
            CreateButton.Content = IntervalRadio.IsChecked == true
                ? "创建周期计划"
                : "创建一次性计划";
        }
    }

    private async void Disable_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduleList.SelectedItem is not ScheduleDisplayItem
            {
                Item.Enabled: true
            } selected)
        {
            return;
        }

        DisableButton.IsEnabled = false;
        try
        {
            await _scheduleService.DisableAsync(
                selected.Item.Id,
                CancellationToken.None);
            StatusText.Text =
                $"已停用：{selected.Item.Name}。这不会删除已有任务记录。";
            RefreshSchedules(selected.Item.Id);
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private void RefreshSchedules(string? selectedId = null)
    {
        try
        {
            var schedules = _scheduleService.GetSchedules();
            var displayItems = schedules
                .Select(item => ScheduleDisplayItem.Create(item))
                .ToArray();
            ScheduleList.ItemsSource = displayItems;
            CountText.Text =
                $"共 {displayItems.Length} 个 · {schedules.Count(item => item.Enabled)} 个启用";
            EmptyState.Visibility = displayItems.Length == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            DisableButton.IsEnabled = false;

            if (!string.IsNullOrWhiteSpace(selectedId))
            {
                var selected = displayItems.FirstOrDefault(item =>
                    item.Item.Id.Equals(
                        selectedId,
                        StringComparison.OrdinalIgnoreCase));
                if (selected is not null)
                {
                    ScheduleList.SelectedItem = selected;
                    ScheduleList.ScrollIntoView(selected);
                }
            }
        }
        catch (Exception exception)
        {
            ScheduleList.ItemsSource = null;
            CountText.Text = "读取异常";
            EmptyState.Visibility = Visibility.Collapsed;
            StatusText.Text =
                $"无法读取计划库：{exception.Message}";
        }
    }

    private DateTimeOffset ParseRunAt()
    {
        if (RunDatePicker.SelectedDate is not { } selectedDate)
        {
            throw new InvalidOperationException("请选择计划运行日期。");
        }
        if (!TimeSpan.TryParseExact(
                RunTimeBox.Text.Trim(),
                ["h\\:mm", "hh\\:mm"],
                CultureInfo.InvariantCulture,
                out var selectedTime))
        {
            throw new InvalidOperationException("运行时间格式应为 09:30。");
        }

        var local = DateTime.SpecifyKind(
            selectedDate.Date.Add(selectedTime),
            DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(local))
        {
            throw new InvalidOperationException("该本地时间位于夏令时跳变区间，请选择其他时间。");
        }
        return new DateTimeOffset(
            local,
            TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private int ParseIntervalMinutes()
    {
        if (IntervalBox.SelectedItem is not ComboBoxItem item
            || !int.TryParse(
                item.Tag?.ToString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var minutes))
        {
            throw new InvalidOperationException("请选择周期执行间隔。");
        }
        return minutes;
    }

    private static string ProviderLabel(string provider)
        => provider.ToLowerInvariant() switch
        {
            "deepseek" => "DeepSeek",
            "kimi" => "Kimi",
            _ => "OpenAI"
        };

    private sealed record ScheduleDisplayItem(
        AgentScheduleItem Item,
        string Name,
        string Prompt,
        string RuntimeLabel,
        string ModeLabel,
        string NextRunLabel,
        string StateLabel,
        Brush StateBrush)
    {
        public static ScheduleDisplayItem Create(AgentScheduleItem item)
        {
            var modeLabel = item.Mode == AgentScheduleMode.Once
                ? "执行一次"
                : item.IntervalMinutes switch
                {
                    1440 => "每天",
                    10080 => "每 7 天",
                    >= 60 when item.IntervalMinutes % 60 == 0
                        => $"每 {item.IntervalMinutes / 60} 小时",
                    _ => $"每 {item.IntervalMinutes} 分钟"
                };
            return new ScheduleDisplayItem(
                item,
                item.Name,
                item.Prompt,
                $"{ProviderLabel(item.Provider)} / {item.Model} · {item.WorkspaceRoot}",
                modeLabel,
                item.Enabled
                    ? item.NextRunAt.ToLocalTime().ToString("MM-dd HH:mm")
                    : "不会继续运行",
                item.Enabled ? "已启用" : "已停用",
                item.Enabled ? EnabledBrush : DisabledBrush);
        }
    }
}
