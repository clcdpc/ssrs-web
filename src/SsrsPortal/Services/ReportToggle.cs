using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Microsoft.AspNetCore.WebUtilities;

namespace SsrsPortal.Services;

public sealed record ToggleRenderContext(string ReportPath, string Callback, byte[] Key, long Revision,
    Dictionary<string, string> Actions);

public enum ToggleRejection { None, Syntax, Icon, Structure, State }

// Evidence and deliberate compatibility boundaries: docs/behavior/report-expand-collapse.md.
// These parsers read metadata; they never evaluate or retain JavaScript.
public sealed partial record ReportToggle(string Id, bool Expanded, string Syntax)
{
    public static bool IsIdentifier(string? id) => id?.Length is > 0 and <= 256 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':');

    public string FocusKey(byte[] key) => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Id))).ToLowerInvariant();

    public static ReportToggle? Recognize(IElement element, Uri server, string executionId, ToggleRenderContext context)
        => Recognize(element, server, executionId, context, out _);

    public static ReportToggle? Recognize(IElement element, Uri server, string executionId, ToggleRenderContext context, out ToggleRejection rejection)
    {
        // Only aggregate potential-toggle failures. Ordinary report links are not
        // unsupported toggles, and no source values enter diagnostic messages.
        var potential = new[] { element.GetAttribute("onclick"), element.GetAttribute("href"), element.QuerySelector("img")?.GetAttribute("src") }
            .Any(v => v?.Contains("Toggle", StringComparison.OrdinalIgnoreCase) == true || v?.Contains("InvokeReportAction", StringComparison.Ordinal) == true);
        rejection = potential ? ToggleRejection.Syntax : ToggleRejection.None;
        var action = ReadAction(element, server, executionId, context);
        if (action == null) { return null; }
        rejection = ToggleRejection.Structure;
        // The renderer emits an anchor containing just its expand/collapse image.
        if (element.NamespaceUri != "http://www.w3.org/1999/xhtml" || element.LocalName != "a" ||
            element.Children.Length != 1 || element.FirstElementChild?.LocalName != "img" ||
            element.FirstElementChild.NamespaceUri != "http://www.w3.org/1999/xhtml" ||
            element.ChildNodes.OfType<IText>().Any(t => !string.IsNullOrWhiteSpace(t.Data)))
        {
            return null;
        }
        var image = element.FirstElementChild;
        if (image.Attributes.Any(a => a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase))) { return null; }
        rejection = ToggleRejection.Icon;
        if (!ReadIcon(image.GetAttribute("src"), server, executionId, context.ReportPath, out var expanded)) { return null; }
        rejection = ToggleRejection.State;
        var aria = element.GetAttribute("aria-expanded");
        if (aria != null && (aria is not "true" and not "false" || (aria == "true") != expanded)) { return null; }
        // The captured native viewer supplies both these indicators. Keep this
        // source-shape requirement independent of the nonce callback/URL formats.
        if (action.Value.Syntax == "ReportViewer InvokeReportAction" && (element.GetAttribute("role") != "button" || aria == null)) { return null; }
        rejection = ToggleRejection.None;
        return new ReportToggle(action.Value.Id, expanded, action.Value.Syntax);
    }

    private static (string Id, string Syntax)? ReadAction(IElement element, Uri server, string executionId, ToggleRenderContext context)
    {
        var href = element.GetAttribute("href");
        var onclick = element.GetAttribute("onclick");
        if (onclick != null)
        {
            // Independently recognize the nonce callback or captured viewer
            // grammar. A URL plus a handler is ambiguous and rejected.
            if (onclick.Length > 1024 || href != null ||
                element.Attributes.Any(a => a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase) && a.Name is not "onclick" and not "onkeypress")) { return null; }
            var match = CallbackAction().Match(onclick);
            var viewer = ViewerAction().Match(onclick);
            var portal = match.Success && match.Groups[1].Value == context.Callback;
            if (!portal && !viewer.Success) { return null; }
            var keypress = element.GetAttribute("onkeypress");
            if (keypress != null)
            {
                if (keypress.Length > 1200) { return null; }
                if (portal)
                {
                    if (keypress != "if(event.keyCode == 13 || event.which == 13){" + onclick + "}") { return null; }
                }
                else
                {
                    var key = ViewerKeypress().Match(keypress);
                    if (!key.Success) { return null; }
                    var keyAction = ViewerAction().Match(key.Groups[1].Value);
                    if (!keyAction.Success || keyAction.Groups[1].Value != viewer.Groups[1].Value || keyAction.Groups[2].Value != viewer.Groups[2].Value) { return null; }
                }
            }
            var callbackId = portal ? match.Groups[2].Value : viewer.Groups[2].Value;
            return IsIdentifier(callbackId) ? (callbackId, portal ? "ActionScript" : "ReportViewer InvokeReportAction") : null;
        }
        if (element.Attributes.Any(a => a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase))) { return null; }
        var uri = ServerUri(href, server);
        if (uri == null) { return null; }
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (!Single(query, "rs:SessionID", out var id) || id != executionId ||
            !Single(query, "rs:ShowHideToggle", out var toggle) || !IsIdentifier(toggle) ||
            !query.TryGetValue(context.ReportPath, out var path) || path.Count != 1 || !string.IsNullOrEmpty(path[0]) ||
            query.Any(p => p.Value.Count != 1 || !AllowedUrlKey(p.Key, context.ReportPath)) ||
            (query.ContainsKey("rs:Command") && query["rs:Command"][0] != "Render") ||
            (query.ContainsKey("rs:Format") && query["rs:Format"][0] != "HTML5"))
        {
            return null;
        }
        return (toggle, "ShowHideToggle URL");
    }

    private static bool ReadIcon(string? source, Uri server, string executionId, string reportPath, out bool expanded)
    {
        expanded = false;
        var resourcePath = server.AbsolutePath.TrimEnd('/') + "/Reserved.ReportViewerWebControl.axd";
        var uri = ServerUri(source, server, resourcePath);
        if (uri != null)
        {
            // Uri normalizes dot segments and encoded path characters. Require
            // the literal endpoint too, so alternate paths cannot masquerade as it.
            var sourceUrl = source!;
            var queryStart = sourceUrl.IndexOf('?');
            if (queryStart < 0) { return false; }
            var location = sourceUrl[..queryStart];
            var schemeEnd = location.IndexOf("://", StringComparison.Ordinal);
            var pathStart = location.StartsWith("//", StringComparison.Ordinal) ? location.IndexOf('/', 2) :
                schemeEnd >= 0 ? location.IndexOf('/', schemeEnd + 3) : 0;
            // Origin was already checked by Uri, including equivalent default
            // ports and host casing. Only the literal path needs a second check.
            if (pathStart < 0 || location[pathStart..] != resourcePath) { return false; }
            // Count raw pairs too: ParseQuery deliberately ignores empty pairs.
            // Exactly three single-valued, case-sensitive decoded names are allowed.
            var pairs = uri.Query.TrimStart('?').Split('&');
            var query = QueryHelpers.ParseQuery(uri.Query);
            if (pairs.Length != 3 || pairs.Any(p => p.Count(c => c == '=') != 1) || query.Count != 3 ||
                query.Any(p => p.Value.Count != 1 || p.Key is not "OpType" and not "Version" and not "Name") ||
                !Single(query, "OpType", out var operation) || operation != "Resource" ||
                !Single(query, "Version", out var version) || !RendererVersion().IsMatch(version) ||
                !Single(query, "Name", out var name) || !ViewerImage().IsMatch(name)) { return false; }
            expanded = name.EndsWith("ToggleMinus.gif", StringComparison.Ordinal);
            return true;
        }
        uri = ServerUri(source, server);
        if (uri == null) { return false; }
        var legacy = QueryHelpers.ParseQuery(uri.Query);
        if (!Single(legacy, "rs:Command", out var command) || command != "Get" ||
            !Single(legacy, "rc:GetImage", out var image) || !ToggleImage().IsMatch(image) ||
            legacy.Any(p => p.Value.Count != 1 || (p.Key != "rs:Command" && p.Key != "rc:GetImage" && p.Key != "rs:SessionID" && p.Key != reportPath)) ||
            (legacy.ContainsKey("rs:SessionID") && legacy["rs:SessionID"][0] != executionId)) { return false; }
        expanded = image.EndsWith("ToggleMinus.gif", StringComparison.Ordinal);
        return true;
    }

    private static bool AllowedUrlKey(string name, string path) => name == path || name is "rs:SessionID" or "rs:ShowHideToggle" or
        "rs:Command" or "rs:Format" or "rc:Section" or "rc:HTMLFragment" or "rc:ActionScript";

    private static bool Single(Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query, string name, out string value)
    {
        value = "";
        if (!query.TryGetValue(name, out var values) || values.Count != 1 || values[0] == null) { return false; }
        value = values[0]!;
        return true;
    }

    private static Uri? ServerUri(string? value, Uri server, string? path = null)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 4096 || value.Any(char.IsControl) ||
            (path != null && value.Any(char.IsWhiteSpace)) || value.Contains('\\') || !ValidEscapes(value) ||
            !Uri.TryCreate(server, value, out var uri) || uri.Scheme != server.Scheme || uri.Authority != server.Authority ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            (path == null ? uri.AbsolutePath.TrimEnd('/') != server.AbsolutePath.TrimEnd('/') : uri.AbsolutePath != path))
        {
            return null;
        }
        return uri;
    }

    private static bool ValidEscapes(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%') { continue; }
            if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2])) { return false; }
            i += 2;
        }
        return true;
    }

    // Bounded DOM control ID, fixed variable/guard/method/action/event/return.
    // Whitespace is permitted only between grammar tokens; no JS is evaluated.
    [GeneratedRegex(@"\A\s*var\s+rp\s*=\s*\$get\(\s*'([A-Za-z_][A-Za-z0-9_]{0,239}_ReportControl)'\s*\)\s*;\s*if\s*\(\s*rp\s*&&\s*rp\.control\s*\)\s*rp\.control\.InvokeReportAction\(\s*'Toggle'\s*,\s*'([A-Za-z0-9_.:\-]{1,256})'\s*,\s*event\s*\)\s*;\s*return[ \t]+false\s*;\s*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ViewerAction();
    [GeneratedRegex(@"\A\s*if\s*\(\s*event\.keyCode\s*==\s*13\s*\|\|\s*event\.which\s*==\s*13\s*\)\s*\{([^{}]{1,1024})\}\s*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ViewerKeypress();
    [GeneratedRegex(@"\A[0-9]{1,5}(?:\.[0-9]{1,5}){1,3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RendererVersion();
    [GeneratedRegex(@"\AMicrosoft\.ReportingServices\.Rendering\.HtmlRenderer\.RendererResources\.Toggle(?:Plus|Minus)\.gif\z", RegexOptions.CultureInvariant)]
    private static partial Regex ViewerImage();
    [GeneratedRegex(@"\A(SsrsPortalAction_[0-9a-f]{64})\('Toggle','([A-Za-z0-9_.:\-]{1,256})',event\);return false;\z", RegexOptions.CultureInvariant)]
    private static partial Regex CallbackAction();
    [GeneratedRegex(@"\A[A-Za-z0-9_.\-]{0,80}Toggle(?:Plus|Minus)\.gif\z", RegexOptions.CultureInvariant)]
    private static partial Regex ToggleImage();
}
