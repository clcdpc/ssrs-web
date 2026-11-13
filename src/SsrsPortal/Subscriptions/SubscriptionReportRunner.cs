using SsrsPortal.Core;
using SsrsPortal.Services;
using SsrsPortal.Soap;

namespace SsrsPortal.Subscriptions;

public sealed record PreparedSubscriptionReport(string Name, string? Link, RenderOutput? Attachment);
public interface ISubscriptionReportRunner
{
    Task<PreparedSubscriptionReport> PrepareAsync(SubscriptionDefinition definition, CancellationToken ct);
}

public sealed class SubscriptionReportRunner(CatalogService catalog, ParameterService parameters, SsrsClient soap,
    SubscriptionOptions options) : ISubscriptionReportRunner
{
    public async Task<(CatalogReport Report, ParameterForm Form, ParameterValue[] Effective)> ResolveAsync(SubscriptionDefinition definition, CancellationToken ct)
    {
        var report = await catalog.ResolvePathAsync(definition.ReportPath, ct);
        var explicitValues = definition.Parameters.Where(p => !p.Value.UseDefault)
            .ToDictionary(p => p.Key, p => p.Value.Values, StringComparer.Ordinal);
        var resolved = await parameters.ResolveSubscriptionAsync(report, new(explicitValues, Initial: true), ct);
        if (definition.Parameters.Keys.Any(k => !resolved.Form.Fields.Any(f => f.Name == k)))
        { throw new SubscriptionValidationException("A subscription input is hidden or no longer available."); }
        if (definition.Delivery != "attachment")
        {
            // DefaultValuesQueryBased describes queries only. A non-query default
            // can still be an expression such as Now() or User!UserID. Management
            // metadata cannot establish that any hidden default is a literal.
            if (resolved.Metadata.Any(p => p.Hidden))
            { throw new SubscriptionValidationException("This report has internal inputs whose defaults cannot be reproduced safely in a link. Choose attachment only."); }
            _ = Link(report, resolved.Form, resolved.ExecutionValues);
        }
        return (report, resolved.Form, resolved.ExecutionValues);
    }
    internal string Link(CatalogReport report, ParameterForm form, ParameterValue[] effective)
    {
        // Hidden execution semantics were rejected during resolution.
        var pairs = new List<string>();
        foreach (var field in form.Fields)
        {
            if (field.Name.Contains(':') || field.Name.Any(char.IsControl) || field.Selected.Length == 0 ||
                field.Selected.Any(v => v == null) && field.Selected.Length != 1)
            { throw new SubscriptionValidationException("The effective inputs cannot be represented accurately in a report link. Choose attachment only."); }
            foreach (var value in field.Selected)
            {
                pairs.Add(Uri.EscapeDataString(field.Name + (value == null ? ":isnull" : "")) + "=" + Uri.EscapeDataString(value ?? "true"));
            }
        }
        var link = options.PortalBaseUrl.TrimEnd('/') + ReportUrl.For(report) + (pairs.Count == 0 ? "" : "?" + string.Join('&', pairs));
        if (link.Length > options.MaxLinkCharacters || link.Length - options.PortalBaseUrl.Length > 224 * 1024)
        { throw new SubscriptionValidationException("The resolved report link exceeds the configured URL limit. Choose attachment only or fewer inputs."); }
        return link;
    }
    public async Task<PreparedSubscriptionReport> PrepareAsync(SubscriptionDefinition definition, CancellationToken ct)
    {
        var resolved = await ResolveAsync(definition, ct);
        await catalog.ResolvePathAsync(definition.ReportPath, ct);
        var info = await soap.LoadAsync(resolved.Report.Path, ct);
        if (info.CredentialsRequired) { throw new PortalException("credentials", 400); }
        var executionId = info.ExecutionId;
        if (resolved.Effective.Length > 0) { info = await soap.SetAsync(executionId, resolved.Effective, ct); }
        if (info.ExecutionId != executionId || info.CredentialsRequired || info.ParametersRequired) { throw new PortalException("values", 400); }
        RenderOutput? attachment = null;
        if (definition.Delivery != "link")
        {
            var format = OutputFormat.All[definition.Format];
            attachment = await soap.RenderAsync(executionId, format.Soap, 1, ct, includeFailureContent: false);
            ExecutionService.ValidateExport(attachment, format);
            if (attachment.Bytes.Length > options.MaxAttachmentBytes) { throw new SubscriptionValidationException("The report attachment exceeds the configured email limit."); }
        }
        await catalog.ResolvePathAsync(definition.ReportPath, ct);
        return new(resolved.Report.Name, definition.Delivery == "attachment" ? null : Link(resolved.Report, resolved.Form, resolved.Effective), attachment);
    }
}
