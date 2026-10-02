using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfAcrobat.App.Services;
using Wpf.Ui.Controls;

namespace PdfAcrobat.App.ViewModels;

public sealed partial class RecentFileItem : ObservableObject
{
    public RecentFileItem(RecentFile file)
    {
        File = file;
        Name = Path.GetFileName(file.Path);
        Folder = Path.GetDirectoryName(file.Path) ?? string.Empty;
        Exists = System.IO.File.Exists(file.Path);
        Thumbnail = LoadThumbnail(file.ThumbnailPath);
    }

    public RecentFile File { get; }

    public string Name { get; }

    public string Folder { get; }

    public bool Exists { get; }

    public BitmapSource? Thumbnail { get; }

    public string LastOpenedText => File.LastOpened.Date == DateTime.Today
        ? $"今日 {File.LastOpened:HH:mm}"
        : File.LastOpened.ToString("yyyy/MM/dd HH:mm");

    public string SizeText => FormatSize(File.FileSize);

    public string PagesText => File.PageCount > 0 ? $"{File.PageCount} ページ" : string.Empty;

    private static BitmapSource? LoadThumbnail(string? path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB",
    };
}

/// <summary>A tool card on the home screen and in the "all tools" pane.</summary>
public sealed record ToolCard(string Key, string Title, string Description, SymbolRegular Icon, bool IsAvailable, string? Phase = null, string Accent = "#1473E6")
{
    public string StatusText => IsAvailable ? string.Empty : $"{Phase} で対応予定";
}

public sealed partial class HomeViewModel : TabViewModel
{
    private readonly MainViewModel _main;

    public HomeViewModel(MainViewModel main)
    {
        _main = main;
        AppServices.Settings.RecentFilesChanged += (_, _) => ReloadRecentFiles();
        ReloadRecentFiles();
    }

    public override string Title => "ホーム";

    public override bool IsClosable => false;

    public ObservableCollection<RecentFileItem> RecentFiles { get; } = new();

    public bool HasRecentFiles => RecentFiles.Count > 0;

    public IReadOnlyList<ToolCard> Tools { get; } = ToolCatalog.HomeTools;

    public void ReloadRecentFiles()
    {
        RecentFiles.Clear();
        foreach (var file in AppServices.Settings.Settings.RecentFiles)
        {
            RecentFiles.Add(new RecentFileItem(file));
        }

        OnPropertyChanged(nameof(HasRecentFiles));
    }

    [RelayCommand]
    private void OpenFile() => _main.OpenFileCommand.Execute(null);

    [RelayCommand]
    private void OpenRecent(RecentFileItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (!item.Exists)
        {
            if (AppServices.Dialogs.Confirm($"ファイルが見つかりません。\n{item.File.Path}\n\n最近使ったファイルの一覧から削除しますか？"))
            {
                AppServices.Settings.RemoveRecentFile(item.File.Path);
            }

            return;
        }

        _main.OpenFiles([item.File.Path]);
    }

    [RelayCommand]
    private void RemoveRecent(RecentFileItem? item)
    {
        if (item is not null)
        {
            AppServices.Settings.RemoveRecentFile(item.File.Path);
        }
    }

    [RelayCommand]
    private void ClearRecent()
    {
        if (AppServices.Dialogs.Confirm("最近使ったファイルの一覧をすべて消去しますか？"))
        {
            AppServices.Settings.ClearRecentFiles();
        }
    }

    [RelayCommand]
    private void RunTool(ToolCard? tool)
    {
        if (tool is null)
        {
            return;
        }

        if (!tool.IsAvailable)
        {
            AppServices.Dialogs.ShowInfo($"「{tool.Title}」は {tool.Phase} で実装予定です。", "準備中");
            return;
        }

        _main.RunHomeTool(tool.Key);
    }
}
