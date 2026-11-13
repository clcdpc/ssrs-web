using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.WebUtilities;
using SsrsPortal.Core;

namespace SsrsPortal.Services;

public sealed partial class HtmlSafety(PortalOptions options, ILogger<HtmlSafety>? logger = null)
{
    private static readonly HashSet<string> Tags = ["div", "span", "p", "br", "hr", "table", "tbody", "thead", "tfoot", "tr", "td", "th",
        "caption", "colgroup", "col", "img", "style", "link", "h1", "h2", "h3", "h4", "h5", "h6", "b", "strong", "em", "i", "u", "s", "ul", "ol", "li", "pre", "a"];
    private static readonly HashSet<string> Attributes = ["class", "id", "title", "alt", "width", "height", "colspan", "rowspan", "scope", "aria-label", "role"];
    private static readonly HashSet<string> CssProperties = ["color", "background-color", "font-family", "font-size", "font-style", "font-weight", "text-align", "text-decoration",
        "vertical-align", "white-space", "word-wrap", "overflow", "overflow-x", "overflow-y", "display", "visibility", "border", "border-color", "border-width", "border-style",
        "border-collapse", "border-spacing", "border-top", "border-bottom", "border-left", "border-right", "width", "height", "min-width", "max-width", "min-height", "max-height",
        "padding", "padding-top", "padding-right", "padding-bottom", "padding-left", "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
        "flex-flow", "-ms-flex-flow", "-webkit-flex-flow", "justify-content"];

    public string Sanitize(byte[] bytes, string session, string executionId, Dictionary<string, string> streams, ToggleRenderContext? toggles = null)
    {
        string html;
        try
        {
            html = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new PortalException("resource", message: "SSRS HTML output is not valid UTF-8.", innerException: exception);
        }
        var doc = new HtmlParser().ParseDocument(html);
        var recognized = new Dictionary<IElement, ReportToggle>();
        var privateIds = new HashSet<string>(StringComparer.Ordinal);
        var rejected = new int[Enum.GetValues<ToggleRejection>().Length];
        if (toggles != null)
        {
            foreach (var anchor in doc.QuerySelectorAll("a"))
            {
                var toggle = ReportToggle.Recognize(anchor, new Uri(options.ReportServer), executionId, toggles, out var reason);
                if (toggle != null) { recognized.Add(anchor, toggle); privateIds.Add(toggle.Id); }
                else if (reason != ToggleRejection.None) { rejected[(int)reason] = Math.Min(10000, rejected[(int)reason] + 1); }
                if (recognized.Count > options.MaxToggleActions) { throw new PortalException("limit"); }
            }
            // Repeated controls are allowed, but contradictory state for one runtime
            // identifier is ambiguous. Leave every occurrence of that action inert.
            foreach (var group in recognized.GroupBy(p => p.Value.Id).Where(g => g.Select(p => p.Value.Expanded).Distinct().Count() > 1).ToArray())
            {
                foreach (var entry in group)
                {
                    recognized.Remove(entry.Key);
                    rejected[(int)ToggleRejection.State] = Math.Min(10000, rejected[(int)ToggleRejection.State] + 1);
                }
            }
        }
        var callbacks = 0;
        var urls = 0;
        var viewers = 0;
        foreach (var element in doc.All.ToArray())
        {
            if (element.LocalName is "html" or "head" or "body")
            {
                continue;
            }
            if (!Tags.Contains(element.LocalName))
            {
                element.Remove();
                continue;
            }
            var src = element.GetAttribute("src");
            var href = element.GetAttribute("href");
            var style = element.GetAttribute("style");
            var cellPadding = element.GetAttribute("cellpadding");
            var cellSpacing = element.GetAttribute("cellspacing");
            foreach (var attr in element.Attributes.ToArray())
            {
                if (!Attributes.Contains(attr.Name) || (attr.Name == "id" && privateIds.Any(id => attr.Value.Contains(id, StringComparison.Ordinal))))
                {
                    element.RemoveAttribute(attr.Name);
                }
            }
            if (style != null)
            {
                element.SetAttribute("style", Declarations(style));
            }
            if (element.LocalName == "table")
            {
                // Presentation attributes have lower priority than authored CSS.
                // SSRS layout tables need zero defaults, including their sizing
                // cells; a td padding reset would erase real report formatting.
                element.SetAttribute("cellpadding", TableSpacing(cellPadding));
                element.SetAttribute("cellspacing", TableSpacing(cellSpacing));
            }
            if (element.LocalName == "style")
            {
                element.TextContent = Css(element.TextContent);
            }
            if (element.LocalName == "a")
            {
                if (toggles != null && recognized.TryGetValue(element, out var toggle))
                {
                    var root = element;
                    while (root.ParentElement != null) { root = root.ParentElement; }
                    if (root != doc.DocumentElement) { continue; }
                    var token = SessionStore.RandomToken();
                    toggles.Actions.Add(token, toggle.Id);
                    if (toggle.Syntax == "ActionScript") { callbacks++; }
                    else if (toggle.Syntax == "ShowHideToggle URL") { urls++; }
                    else { viewers++; }
                    var button = doc.CreateElement("button");
                    button.SetAttribute("type", "button");
                    button.SetAttribute("class", "portal-report-toggle" + (toggle.Syntax == "ReportViewer InvokeReportAction" ? " portal-report-toggle-compact" : ""));
                    button.SetAttribute("data-portal-toggle", token);
                    button.SetAttribute("data-portal-focus", toggle.FocusKey(toggles.Key));
                    button.SetAttribute("aria-expanded", toggle.Expanded ? "true" : "false");
                    var label = (toggle.Expanded ? "Collapse" : "Expand") + " report group";
                    button.SetAttribute("aria-label", label);
                    button.SetAttribute("title", label);
                    button.TextContent = toggle.Expanded ? "−" : "+";
                    element.Replace(button);
                }
                else
                {
                    element.SetAttribute("aria-disabled", "true");
                    element.SetAttribute("title", "Report navigation and interactive actions are unavailable in this viewer.");
                }
            }
            if (element.LocalName == "img")
            {
                var rewritten = Map(src, executionId, session, streams);
                if (rewritten == null)
                {
                    element.Remove();
                }
                else
                {
                    element.SetAttribute("src", rewritten);
                    element.SetAttribute("alt", element.GetAttribute("alt") ?? "Report chart");
                }
            }
            if (element.LocalName == "link")
            {
                var rewritten = Map(href, executionId, session, streams);
                if (rewritten == null)
                {
                    element.Remove();
                }
                else
                {
                    element.SetAttribute("rel", "stylesheet");
                    element.SetAttribute("href", rewritten);
                }
            }
        }
        foreach (var button in doc.QuerySelectorAll("button[data-portal-toggle]"))
        {
            var parent = button.ParentElement;
            if (parent?.QuerySelectorAll("button").Length == 1)
            {
                var context = string.Join(' ', parent.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                context = new string(context.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());
                if (context.StartsWith(button.TextContent, StringComparison.Ordinal)) { context = context[button.TextContent.Length..].Trim(); }
                if (context.Length > 120) { context = context[..120]; }
                if (context.Length > 0)
                {
                    var label = button.GetAttribute("aria-label") + ": " + context;
                    button.SetAttribute("aria-label", label);
                    button.SetAttribute("title", label);
                }
            }
        }
        // Discard head metadata and source wrapper attributes except sanitized
        // font declarations, so explicit document typography beats the fallback.
        var htmlFont = DocumentFont(doc.DocumentElement!);
        var bodyFont = DocumentFont(doc.Body!);
        var safe = string.Concat(doc.Head!.Children.Where(e => e.LocalName is "style" or "link").Select(e => e.OuterHtml)) + doc.Body!.InnerHtml;
        if (new[] { safe, htmlFont, bodyFont }.Any(content => content.Contains(executionId, StringComparison.Ordinal) ||
            content.Contains(options.ReportServer, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PortalException("resource");
        }
        if (options.Diagnostics.Enabled)
        {
            logger?.LogInformation("Report toggle recognition: {Actions} registered controls, {Callbacks} ActionScript, {Urls} ShowHideToggle URL, {Viewers} ReportViewer InvokeReportAction; rejected potential toggles: {Syntax} syntax, {Icons} icon/resource, {Structure} structure, {State} state",
                toggles?.Actions.Count ?? 0, callbacks, urls, viewers, rejected[(int)ToggleRejection.Syntax], rejected[(int)ToggleRejection.Icon], rejected[(int)ToggleRejection.Structure], rejected[(int)ToggleRejection.State]);
        }
        return "<!doctype html><html lang=\"en\"" + htmlFont + "><head><title>Report output</title><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"ssrs-portal-report\" content=\"ready\">" +
            "<meta name=\"ssrs-portal-revision\" content=\"" + (toggles?.Revision ?? 0) + "\">" +
            "<style>html,body{margin:0;padding:0}html{font-family:'Times New Roman',serif;font-size:10pt}button.portal-report-toggle{font:700 14px/1 sans-serif;color:#213951;background:#fff;border:1px solid #687c90;border-radius:2px;min-width:24px;min-height:24px;padding:2px;cursor:pointer;vertical-align:middle}button.portal-report-toggle:focus-visible{outline:3px solid #ab5c00;outline-offset:2px}button.portal-report-toggle[data-portal-pointer-focus=true]:focus-visible{outline:none}button.portal-report-toggle[aria-disabled=true]{cursor:default;opacity:.6}@media(pointer:coarse){button.portal-report-toggle{min-width:44px;min-height:44px}}button.portal-report-toggle-compact{box-sizing:border-box;flex:none;min-width:0;min-height:0;width:16px;height:16px;padding:0}</style>" +
            "</head><body" + bodyFont + ">" + safe + "</body></html>";
    }

    private static string TableSpacing(string? value)
    {
        // HTML table spacing is an unsigned integer in CSS pixels. Bound it to
        // 1000px (well beyond normal report spacing), rejecting CSS/URL syntax.
        value = value?.Trim();
        return value is { Length: > 0 and <= 4 } &&
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pixels) && pixels <= 1000
            ? pixels.ToString(CultureInfo.InvariantCulture) : "0";
    }

    private static string DocumentFont(IElement element)
    {
        var fonts = Declarations(element.GetAttribute("style") ?? "").Split(';')
            .Where(d => d.StartsWith("font-family:", StringComparison.Ordinal) || d.StartsWith("font-size:", StringComparison.Ordinal) ||
                d.StartsWith("font-style:", StringComparison.Ordinal) || d.StartsWith("font-weight:", StringComparison.Ordinal));
        var style = string.Join(';', fonts);
        return style.Length == 0 ? "" : " style=\"" + WebUtility.HtmlEncode(style) + "\"";
    }

    private string? Map(string? reference, string id, string session, Dictionary<string, string> streams)
    {
        if (reference == null || reference.Length > 2048 || reference.Any(char.IsControl) || reference.Contains('\\'))
        {
            return null;
        }
        var root = new Uri(options.ReportServer.TrimEnd('/') + "/");
        if (!Uri.TryCreate(root, reference, out var uri) || uri.Scheme != root.Scheme || uri.Authority != root.Authority ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath.TrimEnd('/') != root.AbsolutePath.TrimEnd('/'))
        {
            return null;
        }
        var query = QueryHelpers.ParseQuery(uri.Query);
        // SSRS HTML renderer: ?/ReportPath&rs:SessionID=...&rs:Format=HTML5&rs:ImageID=...
        if (!query.TryGetValue("rs:ImageID", out var image) || image.Count != 1 ||
            !query.TryGetValue("rs:SessionID", out var execution) || execution.Count != 1 || execution[0] != id ||
            !query.TryGetValue("rs:Format", out var format) || format.Count != 1 || format[0] != "HTML5")
        {
            return null;
        }
        foreach (var pair in query)
        {
            if (pair.Key is not "rs:ImageID" and not "rs:SessionID" and not "rs:Format" && !pair.Key.StartsWith('/'))
            {
                return null;
            }
        }
        var token = streams.FirstOrDefault(p => p.Value == image[0]).Key;
        return token == null ? null : "/api/executions/" + session + "/streams/" + token;
    }

    public static string Declarations(string css)
    {
        var result = new List<string>();
        foreach (var part in css.Split(';'))
        {
            var colon = part.IndexOf(':');
            if (colon < 1)
            {
                continue;
            }
            var property = part[..colon].Trim().ToLowerInvariant();
            var value = part[(colon + 1)..].Trim();
            if (CssProperties.Contains(property) && CssValue().IsMatch(value) &&
                !value.Contains("url", StringComparison.OrdinalIgnoreCase) && !value.Contains("expression", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(property + ":" + value);
            }
        }
        return string.Join(';', result);
    }

    public static string Css(string css)
    {
        if (css.Length > 1024 * 1024)
        {
            throw new PortalException("limit");
        }
        var result = new StringBuilder();
        foreach (Match rule in CssRule().Matches(css))
        {
            var selector = rule.Groups[1].Value.Trim();
            if (CssSelector().IsMatch(selector))
            {
                result.Append(selector).Append('{').Append(Declarations(rule.Groups[2].Value)).Append('}');
            }
        }
        return result.ToString();
    }

    [GeneratedRegex(@"\A[a-zA-Z0-9#\s.,%()+\-\""']{1,200}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CssValue();
    [GeneratedRegex(@"([^{}]+)\{([^{}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex CssRule();
    [GeneratedRegex(@"\A[a-zA-Z0-9#._\s,>:+\-\[\]=\""']{1,300}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CssSelector();
}
