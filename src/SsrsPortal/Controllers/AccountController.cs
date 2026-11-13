using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SsrsPortal.Core;
using SsrsPortal.Security;
using SsrsPortal.Services;
namespace SsrsPortal.Controllers;

public sealed class AccountController(PortalOptions options, SessionStore sessions) : Controller
{
    [AllowAnonymous]
    [HttpGet("signin")]
    public IActionResult SignInPortal() => options.TestAuth ? Redirect("/TestLogin") :
        Challenge(new AuthenticationProperties { RedirectUri = "/" }, OpenIdConnectDefaults.AuthenticationScheme);
    [Authorize(Policy = "Staff")]
    [HttpPost("signout")]
    public async Task<IActionResult> SignOutPortal()
    {
        sessions.CancelOwner(StaffAuthorization.Owner(User));
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/SignedOut");
    }
    [AllowAnonymous]
    [HttpGet("Denied")]
    public ContentResult Denied()
    {
        Response.StatusCode = 403;
        return Content("Sign-in requires an approved organizational tenant and the ReportViewer application role. Ask your administrator to review the application's tenant approval and role assignment.");
    }
    [AllowAnonymous]
    [HttpGet("SignedOut")]
    public ContentResult SignedOut() => Content("You are signed out of the report portal. Your Microsoft session remains active. Visit /signin to sign in again.");
}
