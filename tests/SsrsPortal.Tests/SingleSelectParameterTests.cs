using System.Text.Json;
using SsrsPortal.Core;
using SsrsPortal.Soap;

namespace SsrsPortal.Tests;

public sealed class SingleSelectParameterTests
{
    [Fact]
    public async Task StaticAndQueryDefaultsHaveOffPageLabelsWithoutInventingMissingDefaults()
    {
        using var h = new Harness(); var report = await h.Report("Single Choice Showcase");
        var form = (await h.Parameters.ResolveAsync(report, new(null, true), false, default)).Form;
        foreach (var (name, count, value, label) in new[]
        {
            ("branch", 152, "152", "Synthetic branch 152"),
            ("workstation", 4192, "3500", "Synthetic workstation 3500")
        })
        {
            var field = form.Fields.Single(f => f.Name == name);
            Assert.True(field.HasOptions); Assert.False(field.MultiValue);
            Assert.Equal(count, field.OptionCount); Assert.Empty(field.Options);
            Assert.DoesNotContain(field.Options, o => o.Value == value);
            Assert.Equal(new OptionValue(value, label), Assert.Single(field.SelectedOptions));
            Assert.Equal(value, Assert.Single(form.Values[name])); Assert.Contains(name, form.Defaulted);
        }
        Assert.Empty(form.Values["required"]);
        Assert.Empty(form.Fields.Single(f => f.Name == "required").SelectedOptions);
        Assert.False(form.CanRun);
    }

    [Theory]
    [InlineData("branch", "cLc TeSt", "21", 152)]
    [InlineData("workstation", "4192", "4192", 4192)]
    public async Task CompleteChoicesKeepCommittedValuesAndLabels(string name, string query, string match, int count)
    {
        using var h = new Harness(); var report = await h.Report("Single Choice Showcase");
        var form = (await h.Parameters.ResolveAsync(report, new(null), false, default)).Form;
        var input = new ParameterInput(form.Values, Defaulted: form.Defaulted);
        var original = form.Values[name].ToArray();
        var choices = await h.Parameters.ChoicesAsync(report, input, name, default);
        Assert.Equal(count, choices.Options.Length);
        Assert.Equal(match, Assert.Single(choices.Options, o => o.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).Value);
        Assert.Equal(form.Fields.Single(f => f.Name == name).OptionsKey, choices.OptionsKey);
        Assert.Equal(original, input.Values![name]);
        var again = (await h.Parameters.ResolveAsync(report, input, false, default)).Form;
        Assert.Equal(form.Fields.Single(f => f.Name == name).SelectedOptions, again.Fields.Single(f => f.Name == name).SelectedOptions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("named")]
    public async Task OneValuePerSingleParameterReachesExecutionIncludingDistinctNullAndBlank(string? nullable)
    {
        using var h = new Harness(); var report = await h.Report("Single Choice Showcase");
        var form = (await h.Parameters.ResolveAsync(report, new(null), false, default)).Form;
        form.Values["branch"] = ["21"]; form.Values["required"] = ["a"]; form.Values["nullable"] = [nullable];
        var input = new ParameterInput(form.Values, Changed: ["branch", "required", "nullable"], Defaulted: form.Defaulted);
        var changed = (await h.Parameters.ResolveAsync(report, input, true, default)).Form;
        Assert.Equal("21-team", Assert.Single(changed.Values["department"]));
        Assert.Equal("Keep this note", Assert.Single(changed.Values["note"]));
        Assert.DoesNotContain("branch", changed.Defaulted);
        await h.Execution.RunAsync("owner", report.Id, new(changed.Values, Defaulted: changed.Defaulted), default);
        var sent = h.Mock.Observations.Last(o => o.Operation == "SetExecutionParameters").Request.Descendants(SoapTransport.Execution + "ParameterValue").ToArray();
        foreach (var field in changed.Fields)
        {
            var value = Assert.Single(sent, p => p.Element(SoapTransport.Execution + "Name")!.Value == field.Name);
            Assert.Equal(Assert.Single(changed.Values[field.Name]), value.Element(SoapTransport.Execution + "Value")?.Value);
        }
        Assert.Equal(nullable == null, sent.Single(p => p.Element(SoapTransport.Execution + "Name")!.Value == "nullable").Element(SoapTransport.Execution + "Value") == null);
    }

    [Theory]
    [InlineData("branch")]
    [InlineData("workstation")]
    [InlineData("required")]
    [InlineData("department")]
    public async Task DeliberateClearRemainsEmptyAfterUnrelatedRefreshAndDisablesRun(string name)
    {
        using var h = new Harness(); var report = await h.Report("Single Choice Showcase");
        var form = (await h.Parameters.ResolveAsync(report, new(null), false, default)).Form;
        form.Values["required"] = ["a"]; form.Values[name] = [];
        var cleared = (await h.Parameters.ResolveAsync(report, new(form.Values, Changed: [name], Defaulted: form.Defaulted), false, default)).Form;
        Assert.Empty(cleared.Values[name]); Assert.False(cleared.CanRun);
        Assert.NotNull(cleared.Fields.Single(f => f.Name == name).Error);
        Assert.DoesNotContain(name, cleared.Defaulted);
        cleared.Values["note"] = ["Unrelated edit"];
        var again = (await h.Parameters.ResolveAsync(report, new(cleared.Values, Changed: ["note"], Defaulted: cleared.Defaulted), false, default)).Form;
        Assert.Empty(again.Values[name]); Assert.False(again.CanRun);
        if (name == "branch") { Assert.Empty(again.Values["department"]); }
    }

    [Fact]
    public async Task ValidManualChildSurvivesParentChangeAndStaleDefaultChildIsOmitted()
    {
        using var h = new Harness(); var report = await h.Report("Single Choice Showcase");
        var form = (await h.Parameters.ResolveAsync(report, new(null), false, default)).Form;
        form.Values["branch"] = ["21"];
        var changed = (await h.Parameters.ResolveAsync(report, new(form.Values, Changed: ["branch"], Defaulted: form.Defaulted), false, default)).Form;
        Assert.Equal("21-team", Assert.Single(changed.Values["department"]));
        Assert.Contains(h.Mock.Observations.Where(o => o.Operation == "GetItemParameters"), o =>
            o.Request.Descendants(SoapTransport.Management + "ParameterValue").Any(p => p.Element(SoapTransport.Management + "Name")!.Value == "branch" && p.Element(SoapTransport.Management + "Value")!.Value == "21") &&
            !o.Request.Descendants(SoapTransport.Management + "ParameterValue").Any(p => p.Element(SoapTransport.Management + "Name")!.Value == "department"));
        changed.Values["department"] = ["shared"];
        var manual = (await h.Parameters.ResolveAsync(report, new(changed.Values, Changed: ["department"], Defaulted: changed.Defaulted), false, default)).Form;
        manual.Values["branch"] = ["22"];
        var again = (await h.Parameters.ResolveAsync(report, new(manual.Values, Changed: ["branch"], Defaulted: manual.Defaulted), false, default)).Form;
        Assert.Equal("shared", Assert.Single(again.Values["department"]));
    }

    [Fact]
    public async Task MultipleValuesForSingleParameterRemainRejectedBeforeExecution()
    {
        using var h = new Harness(); var report = await h.Report("Single Choice Showcase");
        var form = (await h.Parameters.ResolveAsync(report, new(null), false, default)).Form;
        form.Values["required"] = ["a"]; form.Values["branch"] = ["21", "22"];
        var error = await Assert.ThrowsAsync<PortalException>(() => h.Execution.RunAsync("owner", report.Id, new(form.Values), default));
        Assert.Equal("values", error.Code);
        Assert.DoesNotContain(h.Mock.Observations, o => o.Operation is "LoadReport" or "SetExecutionParameters");
    }

    [Theory]
    [InlineData(null, "No value")]
    [InlineData("", "(Blank choice)")]
    [InlineData("null", "Literal null")]
    [InlineData("None", "Literal None value")]
    [InlineData("none-labelled", "None")]
    public async Task SearchableChoicesPreserveTypedValuesLabelsAndSoapDelivery(string? value, string label)
    {
        using var h = new Harness();
        var report = await h.Report("Single Choice Showcase");
        var initial = (await h.Parameters.ResolveAsync(report, new(null), false, default)).Form;
        initial.Values["required"] = ["a"]; initial.Values["nullable"] = [value];
        var form = (await h.Parameters.ResolveAsync(report,
            new(initial.Values, Changed: ["nullable", "required"], Defaulted: initial.Defaulted), false, default)).Form;
        Assert.Equal(new OptionValue(value, label), Assert.Single(form.Fields.Single(f => f.Name == "nullable").SelectedOptions));
        Assert.Equal(value, Assert.Single(form.Values["nullable"]));
        var input = new ParameterInput(form.Values, Defaulted: form.Defaulted);
        var choices = await h.Parameters.ChoicesAsync(report, input, "nullable", default);
        Assert.Contains(new OptionValue("", "(Blank choice)"), choices.Options);
        Assert.Contains(new OptionValue("null", "Literal null"), choices.Options);
        Assert.Contains(new OptionValue("None", "Literal None value"), choices.Options);
        Assert.Contains(new OptionValue("none-labelled", "None"), choices.Options);
        // The API keeps [] and [null] distinct independently of display text.
        Assert.NotEqual(JsonSerializer.Serialize(Array.Empty<string?>()), JsonSerializer.Serialize(new[] { value }));
        await h.Execution.RunAsync("owner", report.Id, input, default);
        var sent = h.Mock.Observations.Last(o => o.Operation == "SetExecutionParameters").Request
            .Descendants(SoapTransport.Execution + "ParameterValue")
            .Single(p => p.Element(SoapTransport.Execution + "Name")!.Value == "nullable");
        Assert.Equal(value, sent.Element(SoapTransport.Execution + "Value")?.Value);
        Assert.Equal(value == null, sent.Element(SoapTransport.Execution + "Value") == null);
    }

}
