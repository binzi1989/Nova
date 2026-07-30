using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NovaDesktop.Services;

namespace NovaDesktop.Controls;

public sealed class KnowledgeGraphCanvas : FrameworkElement
{
    private KnowledgeGraphSnapshot _graph = new(DateTimeOffset.MinValue, [], []);
    private string _filter = string.Empty;

    public KnowledgeGraphSnapshot Graph
    {
        get => _graph;
        set
        {
            _graph = value ?? new KnowledgeGraphSnapshot(DateTimeOffset.MinValue, [], []);
            InvalidateVisual();
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            _filter = value?.Trim() ?? string.Empty;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = Math.Max(1, ActualWidth);
        var height = Math.Max(1, ActualHeight);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(9, 13, 21)),
            new Pen(new SolidColorBrush(Color.FromRgb(37, 47, 66)), 1),
            new Rect(0, 0, width, height),
            14,
            14);
        DrawGrid(drawingContext, width, height);

        var nodes = SelectNodes().Take(70).ToArray();
        if (nodes.Length == 0)
        {
            DrawCenteredText(
                drawingContext,
                "暂无认知节点 · 完成任务或添加知识后自动生成",
                new Point(width / 2, height / 2),
                12,
                Color.FromRgb(105, 117, 140));
            return;
        }

        var positions = LayoutNodes(nodes, width, height);
        var retained = positions.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in _graph.Edges
                     .Where(edge => retained.Contains(edge.SourceId) && retained.Contains(edge.TargetId))
                     .Take(180))
        {
            var source = positions[edge.SourceId];
            var target = positions[edge.TargetId];
            var color = Color.FromArgb(
                60,
                (byte)(85 + Math.Min(80, edge.Weight * 30)),
                156,
                188);
            drawingContext.DrawLine(
                new Pen(new SolidColorBrush(color), Math.Clamp(edge.Weight * .55, .55, 1.5)),
                source,
                target);
        }

        foreach (var node in nodes.OrderBy(node => node.Weight))
        {
            DrawNode(drawingContext, node, positions[node.Id]);
        }
    }

    private IEnumerable<KnowledgeNode> SelectNodes()
    {
        var nodes = _graph.Nodes
            .OrderByDescending(node => node.IsManual)
            .ThenByDescending(node => node.Weight)
            .ThenByDescending(node => node.UpdatedAt);
        if (_filter.Length == 0)
        {
            return nodes;
        }
        return nodes.Where(node =>
            node.Label.Contains(_filter, StringComparison.CurrentCultureIgnoreCase)
            || node.Kind.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || node.Detail.Contains(_filter, StringComparison.CurrentCultureIgnoreCase));
    }

    private static Dictionary<string, Point> LayoutNodes(
        IReadOnlyList<KnowledgeNode> nodes,
        double width,
        double height)
    {
        var center = new Point(width / 2, height / 2);
        var maxRadius = Math.Max(80, Math.Min(width, height) * .42);
        var result = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        var ordered = nodes
            .OrderByDescending(node => node.IsManual)
            .ThenByDescending(node => node.Weight)
            .ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var node = ordered[index];
            if (index == 0)
            {
                result[node.Id] = center;
                continue;
            }

            var goldenAngle = Math.PI * (3 - Math.Sqrt(5));
            var radius = Math.Min(maxRadius, 54 + Math.Sqrt(index) * 42);
            var kindOffset = KindOffset(node.Kind);
            var angle = index * goldenAngle + kindOffset;
            var x = center.X + Math.Cos(angle) * radius * Math.Min(1.45, width / Math.Max(1, height));
            var y = center.Y + Math.Sin(angle) * radius;
            result[node.Id] = new Point(
                Math.Clamp(x, 42, width - 42),
                Math.Clamp(y, 36, height - 36));
        }
        return result;
    }

    private static void DrawNode(
        DrawingContext drawingContext,
        KnowledgeNode node,
        Point position)
    {
        var color = KindColor(node.Kind);
        var radius = Math.Clamp(7 + node.Weight * 2.5 + (node.IsManual ? 3 : 0), 9, 17);
        drawingContext.DrawEllipse(
            new SolidColorBrush(Color.FromArgb(35, color.R, color.G, color.B)),
            null,
            position,
            radius + 8,
            radius + 8);
        drawingContext.DrawEllipse(
            new RadialGradientBrush(
                Color.FromArgb(255, 235, 253, 255),
                color),
            new Pen(new SolidColorBrush(Color.FromArgb(150, color.R, color.G, color.B)), 1),
            position,
            radius,
            radius);

        var label = node.Label.Length > 18 ? node.Label[..18] + "…" : node.Label;
        var text = CreateText(label, node.IsManual ? 11.5 : 10, Color.FromRgb(209, 219, 235));
        drawingContext.DrawText(text, new Point(position.X + radius + 6, position.Y - text.Height / 2));
    }

    private static void DrawGrid(DrawingContext drawingContext, double width, double height)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(22, 103, 132, 165)), 1);
        for (var x = 28d; x < width; x += 28)
        {
            drawingContext.DrawLine(pen, new Point(x, 0), new Point(x, height));
        }
        for (var y = 28d; y < height; y += 28)
        {
            drawingContext.DrawLine(pen, new Point(0, y), new Point(width, y));
        }
    }

    private static void DrawCenteredText(
        DrawingContext context,
        string value,
        Point center,
        double size,
        Color color)
    {
        var text = CreateText(value, size, color);
        context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }

    private static FormattedText CreateText(string value, double size, Color color)
        => new(
            value,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable Text"),
            size,
            new SolidColorBrush(color),
            1.0);

    private static double KindOffset(string kind)
        => kind switch
        {
            "Goal" => 0,
            "Project" => .6,
            "Concept" => 1.2,
            "Skill" => 1.8,
            "Tool" => 2.4,
            "Knowledge" => 3,
            "Routine" => 3.6,
            "Document" => 4.0,
            "Artifact" => 4.5,
            _ => 4.2
        };

    private static Color KindColor(string kind)
        => kind switch
        {
            "Goal" => Color.FromRgb(117, 240, 255),
            "Project" => Color.FromRgb(125, 140, 255),
            "Concept" => Color.FromRgb(189, 168, 255),
            "Skill" => Color.FromRgb(107, 229, 169),
            "Tool" => Color.FromRgb(255, 196, 112),
            "Knowledge" => Color.FromRgb(244, 126, 255),
            "Routine" => Color.FromRgb(105, 183, 255),
            "Document" => Color.FromRgb(255, 151, 120),
            "Artifact" => Color.FromRgb(107, 229, 169),
            "Provider" or "Model" => Color.FromRgb(148, 161, 185),
            _ => Color.FromRgb(127, 143, 166)
        };
}
