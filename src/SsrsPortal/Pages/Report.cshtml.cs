using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Http.Features;
using SsrsPortal.Core;
using SsrsPortal.Services;
using SsrsPortal.Security;
namespace SsrsPortal.Pages;
public sealed class ReportModel(CatalogService catalog, SessionStore sessions) : PageModel
{
    public CatalogReport Report { get; private set; } = null!;
    public BreadcrumbNavigation Breadcrumbs { get; private set; } = new([]);
    public string? Token { get; private set; }
    public string? SessionMessage { get; private set; }
    public string CanonicalUrl => ReportUrl.For(Report);
    public string LinkQuery { get; private set; } = "";
    public Dictionary<string, string> FolderRoute => ContainingFolderRoute(Report.ParentId);
    internal static Dictionary<string, string> ContainingFolderRoute(string? parentId) =>
        BreadcrumbNavigation.FolderRoute(parentId);
    public async Task<IActionResult> OnGetAsync(string? id, string? token, CancellationToken ct)
    {
        var rawTarget = Request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;
        var rawPath = string.IsNullOrEmpty(rawTarget)
            ? string.Join('/', Request.Path.Value!.Split('/').Select(Uri.EscapeDataString))
            : rawTarget.Split('?')[0];
        var canonical = rawPath.StartsWith("/report/", StringComparison.OrdinalIgnoreCase);
        if (canonical)
        {
            var resolved = await catalog.ResolvePathAsync(ReportUrl.ParsePath(rawPath), ct);
            (Report, Breadcrumbs) = await catalog.WorkspaceAsync(resolved.Id, ct);
            LinkQuery = Request.QueryString.Value ?? "";
            if (rawPath != CanonicalUrl) { return Redirect(CanonicalUrl + LinkQuery); }
            // Canonical query keys belong to the report, including any named id/token.
            token = null;
        }
        else
        {
            if (string.IsNullOrEmpty(id)) { throw new PortalException("notfound", 404); }
            (Report, Breadcrumbs) = await catalog.WorkspaceAsync(id, ct);
            // Legacy IDs/tokens are routing controls, never report parameter values.
            LinkQuery = ReportLinkQuery.WithoutLegacyControls(Request.QueryString.Value ?? "");
            if (token == null) { return Redirect(CanonicalUrl + LinkQuery); }
        }
        if (token != null)
        {
            try
            {
                using var lease = await sessions.AcquireAsync(token, StaffAuthorization.Owner(User), ct);
                if (lease.Session.Report.Id != Report.Id || lease.Session.Html.Length == 0)
                {
                    throw new PortalException("session", 404);
                }
                Token = lease.Session.Token;
            }
            catch (PortalException error) when (error.Code == "session")
            {
                SessionMessage = PortalException.Description("session");
            }
        }
        return Page();
    }
}
