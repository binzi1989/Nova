using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NovaDesktop.Services;

namespace NovaDesktop.Controls;

public sealed class ProductivityTrendCanvas : FrameworkElement
{
    private IReadOnlyList<ProductivityDay> _days = [];

    public IReadOnlyList<ProductivityDay> Days
    {
        get => _days;
        set
        {
            _days = value ?? [];
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var width = Math.Max(1, ActualWidth);
        var height = Math.Max(1, ActualHeight);
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(13, 18, 27)),
            new Pen(new SolidColorBrush(Color.FromRgb(35, 45, 63)), 1),
            new Rect(0, 0, width, height),
            10,
            10);
        if (_days.Count == 0)
        {
            return;
        }

        var maximum = Math.Max(1, _days.Max(day => Math.Max(day.FocusMinutes, day.Activities)));
        var slot = (width - 28) / _days.Count;
        var baseline = height - 27;
        for (var index = 0; index < _days.Count; index++)
        {
            var day = _days[index];
            var barHeight = Math.Max(3, (height - 50) * Math.Max(day.FocusMinutes, day.Activities) / maximum);
            var x = 14 + index * slot + slot * .18;
            var rect = new Rect(x, baseline - barHeight, slot * .64, barHeight);
            context.DrawRoundedRectangle(
                new LinearGradientBrush(
                    Color.FromRgb(117, 240, 255),
                    Color.FromRgb(101, 116, 255),
                    90),
                null,
                rect,
                4,
                4);
            var label = new FormattedText(
                day.Date.ToString("dd"),
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                9,
                new SolidColorBrush(Color.FromRgb(111, 124, 148)),
                1);
            context.DrawText(label, new Point(x + rect.Width / 2 - label.Width / 2, height - 20));
        }
    }
}
