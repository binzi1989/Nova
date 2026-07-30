using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using NovaDesktop.Models;

namespace NovaDesktop.Infrastructure;

public sealed class TaskStateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is TaskState state
            ? new SolidColorBrush(state switch
            {
                TaskState.Running => Color.FromRgb(117, 240, 255),
                TaskState.Waiting => Color.FromRgb(255, 196, 112),
                TaskState.Paused => Color.FromRgb(189, 168, 255),
                TaskState.Completed => Color.FromRgb(107, 229, 169),
                TaskState.Cancelled => Color.FromRgb(125, 132, 154),
                TaskState.BudgetExhausted => Color.FromRgb(255, 196, 112),
                TaskState.Failed => Color.FromRgb(255, 112, 143),
                TaskState.Stale => Color.FromRgb(255, 196, 112),
                _ => Color.FromRgb(125, 132, 154)
            })
            : Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class TaskStateLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is TaskState state
            ? state switch
            {
                TaskState.Running => "执行中",
                TaskState.Waiting => "等待授权",
                TaskState.Paused => "已暂停",
                TaskState.Completed => "已完成",
                TaskState.Cancelled => "已取消",
                TaskState.BudgetExhausted => "预算已用尽",
                TaskState.Failed => "失败",
                TaskState.Stale => "证据已过期",
                TaskState.Queued => "排队中",
                _ => state.ToString()
            }
            : string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class ActivityKindBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is ActivityKind kind
            ? new SolidColorBrush(kind switch
            {
                ActivityKind.Completed => Color.FromRgb(107, 229, 169),
                ActivityKind.Waiting => Color.FromRgb(255, 196, 112),
                ActivityKind.System => Color.FromRgb(189, 168, 255),
                _ => Color.FromRgb(117, 240, 255)
            })
            : Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class AgentExecutionModeLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is AgentExecutionMode mode
            ? mode switch
            {
                AgentExecutionMode.Ask => "咨询 · 只读",
                AgentExecutionMode.Plan => "规划 · 只读",
                AgentExecutionMode.Build => "构建 · 可写",
                AgentExecutionMode.Autopilot => "自主 · 并行闭环",
                AgentExecutionMode.Goal => "目标 · 自主探索",
                _ => mode.ToString()
            }
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
