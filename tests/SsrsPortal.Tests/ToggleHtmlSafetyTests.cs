using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using SsrsPortal.Core;
using SsrsPortal.Services;

namespace SsrsPortal.Tests;

public sealed class ToggleHtmlSafetyTests
{
    private const string Callback = "SsrsPortalAction_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Server = "https://synthetic.invalid/ReportServer";
    private const string Id = "runtime-19iT0";
    private static string Icon(bool expanded = false) => $"<img src='{Server}?rs:Command=Get&amp;rc:GetImage=8.0.0Toggle{(expanded ? "Minus" : "Plus")}.gif' alt='Untrusted label'/>";
    private static string Action(string id = Id) => $"{Callback}('Toggle','{id}',event);return false;";
    private static string Link(string id = Id) => $"{Server}?%2FTraining%2FReports%2FExample&amp;rs:SessionID=private-execution&amp;rs:Format=HTML5&amp;rs%3aShowHideToggle={id}&amp;rc:Section=2";
    private static ToggleRenderContext Context() => new("/Training/Reports/Example", Callback, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), 7, new());
    private static (IDocument Document, string Html) Sanitize(Harness h, string html, ToggleRenderContext context)
    {
        var safe = new HtmlSafety(h.Options).Sanitize(Encoding.UTF8.GetBytes(html), new string('b', 64), "private-execution", [], context);
        return (new HtmlParser().ParseDocument(safe), safe);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActionScriptReplacementPreservesAdjacentNumericCellValues(bool expanded)
    {
        using var h = new Harness(); var context = Context();
        var (doc, safe) = Sanitize(h, $"<table><tr><td><a onclick=\"{Action()}\">{Icon(expanded)}</a><div>15</div></td><td>3</td></tr></table>", context);
        var button = Assert.Single(doc.QuerySelectorAll("button"));
        Assert.Equal("15", button.NextElementSibling!.TextContent);
        Assert.Equal("3", doc.QuerySelectorAll("td")[1].TextContent);
        Assert.Equal(Id, context.Actions[button.GetAttribute("data-portal-toggle")!]);
        Assert.DoesNotContain(Callback, safe); Assert.DoesNotContain(Id, safe);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DocumentedCallbackProducesOnlyPortalOwnedActionAndAccurateIconState(bool expanded)
    {
        using var h = new Harness(); var context = Context();
        var (doc, safe) = Sanitize(h, $"<a tabindex='1' id='{Id}' onclick=\"{Action()}\" onkeypress=\"if(event.keyCode == 13 || event.which == 13){{{Action()}}}\">{Icon(expanded)}</a>", context);
        var button = Assert.Single(doc.QuerySelectorAll("button"));
        Assert.Equal("button", button.GetAttribute("type"));
        Assert.Equal(expanded ? "true" : "false", button.GetAttribute("aria-expanded"));
        Assert.Equal(expanded ? "Collapse report group" : "Expand report group", button.GetAttribute("aria-label"));
        Assert.Equal(expanded ? "−" : "+", button.TextContent);
        Assert.Equal(Id, context.Actions[button.GetAttribute("data-portal-toggle")!]);
        Assert.Matches("^[0-9a-f]{64}$", button.GetAttribute("data-portal-focus"));
        Assert.Equal("7", doc.QuerySelector("meta[name=ssrs-portal-revision]")!.GetAttribute("content"));
        foreach (var forbidden in new[] { Id, "private-execution", Server, "onclick", "onkeypress", "javascript:", Callback, "<script" }) { Assert.DoesNotContain(forbidden, safe); }
    }

    [Theory]
    [InlineData("absolute")]
    [InlineData("root-relative")]
    [InlineData("query-relative")]
    public void SessionBoundReportUrlSupportsEncodedNamesAndRelativeRepresentations(string kind)
    {
        using var h = new Harness(); var context = Context();
        var href = Link();
        if (kind == "root-relative") { href = href.Replace("https://synthetic.invalid", "", StringComparison.Ordinal); }
        if (kind == "query-relative") { href = href.Replace(Server, "", StringComparison.Ordinal); }
        var (doc, _) = Sanitize(h, $"<a href='{href}'>{Icon()}</a>", context);
        Assert.Single(doc.QuerySelectorAll("button[data-portal-toggle]")); Assert.Equal(Id, Assert.Single(context.Actions).Value);
    }

    [Fact]
    public void LegacyReportUrlRetainsSupportForReportPathsContainingLiteralSpaces()
    {
        using var h = new Harness(); var context = Context() with { ReportPath = "/Training/Reports/Example With Space" };
        var href = Link().Replace("%2FTraining%2FReports%2FExample", "/Training/Reports/Example With Space", StringComparison.Ordinal);
        var (doc, _) = Sanitize(h, $"<a href='{href}'>{Icon()}</a>", context);
        Assert.Single(doc.QuerySelectorAll("button")); Assert.Equal(Id, Assert.Single(context.Actions).Value);
    }

    [Theory]
    [InlineData("wrong-callback")]
    [InlineData("prefix-code")]
    [InlineData("suffix-code")]
    [InlineData("other-action")]
    [InlineData("escape")]
    [InlineData("entity-escape")]
    [InlineData("too-long")]
    [InlineData("ambiguous-href")]
    [InlineData("extra-image")]
    [InlineData("fake-icon")]
    [InlineData("remote-icon")]
    [InlineData("bad-keypress")]
    [InlineData("forged-data")]
    public void DisguisedScriptAndUnrecognizedCallbackMetadataRemainInert(string kind)
    {
        using var h = new Harness(); var context = Context();
        var action = kind switch
        {
            "wrong-callback" => Action().Replace("aaaa", "bbbb", StringComparison.Ordinal),
            "prefix-code" => "evil();" + Action(),
            "suffix-code" => Action() + "evil();",
            "other-action" => Action().Replace("'Toggle'", "'Sort'", StringComparison.Ordinal),
            "escape" => Action("x\\x27);evil();//"),
            "entity-escape" => Action("x&#39;);evil();//"),
            "too-long" => Action(new string('x', 257)),
            _ => Action()
        };
        var icon = kind == "fake-icon" ? "<span>+</span>" : kind == "remote-icon" ? Icon().Replace("synthetic.invalid", "evil.invalid", StringComparison.Ordinal) : Icon();
        var extra = kind == "ambiguous-href" ? $" href='{Link()}'" : kind == "bad-keypress" ? " onkeypress='evil()'" : "";
        var html = kind == "forged-data" ? "<button type='button' data-portal-toggle='forged'>+</button><a data-portal-toggle='forged'>+</a>" :
            $"<a onclick=\"{action}\"{extra}>{icon}{(kind == "extra-image" ? Icon() : "")}</a>";
        var (doc, safe) = Sanitize(h, html, context);
        Assert.Empty(context.Actions); Assert.Empty(doc.QuerySelectorAll("button,[data-portal-toggle]"));
        Assert.Empty(doc.QuerySelectorAll("[onclick],[onkeypress],[href],script"));
        Assert.DoesNotContain(Server, safe); Assert.DoesNotContain("private-execution", safe);
    }

    [Theory]
    [InlineData("foreign-session")]
    [InlineData("foreign-report")]
    [InlineData("foreign-origin")]
    [InlineData("duplicate-toggle")]
    [InlineData("duplicate-session")]
    [InlineData("other-command")]
    [InlineData("other-format")]
    [InlineData("unknown-field")]
    [InlineData("missing-session")]
    [InlineData("injected-id")]
    [InlineData("fragment")]
    public void UrlRecognitionRejectsForeignMalformedAmbiguousOrUnsupportedActions(string kind)
    {
        using var h = new Harness(); var context = Context();
        var link = kind switch
        {
            "foreign-session" => Link().Replace("private-execution", "foreign", StringComparison.Ordinal),
            "foreign-report" => Link().Replace("Example", "Other", StringComparison.Ordinal),
            "foreign-origin" => Link().Replace("synthetic.invalid", "evil.invalid", StringComparison.Ordinal),
            "duplicate-toggle" => Link() + "&amp;rs:ShowHideToggle=other",
            "duplicate-session" => Link() + "&amp;rs:SessionID=private-execution",
            "other-command" => Link() + "&amp;rs:Command=ResetExecution",
            "other-format" => Link().Replace("HTML5", "PDF", StringComparison.Ordinal),
            "unknown-field" => Link() + "&amp;drillthrough=other",
            "missing-session" => Link().Replace("&amp;rs:SessionID=private-execution", "", StringComparison.Ordinal),
            "injected-id" => Link("x%27%3Bevil%28%29"),
            "fragment" => Link() + "#extra",
            _ => throw new InvalidOperationException()
        };
        var (doc, _) = Sanitize(h, $"<a href='{link}'>{Icon()}</a>", context);
        Assert.Empty(context.Actions); Assert.Empty(doc.QuerySelectorAll("button,[href]"));
        Assert.Equal("true", doc.QuerySelector("a")!.GetAttribute("aria-disabled"));
    }

    [Fact]
    public void ContradictoryDuplicateActionsAreInertAndMatchingDuplicatesShareOnlyFocusIdentity()
    {
        using var h = new Harness(); var context = Context();
        var (doc, _) = Sanitize(h, $"<a onclick=\"{Action()}\">{Icon()}</a><a href='{Link()}'>{Icon(true)}</a>", context);
        Assert.Empty(context.Actions); Assert.Empty(doc.QuerySelectorAll("button"));
        var matching = Context();
        (doc, _) = Sanitize(h, $"<a onclick=\"{Action()}\">{Icon()}</a><a href='{Link()}'>{Icon()}</a>", matching);
        Assert.Equal(2, matching.Actions.Count);
        Assert.Single(doc.QuerySelectorAll("button").Select(b => b.GetAttribute("data-portal-focus")).Distinct());
        Assert.Equal(2, doc.QuerySelectorAll("button").Select(b => b.GetAttribute("data-portal-toggle")).Distinct().Count());
    }

    [Fact]
    public void ActionRegistrationIsBoundedAndFocusKeysArePrivateToSession()
    {
        using var h = new Harness(o => o.MaxToggleActions = 1); var context = Context();
        Assert.Equal("limit", Assert.Throws<PortalException>(() => Sanitize(h,
            $"<a onclick=\"{Action()}\">{Icon()}</a><a href='{Link("other")}'>" + Icon() + "</a>", context)).Code);
        Assert.Empty(context.Actions);
        var first = new ReportToggle(Id, false, "test").FocusKey(context.Key);
        Assert.NotEqual(first, new ReportToggle(Id, false, "test").FocusKey(new byte[32]));
    }

    [Theory]
    [InlineData("form")]
    [InlineData("svg")]
    public void ControlsRemovedWithUntrustedAncestorsDoNotRegisterActions(string tag)
    {
        using var h = new Harness(); var context = Context();
        var (doc, _) = Sanitize(h, $"<{tag}><a onclick=\"{Action()}\">{Icon()}</a></{tag}>", context);
        Assert.Empty(context.Actions); Assert.Empty(doc.QuerySelectorAll("button"));
    }

    [Fact]
    public void RuntimeDomIdsAreRemovedAndLabelsUseBoundedCellContext()
    {
        using var h = new Harness(); var context = Context();
        var (doc, html) = Sanitize(h, $"<table><tr><td id='{Id}'><a onclick=\"{Action()}\">{Icon()}</a> Library</td></tr></table>", context);
        Assert.Null(doc.QuerySelector("td[id]")); Assert.DoesNotContain(Id, html);
        Assert.Equal("Expand report group: Library", doc.QuerySelector("button")!.GetAttribute("aria-label"));
    }
}
