using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SsrsPortal.Core;
using SsrsPortal.Services;
namespace SsrsPortal.Pages;

public sealed class IndexModel(CatalogService catalog) : PageModel
{
    public CatalogEntry[] Entries { get; private set; } = [];
    public BreadcrumbNavigation Breadcrumbs { get; private set; } = new([]);
    [BindProperty(SupportsGet = true)]
    public string? Folder { get; set; }
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }
    [BindProperty(SupportsGet = true)]
    public bool Root { get; set; }
    public bool Searching => !string.IsNullOrWhiteSpace(Q);
    public async Task<IActionResult> OnGetAsync(CancellationToken ct) => Populate(await catalog.BrowseAsync(ct));

    internal IActionResult Populate(CatalogEntry[] all)
    {
        if (Folder != null && !all.Any(e => e.Id == Folder && e.Type == "Folder"))
        {
            return NotFound();
        }
        // Use the filtered snapshot; an explicit root must remain reachable.
        if (Folder == null && !Root && !Request.Query.ContainsKey("folder") && !Searching)
        {
            Folder = BreadcrumbNavigation.SingleRoot(all)?.Id;
        }
        Breadcrumbs = BreadcrumbNavigation.ForCatalog(all, Folder, Searching);
        Entries = !Searching ? all.Where(e => e.ParentId == Folder).OrderBy(e => e.Type == "Folder" ? (e.Name.StartsWith('~') ? 0 : 1) : 2).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray()
            : all.Where(e => e.Type != "Folder" && e.Name.Contains(Q![..Math.Min(Q!.Length, 100)], StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return Page();
    }
}
