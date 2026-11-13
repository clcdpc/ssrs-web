using System.Net;
using System.Net.Http.Json;
using SsrsPortal.Core;
using SsrsPortal.Services;

namespace SsrsPortal.Tests;

public sealed class ReportLinkParameterTests
{
    [Fact]
    public async Task ExplicitValuesDefaultsNullBlankAndRepeatedSelectionsUseTheExecutionPipeline()
    {
        using var h = new Harness(); var report = await h.Report("Default Showcase");
        var link = await h.Parameters.ResolveLinkAsync(report,
            "?branch=2&flag=1&note:isnull=true&blank=&lines=A%2BB&lines=%E6%97%A5%E6%9C%AC%E8%AA%9E", default);
        Assert.True(link.HasParameters); Assert.False(link.NoExecute); Assert.True(link.Form.CanRun);
        Assert.Null(link.Form.LinkErrors);
        Assert.Equal(new string?[] {"2"}, link.Form.Values["branch"]); Assert.Equal(new string?[] {"true"}, link.Form.Values["flag"]);
        Assert.Equal(new string?[] {"2-true"}, link.Form.Values["child"]); Assert.Equal(new string?[] {"Branch 2"}, link.Form.Values["branchNote"]);
        Assert.Equal(new string?[] {null}, link.Form.Values["note"]); Assert.Equal(new string?[] {""}, link.Form.Values["blank"]);
        Assert.Equal(new string?[] {"A+B", "日本語"}, link.Form.Values["lines"]);
        Assert.Contains("start", link.Form.Defaulted); Assert.DoesNotContain("branch", link.Form.Defaulted);
        var executed = await h.Parameters.ResolveAsync(report, new(link.Form.Values, Defaulted: link.Form.Defaulted), true, default);
        Assert.True(executed.Form.CanRun);
        Assert.Contains(executed.ExecutionValues, v => v.Name == "internal");
        Assert.False(link.Form.Values.ContainsKey("internal"));
    }

    [Theory]
    [InlineData("1", "true")]
    [InlineData("0", "false")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    public async Task BooleanRepresentationsAreNormalized(string value, string normalized)
    {
        using var h = new Harness(); var report = await h.Report("Default Showcase");
        var form = (await h.Parameters.ResolveLinkAsync(report, "?flag=" + value, default)).Form;
        Assert.True(form.CanRun); Assert.Equal(new string?[] {normalized}, form.Values["flag"]);
    }

    [Theory]
    [InlineData("2026-10-01", "2026-10-01T00:00:00")]
    [InlineData("2026-10-31T23:59:59", "2026-10-31T23:59:59")]
    [InlineData("2026-10-31T23:59:59.1234567", "2026-10-31T23:59:59.1234567")]
    [InlineData("2026-10-31T23:59:59.1234567Z", "2026-10-31T23:59:59.1234567Z")]
    [InlineData("2026-10-31T23:59:59.1234567+05:45", "2026-10-31T23:59:59.1234567+05:45")]
    public async Task DatesKeepWallClockTimePrecisionAndOffset(string value, string expected)
    {
        using var h = new Harness(); var report = await h.Report("Default Showcase");
        var form = (await h.Parameters.ResolveLinkAsync(report, "?start=" + Uri.EscapeDataString(value), default)).Form;
        Assert.True(form.CanRun); Assert.Equal(new string?[] {expected}, form.Values["start"]);
    }

    [Theory]
    [InlineData("branch=2&child=2-false", true)]
    [InlineData("branch=2&child=1-false", false)]
    [InlineData("branch=invalid", false)]
    [InlineData("branch=2&branch=2", false)]
    [InlineData("flag=maybe", false)]
    [InlineData("note=", false)]
    public async Task InvalidExplicitValuesNeverFallBackToUnrelatedDefaults(string query, bool valid)
    {
        using var h = new Harness(); var report = await h.Report("Default Showcase");
        var link = await h.Parameters.ResolveLinkAsync(report, "?" + query, default);
        Assert.Equal(valid, link.Form.CanRun);
        if (query.Contains("child=1-false"))
        {
            Assert.Empty(link.Form.Values["child"]);
            Assert.NotNull(link.Form.Fields.Single(f => f.Name == "child").Error);
            Assert.DoesNotContain("child", link.Form.Defaulted);
            Assert.Equal(new string?[] {"2"}, link.Form.Values["branch"]);
        }
        if (query.StartsWith("branch=invalid")) { Assert.Empty(link.Form.Values["branch"]); }
    }

    [Theory]
    [InlineData("unknown=1")]
    [InlineData("Branch=2")]
    [InlineData("internalMode=override")]
    [InlineData("internal=override")]
    [InlineData("rs:Command=Render")]
    [InlineData("rc:Toolbar=false")]
    [InlineData("noExecute=true")]
    [InlineData("portal:noExecute=true&portal%3AnoExecute=false")]
    [InlineData("portal:noExecute=maybe")]
    [InlineData("portal:NoExecute=true")]
    [InlineData("note:isnull=true&note=value")]
    [InlineData("note=value&note:isnull=true")]
    [InlineData("note:isnull=true&note:isnull=true")]
    [InlineData("note:isnull=false")]
    [InlineData("blank=%")]
    [InlineData("blank=%FF")]
    [InlineData("blank")]
    [InlineData("?branch=2")]
    public async Task UnsupportedOrMalformedQueryProducesSafeErrorsAndLeavesAForm(string query)
    {
        using var h = new Harness(); var report = await h.Report("Default Showcase");
        var form = (await h.Parameters.ResolveLinkAsync(report, "?" + query, default)).Form;
        Assert.NotEmpty(form.LinkErrors!); Assert.NotEmpty(form.Fields);
        Assert.False(form.Values.ContainsKey("internalMode"));
        Assert.DoesNotContain(h.Mock.Observations, o => o.Operation == "LoadReport");
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    public async Task ReservedControlIsSeparateFromActualParameters(string value, bool suppressed)
    {
        using var h = new Harness(); var report = await h.Report("Default Showcase");
        var only = await h.Parameters.ResolveLinkAsync(report, "?portal%3AnoExecute=" + value, default);
        Assert.Equal(suppressed, only.NoExecute); Assert.False(only.HasParameters);
        Assert.Null(only.Form.LinkErrors); Assert.True(only.Form.CanRun);
        var explicitValue = await h.Parameters.ResolveLinkAsync(report, "?branch=2&portal%3AnoExecute=" + value, default);
        Assert.True(explicitValue.HasParameters); Assert.Equal(suppressed, explicitValue.NoExecute);
        Assert.DoesNotContain(h.Mock.Observations.SelectMany(o => o.Request.Descendants()), e => e.Value == "portal:noExecute");
    }

    [Fact]
    public async Task CountsLengthsAndMissingRequiredInputsStayBounded()
    {
        using var h = new Harness(); var report = await h.Report("Default Showcase");
        foreach (var query in new[] { "?blank=" + new string('a', 2049),
            "?" + string.Join('&', Enumerable.Range(0, 101).Select(i => "input" + i + "=1")),
            "?" + string.Join('&', Enumerable.Repeat("lines=entry", 5001)),
            "?blank=" + new string('a', 224 * 1024) })
        {
            var link = await h.Parameters.ResolveLinkAsync(report, query, default);
            Assert.NotEmpty(link.Form.LinkErrors!);
        }
        var missing = await h.Parameters.ResolveLinkAsync(await h.Report("Single Choice Showcase"), "?branch=2", default);
        Assert.False(missing.Form.CanRun); Assert.Empty(missing.Form.Values["required"]);
        var malformed = await h.Parameters.ResolveLinkAsync(report, "?blank=%FF&branch=2", default);
        Assert.NotEmpty(malformed.Form.LinkErrors!);
        Assert.Equal("2", Assert.Single(malformed.Form.Values["branch"]));
    }

    [Fact]
    public async Task LinkInitializationRequiresStaffCsrfAndAuthorizedOpaqueIdentity()
    {
        await using var f = new PortalFactory(); using var user = await f.Login();
        var url = "/api/reports/" + await user.Id() + "/link-parameters";
        using var anonymous = f.Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(url, new ReportLinkInput("?library=1"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Http.PostAsJsonAsync(url, new ReportLinkInput("?library=1"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Post("/api/reports/unknown/link-parameters", new ReportLinkInput("?library=1"))).StatusCode);
        var form = (await (await user.Post(url, new ReportLinkInput("?library=2&applyCollectionFilter=1"))).Content.ReadFromJsonAsync<ReportLinkForm>())!;
        Assert.Equal(new string?[] {"2-on-1"}, form.Form.Values["collections"]);
        Assert.DoesNotContain(f.Mock.Observations, o => o.Operation == "LoadReport");
    }
}
