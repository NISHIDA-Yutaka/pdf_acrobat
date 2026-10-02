using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using PdfAcrobat.Core.Documents;
using PdfAcrobat.Core.Import;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.App.Services;

/// <summary>Loads PDF files (asking for a password when needed) and images as page sources.</summary>
public static class SourceLoader
{
    /// <summary>Loads a PDF. Returns null when the user cancels the password prompt or the file cannot be read.</summary>
    public static PdfSource? LoadPdf(string path, string? password = null)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppServices.Dialogs.ShowError($"{Path.GetFileName(path)} を読み込めませんでした。\n{ex.Message}");
            return null;
        }

        var retry = false;
        while (true)
        {
            try
            {
                return PdfSource.Load(bytes, Path.GetFileName(path), Path.GetFullPath(path), password);
            }
            catch (PdfPasswordException)
            {
                password = AppServices.Dialogs.AskPassword(Path.GetFileName(path), retry);
                if (password is null)
                {
                    return null;
                }

                retry = true;
            }
            catch (PdfiumException ex)
            {
                AppServices.Dialogs.ShowError($"{Path.GetFileName(path)} を開けませんでした。\n{ex.Message}");
                return null;
            }
        }
    }

    /// <summary>What the clipboard holds that can become a PDF (an image wins over text).</summary>
    public readonly record struct ClipboardContent(BitmapSource? Image, string? Text);

    public static ClipboardContent PeekClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                return new ClipboardContent(image, null);
            }

            if (Clipboard.ContainsText() && Clipboard.GetText() is { } text && !string.IsNullOrWhiteSpace(text))
            {
                return new ClipboardContent(null, text);
            }
        }
        catch (COMException)
        {
            // The clipboard is locked by another application.
        }

        return default;
    }

    /// <summary>
    /// Converts the clipboard image (one page) or text (A4 pages) into a source. Tells the user and
    /// returns null when there is nothing usable.
    /// </summary>
    public static async Task<PdfSource?> LoadClipboardAsync(string name, string title)
    {
        var content = PeekClipboard();
        try
        {
            byte[] bytes;
            if (content.Image is { } image)
            {
                if (image.CanFreeze)
                {
                    image.Freeze();
                }

                bytes = await Task.Run(() => ImagePdfConverter.ConvertBitmaps([image]));
            }
            else if (content.Text is { } text)
            {
                bytes = await Task.Run(() => TextPdfConverter.Convert(text));
            }
            else
            {
                AppServices.Dialogs.ShowInfo("クリップボードに画像やテキストがありません。", title);
                return null;
            }

            return PdfSource.Load(bytes, name);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or COMException or PdfiumException)
        {
            AppServices.Dialogs.ShowError($"クリップボードの内容を PDF にできませんでした。\n{ex.Message}", title);
            return null;
        }
    }

    /// <summary>Loads PDFs and images (converted to PDF pages) in the given order.</summary>
    public static async Task<List<PdfSource>> LoadAsync(IReadOnlyList<string> paths, Action<string>? status = null)
    {
        var result = new List<PdfSource>();
        foreach (var path in paths)
        {
            try
            {
                if (ImagePdfConverter.IsImage(path))
                {
                    status?.Invoke($"{Path.GetFileName(path)} を変換しています...");
                    var bytes = await Task.Run(() => ImagePdfConverter.ConvertFiles([path]));
                    result.Add(PdfSource.Load(bytes, Path.ChangeExtension(Path.GetFileName(path), ".pdf")));
                }
                else if (LoadPdf(path) is { } source)
                {
                    result.Add(source);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
                                       or PdfiumException or System.Runtime.InteropServices.COMException or FileFormatException)
            {
                AppServices.Dialogs.ShowError($"{Path.GetFileName(path)} を読み込めませんでした。\n{ex.Message}");
            }
        }

        return result;
    }
}
