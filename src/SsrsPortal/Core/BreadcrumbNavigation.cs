namespace SsrsPortal.Core;

public sealed record BreadcrumbItem(string Name, string? FolderId, bool IsCurrent = false)
{
    public Dictionary<string, string> Route => BreadcrumbNavigation.FolderRoute(FolderId);
}

// Build only from the already filtered, navigable catalog snapshot. Names and
// ancestry never come from URL paths, search context, or report execution state.
public sealed record BreadcrumbNavigation(BreadcrumbItem[] Items)
{
    public static CatalogEntry? SingleRoot(CatalogEntry[] entries)
    {
        var roots = entries.Where(e => e.ParentId == null).Take(2).ToArray();
        return roots.Length == 1 && roots[0].Type == "Folder" ? roots[0] : null;
    }

    public static Dictionary<string, string> FolderRoute(string? folderId) =>
        folderId == null ? new() { ["root"] = "true" } : new() { ["folder"] = folderId };

    public static BreadcrumbNavigation ForCatalog(CatalogEntry[] entries, string? folderId, bool searching)
    {
        var root = SingleRoot(entries);
        if (searching)
        {
            return new([new(root?.Name ?? "All folders", root?.Id), new("Search results", null, true)]);
        }
        // Explicit root requests remain at the catalog root, including empty catalogs.
        if (folderId == null) { return new([new("All folders", null, true)]); }
        var chain = Ancestors(entries, folderId);
        return new([.. Root(entries), .. chain.Select((e, i) => new BreadcrumbItem(e.Name, e.Id, i == chain.Length - 1))]);
    }

    public static BreadcrumbNavigation ForReport(CatalogEntry[] entries, CatalogReport report) =>
        new([.. Root(entries), .. Ancestors(entries, report.ParentId).Select(e => new BreadcrumbItem(e.Name, e.Id)),
            new(report.Name, null, true)]);

    private static BreadcrumbItem[] Root(CatalogEntry[] entries) =>
        SingleRoot(entries) == null ? [new("All folders", null)] : [];

    internal static CatalogEntry[] Ancestors(CatalogEntry[] entries, string? parentId)
    {
        var folders = entries.Where(e => e.Type == "Folder").ToDictionary(e => e.Id, StringComparer.Ordinal);
        var chain = new List<CatalogEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var parent = parentId; parent != null && seen.Add(parent) && folders.TryGetValue(parent, out var folder); parent = folder.ParentId)
        {
            chain.Insert(0, folder);
        }
        return chain.ToArray();
    }
}
