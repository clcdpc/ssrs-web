using System.Text;
using SsrsPortal.Core;
using SsrsPortal.Services;
namespace SsrsPortal.Tests;

public sealed class HtmlSafetyTests
{
    [Fact]
    public void UntrustedMarkupAndCssCannotExecuteNavigateLoadRemoteUrlsOrExposeSession()
    {
        using var h = new Harness(); var sanitizer = new HtmlSafety(h.Options);
        var streamMap = new Dictionary<string, string> { [new string('b', 64)] = "known" };
        var html = "<base href='https://untrusted.invalid'><meta http-equiv='refresh' content='0;url=https://untrusted.invalid'>" +
            "<script>alert(1)</script><svg onload=alert(1)><a href='javascript:alert(1)'>x</a></svg><form action='/signout'><input value='leak'></form>" +
            "<div onclick='alert(1)' style='color:red;background:url(https://untrusted.invalid);position:fixed;behavior:evil'>Safe</div>" +
            "<a href='/api/executions/other/exports/csv'>Drill through</a><img src='data:image/svg+xml,evil'><img src='//untrusted.invalid/ReportServer?rs:ImageID=known'>" +
            "<style>@import 'https://untrusted.invalid';.ok{color:blue;background-image:url(https://untrusted.invalid)}.danger{width:expression(alert(1))}</style>" +
            "<img src='https://synthetic.invalid/ReportServer?/Training/Example&amp;rs:SessionID=private-id&amp;rs:Format=HTML5&amp;rs:ImageID=known' alt='chart'>";
        var safe = sanitizer.Sanitize(Encoding.UTF8.GetBytes(html), new string('a', 64), "private-id", streamMap);
        foreach (var forbidden in new[] { "<script", "<svg", "<form", "<input", "<base", "http-equiv", "onclick", "private-id", "untrusted.invalid", "data:", "javascript:", "href=", "url(", "expression(", "behavior", "position:fixed" })
        {
            Assert.DoesNotContain(forbidden, safe);
        }
        Assert.Contains("Safe", safe); Assert.Contains("color:red", safe); Assert.Contains("/streams/" + new string('b', 64), safe);
    }

    [Theory]
    [InlineData("https://untrusted.invalid/ReportServer?rs:SessionID=private-id&rs:Format=HTML5&rs:ImageID=known")]
    [InlineData("https://synthetic.invalid/ReportServer?rs:SessionID=someone-else&rs:Format=HTML5&rs:ImageID=known")]
    [InlineData("https://synthetic.invalid/ReportServer?rs:SessionID=private-id&rs:Format=HTML5&rs:ImageID=unknown")]
    [InlineData("https://synthetic.invalid/ReportServer?rs:SessionID=private-id&rs:Format=HTML5&rs:ImageID=known&rs:Command=Render")]
    [InlineData("https://synthetic.invalid/ReportServer?rs:SessionID=private-id&rs:SessionID=other&rs:Format=HTML5&rs:ImageID=known")]
    [InlineData("https://user@synthetic.invalid/ReportServer?rs:SessionID=private-id&rs:Format=HTML5&rs:ImageID=known")]
    [InlineData("https://synthetic.invalid/ReportServer/../Other?rs:SessionID=private-id&rs:Format=HTML5&rs:ImageID=known")]
    [InlineData("../ReportServer?rs:ImageID=known")]
    public void UnsafeOrUnmappedResourceUrlsAreRemoved(string url)
    {
        using var h = new Harness(); var safety = new HtmlSafety(h.Options);
        var safe = safety.Sanitize(Encoding.UTF8.GetBytes("<img src='" + url.Replace("&", "&amp;", StringComparison.Ordinal) + "'>"), "portal", "private-id", new() { ["token"] = "known" });
        Assert.DoesNotContain("<img", safe);
    }

    [Fact]
    public void InvalidUtf8AndExecutionIdInReportContentFailClosed()
    {
        using var h = new Harness(); var safety = new HtmlSafety(h.Options);
        Assert.Equal("resource", Assert.Throws<PortalException>(() => safety.Sanitize([255], "portal", "private-id", [])).Code);
        Assert.Equal("resource", Assert.Throws<PortalException>(() => safety.Sanitize(Encoding.UTF8.GetBytes("private-id"), "portal", "private-id", [])).Code);
    }
}
