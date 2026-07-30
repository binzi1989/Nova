using System.Text;

namespace NovaDesktop.Services;

public sealed record TextPatchPreview(
    string RelativePath,
    string UnifiedDiff,
    int Additions,
    int Deletions,
    bool IsNewFile,
    bool IsTruncated)
{
    public string Summary =>
        $"{(IsNewFile ? "新文件" : "修改文件")} · +{Additions:N0} / -{Deletions:N0}";
}

public sealed class TextPatchPreviewService
{
    private const int ContextLines = 3;
    private const int MaximumDiffCharacters = 80_000;
    private const long MaximumLcsCells = 2_000_000;

    public TextPatchPreview Create(
        string relativePath,
        string original,
        string proposed,
        bool originalExists = true)
    {
        var oldLines = SplitLines(original);
        var newLines = SplitLines(proposed);
        var operations = CreateOperations(oldLines, newLines);
        var additions = operations.Count(item => item.Kind == PatchLineKind.Added);
        var deletions = operations.Count(item => item.Kind == PatchLineKind.Removed);
        var diff = BuildUnifiedDiff(relativePath, operations, !originalExists);
        if (original != proposed
            && additions == 0
            && deletions == 0)
        {
            diff += Environment.NewLine
                    + "@@ newline metadata @@"
                    + Environment.NewLine
                    + " 文件内容行相同，但结尾换行状态发生变化。"
                    + Environment.NewLine;
        }
        var truncated = diff.Length > MaximumDiffCharacters;
        if (truncated)
        {
            diff = diff[..MaximumDiffCharacters]
                   + Environment.NewLine
                   + "… PATCH PREVIEW TRUNCATED; full content is still subject to the same approval …";
        }

        return new TextPatchPreview(
            relativePath,
            diff,
            additions,
            deletions,
            !originalExists,
            truncated);
    }

    private static IReadOnlyList<PatchLine> CreateOperations(
        IReadOnlyList<string> oldLines,
        IReadOnlyList<string> newLines)
    {
        if ((long)(oldLines.Count + 1) * (newLines.Count + 1) > MaximumLcsCells)
        {
            return CreatePrefixSuffixOperations(oldLines, newLines);
        }

        var lengths = new int[oldLines.Count + 1, newLines.Count + 1];
        for (var oldIndex = oldLines.Count - 1; oldIndex >= 0; oldIndex--)
        {
            for (var newIndex = newLines.Count - 1; newIndex >= 0; newIndex--)
            {
                lengths[oldIndex, newIndex] = oldLines[oldIndex] == newLines[newIndex]
                    ? lengths[oldIndex + 1, newIndex + 1] + 1
                    : Math.Max(lengths[oldIndex + 1, newIndex], lengths[oldIndex, newIndex + 1]);
            }
        }

        var result = new List<PatchLine>(oldLines.Count + newLines.Count);
        var i = 0;
        var j = 0;
        while (i < oldLines.Count && j < newLines.Count)
        {
            if (oldLines[i] == newLines[j])
            {
                result.Add(new PatchLine(PatchLineKind.Context, oldLines[i]));
                i++;
                j++;
            }
            else if (lengths[i + 1, j] >= lengths[i, j + 1])
            {
                result.Add(new PatchLine(PatchLineKind.Removed, oldLines[i++]));
            }
            else
            {
                result.Add(new PatchLine(PatchLineKind.Added, newLines[j++]));
            }
        }

        while (i < oldLines.Count)
        {
            result.Add(new PatchLine(PatchLineKind.Removed, oldLines[i++]));
        }
        while (j < newLines.Count)
        {
            result.Add(new PatchLine(PatchLineKind.Added, newLines[j++]));
        }
        return result;
    }

    private static IReadOnlyList<PatchLine> CreatePrefixSuffixOperations(
        IReadOnlyList<string> oldLines,
        IReadOnlyList<string> newLines)
    {
        var prefix = 0;
        while (prefix < oldLines.Count
               && prefix < newLines.Count
               && oldLines[prefix] == newLines[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldLines.Count - prefix
               && suffix < newLines.Count - prefix
               && oldLines[oldLines.Count - suffix - 1] == newLines[newLines.Count - suffix - 1])
        {
            suffix++;
        }

        var result = new List<PatchLine>(oldLines.Count + newLines.Count);
        result.AddRange(oldLines.Take(prefix).Select(line => new PatchLine(PatchLineKind.Context, line)));
        result.AddRange(oldLines
            .Skip(prefix)
            .Take(oldLines.Count - prefix - suffix)
            .Select(line => new PatchLine(PatchLineKind.Removed, line)));
        result.AddRange(newLines
            .Skip(prefix)
            .Take(newLines.Count - prefix - suffix)
            .Select(line => new PatchLine(PatchLineKind.Added, line)));
        result.AddRange(oldLines
            .Skip(oldLines.Count - suffix)
            .Select(line => new PatchLine(PatchLineKind.Context, line)));
        return result;
    }

    private static string BuildUnifiedDiff(
        string relativePath,
        IReadOnlyList<PatchLine> operations,
        bool isNewFile)
    {
        var builder = new StringBuilder();
        builder.AppendLine(isNewFile ? "--- /dev/null" : $"--- a/{relativePath}");
        builder.AppendLine($"+++ b/{relativePath}");

        var changedIndexes = operations
            .Select((line, index) => (line, index))
            .Where(item => item.line.Kind != PatchLineKind.Context)
            .Select(item => item.index)
            .ToArray();
        if (changedIndexes.Length == 0)
        {
            builder.AppendLine("@@ no changes @@");
            return builder.ToString();
        }

        var ranges = new List<(int Start, int End)>();
        var rangeStart = Math.Max(0, changedIndexes[0] - ContextLines);
        var rangeEnd = Math.Min(operations.Count - 1, changedIndexes[0] + ContextLines);
        foreach (var changedIndex in changedIndexes.Skip(1))
        {
            var candidateStart = Math.Max(0, changedIndex - ContextLines);
            var candidateEnd = Math.Min(operations.Count - 1, changedIndex + ContextLines);
            if (candidateStart <= rangeEnd + 1)
            {
                rangeEnd = Math.Max(rangeEnd, candidateEnd);
            }
            else
            {
                ranges.Add((rangeStart, rangeEnd));
                rangeStart = candidateStart;
                rangeEnd = candidateEnd;
            }
        }
        ranges.Add((rangeStart, rangeEnd));

        foreach (var (start, end) in ranges)
        {
            var oldStart = 1 + operations.Take(start).Count(line => line.Kind != PatchLineKind.Added);
            var newStart = 1 + operations.Take(start).Count(line => line.Kind != PatchLineKind.Removed);
            var oldCount = operations.Skip(start).Take(end - start + 1)
                .Count(line => line.Kind != PatchLineKind.Added);
            var newCount = operations.Skip(start).Take(end - start + 1)
                .Count(line => line.Kind != PatchLineKind.Removed);
            builder.AppendLine($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@");
            for (var index = start; index <= end; index++)
            {
                var line = operations[index];
                var prefix = line.Kind switch
                {
                    PatchLineKind.Added => '+',
                    PatchLineKind.Removed => '-',
                    _ => ' '
                };
                builder.Append(prefix).AppendLine(line.Text);
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> SplitLines(string value)
    {
        if (value.Length == 0)
        {
            return [];
        }

        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        return lines;
    }

    private sealed record PatchLine(PatchLineKind Kind, string Text);

    private enum PatchLineKind
    {
        Context,
        Added,
        Removed
    }
}
