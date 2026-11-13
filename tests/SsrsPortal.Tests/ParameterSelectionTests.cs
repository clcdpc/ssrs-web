using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Soap;

namespace SsrsPortal.Tests;

public sealed class ParameterSelectionTests
{
    [Theory]
    [InlineData("Check Ins by Day of Week by Hour", "branch", 152)]
    [InlineData("Default Showcase", "workstations", 4192)]
    public async Task SelectAllReturnsEveryValidChoiceWithLabelsInOneBoundedResolution(string name, string parameter, int count)
    {
        using var h = new Harness(); var report = await h.Report(name);
        var initial = (await h.Parameters.ResolveAsync(report, new(null, true), false, default)).Form;
        var input = new ParameterInput(initial.Values, Defaulted: initial.Defaulted);
        var before = CatalogCacheTests.Count(h, "GetItemParameters");
        var all = await h.Parameters.SelectAllAsync(report, input, parameter, default);
        Assert.Equal(count, all.Options.Length);
        Assert.All(all.Options, o => Assert.StartsWith("Synthetic ", o.Label));
        Assert.InRange(CatalogCacheTests.Count(h, "GetItemParameters") - before, 2, 3); // Includes hidden dependent defaults; never page through choices.
        Assert.Empty(initial.Fields.Single(f => f.Name == parameter).Options);
        Assert.NotEqual(count, input.Values![parameter].Length); // Fetching choices never commits.
        input.Values[parameter] = all.Options.Select(o => o.Value).ToArray();
        var applied = (await h.Parameters.ResolveAsync(report, input with { Changed = [parameter] }, false, default)).Form;
        Assert.True(applied.CanRun);
        Assert.True(applied.Fields.Single(f => f.Name == parameter).AllSelected);
        Assert.DoesNotContain(parameter, applied.Defaulted);
        await h.Execution.RunAsync("owner", report.Id, new(applied.Values, Defaulted: applied.Defaulted), default);
        var sent = h.Mock.Observations.Last(o => o.Operation == "SetExecutionParameters").Request.Descendants(SoapTransport.Execution + "ParameterValue")
            .Where(p => p.Element(SoapTransport.Execution + "Name")!.Value == parameter).Select(p => p.Element(SoapTransport.Execution + "Value")?.Value);
        Assert.Equal(applied.Values[parameter], sent);
    }

    [Fact]
    public async Task SelectAllUsesCurrentParentsAndExplicitClearStaysCleared()
    {
        using var h = new Harness(); var report = await h.Report();
        var input = Harness.Inputs("2", "true");
        var all = await h.Parameters.SelectAllAsync(report, input, "collections", default);
        Assert.Equal(73, all.Options.Length);
        Assert.All(all.Options, o => Assert.StartsWith("2-on-", o.Value));
        input.Values!["collections"] = [];
        var cleared = await h.Parameters.ResolveAsync(report, input with { Changed = ["collections"] }, false, default);
        Assert.Empty(cleared.Form.Values["collections"]);
        Assert.False(cleared.Form.CanRun);
    }

    [Theory]
    [InlineData("branch")]
    [InlineData("lines")]
    [InlineData("internal")]
    [InlineData("unknown")]
    public async Task SelectAllRejectsSingleFreeEntryHiddenAndUnknownParameters(string parameter)
    {
        using var h = new Harness(); var report = await h.Report("Default Showcase");
        Assert.Equal("notfound", (await Assert.ThrowsAsync<PortalException>(() => h.Parameters.SelectAllAsync(report, new(null, true), parameter, default))).Code);
    }

    [Fact]
    public async Task SelectionEndpointPreservesPolicyCsrfAndCancellation()
    {
        await using var factory = new PortalFactory(); using var anonymous = factory.Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/reports/x/options/branch/all", new { })).StatusCode);
        using var user = await factory.Login(); var id = await user.Id("Check Ins by Day of Week by Hour");
        var url = "/api/reports/" + id + "/options/branch/all";
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Http.PostAsJsonAsync(url, new ParameterInput(null))).StatusCode);
        using var response = await user.Post(url, new ParameterInput(null, true));
        Assert.Equal(152, (await response.Content.ReadFromJsonAsync<OptionSelection>())!.Options.Length);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Post("/api/reports/unknown/options/branch/all", new ParameterInput(null))).StatusCode);
        using var h = new Harness(); var report = await h.Report("Default Showcase");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var before = h.Mock.Observations.Length;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Parameters.SelectAllAsync(report, new(null), "workstations", cancelled.Token));
        Assert.Equal(before, h.Mock.Observations.Length);
        h.Options.ExcludedPrefixes = [report.Path];
        Assert.Equal("notfound", (await Assert.ThrowsAsync<PortalException>(() => h.Parameters.SelectAllAsync(report, new(null), "workstations", default))).Code);
    }

    [Fact]
    public async Task SelectAllCannotUseUnavailableOrOutstandingChoices()
    {
        using var h = new Harness(); var report = await h.Report();
        Assert.Equal("values", (await Assert.ThrowsAsync<PortalException>(() => h.Parameters.SelectAllAsync(report, new(null), "collections", default))).Code);
        h.Mock.ParameterQueryUnavailable = true;
        Assert.Equal("query", (await Assert.ThrowsAsync<PortalException>(() => h.Parameters.SelectAllAsync(report, Harness.Inputs(), "collections", default))).Code);
    }

    [Theory]
    [InlineData(5001, 6)]
    [InlineData(152, 2000)]
    public async Task SelectAllRejectsSelectionAndSerializedRequestLimits(int count, int valueLength)
    {
        using var h = new Harness(o => o.MaxOptions = 6000, wrapHandler: inner => new LargeChoices(inner, count, valueLength));
        var report = await h.Report("Default Showcase");
        var error = await Assert.ThrowsAsync<PortalException>(() => h.Parameters.SelectAllAsync(report, new(null), "workstations", default));
        Assert.Equal("selection-limit", error.Code);
        Assert.Contains("Select fewer", PortalException.Description(error.Code));
        Assert.DoesNotContain(h.Mock.Observations, o => o.Operation == "LoadReport");
    }

    [Fact]
    public async Task NullBlankLiteralNullAndNamedChoicesRemainDistinctThroughCommitAndExecution()
    {
        using var h = new Harness(); var report = await h.Report("Check Ins by Day of Week by Hour");
        var initial = (await h.Parameters.ResolveAsync(report, new(null), true, default)).Form;
        Assert.Equal("", Assert.Single(initial.Values["blankChoices"]));
        initial.Values["blankChoices"] = [null, "", "null", "named"];
        await h.Execution.RunAsync("owner", report.Id, new(initial.Values, Changed: ["blankChoices"]), default);
        var sent = h.Mock.Observations.Last(o => o.Operation == "SetExecutionParameters").Request.Descendants(SoapTransport.Execution + "ParameterValue")
            .Where(p => p.Element(SoapTransport.Execution + "Name")!.Value == "blankChoices").Select(p => p.Element(SoapTransport.Execution + "Value")?.Value);
        Assert.Equal(new string?[] { null, "", "null", "named" }, sent);
    }

    [Fact]
    public async Task SelectedMaterialLabelsFollowSsrsOrderWithoutReorderingExecutionValues()
    {
        using var h = new Harness(); var report = await h.Report("Check Ins by Day of Week by Hour");
        var form = (await h.Parameters.ResolveAsync(report, new(null), false, default)).Form;
        Assert.Equal(52, form.Values["materials"].Length);
        form.Values["materials"] = ["material-3", "material-1"];
        var changed = (await h.Parameters.ResolveAsync(report, new(form.Values, Changed: ["materials"], Defaulted: form.Defaulted), false, default)).Form;
        Assert.Equal(new[] { "Abstract", "Atlas" }, changed.Fields.Single(f => f.Name == "materials").SelectedOptions.Select(o => o.Label));
        Assert.Equal(new[] { "material-3", "material-1" }, changed.Values["materials"]);
        Assert.DoesNotContain("materials", changed.Defaulted);
        Assert.Contains("branch", changed.Defaulted);
        Assert.DoesNotContain("(Select All)", changed.Values.SelectMany(p => p.Value));
    }

    [Fact]
    public async Task PreviousFullManualSelectionDoesNotSelectNewlyAvailableChoices()
    {
        var expanded = false;
        using var h = new Harness(wrapHandler: inner => new ExpandingChoices(inner, () => expanded));
        var report = await h.Report("Check Ins by Day of Week by Hour");
        var form = (await h.Parameters.ResolveAsync(report, new(null), false, default)).Form;
        var all = await h.Parameters.SelectAllAsync(report, new(form.Values, Defaulted: form.Defaulted), "blankChoices", default);
        Assert.Equal(2, all.Options.Length);
        form.Values["blankChoices"] = all.Options.Select(o => o.Value).ToArray();
        var manual = (await h.Parameters.ResolveAsync(report, new(form.Values, Changed: ["blankChoices"], Defaulted: form.Defaulted), false, default)).Form;
        Assert.Equal(new[] { "", "named" }, manual.Values["blankChoices"]);
        Assert.True(manual.Fields.Single(f => f.Name == "blankChoices").AllSelected);
        expanded = true;
        manual.Values["branch"] = ["80"];
        var changed = (await h.Parameters.ResolveAsync(report, new(manual.Values, Changed: ["branch"], Defaulted: manual.Defaulted), false, default)).Form;
        Assert.Equal(3, changed.Fields.Single(f => f.Name == "blankChoices").OptionCount);
        Assert.Equal(manual.Values["blankChoices"], changed.Values["blankChoices"]);
        Assert.False(changed.Fields.Single(f => f.Name == "blankChoices").AllSelected);
        Assert.NotEqual(manual.Fields.Single(f => f.Name == "blankChoices").OptionsKey, changed.Fields.Single(f => f.Name == "blankChoices").OptionsKey);
        Assert.DoesNotContain("blankChoices", changed.Defaulted);
        Assert.Equal(new[] { "80-team" }, changed.Values["department"]);
    }

    private sealed class ExpandingChoices(HttpMessageHandler inner, Func<bool> expanded) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var parameter = xml.Descendants(SoapTransport.Management + "ItemParameter")
                .SingleOrDefault(p => p.Element(SoapTransport.Management + "Name")?.Value == "blankChoices");
            if (parameter != null)
            {
                parameter.Element(SoapTransport.Management + "Dependencies")!.Add(new XElement(SoapTransport.Management + "Dependency", "branch"));
                if (!expanded()) { parameter.Descendants(SoapTransport.Management + "ValidValue").Where(v => v.Element(SoapTransport.Management + "Value")?.Value == "null").Remove(); }
                response.Content = new StringContent(xml.ToString(), System.Text.Encoding.UTF8, "text/xml");
            }
            return response;
        }
    }

    private sealed class LargeChoices(HttpMessageHandler inner, int count, int valueLength) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var parameter = xml.Descendants(SoapTransport.Management + "ItemParameter").SingleOrDefault(p => p.Element(SoapTransport.Management + "Name")!.Value == "workstations");
            if (parameter != null)
            {
                parameter.Element(SoapTransport.Management + "ParameterTypeName")!.Value = "String";
                parameter.Element(SoapTransport.Management + "DefaultValues")!.RemoveNodes();
                parameter.Element(SoapTransport.Management + "ValidValues")!.ReplaceNodes(Enumerable.Range(1, count).Select(i =>
                    new XElement(SoapTransport.Management + "ValidValue", new XElement(SoapTransport.Management + "Label", "Choice " + i),
                        new XElement(SoapTransport.Management + "Value", i.ToString().PadRight(valueLength, 'x')))));
                response.Content = new StringContent(xml.ToString(), System.Text.Encoding.UTF8, "text/xml");
            }
            return response;
        }
    }
}
