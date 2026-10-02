using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfAcrobat.App.Services;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed class RecentFile
{
    public string Path { get; set; } = string.Empty;

    public DateTime LastOpened { get; set; }

    public int PageIndex { get; set; }

    public long FileSize { get; set; }

    public int PageCount { get; set; }

    public string? ThumbnailPath { get; set; }
}

public sealed class WindowPlacement
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool Maximized { get; set; }
}

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    public string AuthorName { get; set; } = Environment.UserName;

    public bool ToolsPaneOpen { get; set; } = true;

    public string DefaultZoomMode { get; set; } = "FitWidth";

    public string DefaultLayout { get; set; } = "Continuous";

    public WindowPlacement? Window { get; set; }

    public List<RecentFile> RecentFiles { get; set; } = new();
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON under %APPDATA%\PdfAcrobat.</summary>
public sealed class SettingsService
{
    private const int MaxRecentFiles = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public SettingsService(string? folder = null)
    {
        var dir = folder ?? AppInfo.AppDataFolder;
        _path = Path.Combine(dir, "settings.json");
        Settings = Load(_path);
    }

    public AppSettings Settings { get; }

    public event EventHandler? RecentFilesChanged;

    private static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings fall back to defaults.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Settings, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are best-effort.
        }
    }

    public void AddRecentFile(string path, int pageCount, string? thumbnailPath)
    {
        var full = Path.GetFullPath(path);
        var existing = Settings.RecentFiles.FirstOrDefault(r => string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Settings.RecentFiles.Remove(existing);
        }

        var info = new FileInfo(full);
        Settings.RecentFiles.Insert(0, new RecentFile
        {
            Path = full,
            LastOpened = DateTime.Now,
            PageIndex = existing?.PageIndex ?? 0,
            FileSize = info.Exists ? info.Length : 0,
            PageCount = pageCount,
            ThumbnailPath = thumbnailPath ?? existing?.ThumbnailPath,
        });
        if (Settings.RecentFiles.Count > MaxRecentFiles)
        {
            Settings.RecentFiles.RemoveRange(MaxRecentFiles, Settings.RecentFiles.Count - MaxRecentFiles);
        }

        Save();
        RecentFilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateLastPage(string path, int pageIndex)
    {
        var entry = Settings.RecentFiles.FirstOrDefault(r => string.Equals(r.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        if (entry is not null)
        {
            entry.PageIndex = pageIndex;
        }
    }

    public void RemoveRecentFile(string path)
    {
        Settings.RecentFiles.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        Save();
        RecentFilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearRecentFiles()
    {
        Settings.RecentFiles.Clear();
        Save();
        RecentFilesChanged?.Invoke(this, EventArgs.Empty);
    }
}
