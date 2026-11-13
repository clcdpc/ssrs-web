using System.Net;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using SsrsPortal.Core;
using SsrsPortal.Security;
using SsrsPortal.Services;
namespace SsrsPortal.Tests;

public sealed class SecurityTests
{
    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    public void AuthAndMockFlagsFailClosedOutsideIsolatedDevelopment(bool development, bool mock, bool auth, bool valid)
    {
        using var h = new Harness();
        h.Options.UseMock = mock; h.Options.TestAuth = auth;
        if (valid)
        {
            Assert.NotNull(h.Options.Validate(development));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => h.Options.Validate(development));
        }
    }

    [Theory]
    [InlineData("http://host/ReportServer")]
    [InlineData("https://user:pass@host/ReportServer")]
    [InlineData("https://host/ReportServer?next=other")]
    [InlineData("https://host/ReportServer#other")]
    [InlineData("https://host/another")]
    public void UnsafeSsrsEndpointConfigurationIsRejected(string endpoint)
    {
        using var h = new Harness(); h.Options.ReportServer = endpoint;
        Assert.Throws<InvalidOperationException>(() => h.Options.Validate(true));
    }

    [Fact]
    public async Task AnonymousAllPrivilegedRoutesAndMissingCsrfAreRejected()
    {
        await using var factory = new PortalFactory(); using var anonymous = factory.Browser();
        foreach (var path in new[] { "/api/catalog", "/api/csrf", "/api/executions/x/info", "/api/executions/x/viewer", "/api/executions/x/streams/y", "/api/executions/x/exports/pdf" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        }
        foreach (var path in new[] { "/api/reports/x/parameters", "/api/reports/x/run", "/api/reports/x/options/p", "/api/executions/x/page/2", "/api/executions/x/cancel" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(path, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        }
        using var user = await factory.Login();
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Http.PostAsync("/api/reports/" + await user.Id() + "/run", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Http.PostAsync("/signout", new StringContent(""))).StatusCode);
    }

    [Theory]
    [InlineData("outsider")]
    [InlineData("overage")]
    [InlineData("old-group")]
    [InlineData("unknown-tenant")]
    public async Task SyntheticSignInDeniesUnauthorizedAndOverageProfiles(string profile)
    {
        await using var f = new PortalFactory(); using var user = await f.Login(profile);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Http.GetAsync("/api/catalog")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Http.GetAsync("/Denied")).StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task ProductionStartupRefusesMissingConfigurationAndMockFlags(string environment)
    {
        await using var f = new PortalFactory(environment: environment);
        Assert.Throws<InvalidOperationException>(() => f.Browser());
        Assert.Throws<InvalidOperationException>(() => new EntraOptions().Validate(false));
    }

    [Fact]
    public async Task RealAuthRegistrationCannotServeTestLoginAndUsesProcessCredentialTransport()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Portal:UseMock"] = "false", ["Portal:TestAuth"] = "false", ["Portal:ReportServer"] = "https://synthetic.invalid/ReportServer",
            ["Portal:AllowedRoots:0"] = "/Training", ["Portal:ReportAccessPolicy"] = "GlobalBlocklist",
            ["Entra:AllowedTenantIds:0"] = "00000000-0000-0000-0000-000000000001",
            ["Entra:ClientId"] = "00000000-0000-0000-0000-000000000003", ["Entra:ClientSecret"] = "synthetic-test-credential"
        };
        await using var f = new PortalFactory(settings); using var browser = f.Browser();
        // The same real-auth settings also satisfy Production's options contract. Production
        // host startup separately rejects all test-file overrides before any file access.
        f.Services.GetRequiredService<PortalOptions>().Validate(false);
        f.Services.GetRequiredService<EntraOptions>().Validate(false);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/TestLogin")).StatusCode);
        var transport = Assert.IsType<HttpClientHandler>(f.Services.GetRequiredService<HttpMessageHandler>());
        Assert.True(transport.UseDefaultCredentials); Assert.False(transport.AllowAutoRedirect); Assert.False(transport.UseCookies);
        Assert.Null(f.Services.GetService<SsrsPortal.Mock.SyntheticSsrsHandler>());
        // No sign-in challenge or SSRS operation is sent: registrations alone are inspected.
    }

    [Fact]
    public async Task GlobalCatalogPolicyPreservesHierarchyOpaqueIdsAndDirectRestrictions()
    {
        using var h = new Harness(); var items = await h.Catalog.BrowseAsync(default);
        Assert.Equal(new[] { "Additional Report", "Blocked Subreport", "Broken", "Circulation By Workstation", "Compact Inputs", "One Input", "Default Showcase", "Details", "Expandable Groups", "Holds Link", "Holds Ratio", "Prompted", "Reports", "Shortcuts", "Single Choice Showcase", "Training", "Unsafe Link", "User Identity",
            "Check Ins by Day of Week by Hour", "Sorting fixture", "~Tech Services", "Tech sample", "~Archives", "Archive sample", "Administration", "Admin sample", "Circulation", "Circ sample", "Alpha sample", "Linked sample", "~Report sample",
            "Custom", "Regional activity", "Circulation", "Hourly transactions", "Check Ins by Day of Week by Hour with a very long report name for regional circulation review and historical comparisons" }.Order().ToArray(), items.Select(i => i.Name).Order().ToArray());
        Assert.All(items, i => Assert.Matches("^[0-9a-f]{64}$", i.Id));
        Assert.All(items.Where(i => i.Name is "Holds Ratio" or "Details"), i => Assert.Equal(items.Single(p => p.Name == "Reports").Id, i.ParentId));
        await Assert.ThrowsAsync<PortalException>(() => h.Catalog.ResolveAsync("/Training/Reports/Holds Ratio", default));
        await Assert.ThrowsAsync<PortalException>(() => h.Catalog.ResolveAsync(new string('a', 64), default));
        h.Options.ExcludedPrefixes = [.. h.Options.ExcludedPrefixes, SsrsPortal.Mock.SyntheticSsrsHandler.Holds];
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        var refreshed = await h.Catalog.BrowseAsync(default);
        Assert.DoesNotContain(refreshed, i => i.Name == "Holds Ratio");
        Assert.Contains(refreshed, i => i.Name == "Holds Link");
    }

    [Theory]
    [InlineData("/Training/../Outside")]
    [InlineData("/TrainingX/Report")]
    [InlineData("/Training/Internal/Report")]
    [InlineData("/Training/Custom_Restored/Report")]
    [InlineData("/Training/Reports/%2e%2e/Report")]
    [InlineData("/Training/Reports\\Other")]
    public void TraversalAndExcludedPathsFailPolicy(string path)
    {
        using var h = new Harness();
        // URL escapes are validated at the public-route boundary. Catalog names
        // themselves may now legitimately contain literal percent characters.
        string decoded;
        try { decoded = ReportUrl.ParsePath("/report" + path); }
        catch (PortalException error) { Assert.Equal("notfound", error.Code); return; }
        Assert.False(PathPolicy.Permitted(decoded, h.Options));
    }

    [Fact]
    public async Task SignoutRevokesPortalSessionsOnly()
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); var run = await user.Run();
        var response = await user.Post("/signout", new { });
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/SignedOut", response.Headers.Location!.OriginalString);
        Assert.Equal(0, f.Services.GetRequiredService<SessionStore>().Snapshot().Count);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Http.GetAsync("/api/executions/" + run.Token + "/viewer")).StatusCode);
    }
}
