using System.Collections.Immutable;
using PdfAcrobat.Pdfium;

namespace PdfAcrobat.Core.Documents;

/// <summary>
/// One page of the edited document: either a page of a <see cref="PdfSource"/> or a blank page.
/// <see cref="Rotation"/> is an extra rotation in quarter turns applied on top of the source page's /Rotate.
/// </summary>
public sealed record PageRef(Guid Id, Guid? SourceId, int SourceIndex, int Rotation, PdfSize? BlankSize = null)
{
    public bool IsBlank => SourceId is null;

    public static PageRef FromSource(Guid sourceId, int index) => new(Guid.NewGuid(), sourceId, index, 0);

    public static PageRef Blank(PdfSize size) => new(Guid.NewGuid(), null, -1, 0, size);

    public PageRef Rotate(int quarterTurns) => this with { Rotation = (((Rotation + quarterTurns) % 4) + 4) % 4 };

    /// <summary>Creates a copy that refers to the same content but has its own identity.</summary>
    public PageRef Duplicate() => this with { Id = Guid.NewGuid() };
}

/// <summary>Immutable snapshot of everything the user can edit. Undo/redo swaps whole snapshots.</summary>
public sealed record DocumentState(ImmutableList<PageRef> Pages)
{
    public int IndexOf(Guid pageId) => Pages.FindIndex(p => p.Id == pageId);
}
