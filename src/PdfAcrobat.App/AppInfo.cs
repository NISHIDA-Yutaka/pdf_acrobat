using System.Reflection;

namespace PdfAcrobat.App;

public static class AppInfo
{
    /// <summary>Name shown in the title bar and dialogs. This is an unofficial clone, not affiliated with Adobe.</summary>
    public const string DisplayName = "PDF Acrobat";

    public const string FolderName = "PdfAcrobat";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static string AppDataFolder { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

    public static string LocalDataFolder { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);
}
