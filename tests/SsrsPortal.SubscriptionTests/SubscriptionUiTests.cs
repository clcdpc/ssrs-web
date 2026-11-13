using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SsrsPortal.Subscriptions;
using SsrsPortal.Tests;

namespace SsrsPortal.SubscriptionTests;

[TestClass]
public sealed class SubscriptionUiTests
{
    [TestMethod]
    [DataRow("both", "pdf")]
    [DataRow("attachment", "xlsx")]
    [DataRow("attachment", "docx")]
    [DataRow("attachment", "csv")]
    [DataRow("link", "pdf")]
    public async Task HttpCreationThroughUnattendedDeliveryUsesSelectedModeAndFormat(string mode, string format)
    {
        using var factory = SubscriptionTests.Factory(); using var user = await factory.Login();
        using var creation = await user.Post("/api/subscriptions", new SubscriptionWrite(SubscriptionTests.Definition(mode, format)));
        Assert.AreEqual(HttpStatusCode.OK, creation.StatusCode);
        var view = (await creation.Content.ReadFromJsonAsync<SubscriptionView>())!;
        using var dispatch = await user.Post("/api/subscriptions/synthetic-dispatch", new { outcome = "Accepted" }); Assert.AreEqual(HttpStatusCode.OK, dispatch.StatusCode);
        var sent = factory.Services.GetRequiredService<SyntheticSubscriptionDelivery>().Sent.Single();
        Assert.AreEqual(mode != "attachment", sent.Report.Link != null); Assert.AreEqual(mode != "link", sent.Report.Attachment != null);
        if (sent.Report.Attachment != null) { Assert.AreEqual(format, sent.Report.Attachment.Extension); }
        using var repeated = await user.Post("/api/subscriptions/synthetic-dispatch", new { outcome = "Accepted" }); Assert.AreEqual(HttpStatusCode.OK, repeated.StatusCode);
        Assert.AreEqual(1, factory.Services.GetRequiredService<SyntheticSubscriptionDelivery>().Sent.Count);
        Assert.AreEqual("Delivered", (await user.Http.GetFromJsonAsync<SubscriptionExecution[]>($"/api/subscriptions/{view.Id}/history"))!.Single().Status);
    }
    [TestMethod]
    public async Task UncertainHttpDeliveryRemainsUnknownAndOtherOwnerCannotDispatchOrInspectIt()
    {
        using var factory = SubscriptionTests.Factory(); using var one = await factory.Login();
        using var creation = await one.Post("/api/subscriptions", new SubscriptionWrite(SubscriptionTests.Definition()));
        var view = (await creation.Content.ReadFromJsonAsync<SubscriptionView>())!;
        using var dispatched = await one.Post("/api/subscriptions/synthetic-dispatch", new { outcome = "Unknown" }); Assert.AreEqual(HttpStatusCode.OK, dispatched.StatusCode);
        using var repeated = await one.Post("/api/subscriptions/synthetic-dispatch", new { outcome = "Accepted" }); Assert.AreEqual(HttpStatusCode.OK, repeated.StatusCode);
        Assert.AreEqual("Unknown", (await one.Http.GetFromJsonAsync<SubscriptionExecution[]>($"/api/subscriptions/{view.Id}/history"))!.Single().Status);
        using var two = await factory.Login("staff-two"); using var other = await two.Post("/api/subscriptions/synthetic-dispatch", new { outcome = "Accepted" });
        var result = await other.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(); Assert.AreEqual(0, result.GetProperty("messages").GetInt32());
        Assert.AreEqual(1, factory.Services.GetRequiredService<SyntheticSubscriptionDelivery>().Sent.Count);
    }
    [TestMethod]
    public async Task DisabledFeatureHidesUiAndRejectsSyntheticControl()
    {
        using var factory = new PortalFactory(); using var user = await factory.Login();
        using var response = await user.Http.GetAsync(await user.Url("One Input")); var html = await response.Content.ReadAsStringAsync();
        Assert.IsFalse(html.Contains("id=\"subscribe\"")); Assert.IsFalse(html.Contains("subscriptions.js"));
        using var page = await user.Http.GetAsync("/subscriptions"); Assert.AreEqual(HttpStatusCode.NotFound, page.StatusCode);
        using var synthetic = await user.Post("/api/subscriptions/synthetic-dispatch", new { outcome = "Accepted" }); Assert.AreEqual(HttpStatusCode.NotFound, synthetic.StatusCode);
    }
}
