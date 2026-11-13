using System.Diagnostics;
using SsrsPortal.Core;
using SsrsPortal.Soap;

namespace SsrsPortal.Services;

public sealed class ExecutionService(SsrsClient soap, CatalogService catalog, ParameterService parameters, SessionStore sessions, HtmlSafety safety,
    ILogger<ExecutionService>? logger = null)
{
    public async Task<RunResult> RunAsync(string owner, string reportId, ParameterInput input, CancellationToken ct)
    {
        var report = await catalog.ResolveAsync(reportId, ct);
        using var scope = logger?.BeginScope(new Dictionary<string, object?> { ["ReportPath"] = report.Path, ["Operation"] = "Execute" });
        var watch = Stopwatch.StartNew();
        logger?.LogDebug("Report execution selected {ReportPath}; parameters {Parameters}.", report.Path, input.Values);
        (ParameterForm Form, ParameterValue[] ExecutionValues) resolved;
        try { resolved = await parameters.ResolveAsync(report, input, true, ct); }
        catch (PortalException exception) when (exception.Code is "values" or "selection-limit")
        {
            logger?.LogWarning("Report parameter validation failed for {ReportPath}; code {Code}.", report.Path, exception.Code);
            throw;
        }
        // Parameters may await SSRS while SQL invalidates or refreshes catalog membership.
        report = await catalog.ResolveAsync(reportId, ct);
        ct.ThrowIfCancellationRequested();
        var session = sessions.Reserve(owner, report, input.ReplaceToken);
        logger?.LogDebug("Report execution started for {ReportPath}.", report.Path);
        try
        {
            using var lease = await sessions.AcquireAsync(session.Token, owner, ct);
            var info = await soap.LoadAsync(report.Path, lease.Cancellation);
            if (info.CredentialsRequired)
            {
                throw new PortalException("credentials", 400, "LoadReport reports that SSRS requires interactive data-source credentials.");
            }
            session.ExecutionId = info.ExecutionId;
            if (resolved.ExecutionValues.Length > 0)
            {
                info = await soap.SetAsync(info.ExecutionId, resolved.ExecutionValues, lease.Cancellation);
            }
            VerifyInfo(session, info);
            session.Input = new ParameterInput(resolved.Form.Values.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal),
                Defaulted: resolved.Form.Defaulted.ToArray());
            await PageAsync(lease, 1);
            lease.Cancellation.ThrowIfCancellationRequested();
            logger?.LogInformation("Report execution completed for {ReportPath}; page {Page}; pages {Pages}; duration {DurationMs}ms.", report.Path, session.Page, session.Pages, watch.ElapsedMilliseconds);
            return new RunResult(session.Token, report.Name, session.Page, session.Pages, session.Revision);
        }
        catch (Exception exception)
        {
            logger?.LogInformation("Report execution stopped for {ReportPath}; outcome {Outcome}; duration {DurationMs}ms.",
                report.Path, exception is OperationCanceledException ? "cancelled" : exception is PortalException failure ? failure.Code : "failed", watch.ElapsedMilliseconds);
            sessions.Cancel(session.Token, owner);
            throw;
        }
    }

    public async Task<PageResult> PageAsync(SessionLease lease, int page)
    {
        var s = lease.Session;
        if (page < 1 || page > 10000 || (s.Pages.HasValue && page > s.Pages))
        {
            throw new PortalException("values", 400);
        }
        if (s.Html.Length > 0) { sessions.InvalidateView(s); }
        return await RenderPageAsync(lease, page);
    }

    public async Task<PageResult> ToggleAsync(SessionLease lease, string actionToken)
    {
        lease.Cancellation.ThrowIfCancellationRequested();
        var s = lease.Session;
        var toggleId = sessions.BeginToggle(s, actionToken);
        try
        {
            var found = await soap.ToggleAsync(s.ExecutionId, toggleId, lease.Cancellation);
            logger?.LogInformation("Report ToggleItem recognition: {Found}; rerender required", found);
            if (!found) { throw new PortalException("toggle-missing", 409); }
            return await SynchronizeAsync(lease);
        }
        catch (PortalException error) when (error.Code != "toggle-missing")
        {
            // SSRS mutation is not transactional with Render. Never replay it.
            throw new PortalException("synchronize", 409, innerException: error);
        }
    }

    public async Task<PageResult> SynchronizeAsync(SessionLease lease)
    {
        var s = lease.Session;
        lease.Cancellation.ThrowIfCancellationRequested();
        sessions.InvalidateView(s);
        var info = await soap.InfoAsync(s.ExecutionId, lease.Cancellation, includeFailureContent: false);
        VerifyInfo(s, info);
        // Zero is unknown, not an empty report. With no reliable upper bound,
        // page one is the safe resynchronization target after visibility changes.
        var page = info.NumPages > 0 ? Math.Min(s.Page, info.NumPages) : 1;
        return await RenderPageAsync(lease, page);
    }

    private async Task<PageResult> RenderPageAsync(SessionLease lease, int page, bool corrected = false)
    {
        var s = lease.Session;
        using var scope = logger?.BeginScope(new Dictionary<string, object?> { ["ReportPath"] = s.Report.Path, ["Page"] = page, ["Operation"] = "Render" });
        var watch = Stopwatch.StartNew();
        logger?.LogDebug("Report page rendering started for {ReportPath}; page {Page}.", s.Report.Path, page);
        var callback = "SsrsPortalAction_" + SessionStore.RandomToken();
        var output = await soap.RenderAsync(s.ExecutionId, "HTML5", page, lease.Cancellation, callback, includeFailureContent: false);
        if (!string.Equals(output.MimeType, "text/html", StringComparison.OrdinalIgnoreCase) ||
            (output.Encoding.Length > 0 &&
                !output.Encoding.Equals("utf-8", StringComparison.OrdinalIgnoreCase) &&
                !output.Encoding.Equals("Unicode (UTF-8)", StringComparison.OrdinalIgnoreCase)))
        {
            throw new PortalException("resource", message: "Render returned an unsupported HTML MIME type or encoding.");
        }
        var info = await soap.InfoAsync(s.ExecutionId, lease.Cancellation, includeFailureContent: false);
        VerifyInfo(s, info);
        // Some renderers only finish pagination during Render. Correct once after
        // that metadata update, without reissuing ToggleItem or loading a report.
        if (info.NumPages > 0 && page > info.NumPages)
        {
            if (corrected) { throw new PortalException("synchronize", 409); }
            return await RenderPageAsync(lease, info.NumPages, true);
        }
        var streams = output.Streams.ToDictionary(_ => SessionStore.RandomToken(), v => v, StringComparer.Ordinal);
        var actions = new Dictionary<string, string>(StringComparer.Ordinal);
        var context = new ToggleRenderContext(s.Report.Path, callback, s.ActionKey, s.Revision + 1, actions);
        var html = safety.Sanitize(output.Bytes, s.Token, s.ExecutionId, streams, context);
        lease.Cancellation.ThrowIfCancellationRequested();
        sessions.SetPage(s, html, streams, page, info.NumPages == 0 ? null : info.NumPages, actions);
        logger?.LogInformation("Report page rendered for {ReportPath}; page {Page}, pages {Pages}, toggle actions {Actions}, revision {Revision}; duration {DurationMs}ms.",
            s.Report.Path, s.Page, s.Pages, actions.Count, s.Revision, watch.ElapsedMilliseconds);
        return new PageResult(s.Page, s.Pages, "/api/executions/" + s.Token + "/viewer?revision=" + s.Revision, s.Revision);
    }

    private static void VerifyInfo(ReportSession s, ExecutionInfo info)
    {
        if (info.ExecutionId != s.ExecutionId || info.CredentialsRequired || info.ParametersRequired)
        {
            throw new PortalException(info.CredentialsRequired ? "credentials" : "values", 400,
                "SSRS executionInfo changed execution identity or still requires credentials or parameters.");
        }
    }

    public async Task<RenderOutput> ExportAsync(SessionLease lease, OutputFormat format)
    {
        if (lease.Session.NeedsSynchronization) { throw new PortalException("synchronize", 409); }
        await catalog.ResolveAsync(lease.Session.Report.Id, lease.Cancellation);
        using var scope = logger?.BeginScope(new Dictionary<string, object?> { ["ReportPath"] = lease.Session.Report.Path, ["Operation"] = "Export", ["Format"] = format.Soap });
        var watch = Stopwatch.StartNew();
        logger?.LogDebug("Report export started for {ReportPath}; format {Format}.", lease.Session.Report.Path, format.Soap);
        var output = await soap.RenderAsync(lease.Session.ExecutionId, format.Soap, 1, lease.Cancellation,
            includeFailureContent: !lease.Session.HasToggleControls);
        ValidateExport(output, format);
        logger?.LogInformation("Report export completed for {ReportPath}; format {Format}; duration {DurationMs}ms.", lease.Session.Report.Path, format.Soap, watch.ElapsedMilliseconds);
        return output;
    }

    internal static void ValidateExport(RenderOutput output, OutputFormat format)
    {
        if (output.MimeType != format.Mime || output.Extension != format.Extension ||
            (format.Soap == "PDF" && !output.Bytes.AsSpan().StartsWith("%PDF-"u8)))
        {
            throw new PortalException("format", message: "Render returned a MIME type or extension that does not match the requested export format.");
        }
    }

    public async Task<RenderOutput> StreamAsync(SessionLease lease, string token)
    {
        if (lease.Session.NeedsSynchronization) { throw new PortalException("synchronize", 409); }
        if (!lease.Session.Streams.TryGetValue(token, out var stream))
        {
            throw new PortalException("session", 404);
        }
        var output = await soap.StreamAsync(lease.Session.ExecutionId, stream, lease.Cancellation,
            includeFailureContent: !lease.Session.HasToggleControls);
        if (output.MimeType == "text/css")
        {
            var css = HtmlSafety.Css(new System.Text.UTF8Encoding(false, true).GetString(output.Bytes));
            return output with { Bytes = System.Text.Encoding.UTF8.GetBytes(css) };
        }
        if (output.MimeType == "image/png" && output.Bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            output.MimeType == "image/jpeg" && output.Bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }) ||
            output.MimeType == "image/gif" && output.Bytes.AsSpan().StartsWith("GIF8"u8))
        {
            return output;
        }
        throw new PortalException("resource", message: "RenderStream returned an unsupported MIME type or an invalid image signature.");
    }
}
