using System.Globalization;

namespace PdfAcrobat.Core.Documents;

/// <summary>Parses page range expressions such as "1-3, 5, 8-" (one-based, inclusive).</summary>
public static class PageRangeParser
{
    /// <summary>
    /// Returns zero-based page indices in the order written. Open ranges ("8-", "-3") extend to the
    /// document bounds. Returns false when the text is malformed or out of range.
    /// </summary>
    public static bool TryParse(string? text, int pageCount, out IReadOnlyList<int> pages)
    {
        var result = new List<int>();
        pages = result;
        if (string.IsNullOrWhiteSpace(text) || pageCount <= 0)
        {
            return false;
        }

        var normalized = text.Normalize(System.Text.NormalizationForm.FormKC)
            .Replace('、', ',')
            .Replace('，', ',')
            .Replace('～', '-')
            .Replace('〜', '-')
            .Replace('–', '-');
        foreach (var rawPart in normalized.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart.Trim();
            var dash = part.IndexOf('-');
            int start, end;
            if (dash < 0)
            {
                if (!TryNumber(part, out start))
                {
                    return false;
                }

                end = start;
            }
            else
            {
                var left = part[..dash].Trim();
                var right = part[(dash + 1)..].Trim();
                start = 1;
                end = pageCount;
                if (left.Length > 0 && !TryNumber(left, out start))
                {
                    return false;
                }

                if (right.Length > 0 && !TryNumber(right, out end))
                {
                    return false;
                }
            }

            if (start < 1 || end < 1 || start > pageCount || end > pageCount)
            {
                return false;
            }

            var step = start <= end ? 1 : -1;
            for (var p = start; p != end + step; p += step)
            {
                result.Add(p - 1);
            }
        }

        return result.Count > 0;
    }

    private static bool TryNumber(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>Formats zero-based indices as a compact one-based range string ("1-3, 5").</summary>
    public static string Format(IEnumerable<int> pageIndices)
    {
        var sorted = pageIndices.Distinct().Order().ToList();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var start = sorted[i];
            while (i + 1 < sorted.Count && sorted[i + 1] == sorted[i] + 1)
            {
                i++;
            }

            parts.Add(start == sorted[i] ? $"{start + 1}" : $"{start + 1}-{sorted[i] + 1}");
        }

        return string.Join(", ", parts);
    }
}
