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

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!SkipCloseConfirmation && !ViewModel.PrepareToExit())
        {
            e.Cancel = true;
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

    private static IEnumerable<string> DroppedPdfFiles(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => string.Equals(Path.GetExtension(f), ".pdf", StringComparison.OrdinalIgnoreCase))
            : [];

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedPdfFiles(e).Any() ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var files = DroppedPdfFiles(e).ToList();
        if (files.Count > 0)
        {
            Activate();
            ViewModel.OpenFiles(files);
        }

        e.Handled = true;
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
            ViewModel.CloseTab(tab);
            e.Handled = true;
        }
    }
}
