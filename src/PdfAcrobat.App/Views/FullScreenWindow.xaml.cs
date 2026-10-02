using System.Windows.Input;
using PdfAcrobat.App.ViewModels;

namespace PdfAcrobat.App.Views;

/// <summary>Borderless presentation view of a document (Acrobat's full screen mode).</summary>
public partial class FullScreenWindow
{
    private readonly DocumentViewModel _document;

    public FullScreenWindow(DocumentViewModel document)
    {
        InitializeComponent();
        _document = document;
        Viewer.CurrentPageIndex = document.CurrentPageIndex;
        Viewer.Session = document.Session;
        Viewer.ViewRotation = document.ViewRotation;
        Loaded += (_, _) =>
        {
            Viewer.GoToPage(document.CurrentPageIndex);
            Viewer.Focus();
        };
        Closed += (_, _) => _document.CurrentPageIndex = Viewer.CurrentPageIndex;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            Viewer.NextPage();
            e.Handled = true;
        };
        PreviewMouseRightButtonDown += (_, e) =>
        {
            Viewer.PreviousPage();
            e.Handled = true;
        };
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
            case Key.L when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                Close();
                break;
            case Key.Right:
            case Key.Down:
            case Key.Space:
            case Key.Enter:
            case Key.PageDown:
                Viewer.NextPage();
                break;
            case Key.Left:
            case Key.Up:
            case Key.Back:
            case Key.PageUp:
                Viewer.PreviousPage();
                break;
            case Key.Home:
                Viewer.GoToPage(0);
                break;
            case Key.End:
                Viewer.GoToPage(int.MaxValue);
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
