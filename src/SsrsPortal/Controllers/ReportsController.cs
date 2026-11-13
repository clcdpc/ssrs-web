using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using SsrsPortal.Core;
using SsrsPortal.Security;
using SsrsPortal.Services;
namespace SsrsPortal.Controllers;

[ApiController]
[Authorize(Policy = "Staff")]
[Route("api")]
public sealed class ReportsController(CatalogService catalog, ParameterService parameters, SessionStore sessions, ExecutionService execution, IAntiforgery csrf) : ControllerBase
{
    private string Owner => StaffAuthorization.Owner(User);
    [HttpGet("csrf")]
    public object Csrf() => new { token = csrf.GetAndStoreTokens(HttpContext).RequestToken };
    [HttpGet("catalog")]
    public Task<CatalogEntry[]> Catalog(CancellationToken ct) => catalog.BrowseAsync(ct);
    [HttpPost("reports/{id}/parameters")]
    public async Task<ParameterForm> Parameters(string id, ParameterInput input, CancellationToken ct)
    {
        var report = await catalog.ResolveAsync(id, ct);
        return (await parameters.ResolveAsync(report, input, false, ct)).Form;
    }
    [HttpPost("reports/{id}/link-parameters")]
    public async Task<ReportLinkForm> LinkParameters(string id, ReportLinkInput input, CancellationToken ct)
    {
        var report = await catalog.ResolveAsync(id, ct);
        return await parameters.ResolveLinkAsync(report, input.Query, ct);
    }
    [HttpPost("reports/{id}/options/{parameter}")]
    public async Task<ParameterChoices> Options(string id, string parameter, ParameterInput input, CancellationToken ct)
    {
        var report = await catalog.ResolveAsync(id, ct);
        return await parameters.ChoicesAsync(report, input, parameter, ct);
    }
    [HttpPost("reports/{id}/options/{parameter}/all")]
    public async Task<OptionSelection> SelectAll(string id, string parameter, ParameterInput input, CancellationToken ct)
    {
        var report = await catalog.ResolveAsync(id, ct);
        return await parameters.SelectAllAsync(report, input, parameter, ct);
    }
    [HttpPost("reports/{id}/run")]
    public Task<RunResult> Run(string id, ParameterInput input, CancellationToken ct) => execution.RunAsync(Owner, id, input, ct);
    [HttpPost("executions/{token}/page/{page:int}")]
    public async Task<PageResult> Page(string token, int page, CancellationToken ct)
    {
        using var lease = await sessions.AcquireAsync(token, Owner, ct);
        return await execution.PageAsync(lease, page);
    }
    [HttpPost("executions/{token}/toggle/{actionToken}")]
    public async Task<PageResult> Toggle(string token, string actionToken, CancellationToken ct)
    {
        using var lease = await sessions.AcquireAsync(token, Owner, ct);
        return await execution.ToggleAsync(lease, actionToken);
    }
    [HttpPost("executions/{token}/synchronize")]
    public async Task<PageResult> Synchronize(string token, CancellationToken ct)
    {
        using var lease = await sessions.AcquireAsync(token, Owner, ct);
        return await execution.SynchronizeAsync(lease);
    }
    [HttpGet("executions/{token}/info")]
    public async Task<object> Info(string token, CancellationToken ct)
    {
        using var lease = await sessions.AcquireAsync(token, Owner, ct);
        var s = lease.Session;
        return new { s.Token, name = s.Report.Name, reportId = s.Report.Id, s.Page, s.Pages, s.Revision, s.NeedsSynchronization, input = s.Input, pending = s.Replaces != null, sourceToken = s.Replaces };
    }
    [HttpGet("executions/{token}/viewer")]
    public async Task Viewer(string token, CancellationToken ct, [FromQuery] long? revision = null)
    {
        using var lease = await sessions.AcquireAsync(token, Owner, ct);
        if (lease.Session.NeedsSynchronization) { throw new PortalException("synchronize", 409); }
        if (revision.HasValue && revision != lease.Session.Revision) { throw new PortalException("view-stale", 409); }
        Response.ContentType = "text/html; charset=utf-8";
        await Response.WriteAsync(lease.Session.Html, lease.Cancellation);
    }
    [HttpGet("executions/{token}/streams/{stream}")]
    public async Task Stream(string token, string stream, CancellationToken ct)
    {
        using var lease = await sessions.AcquireAsync(token, Owner, ct);
        var output = await execution.StreamAsync(lease, stream);
        Response.ContentType = output.MimeType;
        Response.ContentLength = output.Bytes.Length;
        await Response.Body.WriteAsync(output.Bytes, lease.Cancellation);
    }
    [HttpGet("executions/{token}/exports/{format}")]
    public async Task Export(string token, string format, CancellationToken ct)
    {
        if (!OutputFormat.All.TryGetValue(format, out var definition))
        {
            throw new PortalException("format", 400);
        }
        await WriteExportAsync(token, definition, false, null, ct);
    }
    [HttpGet("executions/{token}/print")]
    public Task Print(string token, CancellationToken ct, [FromQuery] long? revision = null) =>
        WriteExportAsync(token, OutputFormat.All["pdf"], true, revision, ct);

    private async Task WriteExportAsync(string token, OutputFormat definition, bool inline, long? revision, CancellationToken ct)
    {
        using var lease = await sessions.AcquireAsync(token, Owner, ct);
        if (revision.HasValue && revision != lease.Session.Revision) { throw new PortalException("view-stale", 409); }
        var output = await execution.ExportAsync(lease, definition);
        Response.ContentType = definition.Mime;
        Response.ContentLength = output.Bytes.Length;
        var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
        disposition.SetHttpFileName(SafeFilename(lease.Session.Report.Name) + "." + definition.Extension);
        Response.Headers.ContentDisposition = disposition.ToString();
        await Response.Body.WriteAsync(output.Bytes, lease.Cancellation);
    }
    [HttpPost("executions/{token}/cancel")]
    public IActionResult Cancel(string token) => sessions.Cancel(token, Owner) ? NoContent() : NotFound();
    [HttpPost("executions/{token}/commit")]
    public IActionResult Commit(string token)
    {
        sessions.Commit(token, Owner);
        return NoContent();
    }
    [HttpPost("executions/{token}/replacement/cancel")]
    public IActionResult CancelReplacement(string token)
    {
        sessions.CancelReplacement(token, Owner);
        return NoContent();
    }
    public static string SafeFilename(string name)
    {
        var safe = new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ' ').Take(80).ToArray()).Trim();
        return safe.Length == 0 ? "report" : safe;
    }
}
