using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using SsrsPortal.Core;
using SsrsPortal.Services;

namespace SsrsPortal.Tests;

public sealed class HtmlSafetyLayoutTests
{
    private static IDocument Sanitize(string html)
    {
        using var h = new Harness();
        return new HtmlParser().ParseDocument(new HtmlSafety(h.Options).Sanitize(Encoding.UTF8.GetBytes(html), "portal-session", "private-execution", []));
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("1", "1")]
    [InlineData("3", "3")]
    [InlineData("24", "24")]
    [InlineData("1000", "1000")]
    [InlineData(" 002 ", "2")]
    public void TableSpacingPreservesBoundedUnsignedIntegers(string source, string expected)
    {
        var doc = Sanitize($"<table cellpadding='{source}' cellspacing='{source}'><tr><td>Report</td></tr></table>");
        var table = doc.QuerySelector("table")!;
        Assert.Equal(expected, table.GetAttribute("cellpadding"));
        Assert.Equal(expected, table.GetAttribute("cellspacing"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.5")]
    [InlineData("2px")]
    [InlineData("10%")]
    [InlineData("1e2")]
    [InlineData("0x10")]
    [InlineData("1001")]
    [InlineData("2147483648")]
    [InlineData("00000")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("\u0662")]
    [InlineData("2\n3")]
    [InlineData("2;background:url(https://untrusted.invalid)")]
    [InlineData("expression(alert(1))")]
    [InlineData("javascript:alert(1)")]
    [InlineData("&quot; onmouseover=&quot;alert(1)")]
    public void InvalidTableSpacingIsDiscardedAndUsesZeroDefault(string source)
    {
        var doc = Sanitize($"<table cellpadding='{source}' cellspacing='{source}'><tr><td>Report</td></tr></table>");
        var table = doc.QuerySelector("table")!;
        Assert.Equal("0", table.GetAttribute("cellpadding"));
        Assert.Equal("0", table.GetAttribute("cellspacing"));
        Assert.Empty(doc.QuerySelectorAll("[onmouseover],script"));
        Assert.DoesNotContain("untrusted.invalid", doc.DocumentElement!.OuterHtml);
    }

    [Fact]
    public void SpacingAttributesAreTableOnlyAndOmittedTablesDefaultToZero()
    {
        var doc = Sanitize("<div cellpadding='5' cellspacing='6'><table><tr cellpadding='2' cellspacing='3'><td cellpadding='8' cellspacing='9'><span cellpadding='1' cellspacing='2'>Value</span><table><tr><td>Nested</td></tr></table></td></tr></table></div>");
        Assert.Equal(2, doc.QuerySelectorAll("table").Length);
        Assert.All(doc.QuerySelectorAll("table"), t => { Assert.Equal("0", t.GetAttribute("cellpadding")); Assert.Equal("0", t.GetAttribute("cellspacing")); });
        Assert.All(doc.QuerySelectorAll("[cellpadding],[cellspacing]"), t => Assert.Equal("table", t.LocalName));
    }

    [Theory]
    [InlineData("cellpadding", "cellspacing")]
    [InlineData("cellspacing", "cellpadding")]
    public void OneOmittedSpacingAttributeDoesNotEraseTheAuthoredAttribute(string authored, string omitted)
    {
        var table = Sanitize($"<table {authored}='7'><tr><td>Report</td></tr></table>").QuerySelector("table")!;
        Assert.Equal("7", table.GetAttribute(authored));
        Assert.Equal("0", table.GetAttribute(omitted));
    }

    [Fact]
    public void IndependentNestedFixturePreservesDimensionsCellStylesAndExplicitFonts()
    {
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "holds-ratio-layout.html"));
        var source = new HtmlParser().ParseDocument(raw);
        var doc = Sanitize(raw);
        Assert.Equal(5, doc.QuerySelectorAll("table").Length);
        Assert.Equal(18, doc.QuerySelectorAll("#column-headings th").Length);
        Assert.Equal(source.QuerySelectorAll("[style]").Select(n => n.GetAttribute("style")),
            doc.QuerySelectorAll("[style]").Select(n => n.GetAttribute("style")));
        Assert.Equal("3", doc.QuerySelector("#spacing-sample")!.GetAttribute("cellpadding"));
        Assert.Equal("4", doc.QuerySelector("#spacing-sample")!.GetAttribute("cellspacing"));
        Assert.Equal("0", doc.QuerySelector("#ssrs-section")!.GetAttribute("cellpadding"));
        Assert.Equal("0", doc.QuerySelector("#ssrs-section")!.GetAttribute("cellspacing"));
        Assert.Contains("html{font-family:'Times New Roman',serif;font-size:10pt}", doc.Head!.TextContent);
    }

    [Fact]
    public void DocumentFontOverridesSurviveWithoutOtherSourceWrapperAttributes()
    {
        var doc = Sanitize("<html style='font-family:Arial;font-size:11pt;position:fixed' onclick='alert(1)'><head><style>body{font-family:Verdana;font-size:12pt}.special{font-family:Georgia;font-size:14pt}</style></head><body id='source' onload='alert(1)' style='font-family:&quot;Courier New&quot;;font-size:9pt;font-weight:bold;background:url(https://untrusted.invalid);width:100%'><div class='special'>Report</div></body></html>");
        Assert.Equal("font-family:Arial;font-size:11pt", doc.DocumentElement!.GetAttribute("style"));
        Assert.Equal("font-family:\"Courier New\";font-size:9pt;font-weight:bold", doc.Body!.GetAttribute("style"));
        Assert.Null(doc.Body.GetAttribute("id"));
        Assert.Empty(doc.QuerySelectorAll("[onclick],[onload],script"));
        Assert.Contains("body{font-family:Verdana;font-size:12pt}", doc.Body.InnerHtml);
        Assert.Contains(".special{font-family:Georgia;font-size:14pt}", doc.Body.InnerHtml);
        Assert.DoesNotContain("untrusted.invalid", doc.DocumentElement.OuterHtml);
    }

    [Theory]
    [InlineData("html")]
    [InlineData("body")]
    public void PrivateExecutionIdInDocumentFontFailsClosed(string tag)
    {
        using var h = new Harness();
        Assert.Equal("resource", Assert.Throws<PortalException>(() => new HtmlSafety(h.Options).Sanitize(
            Encoding.UTF8.GetBytes($"<{tag} style='font-family:private-execution'>Report</{tag}>"), "portal-session", "private-execution", [])).Code);
    }
}
