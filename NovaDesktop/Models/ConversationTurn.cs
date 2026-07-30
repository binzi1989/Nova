using System.Text.RegularExpressions;

namespace NovaDesktop.Models;

public sealed record ConversationChoice(
    string Id,
    string Ordinal,
    string Title,
    string Description,
    string Prompt);

public sealed record ConversationTurn(
    string Id,
    string TaskId,
    string Role,
    string Content,
    DateTimeOffset CreatedAt)
{
    public string Speaker => Role.Equals("user", StringComparison.OrdinalIgnoreCase)
        ? "你"
        : Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
            ? "NOVA"
            : "系统";

    public string TimeLabel => CreatedAt.ToLocalTime().ToString("HH:mm");

    public IReadOnlyList<ConversationChoice> Choices
        => Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
            ? ParseChoices(Content)
            : [];

    public bool HasChoices => Choices.Count >= 2;

    public string DisplayContent
    {
        get
        {
            var normalized = Normalize(Content);
            var explicitChoices = ExplicitChoicePattern.Matches(normalized);
            if (explicitChoices.Count >= 2)
            {
                return ExplicitChoicePattern
                    .Replace(normalized, string.Empty)
                    .Replace("\n\n\n", "\n\n", StringComparison.Ordinal)
                    .Trim();
            }

            var lines = normalized.Split('\n');
            var optionIndexes = lines
                .Select((line, index) => (Match: MatchLegacyChoice(line), Index: index))
                .Where(item => item.Match.Success)
                .Select(item => item.Index)
                .ToArray();
            if (optionIndexes.Length < 2)
            {
                return Content;
            }

            var first = optionIndexes[0];
            var separator = Array.FindIndex(
                lines,
                optionIndexes[^1] + 1,
                line => line.Trim() is "---" or "***" or "___");
            var visible = lines.Take(first).ToList();
            if (separator >= 0)
            {
                visible.AddRange(lines.Skip(separator + 1));
            }
            return string.Join('\n', visible).Trim();
        }
    }

    public string Preview
    {
        get
        {
            var normalized = string.Join(
                " ",
                Content.Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return normalized.Length <= 180 ? normalized : normalized[..180] + "…";
        }
    }

    private static readonly Regex ExplicitChoicePattern = new(
        @"(?m)^\s*\[\[NOVA_CHOICE\|(?<title>[^|\]\r\n]{1,56})\|(?<prompt>[^\]\r\n]{1,600})\]\]\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LegacyChoicePattern = new(
        @"^\s*(?:#{1,4}\s*)?(?:[-*]\s*)?(?:方向|选项|方案)\s*(?<number>[一二三四五六七八九十\dA-Fa-f]+)\s*[：:、.．\-]\s*(?<title>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static IReadOnlyList<ConversationChoice> ParseChoices(string content)
    {
        var normalized = Normalize(content);
        var explicitChoices = ExplicitChoicePattern.Matches(normalized)
            .Cast<Match>()
            .Take(6)
            .Select((match, index) =>
            {
                var title = match.Groups["title"].Value.Trim();
                var prompt = match.Groups["prompt"].Value.Trim();
                return new ConversationChoice(
                    $"choice-{index + 1}",
                    $"{index + 1:00}",
                    title,
                    prompt.Length <= 150 ? prompt : prompt[..150] + "…",
                    prompt);
            })
            .ToArray();
        if (explicitChoices.Length >= 2)
        {
            return explicitChoices;
        }

        var lines = normalized.Split('\n');
        var headers = lines
            .Select((line, index) => (Match: MatchLegacyChoice(line), Index: index))
            .Where(item => item.Match.Success)
            .Take(6)
            .ToArray();
        if (headers.Length < 2)
        {
            return [];
        }

        var choices = new List<ConversationChoice>();
        for (var index = 0; index < headers.Length; index++)
        {
            var header = headers[index];
            var end = index + 1 < headers.Length ? headers[index + 1].Index : lines.Length;
            var descriptionLines = lines
                .Skip(header.Index + 1)
                .Take(end - header.Index - 1)
                .TakeWhile(line => line.Trim() is not ("---" or "***" or "___"))
                .Select(line => line.Trim().TrimStart('-', '*', '•').Trim())
                .Where(line => line.Length > 0)
                .Take(3)
                .ToArray();
            var title = header.Match.Groups["title"].Value.Trim();
            var description = string.Join(" · ", descriptionLines);
            if (string.IsNullOrWhiteSpace(description))
            {
                description = "按这个方向继续细化并执行";
            }
            var prompt =
                $"我选择「{title}」。请按这个方向继续，先确认关键边界，然后给出并执行完整方案。";
            choices.Add(new ConversationChoice(
                $"choice-{index + 1}",
                $"{index + 1:00}",
                title,
                description.Length <= 150 ? description : description[..150] + "…",
                prompt));
        }
        return choices;
    }

    private static string Normalize(string content)
        => (content ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private static Match MatchLegacyChoice(string line)
        => LegacyChoicePattern.Match(
            line.Replace("**", string.Empty, StringComparison.Ordinal));
}
