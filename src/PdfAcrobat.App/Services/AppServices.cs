using System.IO;
using PdfAcrobat.App.Controls.Viewer;
using PdfAcrobat.Core.Text;

namespace PdfAcrobat.App.Services;

/// <summary>Application-wide service instances (a deliberately small service locator).</summary>
public static class AppServices
{
    private static SettingsService? _settings;
    private static PageRenderService? _render;
    private static PageTextCache? _text;
    private static PageInfoCache? _pageInfo;
    private static DialogService? _dialogs;
    private static AutoSaveService? _autoSave;
    private static string? _dataFolder;

    public static SettingsService Settings => _settings ??= new SettingsService();

    public static PageRenderService Render => _render ??= new PageRenderService();

    public static PageTextCache Text => _text ??= new PageTextCache();

    public static PageInfoCache PageInfo => _pageInfo ??= new PageInfoCache(Text);

    public static DialogService Dialogs => _dialogs ??= new DialogService();

    public static AutoSaveService AutoSave => _autoSave ??= new AutoSaveService(Path.Combine(_dataFolder ?? AppInfo.LocalDataFolder, "AutoSave"));

    /// <summary>
    /// Overrides the settings location (used by automation runs so they do not touch user settings).
    /// Auto-save data goes there too.
    /// </summary>
    public static void UseSettingsFolder(string folder)
    {
        _settings = new SettingsService(folder);
        _dataFolder = Path.GetFullPath(folder);
    }

    public static void Shutdown()
    {
        _autoSave?.Dispose();
        _render?.Dispose();
    }
}
