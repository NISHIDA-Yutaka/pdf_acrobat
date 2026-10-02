using System.Windows;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace PdfAcrobat.App.Services;

/// <summary>Switches WPF-UI's theme and the app's own color dictionary together.</summary>
public static class ThemeService
{
    private const int ColorsDictionaryIndex = 2;

    public static bool IsDark { get; private set; }

    public static event EventHandler? ThemeChanged;

    public static void Apply(AppTheme theme)
    {
        var dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark(),
        };

        ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light, Wpf.Ui.Controls.WindowBackdropType.None, updateAccent: true);

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var source = new Uri(dark ? "Themes/Colors.Dark.xaml" : "Themes/Colors.Light.xaml", UriKind.Relative);
        if (dictionaries.Count > ColorsDictionaryIndex)
        {
            dictionaries[ColorsDictionaryIndex] = new ResourceDictionary { Source = source };
        }

        IsDark = dark;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
