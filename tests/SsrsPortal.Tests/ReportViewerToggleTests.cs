using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using SsrsPortal.Services;

namespace SsrsPortal.Tests;

public sealed class ReportViewerToggleTests
{
    private const string Id = "230iT0R0x11";
    private const string Control = "ReportViewerControl_ctl09_ReportControl";
    private const string Resource = "/ReportServer/Reserved.ReportViewerWebControl.axd?OpType=Resource&Version=16.0.1115.92&Name=Microsoft.ReportingServices.Rendering.HtmlRenderer.RendererResources.TogglePlus.gif";
    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dev-reportviewer-toggle.html"));
    private static IDocument Source() => new HtmlParser().ParseDocument(Fixture());
    private static ToggleRenderContext Context() => new("/Training/Reports/Example", "SsrsPortalAction_" + new string('a', 64), new byte[32], 1, []);
    private static (IDocument Document, string Html) Sanitize(IDocument doc, ToggleRenderContext context)
    {
        using var h = new Harness();
        var html = new HtmlSafety(h.Options).Sanitize(Encoding.UTF8.GetBytes(doc.DocumentElement!.OuterHtml), "portal-session", "private-execution", [], context);
        return (new HtmlParser().ParseDocument(html), html);
    }

    [Theory]
    [InlineData(false, "16.0.1115.92", Control)]
    [InlineData(true, "17.2.12345.6", "ctl00_Main_OtherViewer_ctl17_ReportControl")]
    [InlineData(false, "15.0", "Viewer_ReportControl")]
    public void CapturedStructureUsesRuntimeIdPreservesSiblingAndPublishesPrivateNativeButton(bool expanded, string version, string control)
    {
        var doc = Source(); var anchor = doc.QuerySelector("a")!; var context = Context();
        foreach (var name in new[] { "onclick", "onkeypress" }) { anchor.SetAttribute(name, anchor.GetAttribute(name)!.Replace(Control, control, StringComparison.Ordinal)); }
        anchor.SetAttribute("aria-expanded", expanded ? "true" : "false");
        doc.QuerySelector("img")!.SetAttribute("src", Resource.Replace("16.0.1115.92", version, StringComparison.Ordinal).Replace("TogglePlus", expanded ? "ToggleMinus" : "TogglePlus", StringComparison.Ordinal));
        doc.QuerySelector("img")!.SetAttribute("alt", expanded ? "Collapse 15" : "Expand 15");
        var toggle = ReportToggle.Recognize(anchor, new Uri("https://synthetic.invalid/ReportServer"), "private-execution", context);
        Assert.NotNull(toggle); Assert.Equal(Id, toggle.Id); Assert.Equal(expanded, toggle.Expanded); Assert.Equal("ReportViewer InvokeReportAction", toggle.Syntax);
        var (safe, html) = Sanitize(doc, context);
        var button = Assert.Single(safe.QuerySelectorAll("button"));
        Assert.Equal("button", button.GetAttribute("type")); // Native focus and Enter/Space, wired by parent bridge in Chromium.
        Assert.Equal(expanded ? "true" : "false", button.GetAttribute("aria-expanded"));
        Assert.Equal((expanded ? "Collapse" : "Expand") + " report group: 15", button.GetAttribute("aria-label"));
        Assert.Equal("15", button.NextElementSibling!.TextContent);
        Assert.Equal(Id, context.Actions[button.GetAttribute("data-portal-toggle")!]);
        Assert.Matches("^[0-9a-f]{64}$", button.GetAttribute("data-portal-toggle"));
        Assert.DoesNotContain(Id, html); Assert.DoesNotContain(control, html);
        Assert.Empty(safe.QuerySelectorAll("[onclick],[onkeypress],[href],img,script"));
        Assert.Contains("flex-flow:row-reverse", html); Assert.Contains("justify-content:flex-start", html);
        Assert.Contains("width:12.45mm;min-width:12.45mm", html);
        Assert.Contains("html,body{margin:0;padding:0}", html);
    }

    [Theory]
    [InlineData("absolute")]
    [InlineData("default-port-host-case")]
    [InlineData("scheme-relative")]
    [InlineData("encoded-query")]
    [InlineData("no-keypress")]
    [InlineData("whitespace")]
    public void EquivalentSafeSourceMetadataRemainsRecognized(string kind)
    {
        var doc = Source(); var anchor = doc.QuerySelector("a")!;
        var resource = kind switch
        {
            "absolute" => "https://synthetic.invalid" + Resource,
            "default-port-host-case" => "https://SYNTHETIC.invalid:443" + Resource,
            "scheme-relative" => "//synthetic.invalid" + Resource,
            "encoded-query" => Resource.Replace("Version=", "%56ersion=", StringComparison.Ordinal).Replace("Name=Microsoft", "Name=%4dicrosoft", StringComparison.Ordinal),
            _ => Resource
        };
        doc.QuerySelector("img")!.SetAttribute("src", resource);
        if (kind == "no-keypress") { anchor.RemoveAttribute("onkeypress"); }
        if (kind == "whitespace") { anchor.SetAttribute("onclick", " \n" + anchor.GetAttribute("onclick")!.Replace(";", ";\n", StringComparison.Ordinal).Replace("var rp=", "var rp = ", StringComparison.Ordinal)); }
        var context = Context(); var (safe, _) = Sanitize(doc, context);
        Assert.Single(safe.QuerySelectorAll("button")); Assert.Equal(Id, Assert.Single(context.Actions).Value);
    }

    [Theory]
    [InlineData("action")]
    [InlineData("method")]
    [InlineData("id")]
    [InlineData("long-id")]
    [InlineData("prefix")]
    [InlineData("suffix")]
    [InlineData("variable")]
    [InlineData("guard")]
    [InlineData("object")]
    [InlineData("event")]
    [InlineData("return")]
    [InlineData("return-newline")]
    [InlineData("control-expression")]
    [InlineData("control-shape")]
    [InlineData("control-long")]
    [InlineData("keypress-id")]
    [InlineData("keypress-action")]
    [InlineData("keypress-control")]
    [InlineData("keypress-suffix")]
    [InlineData("keypress-prefix")]
    [InlineData("keypress-guard")]
    [InlineData("mouseover")]
    [InlineData("href")]
    public void UnexpectedExecutableSyntaxNeverRegistersAnAction(string kind)
    {
        var doc = Source(); var anchor = doc.QuerySelector("a")!;
        var action = anchor.GetAttribute("onclick")!;
        var keypress = anchor.GetAttribute("onkeypress")!;
        action = kind switch
        {
            "action" => action.Replace("'Toggle'", "'Sort'", StringComparison.Ordinal),
            "method" => action.Replace("InvokeReportAction", "Navigate", StringComparison.Ordinal),
            "id" => action.Replace(Id, "15');evil();//", StringComparison.Ordinal),
            "long-id" => action.Replace(Id, new string('x', 257), StringComparison.Ordinal),
            "prefix" => "evil();" + action,
            "suffix" => action + "evil();",
            "variable" => action.Replace("rp", "other", StringComparison.Ordinal),
            "guard" => action.Replace("rp&&rp.control", "rp||rp.control", StringComparison.Ordinal),
            "object" => action.Replace("rp.control.Invoke", "other.control.Invoke", StringComparison.Ordinal),
            "event" => action.Replace(",event)", ",window.event)", StringComparison.Ordinal),
            "return" => action.Replace("return false;", "return true;", StringComparison.Ordinal),
            "return-newline" => action.Replace("return false;", "return\nfalse;", StringComparison.Ordinal),
            "control-expression" => action.Replace("'" + Control + "'", "getControl()", StringComparison.Ordinal),
            "control-shape" => action.Replace(Control, "arbitrary", StringComparison.Ordinal),
            "control-long" => action.Replace(Control, new string('x', 256) + "_ReportControl", StringComparison.Ordinal),
            _ => action
        };
        keypress = kind switch
        {
            "keypress-id" => keypress.Replace(Id, "15", StringComparison.Ordinal),
            "keypress-action" => keypress.Replace("'Toggle'", "'Drillthrough'", StringComparison.Ordinal),
            "keypress-control" => keypress.Replace(Control, "Other_ReportControl", StringComparison.Ordinal),
            "keypress-suffix" => keypress + "evil();",
            "keypress-prefix" => "evil();" + keypress,
            "keypress-guard" => keypress.Replace("== 13", "== 32", StringComparison.Ordinal),
            _ => keypress
        };
        anchor.SetAttribute("onclick", action); anchor.SetAttribute("onkeypress", keypress);
        if (kind == "mouseover") { anchor.SetAttribute("onmouseover", "evil()"); }
        if (kind == "href") { anchor.SetAttribute("href", "#navigate"); }
        AssertInert(doc);
    }

    [Theory]
    [InlineData("resource-name")]
    [InlineData("resource-prefix")]
    [InlineData("origin")]
    [InlineData("scheme")]
    [InlineData("userinfo")]
    [InlineData("fragment")]
    [InlineData("path")]
    [InlineData("normalized-path")]
    [InlineData("encoded-path")]
    [InlineData("trailing-slash")]
    [InlineData("duplicate")]
    [InlineData("duplicate-case")]
    [InlineData("duplicate-encoded")]
    [InlineData("extra")]
    [InlineData("empty-pair")]
    [InlineData("no-query")]
    [InlineData("operation")]
    [InlineData("version")]
    [InlineData("version-long")]
    [InlineData("version-suffix")]
    [InlineData("bad-escape")]
    [InlineData("short-escape")]
    [InlineData("bad-utf8")]
    [InlineData("double-encoding")]
    [InlineData("aria")]
    [InlineData("aria-conflict")]
    [InlineData("aria-missing")]
    [InlineData("role")]
    [InlineData("extra-child")]
    [InlineData("image-script")]
    public void InvalidResourceStructureOrConflictingStateStaysInert(string kind)
    {
        var doc = Source(); var anchor = doc.QuerySelector("a")!;
        var resource = kind switch
        {
            "resource-name" => Resource.Replace("TogglePlus", "SortAsc", StringComparison.Ordinal),
            "resource-prefix" => Resource.Replace("Microsoft.ReportingServices", "Attacker.ReportingServices", StringComparison.Ordinal),
            "origin" => "https://evil.invalid" + Resource,
            "scheme" => "http://synthetic.invalid" + Resource,
            "userinfo" => "https://user@synthetic.invalid" + Resource,
            "fragment" => Resource + "#fragment",
            "path" => Resource.Replace("/ReportServer/", "/Other/", StringComparison.Ordinal),
            "normalized-path" => Resource.Replace("/ReportServer/", "/Other/../ReportServer/", StringComparison.Ordinal),
            "encoded-path" => Resource.Replace("/ReportServer/", "/%52eportServer/", StringComparison.Ordinal),
            "trailing-slash" => Resource.Replace(".axd?", ".axd/?", StringComparison.Ordinal),
            "duplicate" => Resource + "&Version=16.0.1115.92",
            "duplicate-case" => Resource + "&version=16.0.1115.92",
            "duplicate-encoded" => Resource + "&%56ersion=16.0.1115.92",
            "extra" => Resource + "&rs:SessionID=foreign",
            "empty-pair" => Resource + "&",
            "no-query" => Resource[..Resource.IndexOf('?')],
            "operation" => Resource.Replace("OpType=Resource", "OpType=Export", StringComparison.Ordinal),
            "version" => Resource.Replace("16.0.1115.92", "16..92", StringComparison.Ordinal),
            "version-long" => Resource.Replace("16.0.1115.92", "123456.0.1.2", StringComparison.Ordinal),
            "version-suffix" => Resource.Replace("16.0.1115.92", "16.0.1.2evil", StringComparison.Ordinal),
            "bad-escape" => Resource.Replace("Version=16", "Version=%GG16", StringComparison.Ordinal),
            "short-escape" => Resource + "%",
            "bad-utf8" => Resource.Replace("Name=", "Name=%FF", StringComparison.Ordinal),
            "double-encoding" => Resource.Replace("Name=Microsoft", "Name=%254dicrosoft", StringComparison.Ordinal),
            _ => Resource
        };
        doc.QuerySelector("img")!.SetAttribute("src", resource);
        if (kind == "aria") { anchor.SetAttribute("aria-expanded", "False"); }
        if (kind == "aria-conflict") { anchor.SetAttribute("aria-expanded", "true"); }
        if (kind == "aria-missing") { anchor.RemoveAttribute("aria-expanded"); }
        if (kind == "role") { anchor.SetAttribute("role", "link"); }
        if (kind == "extra-child") { anchor.AppendChild(doc.CreateElement("span")); }
        if (kind == "image-script") { doc.QuerySelector("img")!.SetAttribute("onerror", "evil()"); }
        AssertInert(doc);
    }

    private static void AssertInert(IDocument doc)
    {
        var context = Context(); var (safe, _) = Sanitize(doc, context);
        Assert.Empty(context.Actions); Assert.Empty(safe.QuerySelectorAll("button,[data-portal-toggle],[onclick],[onkeypress],[onmouseover],[onerror],[href],img,script"));
        Assert.Contains("15", safe.QuerySelector("td")!.TextContent);
    }

    [Fact]
    public void OrdinaryNumbersLinksAndStandaloneViewerImagesNeverBecomeControls()
    {
        var doc = new HtmlParser().ParseDocument("<table><tr><td>15</td><td><a href='/ordinary'>23</a></td></tr></table><img src='" + Resource + "'>");
        var context = Context(); var (safe, _) = Sanitize(doc, context);
        Assert.Empty(context.Actions); Assert.Empty(safe.QuerySelectorAll("button,img,[href]")); Assert.Contains("15", safe.Body!.TextContent);
    }
}
