using PdfAcrobat.Core.Documents;

namespace PdfAcrobat.Tests.Documents;

public class DocumentSessionTests
{
    [Fact]
    public void NewSession_HasOnePageRefPerSourcePage()
    {
        using var session = new DocumentSession(PdfSource.Load(TestFiles.ReadSample("basic.pdf"), "basic.pdf"));

        Assert.Equal(6, session.Pages.Count);
        Assert.Equal(Enumerable.Range(0, 6), session.Pages.Select(p => p.SourceIndex));
        Assert.False(session.IsModified);
        Assert.Equal("basic.pdf", session.DisplayName);
    }

    [Fact]
    public void Apply_UndoRedo_RestoresSnapshots()
    {
        using var session = new DocumentSession(PdfSource.Load(TestFiles.ReadSample("basic.pdf"), "basic.pdf"));
        var original = session.State;
        var changes = 0;
        session.Changed += (_, _) => changes++;

        session.Apply("回転", s => s with { Pages = s.Pages.SetItem(0, s.Pages[0].Rotate(1)) });
        session.Apply("削除", s => s with { Pages = s.Pages.RemoveAt(1) });

        Assert.True(session.IsModified);
        Assert.Equal(5, session.Pages.Count);
        Assert.Equal(1, session.Pages[0].Rotation);
        Assert.Equal("削除", session.UndoDescription);

        session.Undo();
        Assert.Equal(6, session.Pages.Count);
        session.Undo();
        Assert.Same(original, session.State);
        Assert.False(session.IsModified);

        session.Redo();
        Assert.Equal(1, session.Pages[0].Rotation);
        Assert.Equal(5, changes);
    }

    [Fact]
    public void GetPageSize_AccountsForExtraRotation()
    {
        using var session = new DocumentSession(PdfSource.Load(TestFiles.ReadSample("basic.pdf"), "basic.pdf"));
        var page = session.Pages[0];

        var size = session.GetPageSize(page);
        var rotated = session.GetPageSize(page.Rotate(1));

        Assert.Equal(size.Width, rotated.Height);
        Assert.Equal(size.Height, rotated.Width);
    }
}
