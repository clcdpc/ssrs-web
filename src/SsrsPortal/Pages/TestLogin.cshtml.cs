using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SsrsPortal.Core;
using SsrsPortal.Security;
using SsrsPortal.Services;
namespace SsrsPortal.Pages;

[AllowAnonymous]
public sealed class TestLoginModel(PortalOptions options, EntraOptions entra, IWebHostEnvironment environment, SessionStore sessions, ILogger<TestLoginModel> logger) : PageModel
{
    [BindProperty] public string Profile { get; set; } = "";
    private bool Enabled => environment.IsDevelopment() && options.TestAuth && options.UseMock;
    public IActionResult OnGet() => Enabled ? Page() : NotFound();
    public async Task<IActionResult> OnPostAsync()
    {
        if (!Enabled)
        {
            return NotFound();
        }
        if (Profile is not "staff-one" and not "staff-two" and not "staff-other-tenant" and not "outsider" and not "overage" and
            not "role-overage" and not "old-group" and not "unknown-tenant")
        {
            return BadRequest();
        }
        var tenant = Profile switch
        {
            "staff-other-tenant" => "00000000-0000-0000-0000-000000000004",
            "unknown-tenant" => "00000000-0000-0000-0000-000000000099",
            _ => "00000000-0000-0000-0000-000000000001"
        };
        var subject = Profile == "staff-two" ? "00000000-0000-0000-0000-000000000012" : "00000000-0000-0000-0000-000000000011";
        var claims = new List<Claim>
        {
            new("tid", tenant), new("oid", subject), new("iss", StaffAuthorization.Issuer(tenant)),
            new("aud", entra.ClientId), new("name", "Synthetic " + Profile)
        };
        if (Profile is not "outsider" and not "overage" and not "old-group")
        {
            claims.Add(new Claim("roles", StaffAuthorization.RequiredRole));
        }
        if (Profile == "old-group")
        {
            claims.Add(new Claim("groups", "00000000-0000-0000-0000-000000000002"));
        }
        if (Profile is "overage" or "role-overage")
        {
            claims.Add(new Claim("_claim_names", "{\"groups\":\"src1\"}"));
            claims.Add(new Claim("hasgroups", "true"));
        }
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, "name", "roles"));
        if (!StaffAuthorization.Approved(user, entra))
        {
            logger.LogWarning("Application sign-in rejected; tenant {Tenant}; user {Subject}; approved tenant {TenantApproved}; ReportViewer role {HasRole}.",
                tenant, subject, entra.AllowedTenantIds.Contains(tenant), user.HasClaim("roles", StaffAuthorization.RequiredRole));
            return Redirect("/Denied");
        }
        if (User.Identity?.IsAuthenticated == true)
        {
            sessions.CancelOwner(StaffAuthorization.Owner(User));
        }
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, user);
        return Redirect("/");
    }
}
