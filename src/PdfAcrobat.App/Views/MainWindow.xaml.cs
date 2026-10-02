using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PdfAcrobat.App.Services;
using PdfAcrobat.App.ViewModels;

namespace PdfAcrobat.App.Views;

public partial class MainWindow
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ViewModel = viewModel;
        RestorePlacement();
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    public MainViewModel ViewModel { get; }

    /// <summary>Set by automation runs so that closing never prompts.</summary>
    public bool SkipCloseConfirmation { get; set; }

    private void RestorePlacement()
    {
        if (AppServices.Settings.Settings.Window is not { } placement || placement.Width < 400 || placement.Height < 300)
        {
            return;
        }

        // Only restore when the saved rectangle is still on a visible screen area.
        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var saved = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
        if (!virtualScreen.IntersectsWith(saved))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private bool _exitConfirmed;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!SkipCloseConfirmation && !_exitConfirmed)
        {
            // Saving may show dialogs and run asynchronously: cancel now, close again when done.
            e.Cancel = true;
            Dispatcher.BeginInvoke(async () =>
            {
                if (await ViewModel.PrepareToExitAsync())
                {
                    _exitConfirmed = true;
                    Close();
                }
            });
            return;
        }

        if (!SkipCloseConfirmation)
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            AppServices.Settings.Settings.Window = new WindowPlacement
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximized = WindowState == WindowState.Maximized,
            };
            AppServices.Settings.Save();
        }

        base.OnClosing(e);
    }

    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files ? files.Where(File.Exists).ToArray() : [];

    private static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFiles(e).Any(f => IsPdf(f) || Core.Import.ImagePdfConverter.IsImage(f)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>PDFs open in tabs; images are combined into a new PDF.</summary>
    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = DroppedFiles(e);
        var pdfs = files.Where(IsPdf).ToList();
        var images = files.Where(Core.Import.ImagePdfConverter.IsImage).ToList();
        if (pdfs.Count == 0 && images.Count == 0)
        {
            return;
        }

        Activate();
        ViewModel.OpenFiles(pdfs);
        if (images.Count > 0)
        {
            await ViewModel.CreateFromImageFilesAsync(images);
        }
    }

    /// <summary>Middle-click closes a tab, like in browsers.</summary>
    private void OnTabStripPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not ListBoxItem)
        {
            element = VisualTreeHelper.GetParent(element);
        }

        if (element is ListBoxItem { DataContext: TabViewModel tab })
        {
            _ = ViewModel.CloseTab(tab);
            e.Handled = true;
        }
    }
}
