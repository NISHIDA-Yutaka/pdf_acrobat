using PdfAcrobat.Pdfium;

namespace PdfAcrobat.Core.Text;

/// <summary>Hit-testing, selection rectangles and word/line boundaries on a <see cref="PdfTextSnapshot"/>.</summary>
public static class TextGeometry
{
    /// <summary>Character boxes of a range merged into one rectangle per line (or column for vertical text).</summary>
    public static IReadOnlyList<PdfRect> GetRangeRects(PdfTextSnapshot snapshot, int start, int count)
    {
        var result = new List<PdfRect>();
        var end = Math.Min(snapshot.Length, start + count);
        PdfRect? current = null;
        for (var i = Math.Max(0, start); i < end; i++)
        {
            var box = snapshot.Boxes[i];
            if (box.IsEmpty)
            {
                continue;
            }

            if (current is not { } rect)
            {
                current = box;
            }
            else if (OnSameLine(rect, box))
            {
                current = rect.Union(box);
            }
            else
            {
                result.Add(rect);
                current = box;
            }
        }

        if (current is { } last)
        {
            result.Add(last);
        }

        return result;
    }

    private static bool OnSameLine(PdfRect line, PdfRect box)
    {
        var verticalOverlap = Math.Min(line.Top, box.Top) - Math.Max(line.Bottom, box.Bottom);
        if (verticalOverlap > 0.5 * Math.Min(line.Height, box.Height))
        {
            // Horizontal text: same row, and the next glyph does not jump far back to the left.
            return box.Left >= line.Left - box.Width;
        }

        // Vertical text: the run so far is a single column and the glyph sits directly below it.
        // (A horizontal line whose first glyph happens to be under the previous line must not match.)
        var isColumn = line.Width <= box.Width * 1.6;
        var horizontalOverlap = Math.Min(line.Right, box.Right) - Math.Max(line.Left, box.Left);
        var gap = line.Bottom - box.Top;
        return isColumn
            && horizontalOverlap > 0.5 * Math.Min(line.Width, box.Width)
            && gap > -box.Height
            && gap < box.Height * 1.5;
    }

    /// <summary>Index of the character whose box contains the point (with tolerance), or -1.</summary>
    public static int HitTest(PdfTextSnapshot snapshot, PdfPoint point, double tolerance)
    {
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < snapshot.Length; i++)
        {
            var box = snapshot.Boxes[i];
            if (box.IsEmpty)
            {
                continue;
            }

            if (box.Contains(point))
            {
                return i;
            }

            var d = Distance(box, point);
            if (d <= tolerance && d < bestDistance)
            {
                best = i;
                bestDistance = d;
            }
        }

        return best;
    }

    /// <summary>
    /// Nearest character to a point, preferring characters on the same line. Used while extending a
    /// selection so that dragging past the end of a line selects up to the line end.
    /// </summary>
    public static int Nearest(PdfTextSnapshot snapshot, PdfPoint point)
    {
        var bestOnLine = -1;
        var bestOnLineDistance = double.MaxValue;
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < snapshot.Length; i++)
        {
            var box = snapshot.Boxes[i];
            if (box.IsEmpty)
            {
                continue;
            }

            if (box.Contains(point))
            {
                return i;
            }

            if (point.Y >= box.Bottom && point.Y <= box.Top)
            {
                var dx = point.X < box.Left ? box.Left - point.X : point.X - box.Right;
                if (dx < bestOnLineDistance)
                {
                    bestOnLine = i;
                    bestOnLineDistance = dx;
                }
            }

            var d = Distance(box, point);
            if (d < bestDistance)
            {
                best = i;
                bestDistance = d;
            }
        }

        return bestOnLine >= 0 ? bestOnLine : best;
    }

    private static double Distance(PdfRect box, PdfPoint p)
    {
        var dx = Math.Max(Math.Max(box.Left - p.X, 0), p.X - box.Right);
        var dy = Math.Max(Math.Max(box.Bottom - p.Y, 0), p.Y - box.Top);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Range of the word around <paramref name="index"/> (same script class, so Japanese runs work).</summary>
    public static PdfTextRange WordAt(PdfTextSnapshot snapshot, int index)
    {
        if (index < 0 || index >= snapshot.Length)
        {
            return new PdfTextRange(Math.Max(index, 0), 0);
        }

        var text = snapshot.Text;
        var cls = Classify(text[index]);
        if (cls == CharClass.Space)
        {
            return new PdfTextRange(index, 1);
        }

        var start = index;
        while (start > 0 && Classify(text[start - 1]) == cls)
        {
            start--;
        }

        var end = index + 1;
        while (end < text.Length && Classify(text[end]) == cls)
        {
            end++;
        }

        return new PdfTextRange(start, end - start);
    }

    /// <summary>Range of the line containing <paramref name="index"/> (delimited by generated line breaks).</summary>
    public static PdfTextRange LineAt(PdfTextSnapshot snapshot, int index)
    {
        var text = snapshot.Text;
        if (index < 0 || index >= text.Length)
        {
            return new PdfTextRange(Math.Max(index, 0), 0);
        }

        var start = index;
        while (start > 0 && text[start - 1] is not ('\n' or '\r'))
        {
            start--;
        }

        var end = index;
        while (end < text.Length && text[end] is not ('\n' or '\r'))
        {
            end++;
        }

        return new PdfTextRange(start, end - start);
    }

    /// <summary>Text of a range with PDFium's generated CR/LF pairs normalized to Environment.NewLine.</summary>
    public static string GetText(PdfTextSnapshot snapshot, int start, int count)
    {
        if (count <= 0 || start >= snapshot.Length)
        {
            return string.Empty;
        }

        var end = Math.Min(snapshot.Length, start + count);
        var builder = new System.Text.StringBuilder(end - start);
        for (var i = Math.Max(0, start); i < end; i++)
        {
            var c = snapshot.Text[i];
            if (c == '\r')
            {
                continue;
            }

            if (c == '\n')
            {
                builder.Append(Environment.NewLine);
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    internal enum CharClass
    {
        Space,
        Word,
        Hiragana,
        Katakana,
        Kanji,
        FullWidthAlnum,
        Other,
    }

    internal static CharClass Classify(char c)
    {
        if (char.IsWhiteSpace(c) || c == '　')
        {
            return CharClass.Space;
        }

        if (c is >= 'ぁ' and <= 'ゟ')
        {
            return CharClass.Hiragana;
        }

        if (c is >= '゠' and <= 'ヿ' or >= 'ｦ' and <= 'ﾟ' or 'ー')
        {
            return CharClass.Katakana;
        }

        if (c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿' or '々')
        {
            return CharClass.Kanji;
        }

        if (c is >= '０' and <= '９' or >= 'Ａ' and <= 'Ｚ' or >= 'ａ' and <= 'ｚ')
        {
            return CharClass.FullWidthAlnum;
        }

        return char.IsLetterOrDigit(c) || c == '_' ? CharClass.Word : CharClass.Other;
    }

    internal static bool IsCjk(char c) => Classify(c) is CharClass.Hiragana or CharClass.Katakana or CharClass.Kanji
        || c is >= '　' and <= '〿' or >= '＀' and <= '￯';
}
