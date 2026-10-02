using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using PdfSharp.Fonts;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfGlyphTypeface = System.Windows.Media.GlyphTypeface;
using WpfStyleSimulations = System.Windows.Media.StyleSimulations;
using WpfTypeface = System.Windows.Media.Typeface;

namespace PdfAcrobat.Core.Fonts;

/// <summary>
/// PDFsharp font resolver backed by the fonts installed in Windows. Uses WPF to pick the right
/// file and face (including faces inside .ttc collections) and hands PDFsharp a standalone TTF,
/// so Japanese fonts such as Yu Gothic or Meiryo can be embedded as subsets.
/// </summary>
public sealed class WindowsFontResolver : IFontResolver
{
    private static readonly object InitLock = new();
    private static bool _installed;

    private readonly ConcurrentDictionary<string, byte[]> _fontData = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers the resolver with PDFsharp. Safe to call more than once.</summary>
    public static void Install()
    {
        lock (InitLock)
        {
            if (_installed)
            {
                return;
            }

            GlobalFontSettings.FontResolver = new WindowsFontResolver();
            _installed = true;
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        var typeface = new WpfTypeface(
            new WpfFontFamily(familyName),
            italic ? FontStyles.Italic : FontStyles.Normal,
            bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);
        if (!typeface.TryGetGlyphTypeface(out var glyphTypeface) || !MatchesFamily(typeface, familyName))
        {
            return null;
        }

        var (path, faceIndex) = GetFontLocation(glyphTypeface);
        var key = $"{path}#{faceIndex}";
        var simulateBold = (glyphTypeface.StyleSimulations & WpfStyleSimulations.BoldSimulation) != 0;
        var simulateItalic = (glyphTypeface.StyleSimulations & WpfStyleSimulations.ItalicSimulation) != 0;
        return new FontResolverInfo(key, simulateBold, simulateItalic);
    }

    public byte[]? GetFont(string faceName)
    {
        return _fontData.GetOrAdd(faceName, static key =>
        {
            var separator = key.LastIndexOf('#');
            var path = key[..separator];
            var faceIndex = int.Parse(key[(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            var bytes = File.ReadAllBytes(path);
            return TrueTypeCollection.IsCollection(bytes) ? TrueTypeCollection.ExtractFace(bytes, faceIndex) : bytes;
        });
    }

    /// <summary>WPF silently falls back to a default font for unknown names; reject that case.</summary>
    private static bool MatchesFamily(WpfTypeface typeface, string familyName) =>
        typeface.FontFamily.FamilyNames.Values.Any(name => string.Equals(name, familyName, StringComparison.OrdinalIgnoreCase));

    private static (string Path, int FaceIndex) GetFontLocation(WpfGlyphTypeface glyphTypeface)
    {
        var uri = glyphTypeface.FontUri;
        var faceIndex = 0;
        if (!string.IsNullOrEmpty(uri.Fragment) && int.TryParse(uri.Fragment.TrimStart('#'), out var index))
        {
            faceIndex = index;
        }

        return (uri.LocalPath, faceIndex);
    }
}
