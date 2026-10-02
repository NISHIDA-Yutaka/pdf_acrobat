using System.Windows;
using Microsoft.Win32;
using PdfAcrobat.App.Views.Dialogs;

namespace PdfAcrobat.App.Services;

/// <summary>File pickers and message boxes, kept out of view models so they stay testable.</summary>
public sealed class DialogService
{
    public const string PdfFilter = "PDF ファイル (*.pdf)|*.pdf|すべてのファイル (*.*)|*.*";

    private static Window? Owner => Application.Current?.MainWindow;

    /// <summary>When set (automation runs), dialogs are not shown; messages go to this callback and questions are declined.</summary>
    public Action<string>? AutomationLog { get; set; }

    public string[] PickPdfFiles(bool multiselect = true, string title = "開く")
    {
        var dialog = new OpenFileDialog
        {
            Filter = PdfFilter,
            Multiselect = multiselect,
            Title = title,
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileNames : [];
    }

    public string[] PickFiles(string filter, bool multiselect, string title)
    {
        var dialog = new OpenFileDialog { Filter = filter, Multiselect = multiselect, Title = title };
        return dialog.ShowDialog(Owner) == true ? dialog.FileNames : [];
    }

    public string? PickSavePath(string suggestedName, string filter = PdfFilter, string title = "名前を付けて保存")
    {
        var dialog = new SaveFileDialog
        {
            Filter = filter,
            FileName = suggestedName,
            Title = title,
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public void ShowError(string message, string title = "エラー")
    {
        if (AutomationLog is { } log)
        {
            log($"[error dialog] {message}");
            return;
        }

        MessageBox.Show(Owner!, message, $"{title} - {AppInfo.DisplayName}", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public void ShowInfo(string message, string title = "お知らせ")
    {
        if (AutomationLog is { } log)
        {
            log($"[info dialog] {message}");
            return;
        }

        MessageBox.Show(Owner!, message, $"{title} - {AppInfo.DisplayName}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public bool Confirm(string message, string title = "確認")
    {
        if (AutomationLog is { } log)
        {
            log($"[confirm dialog -> no] {message}");
            return false;
        }

        return MessageBox.Show(Owner!, message, $"{title} - {AppInfo.DisplayName}", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    }

    /// <summary>Yes / No / Cancel. Returns null for cancel.</summary>
    public bool? AskYesNoCancel(string message, string title = "確認")
    {
        if (AutomationLog is { } log)
        {
            log($"[yes/no/cancel dialog -> no] {message}");
            return false;
        }

        return MessageBox.Show(Owner!, message, $"{title} - {AppInfo.DisplayName}", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };
    }

    /// <summary>Prompts for a document password. Returns null when cancelled.</summary>
    public string? AskPassword(string fileName, bool retry)
    {
        if (AutomationLog is { } log)
        {
            log($"[password dialog -> cancel] {fileName}");
            return null;
        }

        var dialog = new PasswordDialog(fileName, retry) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Password : null;
    }
}
