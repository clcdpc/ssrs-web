using System.Text;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SsrsPortal.Mock;
using SsrsPortal.Subscriptions;
using SsrsPortal.Tests;

namespace SsrsPortal.SubscriptionTests;

[TestClass]
public sealed class ParameterResolutionTests
{
    private sealed class DefaultsHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public string Date { get; set; } = "11/1/2026 12:00:00 AM";
        public string Internal { get; set; } = "first-evaluation";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var ns = SyntheticSsrsHandler.Management;
            foreach (var parameter in xml.Descendants(ns + "ItemParameter"))
            {
                var name = parameter.Element(ns + "Name")?.Value;
                if (name is not ("beginDate" or "internalMode")) { continue; }
                parameter.Element(ns + "DefaultValues")!.ReplaceNodes(new XElement(ns + "Value", name == "beginDate" ? Date : Internal));
            }
            response.Content = new StringContent(xml.ToString(), Encoding.UTF8, "text/xml"); return response;
        }
    }
    [TestMethod]
    public async Task DefaultsReevaluateAndExplicitDateStaysFixed()
    {
        DefaultsHandler? handler = null;
        using var h = new Harness(wrapHandler: inner => handler = new(inner));
        var runner = SubscriptionTests.Runner(h);
        var definition = SubscriptionTests.Definition("link") with { Parameters = new() { ["beginDate"] = new(true, []) } };
        var first = await runner.PrepareAsync(definition, default);
        handler!.Date = "11/2/2026 12:00:00 AM";
        var second = await runner.PrepareAsync(definition, default);
        StringAssert.Contains(first.Link!, "beginDate=2026-11-01"); StringAssert.Contains(second.Link!, "beginDate=2026-11-02");
        var explicitDate = definition with { Parameters = new() { ["beginDate"] = new(false, ["2026-10-15"]) } };
        var third = await runner.PrepareAsync(explicitDate, default);
        handler.Date = "11/3/2026 12:00:00 AM";
        var fourth = await runner.PrepareAsync(explicitDate, default);
        StringAssert.Contains(third.Link!, "beginDate=2026-10-15"); Assert.AreEqual(third.Link, fourth.Link);
    }
    [TestMethod]
    public async Task NonQueryDependencyFreeHiddenExpressionStillDisablesLinks()
    {
        DefaultsHandler? handler = null;
        using var h = new Harness(wrapHandler: inner => handler = new(inner));
        var runner = SubscriptionTests.Runner(h);
        var definition = SubscriptionTests.Definition() with { ReportPath = SyntheticSsrsHandler.Holds,
            Parameters = Harness.Inputs().Values!.ToDictionary(p => p.Key, p => new SubscriptionParameter(false, p.Value)) };
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => runner.PrepareAsync(definition, default));
        await runner.PrepareAsync(definition with { Delivery = "attachment" }, default);
        handler!.Internal = "second-evaluation";
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => runner.PrepareAsync(definition, default));
        await runner.PrepareAsync(definition with { Delivery = "attachment" }, default);
        var sets = h.Mock.Observations.Where(o => o.Operation == "SetExecutionParameters").Select(o => o.Request.ToString()).ToArray();
        Assert.IsTrue(sets[0].Contains("first-evaluation")); Assert.IsTrue(sets[1].Contains("second-evaluation"));
    }
    [TestMethod]
    public async Task CascadingDefaultFollowsExplicitParentAcrossExecutions()
    {
        using var h = new Harness(); var runner = SubscriptionTests.Runner(h);
        var definition = SubscriptionTests.Definition("link") with { Parameters = new() { ["branch"] = new(false, ["2"]), ["department"] = new(true, []) } };
        StringAssert.Contains((await runner.PrepareAsync(definition, default)).Link!, "department=2-team");
        definition = definition with { Parameters = new() { ["branch"] = new(false, ["3"]), ["department"] = new(true, []) } };
        StringAssert.Contains((await runner.PrepareAsync(definition, default)).Link!, "department=3-team");
    }
    [TestMethod]
    public async Task PreviewPreservesStaleExplicitCascadeAndBlocksSaveUntilCorrected()
    {
        using var h = new Harness(); var options = SubscriptionTests.Options; var store = new SyntheticSubscriptionStore(options);
        var service = new SubscriptionService(store, SubscriptionTests.Runner(h), options, h.Clock, h.Catalog, h.Parameters);
        var definition = SubscriptionTests.Definition() with { Parameters = new() { ["branch"] = new(false, ["3"]), ["department"] = new(false, ["2-team"]) } };
        var form = await service.PreviewParametersAsync(definition, default);
        Assert.IsFalse(form.CanRun); CollectionAssert.AreEqual(new[] { "2-team" }, form.Values["department"]);
        Assert.IsNotNull(form.Fields.Single(field => field.Name == "department").Error);
        await Assert.ThrowsAsync<SsrsPortal.Core.PortalException>(() => service.CreateAsync(SubscriptionTests.Owner, definition, default));
        definition = definition with { Parameters = new() { ["branch"] = new(false, ["3"]), ["department"] = new(true, []) } };
        form = await service.PreviewParametersAsync(definition, default); Assert.IsTrue(form.CanRun);
        CollectionAssert.AreEqual(new[] { "3-team" }, form.Values["department"]);
        definition = definition with { Parameters = new() { ["branch"] = new(false, ["3"]), ["department"] = new(false, ["3-team"]) } };
        Assert.IsTrue((await service.PreviewParametersAsync(definition, default)).CanRun);
    }
    [TestMethod]
    public async Task SavedReportPathUsesCanonicalCatalogSpelling()
    {
        using var h = new Harness(); var options = SubscriptionTests.Options; var store = new SyntheticSubscriptionStore(options);
        var service = new SubscriptionService(store, SubscriptionTests.Runner(h), options, h.Clock, h.Catalog, h.Parameters);
        var definition = SubscriptionTests.Definition() with { ReportPath = SyntheticSsrsHandler.BranchDates.ToLowerInvariant() };
        var saved = await service.CreateAsync(SubscriptionTests.Owner, definition, default);
        Assert.AreEqual(SyntheticSsrsHandler.BranchDates, saved.Definition.ReportPath);
    }
    [TestMethod]
    public async Task UnrelatedEditWithInactiveScheduleDefaultsPreservesOverdueOccurrence()
    {
        using var h = new Harness(); var options = SubscriptionTests.Options; var store = new SyntheticSubscriptionStore(options);
        var service = new SubscriptionService(store, SubscriptionTests.Runner(h), options, h.Clock, h.Catalog, h.Parameters);
        var definition = SubscriptionTests.Definition() with { Schedule = new("daily", new DateTime(2020, 1, 1, 8, 0, 0)) };
        var saved = await service.CreateAsync(SubscriptionTests.Owner, definition, default);
        h.Clock.Advance(TimeSpan.FromDays(3)); Assert.IsTrue(saved.NextDueUtc < h.Clock.GetUtcNow());
        var changed = saved.Definition with { Name = "Renamed", Schedule = saved.Definition.Schedule with { Weekdays = [1], MonthDays = [1], Week = 2, Weekday = 2, Cron = "0 8 * * 1-5" } };
        var updated = await service.UpdateAsync(saved.Id, SubscriptionTests.Owner, new(changed, saved.Revision), default);
        Assert.AreEqual(saved.NextDueUtc, updated.NextDueUtc);
    }
}
