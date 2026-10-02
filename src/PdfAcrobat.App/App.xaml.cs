using System.IO;
using System.Windows;
using System.Windows.Threading;
using PdfAcrobat.App.Automation;
using PdfAcrobat.App.Services;
using PdfAcrobat.App.ViewModels;
using PdfAcrobat.App.Views;
using PdfAcrobat.Core.Fonts;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.App;

public partial class App
{
    private bool _automation;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => ErrorLog.Write(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write(args.Exception);
            args.SetObserved();
        };

        var options = CommandLineOptions.Parse(e.Args);
        _automation = options.ScriptPath is not null;
        if (options.SettingsFolder is not null)
        {
            AppServices.UseSettingsFolder(options.SettingsFolder);
        }

        PdfiumLibrary.EnsureInitialized();
        WindowsFontResolver.Install();
        ThemeService.Apply(options.Theme ?? AppServices.Settings.Settings.Theme);

        var viewModel = new MainViewModel();
        var window = new MainWindow(viewModel);
        MainWindow = window;
        if (options.WindowSize is { } size)
        {
            window.WindowState = WindowState.Normal;
            window.Width = size.Width;
            window.Height = size.Height;
        }

        if (options.Offscreen)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.ShowActivated = false;
        }

        window.Show();
        viewModel.OpenFiles(options.Files.Where(File.Exists));
        AppServices.AutoSave.Start(() => viewModel.Documents.Select(d => d.Session));

        if (options.ScriptPath is { } script)
        {
            window.SkipCloseConfirmation = true;
            _ = new AutomationRunner(window, viewModel).RunAsync(script);
        }
        else
        {
            // Offer documents left unsaved by a crash once the window is up.
            Dispatcher.BeginInvoke(() => viewModel.RecoverDocuments(ask: true), DispatcherPriority.ApplicationIdle);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppServices.Shutdown();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write(e.Exception);
        e.Handled = true;
        if (_automation)
        {
            return;
        }

        MessageBox.Show(
            $"予期しないエラーが発生しました。\n\n{e.Exception.Message}\n\n詳細はログに記録されています:\n{ErrorLog.FilePath}",
            AppInfo.DisplayName,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}

/// <summary>Appends unhandled exceptions to %LOCALAPPDATA%\PdfAcrobat\logs\error.log.</summary>
public static class ErrorLog
{
    public static string FilePath { get; } = Path.Combine(AppInfo.LocalDataFolder, "logs", "error.log");

    public static void Write(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {AppInfo.Version}\n{exception}\n\n");
        }
        catch (IOException)
        {
            // Nothing else we can do.
        }
    }
}

/// <summary>
/// Command line: <c>PdfAcrobat.exe [file.pdf ...] [--script steps.txt] [--size 1400x900] [--theme light|dark]
/// [--offscreen] [--settings-folder dir]</c>. The script options are for automated UI checks.
/// </summary>
public sealed record CommandLineOptions(
    IReadOnlyList<string> Files,
    string? ScriptPath,
    Size? WindowSize,
    AppTheme? Theme,
    bool Offscreen,
    string? SettingsFolder)
{
    public static CommandLineOptions Parse(string[] args)
    {
        var files = new List<string>();
        string? script = null;
        string? settings = null;
        Size? size = null;
        AppTheme? theme = null;
        var offscreen = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (arg)
            {
                case "--script":
                    script = Next();
                    break;
                case "--settings-folder":
                    settings = Next();
                    break;
                case "--size" when Next() is { } value && value.Split('x') is [var w, var h]
                                   && double.TryParse(w, out var width) && double.TryParse(h, out var height):
                    size = new Size(width, height);
                    break;
                case "--theme" when Next() is { } value && Enum.TryParse<AppTheme>(value, ignoreCase: true, out var parsed):
                    theme = parsed;
                    break;
                case "--offscreen":
                    offscreen = true;
                    break;
                default:
                    if (!arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        files.Add(arg);
                    }

                    break;
            }
        }

        return new CommandLineOptions(files, script, size, theme, offscreen, settings);
    }
}
