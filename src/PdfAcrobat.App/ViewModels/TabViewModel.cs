using CommunityToolkit.Mvvm.ComponentModel;

namespace PdfAcrobat.App.ViewModels;

/// <summary>Base class for everything shown as a tab in the title bar.</summary>
public abstract partial class TabViewModel : ObservableObject
{
    public abstract string Title { get; }

    public virtual string? ToolTip => Title;

    public abstract bool IsClosable { get; }

    /// <summary>Kept in sync by <see cref="MainViewModel"/>; drives which tab content is visible.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
