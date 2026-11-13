using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SsrsPortal.Core;
using SsrsPortal.Services;
using SsrsPortal.Soap;

namespace SsrsPortal.Tests;

public sealed class ParameterDefaultsTests
{
    private static readonly XNamespace Management = "http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer";
    private static readonly XNamespace Execution = "http://schemas.microsoft.com/sqlserver/2005/06/30/reporting/reportingservices";
    // This envelope is independent of the production parser and the mock response builder.
    [Fact]
    public async Task LiteralDefaultsNormalizeTypesAndPreserveNullBlankMultiAndHiddenValues()
    {
        using var wire = new ParameterWire();
        var result = await wire.Parameters.ResolveAsync(wire.Report, new(null, true), true, default);
        Assert.True(result.Form.CanRun);
        Assert.Equal("21", Assert.Single(result.Form.Values["branch"]));
        Assert.Equal("false", Assert.Single(result.Form.Values["enabled"]));
        Assert.Equal("2026-10-30T09:15:23.1234567", Assert.Single(result.Form.Values["start"]));
        Assert.Null(Assert.Single(result.Form.Values["note"]));
        Assert.Equal("", Assert.Single(result.Form.Values["blank"]));
        Assert.Equal("1.5", Assert.Single(result.Form.Values["ratio"]));
        Assert.Equal(new[] { "a", "b" }, result.Form.Values["tags"]);
        Assert.DoesNotContain(result.Form.Fields, f => f.Name == "internal");
        Assert.Contains(result.ExecutionValues, v => v.Name == "internal" && v.Value == "server default");
        Assert.InRange(wire.Handler.Requests.Count, 1, 3);
    }

    [Fact]
    public async Task ExplicitlyClearedQueryDefaultStaysClearedAcrossRefreshAndExecution()
    {
        using var h = new Harness();
        var report = await h.Report();
        var input = Harness.Inputs();
        input.Values!["material"] = [];
        var result = await h.Parameters.ResolveAsync(report, input, false, default);
        Assert.Empty(result.Form.Values["material"]);
        Assert.False(result.Form.CanRun);
        Assert.Equal("values", (await Assert.ThrowsAsync<PortalException>(() => h.Parameters.ResolveAsync(report, input, true, default))).Code);
    }

    [Theory]
    [InlineData("2026-10-30", "2026-10-30T00:00:00")]
    [InlineData("2026-10-30T09:15", "2026-10-30T09:15:00")]
    [InlineData("2026-10-30T09:15:23", "2026-10-30T09:15:23")]
    [InlineData("2026-10-30T09:15:23.1", "2026-10-30T09:15:23.1")]
    [InlineData("2026-10-30T09:15:23.123", "2026-10-30T09:15:23.123")]
    [InlineData("2026-10-30T09:15:23.1234567", "2026-10-30T09:15:23.1234567")]
    [InlineData("2026-10-30T09:15:23Z", "2026-10-30T09:15:23Z")]
    [InlineData("2026-10-30T09:15Z", "2026-10-30T09:15:00Z")]
    [InlineData("2026-10-30T09:15:23.1234567Z", "2026-10-30T09:15:23.1234567Z")]
    [InlineData("2026-10-30T09:15:23.123-04:00", "2026-10-30T09:15:23.123-04:00")]
    [InlineData("2026-10-30T09:15:23.1234567+05:45", "2026-10-30T09:15:23.1234567+05:45")]
    [InlineData("2026-10-30T09:15-04:00", "2026-10-30T09:15:00-04:00")]
    [InlineData("2026-10-30T09:15:23+14:00", "2026-10-30T09:15:23+14:00")]
    [InlineData("2026-10-30T09:15:23-14:00", "2026-10-30T09:15:23-14:00")]
    public async Task IsoDatePrecisionAndOffsetSurviveToExecution(string value, string expected)
    {
        using var h = new Harness();
        var report = await h.Report();
        var input = Harness.Inputs();
        input.Values!["start"] = [value];
        var result = await h.Parameters.ResolveAsync(report, input, true, default);
        Assert.Equal(expected, Assert.Single(result.Form.Values["start"]));
        Assert.Contains(result.ExecutionValues, p => p.Name == "start" && p.Value == expected);
        await h.Soap.SetAsync((await h.Soap.LoadAsync(report.Path, default)).ExecutionId, result.ExecutionValues, default);
        Assert.Contains(h.Mock.Observations.Last(o => o.Operation == "SetExecutionParameters").Request.Descendants(Execution + "ParameterValue"),
            p => p.Element(Execution + "Name")!.Value == "start" && p.Element(Execution + "Value")!.Value == expected);
    }

    [Theory]
    [InlineData("9/1/2026 12:00:00 AM", "2026-09-01T00:00:00")]
    [InlineData("9/30/2026 11:59:59 PM", "2026-09-30T23:59:59")]
    [InlineData("9/1/2026 12:00:00 PM", "2026-09-01T12:00:00")]
    [InlineData("9/1/2026 1:00:00 PM", "2026-09-01T13:00:00")]
    [InlineData("9/1/2026 11:59:59 PM", "2026-09-01T23:59:59")]
    [InlineData("2/29/2024 12:00:00 AM", "2024-02-29T00:00:00")]
    public async Task SsrsDatesNormalizeIdenticallyInMetadataAndIncomingValuesAcrossCultures(string value, string expected)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var culture in new[] { CultureInfo.InvariantCulture, new("en-GB"), new("fr-FR"), new("ar-SA") })
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
                using var wire = new ParameterWire(); wire.Handler.Fixture = "dev-date-defaults";
                wire.Handler.Transform = (_, xml) => xml.Descendants(Management + "DefaultValues").First().Elements().Single().Value = value;
                var defaults = await wire.Parameters.ResolveAsync(wire.Report, new(null, true), true, default);
                Assert.Equal(expected, Assert.Single(defaults.Form.Values["beginDate"]));
                Assert.Contains("beginDate", defaults.Form.Defaulted);
                var incoming = await wire.Parameters.ResolveAsync(wire.Report,
                    new(new() { ["beginDate"] = [value], ["endDate"] = ["9/30/2026 11:59:59 PM"] }, Changed: ["beginDate"]), true, default);
                Assert.True(incoming.Form.CanRun);
                Assert.Equal(expected, Assert.Single(incoming.Form.Values["beginDate"]));
                Assert.Equal("2026-09-30T23:59:59", Assert.Single(incoming.Form.Values["endDate"]));
                Assert.DoesNotContain("beginDate", incoming.Form.Defaulted);
                var again = await wire.Parameters.ResolveAsync(wire.Report, new(incoming.Form.Values, Defaulted: incoming.Form.Defaulted), true, default);
                Assert.Equal(expected, Assert.Single(again.Form.Values["beginDate"]));
                await wire.Soap.SetAsync("literal-execution", again.ExecutionValues, default);
                Assert.Equal(new[] { new ParameterValue("beginDate", expected), new("endDate", "2026-09-30T23:59:59") }, SentExecutionValues(wire));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Theory]
    [InlineData("2026-02-30T00:00:00")]
    [InlineData("10/30/2026")]
    [InlineData("2026-10-30T09:15:23.12345678")]
    [InlineData("2026-10-30T09:15:23+25:00")]
    [InlineData("2026-10-30T09:15:23.")]
    [InlineData("2026-10-30T09:15:23.12345678Z")]
    [InlineData("2026-10-30T09:15:23+14:01")]
    [InlineData("2026-10-30T09:15:23-14:01")]
    [InlineData("2026-10-30T09:15:23+04:60")]
    [InlineData("2026-10-30T09:15:23+0400")]
    [InlineData("2026-10-30T09:15:23.Z")]
    [InlineData("2/29/2025 12:00:00 AM")]
    [InlineData("2/30/2026 12:00:00 AM")]
    [InlineData("13/1/2026 12:00:00 AM")]
    [InlineData("9/31/2026 11:59:59 PM")]
    [InlineData("30/9/2026 11:59:59 PM")]
    [InlineData("9/1/2026")]
    [InlineData("01/09/2026")]
    [InlineData("September 1, 2026")]
    [InlineData("9/1/2026 13:00:00 PM")]
    [InlineData("9/1/2026 12:60:00 AM")]
    [InlineData("9/1/2026 12:00:60 AM")]
    [InlineData("9/1/2026 12:00:00 XM")]
    [InlineData("9/1/2026 12:00:00")]
    [InlineData("9/1/2026 12:00 AM")]
    [InlineData("9/1/2026 12:00:00 AMZ")]
    public async Task InvalidOrAmbiguousDatesRemainRejected(string value)
    {
        using var h = new Harness();
        var input = Harness.Inputs();
        input.Values!["start"] = [value];
        var report = await h.Report();
        Assert.Equal("values", (await Assert.ThrowsAsync<PortalException>(() => h.Parameters.ResolveAsync(report, input, true, default))).Code);
        using var wire = new ParameterWire(); wire.Handler.Fixture = "dev-date-defaults";
        wire.Handler.Transform = (_, xml) => xml.Descendants(Management + "DefaultValues").First().Elements().Single().Value = value;
        var form = (await wire.Parameters.ResolveAsync(wire.Report, new(null, true), false, default)).Form;
        Assert.False(form.CanRun);
        Assert.Empty(form.Values["beginDate"]);
        Assert.NotNull(form.Fields.Single(f => f.Name == "beginDate").Error);
        Assert.Equal("values", (await Assert.ThrowsAsync<PortalException>(() => wire.Parameters.ResolveAsync(wire.Report, new(null, true), true, default))).Code);
    }

    [Fact]
    public async Task CapturedDevDateDefaultsNormalizeAndReachSetExecutionParametersWithSeconds()
    {
        using var wire = new ParameterWire(); wire.Handler.Fixture = "dev-date-defaults";
        var metadata = await wire.Soap.ParametersAsync(wire.Report.Path, [], true, default);
        Assert.Equal("9/1/2026 12:00:00 AM", Assert.Single(metadata[0].Defaults));
        Assert.Equal("9/30/2026 11:59:59 PM", Assert.Single(metadata[1].Defaults));
        var resolved = await wire.Parameters.ResolveAsync(wire.Report, new(null, true), true, default);
        Assert.True(resolved.Form.CanRun);
        Assert.Equal("2026-09-01T00:00:00", Assert.Single(resolved.Form.Values["beginDate"]));
        Assert.Equal("2026-09-30T23:59:59", Assert.Single(resolved.Form.Values["endDate"]));
        Assert.Equal(new[] { "beginDate", "endDate" }, resolved.Form.Defaulted);
        var again = await wire.Parameters.ResolveAsync(wire.Report, new(resolved.Form.Values, Defaulted: resolved.Form.Defaulted), true, default);
        Assert.True(again.Form.CanRun);
        Assert.Equal(resolved.Form.Defaulted, again.Form.Defaulted);
        await wire.Soap.SetAsync("literal-execution", again.ExecutionValues, default);
        var sent = wire.Handler.Requests.Last().Descendants(Execution + "ParameterValue").ToArray();
        Assert.Contains(sent, p => p.Element(Execution + "Name")!.Value == "beginDate" && p.Element(Execution + "Value")!.Value == "2026-09-01T00:00:00");
        Assert.Contains(sent, p => p.Element(Execution + "Name")!.Value == "endDate" && p.Element(Execution + "Value")!.Value == "2026-09-30T23:59:59");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DateDefaultsAndManualEditsKeepProvenanceAcrossUnrelatedRefreshAndExecution(bool manualEdit)
    {
        using var wire = new ParameterWire(); wire.Handler.Fixture = "dev-date-defaults";
        wire.Handler.Transform = (_, xml) => xml.Descendants(Management + "Parameters").Single().Add(NoteParameter());
        var initial = await wire.Parameters.ResolveAsync(wire.Report, new(null, true), false, default);
        Assert.True(initial.Form.CanRun);
        var expectedBegin = "2026-09-01T00:00:00";
        if (manualEdit)
        {
            expectedBegin = "2026-10-01T01:02:03";
            initial.Form.Values["beginDate"] = [expectedBegin];
            initial = await wire.Parameters.ResolveAsync(wire.Report,
                new(initial.Form.Values, Changed: ["beginDate"], Defaulted: initial.Form.Defaulted), false, default);
        }
        initial.Form.Values["note"] = ["edited unrelated note"];
        var refreshed = await wire.Parameters.ResolveAsync(wire.Report,
            new(initial.Form.Values, Changed: ["note"], Defaulted: initial.Form.Defaulted), false, default);
        Assert.True(refreshed.Form.CanRun);
        Assert.Equal(expectedBegin, Assert.Single(refreshed.Form.Values["beginDate"]));
        Assert.Equal("2026-09-30T23:59:59", Assert.Single(refreshed.Form.Values["endDate"]));
        Assert.Equal(!manualEdit, refreshed.Form.Defaulted.Contains("beginDate"));
        Assert.Contains("endDate", refreshed.Form.Defaulted);
        Assert.DoesNotContain("note", refreshed.Form.Defaulted);
        var execution = await wire.Parameters.ResolveAsync(wire.Report,
            new(refreshed.Form.Values, Defaulted: refreshed.Form.Defaulted), true, default);
        await wire.Soap.SetAsync("literal-execution", execution.ExecutionValues, default);
        Assert.Equal(new[] { new ParameterValue("beginDate", expectedBegin), new("endDate", "2026-09-30T23:59:59"), new("note", "edited unrelated note") },
            SentExecutionValues(wire));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CascadingDatesUseCurrentParentsAndPreserveManualDates(bool manualEdit)
    {
        using var wire = new ParameterWire(); wire.Handler.Fixture = "dev-date-defaults";
        wire.Handler.Transform = (request, xml) =>
        {
            var values = request.Descendants(Management + "ParameterValue").ToDictionary(p => p.Element(Management + "Name")!.Value,
                p => p.Element(Management + "Value")?.Value);
            var parameters = xml.Descendants(Management + "Parameters").Single();
            parameters.AddFirst(new XElement(Management + "ItemParameter", new XElement(Management + "Name", "period"),
                new XElement(Management + "ParameterTypeName", "String"), new XElement(Management + "PromptUser", "true"),
                new XElement(Management + "DefaultValues", new XElement(Management + "Value", "september")),
                new XElement(Management + "ParameterStateName", "HasValidValue")));
            parameters.Add(NoteParameter());
            foreach (var p in parameters.Elements().Where(p => p.Element(Management + "ParameterTypeName")!.Value == "DateTime"))
            {
                var begin = p.Element(Management + "Name")!.Value == "beginDate";
                var parent = begin ? "period" : "beginDate";
                p.Add(new XElement(Management + "Dependencies", new XElement(Management + "Dependency", parent)),
                    new XElement(Management + "DefaultValuesQueryBased", "true"));
                if (!values.TryGetValue(parent, out var current))
                {
                    // Omitted parents let SSRS evaluate its September default
                    // context. Those dates must wait for the current parent wave.
                    continue;
                }
                else
                {
                    p.Element(Management + "DefaultValues")!.Elements().Single().Value = begin
                        ? current == "october" ? "10/1/2026 12:00:00 AM" : "9/1/2026 12:00:00 AM"
                        : current!.StartsWith("2026-11", StringComparison.Ordinal) ? "11/30/2026 11:59:59 PM"
                        : current.StartsWith("2026-10", StringComparison.Ordinal) ? "10/31/2026 11:59:59 PM" : "9/30/2026 11:59:59 PM";
                }
            }
        };
        var initial = await wire.Parameters.ResolveAsync(wire.Report, new(null, true), false, default);
        Assert.True(initial.Form.CanRun);
        Assert.Equal("2026-09-01T00:00:00", Assert.Single(initial.Form.Values["beginDate"]));
        Assert.Equal("2026-09-30T23:59:59", Assert.Single(initial.Form.Values["endDate"]));
        if (manualEdit)
        {
            initial.Form.Values["beginDate"] = ["2026-11-02T01:02:03"];
            initial = await wire.Parameters.ResolveAsync(wire.Report,
                new(initial.Form.Values, Changed: ["beginDate"], Defaulted: initial.Form.Defaulted), false, default);
        }
        initial.Form.Values["period"] = ["october"];
        wire.Handler.Requests.Clear();
        var changed = await wire.Parameters.ResolveAsync(wire.Report,
            new(initial.Form.Values, Changed: ["period"], Defaulted: initial.Form.Defaulted), false, default);
        var expectedBegin = manualEdit ? "2026-11-02T01:02:03" : "2026-10-01T00:00:00";
        var expectedEnd = manualEdit ? "2026-11-30T23:59:59" : "2026-10-31T23:59:59";
        Assert.True(changed.Form.CanRun);
        Assert.Equal(expectedBegin, Assert.Single(changed.Form.Values["beginDate"]));
        Assert.Equal(expectedEnd, Assert.Single(changed.Form.Values["endDate"]));
        Assert.Equal(!manualEdit, changed.Form.Defaulted.Contains("beginDate"));
        Assert.Contains("endDate", changed.Form.Defaulted);
        // Every evaluated wave must omit stale defaults, including the response
        // whose endDate was evaluated before beginDate changed in that same wave.
        foreach (var request in wire.Handler.Requests.Skip(1))
        {
            var sent = request.Descendants(Management + "ParameterValue").ToArray();
            Assert.Contains(sent, p => p.Element(Management + "Name")!.Value == "period" && p.Element(Management + "Value")!.Value == "october");
            Assert.DoesNotContain(sent, p => p.Element(Management + "Name")!.Value == "endDate" && p.Element(Management + "Value")!.Value != expectedEnd);
        }
        changed.Form.Values["note"] = ["keep manual note"];
        var refreshed = await wire.Parameters.ResolveAsync(wire.Report,
            new(changed.Form.Values, Changed: ["note"], Defaulted: changed.Form.Defaulted), true, default);
        Assert.Equal(expectedBegin, Assert.Single(refreshed.Form.Values["beginDate"]));
        Assert.Equal(expectedEnd, Assert.Single(refreshed.Form.Values["endDate"]));
        Assert.Equal(!manualEdit, refreshed.Form.Defaulted.Contains("beginDate"));
        Assert.Contains("endDate", refreshed.Form.Defaulted);
        await wire.Soap.SetAsync("literal-execution", refreshed.ExecutionValues, default);
        Assert.Equal(refreshed.ExecutionValues, SentExecutionValues(wire));
        Assert.Contains(SentExecutionValues(wire), p => p.Name == "beginDate" && p.Value == expectedBegin);
        Assert.Contains(SentExecutionValues(wire), p => p.Name == "endDate" && p.Value == expectedEnd);
    }

    [Fact]
    public async Task ServerDateValidChoicesNormalizeConsistentlyWithDefaults()
    {
        using var wire = new ParameterWire(); wire.Handler.Fixture = "dev-date-defaults";
        wire.Handler.Transform = (_, xml) =>
        {
            foreach (var p in xml.Descendants(Management + "ItemParameter"))
            {
                p.Add(new XElement(Management + "ValidValues", new XElement(Management + "ValidValue",
                    new XElement(Management + "Value", p.Element(Management + "DefaultValues")!.Value),
                    new XElement(Management + "Label", "Server date label"))));
            }
        };
        var result = await wire.Parameters.ResolveAsync(wire.Report, new(null, true), true, default);
        Assert.True(result.Form.CanRun);
        Assert.Equal("Server date label", Assert.Single(result.Form.Fields[1].SelectedOptions).Label);
        Assert.Equal("2026-09-30T23:59:59", (await wire.Parameters.ChoicesAsync(wire.Report, new(result.Form.Values, Defaulted: result.Form.Defaulted), result.Form.Fields[1].Name, default)).Options.Single().Value);
        var incoming = await wire.Parameters.ResolveAsync(wire.Report,
            new(new() { ["beginDate"] = ["9/1/2026 12:00:00 AM"], ["endDate"] = ["9/30/2026 11:59:59 PM"] }), true, default);
        Assert.True(incoming.Form.CanRun);
        await wire.Soap.SetAsync("literal-execution", incoming.ExecutionValues, default);
        Assert.Equal(new[] { new ParameterValue("beginDate", "2026-09-01T00:00:00"), new("endDate", "2026-09-30T23:59:59") }, SentExecutionValues(wire));
    }

    [Fact]
    public async Task SelectedChoicesCarryLabelsOutsideTheFirstPage()
    {
        using var h = new Harness();
        var result = await h.Parameters.ResolveAsync(await h.Report("Circulation By Workstation"),
            new(new() { ["library"] = ["1"], ["workstations"] = ["3500"] }), true, default);
        var field = result.Form.Fields.Single(f => f.Name == "workstations");
        Assert.Empty(field.Options);
        Assert.DoesNotContain(field.Options, o => o.Value == "3500");
        var json = JsonSerializer.SerializeToElement(field, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("Synthetic workstation 3500", json.GetProperty("selectedOptions")[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task MissingDefaultAndExplicitClearAreDistinctFromNullAndBlankDefaults()
    {
        using var wire = new ParameterWire();
        wire.Handler.Transform = (_, xml) => xml.Descendants(Management + "ItemParameter").First().Element(Management + "DefaultValues")!.Remove();
        var noDefault = await wire.Parameters.ResolveAsync(wire.Report, new(null, true), false, default);
        Assert.Empty(noDefault.Form.Values["branch"]); Assert.False(noDefault.Form.CanRun);
        Assert.Null(Assert.Single(noDefault.Form.Values["note"]));
        Assert.Equal("", Assert.Single(noDefault.Form.Values["blank"]));
        wire.Handler.Transform = null;
        var cleared = await wire.Parameters.ResolveAsync(wire.Report, new(new() { ["branch"] = [] }, true), false, default);
        Assert.Empty(cleared.Form.Values["branch"]);
    }

    [Fact]
    public async Task LiteralMultiLevelAndMultiParentDefaultsWaitForCurrentContextAndReachSetExecutionParameters()
    {
        using var wire = new ParameterWire(); wire.Handler.Fixture = "cascade-defaults";
        wire.Handler.Transform = (request, xml) =>
        {
            var values = request.Descendants(Management + "ParameterValue").Select(p => p.Element(Management + "Name")!.Value).ToHashSet();
            foreach (var p in xml.Descendants(Management + "ItemParameter"))
            {
                if (p.Element(Management + "Dependencies")?.Elements().Any(d => !values.Contains(d.Value)) == true)
                {
                    p.Element(Management + "DefaultValues")?.Remove();
                    p.Element(Management + "ValidValues")?.Remove();
                    p.Element(Management + "ParameterStateName")!.Value = "HasOutstandingDependencies";
                }
            }
        };
        var resolved = await wire.Parameters.ResolveAsync(wire.Report, new(null, true), true, default);
        Assert.True(resolved.Form.CanRun);
        Assert.Equal(new[] { "north", "shared" }, resolved.Form.Values["child"]);
        Assert.Equal("north-team", Assert.Single(resolved.Form.Values["sibling"]));
        Assert.Equal("leaf", Assert.Single(resolved.Form.Values["leaf"]));
        Assert.Contains(resolved.ExecutionValues, p => p.Name == "internal" && p.Value == "north-internal");
        Assert.Equal(4, wire.Handler.Requests.Count);
        Assert.DoesNotContain(wire.Handler.Requests[1].Descendants(Management + "Name"), n => n.Value == "child");
        Assert.Contains(wire.Handler.Requests[2].Descendants(Management + "ParameterValue"), p => p.Element(Management + "Name")!.Value == "child");
        await wire.Soap.SetAsync("literal-execution", resolved.ExecutionValues, default);
        var sent = wire.Handler.Requests.Last().Descendants(Execution + "ParameterValue").Select(p =>
            new ParameterValue(p.Element(Execution + "Name")!.Value, p.Element(Execution + "Value")?.Value)).ToArray();
        Assert.Equal(resolved.ExecutionValues, sent);
    }

    [Fact]
    public async Task ParentEditsPreserveValidChildrenAndUnrelatedSelectionsButInitializeInvalidatedChildren()
    {
        using var h = new Harness(); var report = await h.Report();
        var input = Harness.Inputs("2", "true", ["2-on-7"]) with { Changed = ["library"] };
        input.Values!["note"] = ["keep my note"];
        input.Values["material"] = [];
        var resolved = await h.Parameters.ResolveAsync(report, input, false, default);
        Assert.Equal("2-on-7", Assert.Single(resolved.Form.Values["collections"]));
        Assert.Equal("keep my note", Assert.Single(resolved.Form.Values["note"]));
        Assert.Empty(resolved.Form.Values["material"]);
        input.Values["collections"] = [];
        resolved = await h.Parameters.ResolveAsync(report, input, false, default);
        Assert.Equal("2-on-1", Assert.Single(resolved.Form.Values["collections"]));
        var clearedChild = input with { Changed = ["collections"] };
        resolved = await h.Parameters.ResolveAsync(report, clearedChild, false, default);
        Assert.Empty(resolved.Form.Values["collections"]);
        Assert.False(resolved.Form.CanRun);
    }

    [Fact]
    public async Task RefreshedSelectionsAreExactlyTheValuesSentForExecutionAndOptionSearchUsesNoExtraMetadataCall()
    {
        using var h = new Harness(); var report = await h.Report();
        var displayed = await h.Parameters.ResolveAsync(report, Harness.Inputs("3", "false", ["1-off-1"]), false, default);
        Assert.True(displayed.Form.CanRun);
        await h.Execution.RunAsync("owner", report.Id, new(displayed.Form.Values), default);
        var sent = h.Mock.Observations.Last(o => o.Operation == "SetExecutionParameters").Request.Descendants(Execution + "ParameterValue");
        foreach (var field in displayed.Form.Fields)
        {
            Assert.Equal(field.Selected, sent.Where(v => v.Element(Execution + "Name")!.Value == field.Name).Select(v => v.Element(Execution + "Value")?.Value));
        }
        var before = CatalogCacheTests.Count(h, "GetItemParameters");
        await h.Parameters.ChoicesAsync(report, new(displayed.Form.Values), "collections", default);
        Assert.Equal(2, CatalogCacheTests.Count(h, "GetItemParameters") - before);
    }

    [Fact]
    public async Task NonConvergingMetadataStopsAtTheFixedPointBudget()
    {
        using var wire = new ParameterWire();
        wire.Handler.Transform = (_, xml) =>
        {
            var parameters = xml.Descendants(Management + "Parameters").Single();
            parameters.Elements().Skip(1).Remove();
            var p = parameters.Elements().Single();
            var value = wire.Handler.Requests.Count % 2 == 0 ? "22" : "21";
            p.Element(Management + "DefaultValues")!.Element(Management + "Value")!.Value = value;
            p.Element(Management + "ValidValues")!.Descendants(Management + "Value").Single().Value = value;
        };
        Assert.Equal("query", (await Assert.ThrowsAsync<PortalException>(() => wire.Parameters.ResolveAsync(wire.Report, new(null, true), false, default))).Code);
        Assert.Equal(5, wire.Handler.Requests.Count);
    }

    [Fact]
    public async Task QueryDefaultsUseSuppliedParentsEvenWhenTheOldDefaultIsStillAnAvailableChoice()
    {
        using var wire = new ParameterWire(); wire.Handler.Fixture = "cascade-defaults";
        wire.Handler.Transform = (request, xml) =>
        {
            if (!request.Descendants(Management + "ParameterValue").Any(p => p.Element(Management + "Name")!.Value == "branch" && p.Element(Management + "Value")!.Value == "2")) { return; }
            foreach (var p in xml.Descendants(Management + "ItemParameter"))
            {
                var name = p.Element(Management + "Name")!.Value;
                if (name is not ("child" or "sibling" or "internal")) { continue; }
                var value = name == "child" ? "south" : name == "sibling" ? "south-team" : "south-internal";
                p.Element(Management + "DefaultValues")!.Elements().First().Value = value;
                p.Element(Management + "ValidValues")?.Add(new XElement(Management + "ValidValue",
                    new XElement(Management + "Label", "South"), new XElement(Management + "Value", value)));
            }
        };
        var result = await wire.Parameters.ResolveAsync(wire.Report, new(new() { ["branch"] = ["2"], ["flag"] = ["false"] }, true), true, default);
        Assert.Equal(new[] { "south", "shared" }, result.Form.Values["child"]);
        Assert.Equal("south-team", Assert.Single(result.Form.Values["sibling"]));
        Assert.Contains(result.ExecutionValues, p => p.Name == "internal" && p.Value == "south-internal");
        var initial = await wire.Parameters.ResolveAsync(wire.Report, new(null, true), false, default);
        initial.Form.Values["branch"] = ["2"];
        var followDefault = await wire.Parameters.ResolveAsync(wire.Report,
            new(initial.Form.Values, Changed: ["branch"], Defaulted: initial.Form.Defaulted), false, default);
        Assert.Equal(new[] { "south", "shared" }, followDefault.Form.Values["child"]);
        Assert.Contains("child", followDefault.Form.Defaulted);
        var userSelected = await wire.Parameters.ResolveAsync(wire.Report,
            new(initial.Form.Values, Changed: ["branch"], Defaulted: initial.Form.Defaulted.Where(n => n != "child").ToArray()), false, default);
        Assert.Equal(new[] { "north", "shared" }, userSelected.Form.Values["child"]);
        Assert.DoesNotContain("child", userSelected.Form.Defaulted);
    }

    [Fact]
    public async Task ShowcaseDefaultsAndHiddenQueryDefaultsSurviveParentEditsAndExecution()
    {
        using var h = new Harness(); var report = await h.Report("Default Showcase");
        var initial = await h.Parameters.ResolveAsync(report, new(null, true), false, default);
        Assert.True(initial.Form.CanRun);
        Assert.Equal(new[] { "3500", "4192" }, initial.Form.Values["workstations"]);
        Assert.Equal(new[] { "First entry", "Second entry" }, initial.Form.Values["lines"]);
        initial.Form.Values["branch"] = ["2"];
        initial.Form.Values["lines"] = ["User entry", "Other entry"];
        var changed = await h.Parameters.ResolveAsync(report, new(initial.Form.Values, Changed: ["branch"],
            Defaulted: initial.Form.Defaulted.Where(n => n != "lines").ToArray()), false, default);
        Assert.True(changed.Form.CanRun);
        Assert.Equal("2-false", Assert.Single(changed.Form.Values["child"]));
        Assert.Equal("Branch 2", Assert.Single(changed.Form.Values["branchNote"]));
        Assert.Contains(changed.ExecutionValues, p => p.Name == "internal" && p.Value == "internal-2");
        await h.Execution.RunAsync("owner", report.Id, new(changed.Form.Values), default);
        var sent = h.Mock.Observations.Last(p => p.Operation == "SetExecutionParameters").Request.Descendants(Execution + "ParameterValue").ToArray();
        Assert.Contains(sent, p => p.Element(Execution + "Name")!.Value == "internal" && p.Element(Execution + "Value")!.Value == "internal-2");
        Assert.Equal(new[] { "User entry", "Other entry" }, sent.Where(p => p.Element(Execution + "Name")!.Value == "lines").Select(p => p.Element(Execution + "Value")!.Value));
    }

    [Theory]
    [InlineData("internalMode")]
    [InlineData("unknown")]
    [InlineData(null)]
    public async Task StateHintsRejectHiddenUnknownAndNullNames(string? name)
    {
        using var h = new Harness(); var report = await h.Report();
        foreach (var input in new[] { Harness.Inputs() with { Changed = [name!] }, Harness.Inputs() with { Defaulted = [name!] } })
        {
            Assert.Equal("values", (await Assert.ThrowsAsync<PortalException>(() => h.Parameters.ResolveAsync(report, input, true, default))).Code);
        }
        Assert.DoesNotContain(h.Mock.Observations, o => o.Operation == "LoadReport");
    }

    private static XElement NoteParameter() => new(Management + "ItemParameter", new XElement(Management + "Name", "note"),
        new XElement(Management + "ParameterTypeName", "String"), new XElement(Management + "PromptUser", "true"),
        new XElement(Management + "DefaultValues", new XElement(Management + "Value", "unrelated note")),
        new XElement(Management + "ParameterStateName", "HasValidValue"));

    private static ParameterValue[] SentExecutionValues(ParameterWire wire) => wire.Handler.Requests.Last()
        .Descendants(Execution + "ParameterValue").Select(p => new ParameterValue(p.Element(Execution + "Name")!.Value,
            p.Element(Execution + "Value")?.Value)).ToArray();

    private sealed class ParameterWire : IDisposable
    {
        public WireHandler Handler { get; } = new();
        private readonly HttpClient _http;
        private readonly SoapTransport _transport;
        public ParameterService Parameters { get; }
        public SsrsClient Soap { get; }
        public CatalogReport Report { get; }
        public ParameterWire()
        {
            var options = new PortalOptions { ReportServer = "https://synthetic.invalid/ReportServer", AllowedRoots = ["/Training"], ReportAccessPolicy = "GlobalBlocklist" };
            _http = new HttpClient(Handler);
            _transport = new SoapTransport(_http, options, new CaptureLogger<SoapTransport>());
            Soap = new SsrsClient(_transport, options);
            Parameters = new ParameterService(Soap, options);
            Report = new("opaque", "Defaults", "/Training/Defaults", PathPolicy.Identity(options));
        }
        public void Dispose() { _transport.Dispose(); _http.Dispose(); }
    }

    private sealed class WireHandler : HttpMessageHandler
    {
        public List<XDocument> Requests { get; } = [];
        public string Fixture { get; set; } = "parameter-defaults";
        public Action<XDocument, XDocument>? Transform { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var sent = XDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Requests.Add(sent);
            var setting = sent.Descendants(Execution + "SetExecutionParameters").Any();
            var xml = XDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures",
                (setting ? "set-execution" : Fixture) + ".xml"), ct));
            if (!setting) { Transform?.Invoke(sent, xml); }
            return new(HttpStatusCode.OK) { Content = new StringContent(xml.ToString(), Encoding.UTF8, "text/xml") };
        }
    }
}
