using System.Globalization;
using System.IO;
using System.Text;
using PdfAcrobat.Core.Fonts;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SharpPdfDocument = PdfSharp.Pdf.PdfDocument;

namespace PdfAcrobat.Core.Import;

/// <summary>
/// Lays out plain text on pages with an embedded Japanese font (used for "create PDF from the clipboard").
/// Lines wrap at the page width: Latin words break at spaces, other text at any character, and closing
/// punctuation such as 「、」「。」 never starts a line.
/// </summary>
public static class TextPdfConverter
{
    private const string NoLineStart = "、。，．,.）)]｝}〕〉》」』】〙〗〟’”｠»ゝゞーァィゥェォッャュョヮヵヶぁぃぅぇぉっゃゅょゎゕゖㇰㇱㇲㇳㇴㇵㇶㇷㇸㇹㇺㇻㇼㇽㇾㇿ々〻‐゠–〜～？！?!‼⁇⁈⁉・：；:;";

    private static readonly string[] FontCandidates = ["Yu Gothic", "Meiryo", "MS Gothic", "Arial"];

    public static byte[] Convert(string text, double pageWidth = 595.28, double pageHeight = 841.89, double fontSize = 10.5)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("変換するテキストがありません。");
        }

        WindowsFontResolver.Install();
        var font = CreateFont(fontSize);
        var margin = 20 * 72 / 25.4; // 20 mm
        var lineHeight = fontSize * 1.65;
        var maxWidth = pageWidth - 2 * margin;
        var linesPerPage = Math.Max(1, (int)((pageHeight - 2 * margin) / lineHeight));

        var measure = XGraphics.CreateMeasureContext(new XSize(pageWidth, pageHeight), XGraphicsUnit.Point, XPageDirection.Downwards);
        var widths = new Dictionary<string, double>();
        double WidthOf(string element)
        {
            if (!widths.TryGetValue(element, out var width))
            {
                width = measure.MeasureString(element, font).Width;
                widths[element] = width;
            }

            return width;
        }

        var lines = new List<string>();
        foreach (var paragraph in Normalize(text).Split('\n'))
        {
            Wrap(paragraph, maxWidth, WidthOf, lines);
        }

        // Drop trailing empty lines so that no blank page is produced at the end.
        while (lines.Count > 1 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var document = new SharpPdfDocument();
        for (var start = 0; start < lines.Count; start += linesPerPage)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(pageWidth);
            page.Height = XUnit.FromPoint(pageHeight);
            using var gfx = XGraphics.FromPdfPage(page);
            for (var k = 0; k < linesPerPage && start + k < lines.Count; k++)
            {
                var line = lines[start + k];
                if (line.Length > 0)
                {
                    gfx.DrawString(line, font, XBrushes.Black, new XRect(margin, margin + k * lineHeight, maxWidth, lineHeight), XStringFormats.TopLeft);
                }
            }
        }

        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Unifies line breaks, expands tabs and removes other control characters.</summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (c == '\t')
            {
                builder.Append("    ");
            }
            else if (c == '\n' || !char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Breaks one paragraph into lines that fit <paramref name="maxWidth"/>.</summary>
    internal static void Wrap(string paragraph, double maxWidth, Func<string, double> widthOf, List<string> lines)
    {
        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(paragraph);
        while (enumerator.MoveNext())
        {
            elements.Add(enumerator.GetTextElement());
        }

        if (elements.Count == 0)
        {
            lines.Add(string.Empty);
            return;
        }

        var start = 0;
        while (start < elements.Count)
        {
            var width = 0.0;
            var end = start;
            var lastSpace = -1;
            while (end < elements.Count)
            {
                var w = widthOf(elements[end]);
                if (width + w > maxWidth && end > start)
                {
                    break;
                }

                if (elements[end] == " ")
                {
                    lastSpace = end;
                }

                width += w;
                end++;
            }

            if (end < elements.Count)
            {
                if (IsWordCharacter(elements[end]) && end > 0 && IsWordCharacter(elements[end - 1]) && lastSpace > start)
                {
                    // Do not split a Latin word: break after the last space instead.
                    end = lastSpace + 1;
                }
                else if (NoLineStart.Contains(elements[end], StringComparison.Ordinal) && end - start > 1)
                {
                    // Keep closing punctuation on the current line (it may hang slightly into the margin).
                    end++;
                }
            }

            lines.Add(string.Concat(elements.Skip(start).Take(end - start)).TrimEnd());
            start = end;
            while (start < elements.Count && elements[start] == " " && start > 0)
            {
                start++;
            }
        }
    }

    private static bool IsWordCharacter(string element) =>
        element.Length == 1 && element[0] < 0x2000 && (char.IsLetterOrDigit(element[0]) || element[0] is '\'' or '-' or '_');

    private static XFont CreateFont(double size)
    {
        var resolver = PdfSharp.Fonts.GlobalFontSettings.FontResolver;
        var family = FontCandidates.FirstOrDefault(f => resolver?.ResolveTypeface(f, false, false) is not null)
            ?? throw new InvalidOperationException("テキストの描画に使えるフォントが見つかりません。");
        return new XFont(family, size, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
    }
}
