using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace NovaDesktop.Controls;

public sealed class MarkdownMessageView : StackPanel
{
    private static readonly Regex InlineTokenPattern = new(
        @"(\*\*.+?\*\*|`[^`]+`)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OrderedListPattern = new(
        @"^\s*(\d+)[.)]\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Brush PrimaryText =
        new SolidColorBrush(Color.FromRgb(237, 236, 229));
    private static readonly Brush SecondaryText =
        new SolidColorBrush(Color.FromRgb(183, 192, 185));
    private static readonly Brush MutedText =
        new SolidColorBrush(Color.FromRgb(126, 143, 135));
    private static readonly Brush Cyan =
        new SolidColorBrush(Color.FromRgb(120, 200, 182));
    private static readonly Brush CodeBackground =
        new SolidColorBrush(Color.FromRgb(8, 12, 10));
    private static readonly Brush CodeBorder =
        new SolidColorBrush(Color.FromRgb(54, 72, 65));
    private static readonly Brush TableBorder =
        new SolidColorBrush(Color.FromRgb(57, 77, 70));
    private static readonly Brush TableHeader =
        new SolidColorBrush(Color.FromRgb(27, 40, 35));
    private static readonly Brush TableAlternate =
        new SolidColorBrush(Color.FromArgb(78, 25, 37, 32));

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownMessageView),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.AffectsMeasure,
            (dependencyObject, _) => ((MarkdownMessageView)dependencyObject).Render()));

    static MarkdownMessageView()
    {
        foreach (var brush in new[]
                 {
                     PrimaryText, SecondaryText, MutedText, Cyan, CodeBackground,
                     CodeBorder, TableBorder, TableHeader, TableAlternate
                 }.OfType<Freezable>())
        {
            brush.Freeze();
        }
    }

    public MarkdownMessageView()
    {
        Orientation = Orientation.Vertical;
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void Render()
    {
        Children.Clear();
        var lines = (Markdown ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var paragraph = new List<string>();
        for (var index = 0; index < lines.Length;)
        {
            var line = lines[index];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph(paragraph);
                var language = line.Trim()[3..].Trim();
                var code = new List<string>();
                index++;
                while (index < lines.Length
                       && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[index]);
                    index++;
                }
                if (index < lines.Length)
                {
                    index++;
                }
                AddCodeBlock(language, string.Join(Environment.NewLine, code));
                continue;
            }

            if (LooksLikeTableHeader(lines, index))
            {
                FlushParagraph(paragraph);
                var tableLines = new List<string> { lines[index] };
                index += 2;
                while (index < lines.Length && IsTableRow(lines[index]))
                {
                    tableLines.Add(lines[index]);
                    index++;
                }
                AddTable(tableLines);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph(paragraph);
                index++;
                continue;
            }

            var trimmed = line.Trim();
            var headingLevel = CountHeadingPrefix(trimmed);
            if (headingLevel > 0)
            {
                FlushParagraph(paragraph);
                AddHeading(trimmed[(headingLevel + 1)..], headingLevel);
                index++;
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph(paragraph);
                Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 8, 0, 9),
                    Background = TableBorder
                });
                index++;
                continue;
            }

            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushParagraph(paragraph);
                AddQuote(trimmed[2..]);
                index++;
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal)
                || trimmed.StartsWith("* ", StringComparison.Ordinal)
                || trimmed.StartsWith("• ", StringComparison.Ordinal))
            {
                FlushParagraph(paragraph);
                AddListItem("•", trimmed[2..]);
                index++;
                continue;
            }

            var ordered = OrderedListPattern.Match(line);
            if (ordered.Success)
            {
                FlushParagraph(paragraph);
                AddListItem(ordered.Groups[1].Value + ".", ordered.Groups[2].Value);
                index++;
                continue;
            }

            paragraph.Add(trimmed);
            index++;
        }
        FlushParagraph(paragraph);
    }

    private void FlushParagraph(List<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var block = CreateTextBlock(PrimaryText, 13, 21);
        AppendInline(block, string.Join(" ", lines));
        block.Margin = new Thickness(0, 0, 0, 9);
        Children.Add(block);
        lines.Clear();
    }

    private void AddHeading(string text, int level)
    {
        var block = CreateTextBlock(
            level <= 2 ? PrimaryText : Cyan,
            level switch
            {
                1 => 20,
                2 => 16.5,
                _ => 13.5
            },
            level <= 2 ? 27 : 21);
        block.FontWeight = FontWeights.Bold;
        block.Margin = new Thickness(0, level == 1 ? 4 : 2, 0, 9);
        AppendInline(block, text.Trim());
        Children.Add(block);
    }

    private void AddCodeBlock(string language, string code)
    {
        var panel = new StackPanel();
        if (!string.IsNullOrWhiteSpace(language))
        {
            panel.Children.Add(new TextBlock
            {
                Text = language.ToUpperInvariant(),
                Margin = new Thickness(0, 0, 0, 7),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 8.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = MutedText
            });
        }
        panel.Children.Add(new TextBlock
        {
            Text = code,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            LineHeight = 19,
            Foreground = new SolidColorBrush(Color.FromRgb(199, 211, 203)),
            TextWrapping = TextWrapping.Wrap
        });
        Children.Add(new Border
        {
            Margin = new Thickness(0, 2, 0, 11),
            Padding = new Thickness(12, 10, 12, 11),
            Background = CodeBackground,
            BorderBrush = CodeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Child = panel
        });
    }

    private void AddQuote(string text)
    {
        var block = CreateTextBlock(SecondaryText, 12.5, 20);
        block.FontStyle = FontStyles.Italic;
        AppendInline(block, text);
        Children.Add(new Border
        {
            Margin = new Thickness(0, 1, 0, 10),
            Padding = new Thickness(11, 7, 9, 7),
            Background = new SolidColorBrush(Color.FromArgb(70, 26, 36, 52)),
            BorderBrush = Cyan,
            BorderThickness = new Thickness(2, 0, 0, 0),
            Child = block
        });
    }

    private void AddListItem(string marker, string text)
    {
        var grid = new Grid { Margin = new Thickness(2, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new TextBlock
        {
            Text = marker,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = Cyan
        });
        var content = CreateTextBlock(PrimaryText, 12.5, 20);
        AppendInline(content, text);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        Children.Add(grid);
    }

    private void AddTable(IReadOnlyList<string> rows)
    {
        var cells = rows.Select(ParseTableRow).ToArray();
        var columns = Math.Max(1, cells.Max(row => row.Count));
        var grid = new Grid();
        for (var column = 0; column < columns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        }

        for (var rowIndex = 0; rowIndex < cells.Length; rowIndex++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var columnIndex = 0; columnIndex < columns; columnIndex++)
            {
                var content = columnIndex < cells[rowIndex].Count
                    ? cells[rowIndex][columnIndex]
                    : string.Empty;
                var text = CreateTextBlock(
                    rowIndex == 0 ? PrimaryText : SecondaryText,
                    rowIndex == 0 ? 10.5 : 10,
                    16);
                text.FontWeight = rowIndex == 0 ? FontWeights.SemiBold : FontWeights.Normal;
                AppendInline(text, content);
                var cell = new Border
                {
                    Padding = new Thickness(8, 6, 8, 7),
                    Background = rowIndex == 0
                        ? TableHeader
                        : rowIndex % 2 == 0
                            ? TableAlternate
                            : Brushes.Transparent,
                    BorderBrush = TableBorder,
                    BorderThickness = new Thickness(
                        columnIndex == 0 ? 1 : 0,
                        rowIndex == 0 ? 1 : 0,
                        1,
                        1),
                    Child = text
                };
                Grid.SetRow(cell, rowIndex);
                Grid.SetColumn(cell, columnIndex);
                grid.Children.Add(cell);
            }
        }

        Children.Add(new Border
        {
            Margin = new Thickness(0, 2, 0, 11),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = grid
        });
    }

    private static TextBlock CreateTextBlock(Brush foreground, double fontSize, double lineHeight)
        => new()
        {
            FontFamily = new FontFamily(
                "Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI"),
            FontSize = fontSize,
            FontWeight = FontWeights.Medium,
            LineHeight = lineHeight,
            Foreground = foreground,
            TextWrapping = TextWrapping.Wrap
        };

    private static void AppendInline(TextBlock block, string text)
    {
        var start = 0;
        foreach (Match match in InlineTokenPattern.Matches(text))
        {
            if (match.Index > start)
            {
                block.Inlines.Add(new Run(text[start..match.Index]));
            }

            var token = match.Value;
            if (token.StartsWith("**", StringComparison.Ordinal))
            {
                block.Inlines.Add(new Run(token[2..^2])
                {
                    FontWeight = FontWeights.SemiBold,
                    Foreground = PrimaryText
                });
            }
            else
            {
                block.Inlines.Add(new Run(token[1..^1])
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = Math.Max(9, block.FontSize - 1),
                    Foreground = Cyan,
                    Background = new SolidColorBrush(Color.FromArgb(80, 35, 52, 68))
                });
            }
            start = match.Index + match.Length;
        }
        if (start < text.Length)
        {
            block.Inlines.Add(new Run(text[start..]));
        }
    }

    private static int CountHeadingPrefix(string line)
    {
        var count = 0;
        while (count < line.Length && count < 4 && line[count] == '#')
        {
            count++;
        }
        return count > 0 && count < line.Length && line[count] == ' ' ? count : 0;
    }

    private static bool LooksLikeTableHeader(IReadOnlyList<string> lines, int index)
        => index + 1 < lines.Count
           && IsTableRow(lines[index])
           && lines[index + 1]
               .Trim()
               .Trim('|')
               .Split('|')
               .All(cell => cell.Trim().Trim(':').All(character => character == '-')
                            && cell.Trim().Trim(':').Length >= 3);

    private static bool IsTableRow(string line)
        => line.Contains('|')
           && line.Trim().Trim('|').Contains('|');

    private static IReadOnlyList<string> ParseTableRow(string line)
        => line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
}
