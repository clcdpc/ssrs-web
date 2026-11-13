using System.Text.Json;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Soap;
namespace SsrsPortal.Tests;

public sealed class ParameterTests
{
    [Theory]
    [InlineData("1", "false", 71)]
    [InlineData("2", "false", 73)]
    [InlineData("3", "false", 72)]
    [InlineData("1", "true", 71)]
    [InlineData("2", "true", 73)]
    [InlineData("3", "true", 72)]
    public async Task MultiParentCascadeUsesBothParentsAndFalse(string library, string flag, int count)
    {
        using var h = new Harness(); var report = await h.Report();
        var (form, execution) = await h.Parameters.ResolveAsync(report, Harness.Inputs(library, flag), true, default);
        Assert.True(form.CanRun); Assert.Equal(count, form.Fields.Single(p => p.Name == "collections").OptionCount);
        Assert.Contains(execution, v => v.Name == "applyCollectionFilter" && v.Value == flag);
        var last = h.Mock.Observations.Last(p => p.Operation == "GetItemParameters").Request;
        Assert.Contains(last.Descendants(SoapTransport.Management + "ParameterValue"), x => x.Element(SoapTransport.Management + "Name")?.Value == "applyCollectionFilter" && x.Element(SoapTransport.Management + "Value")?.Value == flag);
        Assert.Contains(last.Descendants(SoapTransport.Management + "ParameterValue"), x => x.Element(SoapTransport.Management + "Name")?.Value == "library" && x.Element(SoapTransport.Management + "Value")?.Value == library);
    }

    [Fact]
    public async Task MissingBooleanIsOutstandingAndInitialDefaultsDoNotInventFalse()
    {
        using var h = new Harness(); var report = await h.Report();
        var (form, _) = await h.Parameters.ResolveAsync(report, new(null, true), false, default);
        Assert.False(form.CanRun); Assert.Empty(form.Values["applyCollectionFilter"]);
        Assert.Equal("HasOutstandingDependencies", form.Fields.Single(p => p.Name == "collections").State);
        Assert.Equal("2026-01-01T00:00:00", form.Values["start"].Single()); Assert.Null(form.Values["note"].Single());
        Assert.DoesNotContain(form.Fields, p => p.Name == "internalMode"); Assert.False(form.Values.ContainsKey("internalMode"));
    }

    [Fact]
    public async Task StaleSelectionsRemovedAndDefaultsFollowChangedParents()
    {
        using var h = new Harness(); var report = await h.Report();
        var old = Harness.Inputs("2", "true", ["1-off-1", "1-off-2"]);
        var (form, _) = await h.Parameters.ResolveAsync(report, old, false, default);
        Assert.DoesNotContain("1-off-1", form.Values["collections"]); Assert.Contains("2-on-1", form.Values["collections"]);
        Assert.Null(form.Fields.Single(p => p.Name == "collections").Error);
        Assert.Equal("values", (await Assert.ThrowsAsync<PortalException>(() => h.Parameters.ResolveAsync(report, old, true, default))).Code);
        var missingChild = Harness.Inputs("3", "false", []) with { Changed = ["library"] };
        var fresh = await h.Parameters.ResolveAsync(report, missingChild, true, default);
        Assert.Equal("3-off-1", fresh.Form.Values["collections"].Single());
    }

    [Fact]
    public async Task TypedNullBlankHiddenAndMultivalueInputsReachFreshExecution()
    {
        using var h = new Harness(); var report = await h.Report();
        var input = Harness.Inputs(collections: ["1-off-1", "1-off-2"]); input.Values!["note"] = [""];
        var result = await h.Execution.RunAsync("owner", report.Id, input, default);
        var set = h.Mock.Observations.Last(x => x.Operation == "SetExecutionParameters");
        var parameters = set.Request.Descendants(SoapTransport.Execution + "ParameterValue").ToArray();
        Assert.Equal(new[] { "1-off-1", "1-off-2" }, parameters.Where(x => x.Element(SoapTransport.Execution + "Name")!.Value == "collections").Select(x => x.Element(SoapTransport.Execution + "Value")!.Value));
        Assert.Contains(parameters, x => x.Element(SoapTransport.Execution + "Name")?.Value == "internalMode" && x.Element(SoapTransport.Execution + "Value")?.Value == "safe-default");
        Assert.Contains(parameters, x => x.Element(SoapTransport.Execution + "Name")?.Value == "note" && x.Element(SoapTransport.Execution + "Value")?.Value == "");
        input.Values["note"] = [null]; input.Values["start"] = ["2026-05-07"];
        var resolved = await h.Parameters.ResolveAsync(report, input, true, default);
        Assert.Null(resolved.Form.Values["material"].Single());
        Assert.Equal(50, resolved.Form.Fields.Single(p => p.Name == "material").OptionCount);
        Assert.Null(resolved.Form.Fields.Single(p => p.Name == "material").Error);
        Assert.Contains(resolved.ExecutionValues, p => p.Name == "note" && p.Value == null);
        Assert.Contains(resolved.ExecutionValues, p => p.Name == "start" && p.Value == "2026-05-07T00:00:00");
        var nullRequest = h.Mock.Observations.Last(p => p.Operation == "GetItemParameters").Request.Descendants(SoapTransport.Management + "ParameterValue").Single(p => p.Element(SoapTransport.Management + "Name")!.Value == "note");
        Assert.Null(nullRequest.Element(SoapTransport.Management + "Value"));
        Assert.Matches("^[0-9a-f]{64}$", result.Token);
        Assert.DoesNotContain(h.Logger.Messages, m => m.Contains("safe-default", StringComparison.Ordinal) || m.Contains("1-off-1", StringComparison.Ordinal) || m.Contains("Synthetic library", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("library", "999")]
    [InlineData("library", "abc")]
    [InlineData("applyCollectionFilter", "2")]
    [InlineData("start", "May 7 2026")]
    [InlineData("ratio", "NaN")]
    [InlineData("ratio", "Infinity")]
    [InlineData("internalMode", "unsafe")]
    [InlineData("unknown", "anything")]
    public async Task ExecutionRejectsInvalidTypedOptionHiddenAndUnknownValues(string name, string value)
    {
        using var h = new Harness(); var report = await h.Report(); var input = Harness.Inputs(); input.Values![name] = [value];
        Assert.Equal("values", (await Assert.ThrowsAsync<PortalException>(() => h.Execution.RunAsync("user", report.Id, input, default))).Code);
        Assert.DoesNotContain(h.Mock.Observations, o => o.Operation == "LoadReport"); Assert.Equal(0, h.Sessions.Snapshot().Count);
    }

    [Fact]
    public async Task MetadataOnlyDynamicStateDiffersFromRuntimeQueryFailure()
    {
        using var h = new Harness(); var report = await h.Report();
        Assert.Equal("DynamicValuesUnavailable", (await h.Soap.ParametersAsync(report.Path, [], false, default))[0].State);
        h.Mock.ParameterQueryUnavailable = true;
        var (form, _) = await h.Parameters.ResolveAsync(report, Harness.Inputs(), false, default);
        Assert.Contains(form.Fields, p => p.State == "DynamicValuesUnavailable" && p.Error != null);
        Assert.Equal("query", (await Assert.ThrowsAsync<PortalException>(() => h.Parameters.ResolveAsync(report, Harness.Inputs(), true, default))).Code);
    }

    [Fact]
    public async Task Complete4192ChoicesLoadSeparatelyAndConfiguredLimitsStillApply()
    {
        using var h = new Harness(); var report = await h.Report("Circulation By Workstation");
        var input = new ParameterInput(new Dictionary<string, string?[]> { ["library"] = ["1"], ["workstations"] = ["4192"] });
        var resolved = await h.Parameters.ResolveAsync(report, input, true, default);
        Assert.Equal(4192, resolved.Form.Fields.Single(p => p.Name == "workstations").OptionCount);
        Assert.Empty(resolved.Form.Fields.Single(p => p.Name == "workstations").Options);
        var choices = await h.Parameters.ChoicesAsync(report, input, "workstations", default);
        Assert.Equal(4192, choices.Options.Length);
        Assert.Equal("4192", choices.Options[^1].Value);
        h.Options.MaxOptions = 4000;
        Assert.Equal("limit", (await Assert.ThrowsAsync<PortalException>(() => h.Parameters.ChoicesAsync(report, input, "workstations", default))).Code);
    }
}
