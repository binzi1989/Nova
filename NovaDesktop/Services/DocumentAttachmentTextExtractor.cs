using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using b2xtranslator.txt;

namespace NovaDesktop.Services;

internal sealed record ExtractedDocumentText(
    string Text,
    string Format,
    int? PageCount = null);

internal static class DocumentAttachmentTextExtractor
{
    public static Task<ExtractedDocumentText> ExtractAsync(
        string path,
        CancellationToken cancellationToken)
        => Task.Run(() => Extract(path, cancellationToken), cancellationToken);

    private static ExtractedDocumentText Extract(
        string path,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return extension switch
            {
                ".pdf" => ExtractPdf(path, cancellationToken),
                ".doc" => ExtractLegacyWord(path, cancellationToken),
                ".docx" or ".docm" or ".dotx" or ".dotm"
                    => ExtractOpenXmlWord(path, cancellationToken),
                _ => throw new InvalidOperationException($"不支持的文档格式：{extension}")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} 无法解析。请确认文件没有损坏、加密或设置打开密码。",
                exception);
        }
    }

    private static ExtractedDocumentText ExtractPdf(
        string path,
        CancellationToken cancellationToken)
    {
        using var document = PdfDocument.Open(path);
        var text = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (text.Length > 0)
            {
                text.AppendLine().AppendLine($"--- 第 {page.Number} 页 ---");
            }
            text.Append(page.Text);
        }
        return new ExtractedDocumentText(
            text.ToString(),
            "PDF",
            document.NumberOfPages);
    }

    private static ExtractedDocumentText ExtractOpenXmlWord(
        string path,
        CancellationToken cancellationToken)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var output = new StringBuilder();
        AppendPart(document.MainDocumentPart?.Document, output, cancellationToken);

        if (document.MainDocumentPart is { } mainPart)
        {
            foreach (var header in mainPart.HeaderParts)
            {
                AppendPart(header.Header, output, cancellationToken);
            }
            foreach (var footer in mainPart.FooterParts)
            {
                AppendPart(footer.Footer, output, cancellationToken);
            }
            AppendPart(mainPart.FootnotesPart?.Footnotes, output, cancellationToken);
            AppendPart(mainPart.EndnotesPart?.Endnotes, output, cancellationToken);
        }

        return new ExtractedDocumentText(output.ToString(), "Word Open XML");
    }

    private static void AppendPart(
        DocumentFormat.OpenXml.OpenXmlElement? root,
        StringBuilder output,
        CancellationToken cancellationToken)
    {
        if (root is null)
        {
            return;
        }
        foreach (var paragraph in root.Descendants<Paragraph>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = string.Concat(paragraph.Descendants<Text>().Select(item => item.Text));
            if (!string.IsNullOrWhiteSpace(line))
            {
                output.AppendLine(line.Trim());
            }
        }
    }

    private static ExtractedDocumentText ExtractLegacyWord(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = DocTextExtractor.ExtractTextFromFile(path);
        cancellationToken.ThrowIfCancellationRequested();
        return new ExtractedDocumentText(text ?? string.Empty, "Word 97-2003");
    }
}
