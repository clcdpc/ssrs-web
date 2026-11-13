using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SsrsPortal.Core;
using SsrsPortal.Security;
using SsrsPortal.Services;

namespace SsrsPortal.Pages;

// Legacy links enter the only workspace implementation after ownership validation.
public sealed class ViewerModel(SessionStore sessions, CatalogService catalog) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token)) { throw new PortalException("session", 404); }
        using var lease = await sessions.AcquireAsync(token, StaffAuthorization.Owner(User), ct);
        var session = lease.Session;
        if (session.Html.Length == 0) { throw new PortalException("session", 404); }
        var (report, _) = await catalog.WorkspaceAsync(session.Report.Id, ct);
        return Redirect("/Report?id=" + Uri.EscapeDataString(report.Id) + "&token=" + Uri.EscapeDataString(session.Token));
    }
}
