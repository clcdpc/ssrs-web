using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using SsrsPortal.Core;
using SsrsPortal.Soap;

namespace SsrsPortal.Tests;

public sealed class ParameterChoicesTests
{
    [Theory]
    [InlineData("Compact Inputs", "format", 5)]
    [InlineData("Holds Ratio", "library", 19)]
    [InlineData("Single Choice Showcase", "branch", 152)]
    [InlineData("Single Choice Showcase", "workstation", 4192)]
    public async Task CompleteListsHaveNoPagingAndFormPayloadContainsOnlySelectedLabels(string name, string parameter, int count)
    {
        using var h = new Harness(); var report = await h.Report(name);
        var form = (await h.Parameters.ResolveAsync(report, new(null), false, default)).Form;
        Assert.All(form.Fields, f => Assert.Empty(f.Options));
        var input = new ParameterInput(form.Values, Defaulted: form.Defaulted);
        var choices = await h.Parameters.ChoicesAsync(report, input, parameter, default);
        Assert.Equal(count, choices.Options.Length);
        Assert.Equal(form.Fields.Single(f => f.Name == parameter).OptionsKey, choices.OptionsKey);
        Assert.All(choices.Options, o => Assert.NotNull(o.Label));
        var json = JsonSerializer.SerializeToUtf8Bytes(form);
        Assert.InRange(json.Length, 1, 14000); // Not thousands of unselected labels on each refresh.
    }

    [Fact]
    public async Task ParentChangesInvalidateChoicesButUnrelatedAndOwnSelectionEditsReuseTheKey()
    {
        using var h = new Harness(); var report = await h.Report("Single Choice Showcase");
        var first = (await h.Parameters.ResolveAsync(report, new(null), false, default)).Form;
        first.Values["branch"] = ["21"];
        var changed = (await h.Parameters.ResolveAsync(report, new(first.Values, Changed: ["branch"], Defaulted: first.Defaulted), false, default)).Form;
        Assert.NotEqual(Key(first, "department"), Key(changed, "department"));
        Assert.Equal(Key(first, "branch"), Key(changed, "branch"));
        Assert.Equal(Key(first, "workstation"), Key(changed, "workstation"));
        changed.Values["note"] = ["Unrelated edit"];
        var unrelated = (await h.Parameters.ResolveAsync(report, new(changed.Values, Changed: ["note"], Defaulted: changed.Defaulted), false, default)).Form;
        Assert.Equal(Key(changed, "department"), Key(unrelated, "department"));
        var choices = await h.Parameters.ChoicesAsync(report, new(unrelated.Values, Defaulted: unrelated.Defaulted), "department", default);
        Assert.Equal(Key(unrelated, "department"), choices.OptionsKey);
        Assert.Equal("21-team", choices.Options[0].Value);
    }

    [Fact]
    public async Task TransitiveParentsInvalidateEvenWhenTheActualChoicesAreIdentical()
    {
        using var h = new Harness(wrapHandler: inner => new TransitiveChoices(inner));
        var report = await h.Report("Single Choice Showcase");
        var first = (await h.Parameters.ResolveAsync(report, new(null), false, default)).Form;
        first.Values["branch"] = ["21"];
        first.Values["department"] = ["shared"];
        var changed = (await h.Parameters.ResolveAsync(report, new(first.Values, Changed: ["branch", "department"], Defaulted: first.Defaulted), false, default)).Form;
        Assert.NotEqual(Key(first, "workstation"), Key(changed, "workstation"));
        changed.Values["branch"] = ["22"];
        var transitive = (await h.Parameters.ResolveAsync(report, new(changed.Values, Changed: ["branch"], Defaulted: changed.Defaulted), false, default)).Form;
        Assert.Equal("shared", Assert.Single(transitive.Values["department"]));
        Assert.NotEqual(Key(changed, "workstation"), Key(transitive, "workstation"));
    }

    [Fact]
    public async Task ChangedLabelsInvalidateTheCollectionEvenWhenCountAndParentValuesStayTheSame()
    {
        var changeLabels = false;
        using var h = new Harness(wrapHandler: inner => new TransitiveChoices(inner, () => changeLabels));
        var report = await h.Report("Single Choice Showcase");
        var first = (await h.Parameters.ResolveAsync(report, new(null), false, default)).Form;
        changeLabels = true;
        var choices = await h.Parameters.ChoicesAsync(report, new(first.Values, Defaulted: first.Defaulted), "workstation", default);
        Assert.Equal(4192, choices.Options.Length);
        Assert.NotEqual(Key(first, "workstation"), choices.OptionsKey);
        Assert.EndsWith(" updated", choices.Options[^1].Label);
    }

    [Theory]
    [InlineData("internal")]
    [InlineData("lines")]
    [InlineData("unknown")]
    public async Task ChoicesRejectHiddenFreeEntryAndUnknownParameters(string name)
    {
        using var h = new Harness(); var report = await h.Report("Default Showcase");
        Assert.Equal("notfound", (await Assert.ThrowsAsync<PortalException>(() => h.Parameters.ChoicesAsync(report, new(null), name, default))).Code);
    }

    [Fact]
    public async Task ChoicesEnforceUnavailableOutstandingCancellationAndPolicy()
    {
        using var h = new Harness(); var report = await h.Report();
        Assert.Equal("values", (await Assert.ThrowsAsync<PortalException>(() => h.Parameters.ChoicesAsync(report, new(null), "collections", default))).Code);
        h.Mock.ParameterQueryUnavailable = true;
        Assert.Equal("query", (await Assert.ThrowsAsync<PortalException>(() => h.Parameters.ChoicesAsync(report, Harness.Inputs(), "collections", default))).Code);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var before = h.Mock.Observations.Length;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Parameters.ChoicesAsync(report, Harness.Inputs(), "library", cancelled.Token));
        Assert.Equal(before, h.Mock.Observations.Length);
        h.Options.ExcludedPrefixes = [report.Path];
        Assert.Equal("notfound", (await Assert.ThrowsAsync<PortalException>(() => h.Parameters.ChoicesAsync(report, Harness.Inputs(), "library", default))).Code);
    }

    [Fact]
    public async Task CompleteChoiceEndpointRetainsAuthenticationCsrfRequestLimitsAndTypedValues()
    {
        await using var factory = new PortalFactory(); using var anonymous = factory.Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/reports/x/options/branch", new { })).StatusCode);
        using var user = await factory.Login(); var id = await user.Id("Single Choice Showcase");
        var url = "/api/reports/" + id + "/options/branch";
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Http.PostAsJsonAsync(url, new ParameterInput(null))).StatusCode);
        using var response = await user.Post(url, new ParameterInput(null));
        response.EnsureSuccessStatusCode();
        Assert.Equal(152, (await response.Content.ReadFromJsonAsync<ParameterChoices>())!.Options.Length);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Post("/api/reports/unknown/options/branch", new ParameterInput(null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Post(url, new ParameterInput(new() { ["branch"] = [new string('x', 2049)] }))).StatusCode);
        // TestServer does not implement Kestrel's body limit; the value bound still rejects this.
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Post(url, new { values = new { branch = new[] { new string('x', 300000) } } })).StatusCode);
    }

    [Fact]
    public async Task StableWidthCategoriesUseTypesAndSemanticMetadata()
    {
        using var h = new Harness(); var report = await h.Report("Compact Inputs");
        var form = (await h.Parameters.ResolveAsync(report, new(null), false, default)).Form;
        foreach (var (name, width) in new[] { ("OrganizationID", "number"), ("percentage", "number"), ("inactive", "boolean"),
            ("reportYear", "short"), ("materialCode", "short"), ("sortDirection", "short"), ("expression", "text"), ("format", "choice") })
        {
            Assert.Equal(width, form.Fields.Single(f => f.Name == name).Width);
        }
        form.Values["materialCode"] = ["A substantially longer value is still editable"];
        var edited = (await h.Parameters.ResolveAsync(report, new(form.Values, Changed: ["materialCode"]), true, default)).Form;
        Assert.Equal("short", edited.Fields.Single(f => f.Name == "materialCode").Width);
    }

    private static string Key(ParameterForm form, string name) => form.Fields.Single(f => f.Name == name).OptionsKey;

    private sealed class TransitiveChoices(HttpMessageHandler inner, Func<bool>? changeLabels = null) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var workstation = xml.Descendants(SoapTransport.Management + "ItemParameter")
                .SingleOrDefault(p => p.Element(SoapTransport.Management + "Name")?.Value == "workstation");
            if (workstation != null)
            {
                workstation.Element(SoapTransport.Management + "Dependencies")!.Add(new XElement(SoapTransport.Management + "Dependency", "department"));
                if (changeLabels?.Invoke() == true)
                {
                    foreach (var label in workstation.Descendants(SoapTransport.Management + "Label")) { label.Value += " updated"; }
                }
                response.Content = new StringContent(xml.ToString(), System.Text.Encoding.UTF8, "text/xml");
            }
            return response;
        }
    }
}
