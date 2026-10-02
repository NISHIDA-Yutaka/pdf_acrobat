using System.IO;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfAcrobat.Core.Export;

public sealed record FinalizeOptions(IReadOnlyList<ExportBookmark> Bookmarks, string? Title);

/// <summary>
/// Post-processes an assembled PDF with PDFsharp:
/// <list type="bullet">
/// <item>removes bookmarks, links and named destinations that point to pages which no longer exist,</item>
/// <item>scrubs every other reference to such pages (structure tree, annotation /P …),</item>
/// <item>registers form fields of imported pages in /AcroForm and drops fields of removed pages,</item>
/// <item>appends requested bookmarks and updates metadata.</item>
/// </list>
/// PDFsharp then writes only objects reachable from the trailer, so removed pages leave no data behind.
/// </summary>
public static class PdfFinalizer
{
    public static byte[] Finalize(byte[] pdf, FinalizeOptions options)
    {
        PdfDocument document;
        try
        {
            document = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Modify);
        }
        catch (Exception)
        {
            // PDFsharp could not parse the file; PDFium's output is still a valid PDF.
            return pdf;
        }

        var pageIds = new HashSet<PdfObjectID>(document.Pages.Cast<PdfPage>()
            .Where(p => p.Reference is not null)
            .Select(p => p.Reference!.ObjectID));
        var catalog = document.Internals.Catalog;
        var cleaner = new ReferenceCleaner(catalog, pageIds);

        cleaner.CleanOutlines();
        cleaner.CleanNamedDestinations();
        cleaner.CleanPageAnnotations(document);
        cleaner.CleanOpenAction();
        AcroFormRepair.Repair(document, pageIds);
        cleaner.ScrubRemainingPageReferences();

        if (options.Bookmarks.Count > 0)
        {
            AppendBookmarks(document, options.Bookmarks);
        }

        if (options.Title is not null)
        {
            document.Info.Title = options.Title;
        }

        document.Info.ModificationDate = DateTime.Now;
        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private static void AppendBookmarks(PdfDocument document, IReadOnlyList<ExportBookmark> bookmarks)
    {
        var catalog = document.Internals.Catalog;
        var root = catalog.Elements.GetDictionary("/Outlines");
        if (root is null)
        {
            root = new PdfDictionary(document);
            root.Elements.SetName("/Type", "/Outlines");
            document.Internals.AddObject(root);
            catalog.Elements.SetReference("/Outlines", root);
        }

        var last = root.Elements.GetDictionary("/Last");
        var added = 0;
        foreach (var bookmark in bookmarks)
        {
            if (bookmark.PageIndex < 0 || bookmark.PageIndex >= document.PageCount)
            {
                continue;
            }

            var item = new PdfDictionary(document);
            document.Internals.AddObject(item);
            item.Elements["/Title"] = new PdfString(bookmark.Title, PdfStringEncoding.Unicode);
            item.Elements.SetReference("/Parent", root);
            var dest = new PdfArray(document);
            dest.Elements.Add(document.Pages[bookmark.PageIndex].Reference!);
            dest.Elements.Add(new PdfName("/Fit"));
            item.Elements["/Dest"] = dest;
            if (last is null)
            {
                root.Elements.SetReference("/First", item);
            }
            else
            {
                last.Elements.SetReference("/Next", item);
                item.Elements.SetReference("/Prev", last);
            }

            last = item;
            added++;
        }

        if (last is not null)
        {
            root.Elements.SetReference("/Last", last);
        }

        root.Elements.SetInteger("/Count", Math.Abs(root.Elements.GetInteger("/Count")) + added);
    }
}

/// <summary>Removes references to pages that are no longer part of the page tree.</summary>
internal sealed class ReferenceCleaner(PdfDictionary catalog, HashSet<PdfObjectID> pageIds)
{
    private bool IsMissingPage(PdfItem? item) =>
        item is PdfReference reference
        && !pageIds.Contains(reference.ObjectID)
        && reference.Value is PdfDictionary dict
        && dict.Elements.GetName("/Type") == "/Page";

    /// <summary>True when a destination (array, name, string or GoTo action) targets a removed page.</summary>
    private bool TargetsMissingPage(PdfItem? destination, int depth = 0)
    {
        if (depth > 8)
        {
            return false;
        }

        switch (destination)
        {
            case PdfReference reference:
                return IsMissingPage(reference) || TargetsMissingPage(reference.Value, depth + 1);
            case PdfArray array:
                // Valid when the first entry is a page that still exists (or a page number for remote targets).
                return array.Elements.Count > 0
                    && array.Elements[0] is not PdfInteger
                    && !(array.Elements[0] is PdfReference target && pageIds.Contains(target.ObjectID));
            case PdfDictionary dict when dict.Elements.ContainsKey("/D"):
                return TargetsMissingPage(dict.Elements["/D"], depth + 1);
            case PdfName name:
                return TargetsMissingPage(LookupNamedDestination(name.Value.TrimStart('/')), depth + 1);
            case PdfString text:
                return TargetsMissingPage(LookupNamedDestination(text.Value), depth + 1);
            default:
                return false;
        }
    }

    private bool LinkTargetsMissingPage(PdfDictionary dict)
    {
        if (dict.Elements.ContainsKey("/Dest"))
        {
            return TargetsMissingPage(dict.Elements["/Dest"]);
        }

        var action = dict.Elements.GetDictionary("/A");
        return action is not null && action.Elements.GetName("/S") == "/GoTo" && TargetsMissingPage(action.Elements["/D"]);
    }

    private PdfItem? LookupNamedDestination(string name)
    {
        var dests = catalog.Elements.GetDictionary("/Dests");
        if (dests is not null && dests.Elements.ContainsKey("/" + name))
        {
            return dests.Elements["/" + name];
        }

        var tree = catalog.Elements.GetDictionary("/Names")?.Elements.GetDictionary("/Dests");
        return tree is null ? null : LookupNameTree(tree, name, 0);
    }

    private static PdfItem? LookupNameTree(PdfDictionary node, string name, int depth)
    {
        if (depth > 32)
        {
            return null;
        }

        if (node.Elements.GetArray("/Names") is { } names)
        {
            for (var i = 0; i + 1 < names.Elements.Count; i += 2)
            {
                if (names.Elements[i] is PdfString key && key.Value == name)
                {
                    return names.Elements[i + 1];
                }
            }
        }

        if (node.Elements.GetArray("/Kids") is { } kids)
        {
            for (var i = 0; i < kids.Elements.Count; i++)
            {
                if (kids.Elements.GetDictionary(i) is { } kid && LookupNameTree(kid, name, depth + 1) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    public void CleanOutlines()
    {
        var root = catalog.Elements.GetDictionary("/Outlines");
        if (root is null)
        {
            return;
        }

        var visible = CleanOutlineLevel(root, new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance), 0);
        if (root.Elements.GetDictionary("/First") is null)
        {
            catalog.Elements.Remove("/Outlines");
        }
        else
        {
            root.Elements.SetInteger("/Count", visible);
        }
    }

    /// <summary>Cleans the children of <paramref name="parent"/>; returns the number of visible descendants.</summary>
    private int CleanOutlineLevel(PdfDictionary parent, HashSet<PdfDictionary> visited, int depth)
    {
        var items = new List<PdfDictionary>();
        for (var item = parent.Elements.GetDictionary("/First"); item is not null && visited.Add(item) && depth < 64; item = item.Elements.GetDictionary("/Next"))
        {
            items.Add(item);
        }

        var kept = new List<PdfDictionary>();
        var visible = 0;
        foreach (var item in items)
        {
            var childVisible = CleanOutlineLevel(item, visited, depth + 1);
            var hasChildren = item.Elements.GetDictionary("/First") is not null;
            if (LinkTargetsMissingPage(item))
            {
                if (!hasChildren)
                {
                    continue;
                }

                // Keep the heading for its children but drop its broken destination.
                item.Elements.Remove("/Dest");
                item.Elements.Remove("/A");
            }

            kept.Add(item);
            var open = item.Elements.GetInteger("/Count") > 0;
            if (hasChildren)
            {
                item.Elements.SetInteger("/Count", open ? childVisible : -childVisible);
            }

            visible += 1 + (open ? childVisible : 0);
        }

        for (var i = 0; i < kept.Count; i++)
        {
            var item = kept[i];
            item.Elements.SetReference("/Parent", parent);
            if (i > 0)
            {
                item.Elements.SetReference("/Prev", kept[i - 1]);
            }
            else
            {
                item.Elements.Remove("/Prev");
            }

            if (i < kept.Count - 1)
            {
                item.Elements.SetReference("/Next", kept[i + 1]);
            }
            else
            {
                item.Elements.Remove("/Next");
            }
        }

        if (kept.Count > 0)
        {
            parent.Elements.SetReference("/First", kept[0]);
            parent.Elements.SetReference("/Last", kept[^1]);
        }
        else
        {
            parent.Elements.Remove("/First");
            parent.Elements.Remove("/Last");
            parent.Elements.Remove("/Count");
        }

        return visible;
    }

    public void CleanNamedDestinations()
    {
        if (catalog.Elements.GetDictionary("/Dests") is { } dests)
        {
            foreach (var key in dests.Elements.Keys.ToList())
            {
                if (TargetsMissingPage(dests.Elements[key]))
                {
                    dests.Elements.Remove(key);
                }
            }
        }

        if (catalog.Elements.GetDictionary("/Names")?.Elements.GetDictionary("/Dests") is { } tree)
        {
            CleanNameTree(tree, 0);
        }
    }

    private void CleanNameTree(PdfDictionary node, int depth)
    {
        if (depth > 32)
        {
            return;
        }

        if (node.Elements.GetArray("/Names") is { } names)
        {
            for (var i = names.Elements.Count - 2; i >= 0; i -= 2)
            {
                if (TargetsMissingPage(names.Elements[i + 1]))
                {
                    names.Elements.RemoveAt(i + 1);
                    names.Elements.RemoveAt(i);
                }
            }
        }

        if (node.Elements.GetArray("/Kids") is { } kids)
        {
            for (var i = 0; i < kids.Elements.Count; i++)
            {
                if (kids.Elements.GetDictionary(i) is { } kid)
                {
                    CleanNameTree(kid, depth + 1);
                }
            }
        }
    }

    /// <summary>Removes link annotations whose target page is gone and fixes /P of the remaining ones.</summary>
    public void CleanPageAnnotations(PdfDocument document)
    {
        foreach (var page in document.Pages.Cast<PdfPage>())
        {
            if (page.Elements.GetArray("/Annots") is not { } annots)
            {
                continue;
            }

            for (var i = annots.Elements.Count - 1; i >= 0; i--)
            {
                if (annots.Elements.GetDictionary(i) is not { } annot)
                {
                    continue;
                }

                if (annot.Elements.GetName("/Subtype") == "/Link" && LinkTargetsMissingPage(annot))
                {
                    annots.Elements.RemoveAt(i);
                    continue;
                }

                if (IsMissingPage(annot.Elements["/P"]) && page.Reference is not null)
                {
                    annot.Elements.SetReference("/P", page);
                }
            }
        }
    }

    public void CleanOpenAction()
    {
        if (catalog.Elements.ContainsKey("/OpenAction") && TargetsMissingPage(catalog.Elements["/OpenAction"]))
        {
            catalog.Elements.Remove("/OpenAction");
        }
    }

    /// <summary>
    /// Final safety net: walks the object graph from the catalog and removes any remaining reference to a
    /// removed page (structure tree /Pg, OBJR, stray destinations …) so the page becomes unreachable.
    /// </summary>
    public void ScrubRemainingPageReferences()
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfItem>();
        stack.Push(catalog);
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            switch (item)
            {
                case PdfReference reference:
                    if (!IsMissingPage(reference) && reference.Value is { } value && visited.Add(value))
                    {
                        stack.Push(value);
                    }

                    break;
                case PdfDictionary dict:
                    foreach (var key in dict.Elements.Keys.ToList())
                    {
                        var child = dict.Elements[key];
                        if (IsMissingPage(child))
                        {
                            dict.Elements.Remove(key);
                        }
                        else if (child is not null)
                        {
                            stack.Push(child);
                        }
                    }

                    break;
                case PdfArray array:
                    for (var i = 0; i < array.Elements.Count; i++)
                    {
                        var child = array.Elements[i];
                        if (IsMissingPage(child))
                        {
                            array.Elements[i] = PdfNull.Value;
                        }
                        else if (child is not null)
                        {
                            stack.Push(child);
                        }
                    }

                    break;
            }
        }
    }
}

/// <summary>Keeps /AcroForm consistent with the widgets that are actually on the pages.</summary>
internal static class AcroFormRepair
{
    public static void Repair(PdfDocument document, HashSet<PdfObjectID> pageIds)
    {
        // Top-level fields of all widgets on the remaining pages, and the set of those widgets.
        var widgets = new HashSet<PdfObjectID>();
        var topFields = new List<PdfDictionary>();
        var seenTop = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var page in document.Pages.Cast<PdfPage>())
        {
            if (page.Elements.GetArray("/Annots") is not { } annots)
            {
                continue;
            }

            for (var i = 0; i < annots.Elements.Count; i++)
            {
                if (annots.Elements[i] is not PdfReference reference || reference.Value is not PdfDictionary annot
                    || annot.Elements.GetName("/Subtype") != "/Widget")
                {
                    continue;
                }

                widgets.Add(reference.ObjectID);
                var top = annot;
                for (var guard = 0; guard < 32 && top.Elements.GetDictionary("/Parent") is { } parent; guard++)
                {
                    top = parent;
                }

                if (seenTop.Add(top))
                {
                    topFields.Add(top);
                }
            }
        }

        var catalog = document.Internals.Catalog;
        var acroForm = catalog.Elements.GetDictionary("/AcroForm");
        if (acroForm is null && topFields.Count == 0)
        {
            return;
        }

        if (acroForm is null)
        {
            acroForm = new PdfDictionary(document);
            document.Internals.AddObject(acroForm);
            catalog.Elements.SetReference("/AcroForm", acroForm);
        }

        // Rebuild /Fields: keep fields that still have widgets, add fields of imported pages.
        var fields = new PdfArray(document);
        foreach (var field in topFields.Where(f => f.Reference is not null))
        {
            PruneKids(field, widgets);
            fields.Elements.Add(field.Reference!);
        }

        acroForm.Elements["/Fields"] = fields;
        if (topFields.Count == 0)
        {
            catalog.Elements.Remove("/AcroForm");
        }
    }

    /// <summary>Removes kid widgets that are no longer placed on any page.</summary>
    private static void PruneKids(PdfDictionary field, HashSet<PdfObjectID> widgets)
    {
        if (field.Elements.GetArray("/Kids") is not { } kids)
        {
            return;
        }

        for (var i = kids.Elements.Count - 1; i >= 0; i--)
        {
            if (kids.Elements[i] is not PdfReference reference || reference.Value is not PdfDictionary kid)
            {
                continue;
            }

            var isWidget = kid.Elements.GetName("/Subtype") == "/Widget" || kid.Elements.ContainsKey("/Rect");
            if (isWidget && !widgets.Contains(reference.ObjectID) && !kid.Elements.ContainsKey("/Kids"))
            {
                kids.Elements.RemoveAt(i);
                continue;
            }

            PruneKids(kid, widgets);
        }
    }
}
