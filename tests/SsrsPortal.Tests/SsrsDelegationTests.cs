using System.Net;
using SsrsPortal.Core;
using SsrsPortal.Mock;

namespace SsrsPortal.Tests;

public sealed class SsrsDelegationTests
{
    [Theory]
    [InlineData("Custom code")]
    [InlineData("Code modules")]
    [InlineData("Shared datasets")]
    [InlineData("External images")]
    [InlineData("User!UserID")]
    [InlineData("Non-SQL data source")]
    [InlineData("Subreport cycle")]
    [InlineData("Deep subreports")]
    public async Task SsrsOwnedReportFeaturesDoNotParticipateInCatalogAdmissionOrExecution(string feature)
    {
        // Catalog metadata is identical regardless of the opaque server-owned definition.
        // The mock has no definition/source operations: an accidental preflight would fail.
        var path = "/Training/Reports/" + feature;
        using var h = new Harness(); CatalogCacheTests.Graph(h, path);
        var id = (await h.Catalog.BrowseAsync(default)).Single(e => e.Name == feature).Id;
        CatalogCacheTests.MetadataOnly(h);
        var run = await h.Execution.RunAsync("owner", id, new(new()), default);
        Assert.NotEmpty(run.Token); Assert.Equal(1, h.Mock.Calls["LoadReport|" + path]);
        Assert.Equal(1, CatalogCacheTests.Count(h, "ListChildren")); CatalogCacheTests.NoPreflight(h);
    }

    [Theory]
    [InlineData("Prompted", "credentials", HttpStatusCode.BadRequest)]
    [InlineData("Broken", "service", HttpStatusCode.BadGateway)]
    public async Task VisibleReportsRejectedBySsrsReturnControlledErrorsAndReleaseSessions(string report, string code, HttpStatusCode status)
    {
        await using var f = new PortalFactory(); using var user = await f.Login();
        var id = await user.Id(report);
        using var response = await user.Post("/api/reports/" + id + "/run", new ParameterInput(new()));
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(code, body); Assert.DoesNotContain("private", body);
        Assert.DoesNotContain("Synthetic invalid", body);
        Assert.Equal(1, f.Mock.Calls["LoadReport|/Training/Reports/" + report]);
        var sessions = f.Services.GetService(typeof(SsrsPortal.Services.SessionStore)) as SsrsPortal.Services.SessionStore;
        Assert.Equal(0, sessions!.Snapshot().Count);
        Assert.DoesNotContain(f.Mock.Calls.Keys, k => k.StartsWith("Render|", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LinkedAliasOutsideItsTargetScopeUsesAliasForParametersAndExecution()
    {
        const string target = "/Outside/Target";
        using var h = new Harness(); CatalogCacheTests.Graph(h, SyntheticSsrsHandler.Link);
        h.Mock.CatalogItems = [.. h.Mock.CatalogItems!.Select(i => i.Path == SyntheticSsrsHandler.Link ? i with { Type = "LinkedReport" } : i), new(target)];
        h.Mock.LinkedTargets[SyntheticSsrsHandler.Link] = target;
        h.Options.ExcludedPrefixes = ["/Outside"];
        var alias = (await h.Catalog.BrowseAsync(default)).Single(e => e.Name == "Holds Link");
        var run = await h.Execution.RunAsync("owner", alias.Id, Harness.Inputs(), default);
        Assert.NotEmpty(run.Token);
        Assert.Equal(1, h.Mock.Calls["LoadReport|" + SyntheticSsrsHandler.Link]);
        Assert.True(h.Mock.Calls["GetItemParameters|" + SyntheticSsrsHandler.Link] > 0);
        Assert.DoesNotContain(h.Mock.Calls.Keys, k => k.EndsWith("|" + target, StringComparison.Ordinal));
        CatalogCacheTests.NoPreflight(h);
    }

    [Fact]
    public async Task BrokenLinkedTargetStaysVisibleUntilSsrsRejectsExecution()
    {
        using var h = new Harness();
        h.Mock.LinkedTargets[SyntheticSsrsHandler.Link] = "/Missing/Report";
        var alias = (await h.Catalog.BrowseAsync(default)).Single(e => e.Name == "Holds Link");
        var error = await Assert.ThrowsAsync<PortalException>(() => h.Execution.RunAsync("owner", alias.Id, Harness.Inputs(), default));
        Assert.Equal("service", error.Code); Assert.Equal(0, h.Sessions.Snapshot().Count);
        Assert.Contains(await h.Catalog.BrowseAsync(default), e => e.Id == alias.Id);
        CatalogCacheTests.NoPreflight(h);
    }
}
