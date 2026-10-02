using System.Text;

namespace PdfAcrobat.Core.Text;

/// <summary>
/// Normalizes page text for searching while keeping a map back to original character indices.
/// <list type="bullet">
/// <item>Optional case folding and NFKC width folding (全角/半角, ① → 1, ｱ → ア).</item>
/// <item>Whitespace runs (including PDFium's generated line breaks) collapse to one space.</item>
/// <item>Whitespace between two CJK characters is dropped, because a line break inside a Japanese
/// sentence does not separate words.</item>
/// </list>
/// </summary>
public static class TextNormalizer
{
    public readonly record struct Result(string Text, int[] Map);

    public static Result Normalize(string text, bool ignoreCase, bool ignoreWidth)
    {
        var builder = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        var lastNonSpace = '\0';
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (IsSpace(c))
            {
                var runStart = i;
                while (i < text.Length && IsSpace(text[i]))
                {
                    i++;
                }

                var next = i < text.Length ? text[i] : '\0';
                var betweenCjk = TextGeometry.IsCjk(lastNonSpace) && next != '\0' && TextGeometry.IsCjk(next);
                if (builder.Length > 0 && next != '\0' && !betweenCjk)
                {
                    builder.Append(' ');
                    map.Add(runStart);
                }

                continue;
            }

            var folded = Fold(c, ignoreCase, ignoreWidth);
            foreach (var f in folded)
            {
                builder.Append(f);
                map.Add(i);
            }

            lastNonSpace = c;
            i++;
        }

        return new Result(builder.ToString(), map.ToArray());
    }

    private static bool IsSpace(char c) => char.IsWhiteSpace(c) || c == '　' || c == '\0';

    private static string Fold(char c, bool ignoreCase, bool ignoreWidth)
    {
        var s = c.ToString();
        if (ignoreWidth && !char.IsSurrogate(c))
        {
            s = s.Normalize(NormalizationForm.FormKC);
        }

        return ignoreCase ? s.ToLowerInvariant() : s;
    }
}
