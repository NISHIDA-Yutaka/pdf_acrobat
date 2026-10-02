using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfAcrobat.App.Services;
using PdfAcrobat.App.Views;
using PdfAcrobat.App.Views.Dialogs;
using PdfAcrobat.Core.Documents;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        Home = new HomeViewModel(this);
        Tabs.Add(Home);
        SelectedTab = Home;
    }

    public HomeViewModel Home { get; }

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveDocument), nameof(WindowTitle))]
    public partial TabViewModel? SelectedTab { get; set; }

    public DocumentViewModel? ActiveDocument => SelectedTab as DocumentViewModel;

    public IEnumerable<DocumentViewModel> Documents => Tabs.OfType<DocumentViewModel>();

    public string WindowTitle => ActiveDocument is { } doc ? $"{doc.Title} - {AppInfo.DisplayName}" : AppInfo.DisplayName;

    [ObservableProperty]
    public partial bool IsDarkTheme { get; set; } = ThemeService.IsDark;

    partial void OnSelectedTabChanged(TabViewModel? value)
    {
        foreach (var tab in Tabs)
        {
            tab.IsSelected = ReferenceEquals(tab, value);
        }
    }

    public void OnDocumentTitleChanged(DocumentViewModel document)
    {
        if (ReferenceEquals(document, SelectedTab))
        {
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    // ---- Opening & closing -----------------------------------------------------------------

    [RelayCommand]
    private void OpenFile()
    {
        var files = AppServices.Dialogs.PickPdfFiles();
        if (files.Length > 0)
        {
            OpenFiles(files);
        }
    }

    public void OpenFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            OpenFile(path);
        }
    }

    /// <summary>Opens a file (or activates it when already open). Returns null on failure or cancel.</summary>
    public DocumentViewModel? OpenFile(string path, string? password = null)
    {
        var fullPath = Path.GetFullPath(path);
        var existing = Documents.FirstOrDefault(d => string.Equals(d.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedTab = existing;
            return existing;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppServices.Dialogs.ShowError($"{Path.GetFileName(fullPath)} を読み込めませんでした。\n{ex.Message}");
            return null;
        }

        var retry = false;
        while (true)
        {
            try
            {
                var source = PdfSource.Load(bytes, Path.GetFileName(fullPath), fullPath, password);
                var document = new DocumentViewModel(new DocumentSession(source), this);
                var recent = AppServices.Settings.Settings.RecentFiles
                    .FirstOrDefault(r => string.Equals(r.Path, fullPath, StringComparison.OrdinalIgnoreCase));
                if (recent is not null && recent.PageIndex > 0 && recent.PageIndex < document.PageCount)
                {
                    document.CurrentPageIndex = recent.PageIndex;
                }

                Tabs.Add(document);
                SelectedTab = document;
                AppServices.Settings.AddRecentFile(fullPath, source.PageCount, null);
                _ = SaveRecentThumbnailAsync(fullPath, source);
                return document;
            }
            catch (PdfPasswordException)
            {
                password = AppServices.Dialogs.AskPassword(Path.GetFileName(fullPath), retry);
                if (password is null)
                {
                    return null;
                }

                retry = true;
            }
            catch (PdfiumException ex)
            {
                AppServices.Dialogs.ShowError($"{Path.GetFileName(fullPath)} を開けませんでした。\n{ex.Message}");
                return null;
            }
        }
    }

    private static async Task SaveRecentThumbnailAsync(string path, PdfSource source)
    {
        try
        {
            var size = source.PageSizes[0];
            const int Width = 160;
            var height = (int)Math.Round(Width * size.Height / Math.Max(1, size.Width));
            var bitmap = await AppServices.Render.RenderAsync(source, 0, 0, Width, height, RenderPriority.Background, CancellationToken.None);
            if (bitmap is null)
            {
                return;
            }

            var folder = Path.Combine(AppInfo.LocalDataFolder, "thumbnails");
            Directory.CreateDirectory(folder);
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
            var file = Path.Combine(folder, hash + ".png");
            await Task.Run(() =>
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(file);
                encoder.Save(stream);
            });

            var entry = AppServices.Settings.Settings.RecentFiles
                .FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                entry.ThumbnailPath = file;
                AppServices.Settings.Save();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PdfiumException)
        {
            // Thumbnails are cosmetic.
        }
    }

    [RelayCommand]
    public void CloseTab(TabViewModel? tab)
    {
        if (tab is not DocumentViewModel document)
        {
            return;
        }

        if (!ConfirmDiscard(document))
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (document.FilePath is { } path)
        {
            AppServices.Settings.UpdateLastPage(path, document.CurrentPageIndex);
            AppServices.Settings.Save();
        }

        Tabs.Remove(tab);
        document.Dispose();
        if (ReferenceEquals(SelectedTab, tab) || SelectedTab is null)
        {
            SelectedTab = Tabs[Math.Clamp(index - 1, 0, Tabs.Count - 1)];
        }
    }

    [RelayCommand]
    private void CloseActiveTab() => CloseTab(SelectedTab);

    [RelayCommand]
    private void NextTab() => SelectedTab = Tabs[(Tabs.IndexOf(SelectedTab!) + 1) % Tabs.Count];

    [RelayCommand]
    private void PreviousTab() => SelectedTab = Tabs[(Tabs.IndexOf(SelectedTab!) - 1 + Tabs.Count) % Tabs.Count];

    [RelayCommand]
    private void GoHome() => SelectedTab = Home;

    /// <summary>Asks whether unsaved changes may be discarded. Returns false to cancel closing.</summary>
    private bool ConfirmDiscard(DocumentViewModel document)
    {
        if (!document.Session.IsModified)
        {
            return true;
        }

        SelectedTab = document;
        var answer = AppServices.Dialogs.AskYesNoCancel($"「{document.Session.DisplayName}」への変更を保存しますか？", "変更の保存");
        if (answer is null)
        {
            return false;
        }

        if (answer == true)
        {
            document.SaveAsCommand.Execute(null);
            return !document.Session.IsModified;
        }

        return true;
    }

    /// <summary>Called when the main window is closing.</summary>
    public bool PrepareToExit()
    {
        foreach (var document in Documents.ToList())
        {
            if (!ConfirmDiscard(document))
            {
                return false;
            }
        }

        foreach (var document in Documents)
        {
            if (document.FilePath is { } path)
            {
                AppServices.Settings.UpdateLastPage(path, document.CurrentPageIndex);
            }
        }

        AppServices.Settings.Save();
        return true;
    }

    // ---- Misc -------------------------------------------------------------------------------

    [RelayCommand]
    private void ToggleTheme()
    {
        var theme = ThemeService.IsDark ? AppTheme.Light : AppTheme.Dark;
        ThemeService.Apply(theme);
        IsDarkTheme = ThemeService.IsDark;
        AppServices.Settings.Settings.Theme = theme;
        AppServices.Settings.Save();
    }

    public void RunHomeTool(string key)
    {
        // Tools become available phase by phase; for now every available tool starts with opening a file.
        OpenFile();
    }

    public void ShowDocumentProperties(DocumentViewModel document)
    {
        var dialog = new DocumentPropertiesDialog(document) { Owner = Application.Current.MainWindow };
        dialog.ShowDialog();
    }

    public void PrintDocument(DocumentViewModel document)
    {
        var dialog = new PrintDialogWindow(document) { Owner = Application.Current.MainWindow };
        dialog.ShowDialog();
    }

    public void ShowFullScreen(DocumentViewModel document)
    {
        var window = new FullScreenWindow(document);
        window.Show();
    }
}
