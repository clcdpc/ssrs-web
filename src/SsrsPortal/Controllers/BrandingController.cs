using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SsrsPortal.Services;

namespace SsrsPortal.Controllers;

[Authorize(Policy = "Staff")]
[Route("branding")]
public sealed class BrandingController(SsrsBrandingService branding) : ControllerBase
{
    [HttpGet("{version}/theme.css")]
    public IActionResult Theme(string version) => branding.Find(version) is { } snapshot
        ? Content(snapshot.Css, "text/css; charset=utf-8") : NotFound();

    [HttpGet("{version}/logo.png")]
    public IActionResult Logo(string version) => branding.Find(version)?.Logo is { } png
        ? File(png, "image/png") : NotFound();
}
