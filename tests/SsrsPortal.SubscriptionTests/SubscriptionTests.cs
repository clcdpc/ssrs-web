using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Services;
using SsrsPortal.Subscriptions;
using SsrsPortal.Tests;

[assembly: DoNotParallelize]
namespace SsrsPortal.SubscriptionTests;

[TestClass]
public sealed class SubscriptionTests
{
    internal static SubscriptionOwner Owner => new(Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000011"), "staff@example.invalid");
    internal static SubscriptionOptions Options => new() { Enabled = true, ConnectionString = "Server=localhost;Database=SsrsWeb;Integrated Security=true;Encrypt=true;TrustServerCertificate=false", PortalBaseUrl = "https://portal.example.invalid", From = "reports@example.invalid", PostmarkServerToken = "synthetic-only" };
    internal static SubscriptionDefinition Definition(string delivery = "both", string format = "pdf") => new("Morning circulation", SyntheticSsrsHandler.BranchDates,
        new(["staff@example.invalid"], ["copy@example.invalid"], ["blind@example.invalid"]), delivery, format,
        new("once", new DateTime(2026, 10, 9, 8, 0, 0)), []);
    internal static PortalFactory Factory() => new(new()
    {
        ["Subscriptions:Enabled"] = "true", ["Subscriptions:ConnectionString"] = Options.ConnectionString,
        ["Subscriptions:PortalBaseUrl"] = Options.PortalBaseUrl, ["Subscriptions:From"] = Options.From, ["Subscriptions:PostmarkServerToken"] = "synthetic-only"
    });
    internal static SubscriptionReportRunner Runner(Harness h, SubscriptionOptions? options = null) => new(h.Catalog, h.Parameters, h.Soap, options ?? Options);
    internal static async Task<Guid> Queue(ISubscriptionStore store, SubscriptionOwner owner, SubscriptionView view, DateTimeOffset now)
    {
        var row = (await store.GetAsync(view.Id, owner, default))!;
        await store.ReconcileAsync(row, row.NextDueUtc!.Value, null, now, default);
        return (await store.WorkAsync(now, default)).Single();
    }
    [TestMethod]
    public void RecipientsNormalizeAcrossFieldsWithoutLeakingBcc()
    {
        var recipients = new Recipients([" staff@example.invalid "], ["STAFF@example.invalid", "copy@example.invalid"], ["copy@example.invalid", "blind@example.invalid"]).Normalize(3);
        CollectionAssert.AreEqual(new[] { "staff@example.invalid" }, recipients.To);
        CollectionAssert.AreEqual(new[] { "copy@example.invalid" }, recipients.Cc);
        CollectionAssert.AreEqual(new[] { "blind@example.invalid" }, recipients.Bcc);
    }
    [TestMethod]
    [DataRow("Staff <staff@example.invalid>")]
    [DataRow("staff@example.invalid\r\nBcc: injected@example.invalid")]
    [DataRow("not-an-address")]
    public void InvalidRecipientRejected(string address) => Assert.Throws<SubscriptionValidationException>(() => new Recipients([address], [], []).Normalize(20));
    [TestMethod]
    public void RecipientLimitAndRequiredToEnforced()
    {
        Assert.Throws<SubscriptionValidationException>(() => new Recipients(["one@example.invalid", "two@example.invalid"], [], []).Normalize(1));
        Assert.Throws<SubscriptionValidationException>(() => new Recipients([], ["one@example.invalid"], []).Normalize(20));
    }
    [TestMethod]
    public async Task CanonicalLinksRoundTripResolvedInputsWithoutInternalParameters()
    {
        using var h = new Harness();
        var runner = Runner(h);
        var definition = Definition() with { Parameters = new() { ["branch"] = new(false, ["1", "2"]), ["blankChoices"] = new(false, [""]) } };
        var result = await runner.PrepareAsync(definition, default);
        StringAssert.StartsWith(result.Link!, "https://portal.example.invalid/report/Training/Reports/Check%20Ins%20by%20Day%20of%20Week%20by%20Hour?");
        Assert.IsFalse(result.Link!.Contains("internalMode"));
        var report = await h.Report("Check Ins by Day of Week by Hour");
        var roundTrip = await h.Parameters.ResolveLinkAsync(report, new Uri(result.Link).Query, default);
        Assert.IsTrue(roundTrip.Form.CanRun);
        CollectionAssert.AreEqual(new[] { "1", "2" }, roundTrip.Form.Values["branch"]);
        CollectionAssert.AreEqual(new[] { "" }, roundTrip.Form.Values["blankChoices"]);
        Assert.IsNotNull(result.Attachment);
    }
    [TestMethod]
    public async Task NullLinksRetainNullRatherThanBlank()
    {
        using var h = new Harness();
        var result = await Runner(h).PrepareAsync(Definition("link") with { Parameters = new() { ["blankChoices"] = new(false, [null]) } }, default);
        StringAssert.Contains(result.Link!, "blankChoices%3Aisnull=true");
        Assert.IsNull(result.Attachment);
    }
    [TestMethod]
    public async Task DynamicHiddenDefaultsDisableLinksButAllowAttachments()
    {
        using var h = new Harness();
        var definition = Definition() with { ReportPath = SyntheticSsrsHandler.Defaults, Parameters = [] };
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => Runner(h).PrepareAsync(definition, default));
        var attachment = await Runner(h).PrepareAsync(definition with { Delivery = "attachment" }, default);
        Assert.IsNotNull(attachment.Attachment); Assert.IsNull(attachment.Link);
    }
    [TestMethod]
    public async Task MissingDefaultAndHiddenOrCaseMismatchedExplicitInputReject()
    {
        using var h = new Harness();
        var definition = Definition("attachment") with { ReportPath = SyntheticSsrsHandler.Holds, Parameters = [] };
        await Assert.ThrowsAsync<PortalException>(() => Runner(h).PrepareAsync(definition, default));
        foreach (var name in new[] { "internalMode", "Library" })
        {
            definition = Definition("attachment") with { ReportPath = SyntheticSsrsHandler.Holds, Parameters = new() { [name] = new(false, ["injected"]) } };
            await Assert.ThrowsAsync<PortalException>(() => Runner(h).PrepareAsync(definition, default));
        }
    }
    [TestMethod]
    public async Task OversizedLinkAndAttachmentRejectBeforeDelivery()
    {
        using var h = new Harness();
        var options = Options; options.MaxLinkCharacters = 30;
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => Runner(h, options).PrepareAsync(Definition("link"), default));
        options = Options; options.MaxAttachmentBytes = 1;
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => Runner(h, options).PrepareAsync(Definition("attachment"), default));
    }
    [TestMethod]
    [DataRow("pdf")]
    [DataRow("xlsx")]
    [DataRow("docx")]
    [DataRow("csv")]
    public async Task AllAttachmentFormatsUseExistingSoapRenderer(string format)
    {
        using var h = new Harness();
        var result = await Runner(h).PrepareAsync(Definition("attachment", format), default);
        Assert.AreEqual(OutputFormat.All[format].Mime, result.Attachment!.MimeType);
        Assert.AreEqual(OutputFormat.All[format].Extension, result.Attachment.Extension);
        Assert.AreEqual(0, h.Sessions.Snapshot().Count);
    }
    [TestMethod]
    [DataRow("/Training/Reports/Hidden")]
    [DataRow("/Training/Internal/Blocked")]
    [DataRow("/Outside/Report")]
    public async Task CatalogPolicyPreventsSubscriptionAndUnattendedExecution(string path)
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<PortalException>(() => Runner(h).PrepareAsync(Definition() with { ReportPath = path }, default));
        Assert.AreEqual(0, h.Mock.Observations.Count(o => o.Operation == "LoadReport"));
    }
    [TestMethod]
    [DataRow("Accepted", "Delivered", 1)]
    [DataRow("Unknown", "Unknown", 1)]
    [DataRow("Rejected", "Failed", 1)]
    [DataRow("TransientRejected", "Failed", 3)]
    public async Task DeliveryOutcomesAndRetryDeduplicate(string outcome, string expected, int sends)
    {
        using var h = new Harness();
        var options = Options;
        var store = new SyntheticSubscriptionStore(options);
        var runner = Runner(h);
        var service = new SubscriptionService(store, runner, options, h.Clock, h.Catalog, h.Parameters);
        var view = await service.CreateAsync(Owner, Definition(), default);
        var id = await Queue(store, Owner, view, h.Clock.GetUtcNow());
        var delivery = new SyntheticSubscriptionDelivery { Outcome = outcome };
        var worker = new SubscriptionWorker(store, runner, delivery, options, h.Clock, h.Catalog, NullLogger<SubscriptionWorker>.Instance);
        for (var i = 0; i < 5; i++)
        {
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => worker.ExecuteAsync(id, default)));
            h.Clock.Advance(TimeSpan.FromMinutes(20));
        }
        Assert.AreEqual(expected, (await store.HistoryAsync(view.Id, Owner, default)).Single().Status);
        Assert.AreEqual(sends, delivery.Sent.Count);
    }
    [TestMethod]
    public async Task RevocationAndScheduleEditsCancelAlreadyPreparedExecution()
    {
        using var h = new Harness(); var options = Options; var store = new SyntheticSubscriptionStore(options); var runner = Runner(h);
        var service = new SubscriptionService(store, runner, options, h.Clock, h.Catalog, h.Parameters);
        var view = await service.CreateAsync(Owner, Definition(), default);
        var id = await Queue(store, Owner, view, h.Clock.GetUtcNow());
        await using (var lease = await store.AcquireAsync(id, h.Clock.GetUtcNow(), default))
        {
            await store.RevokeAsync(Owner, default);
            Assert.IsFalse(await lease!.BeginSendAsync(h.Clock.GetUtcNow(), default));
        }
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => service.CreateAsync(Owner, Definition(), default));
    }
    [TestMethod]
    public async Task RestartAfterSendBoundaryRemainsUnknownWithoutResend()
    {
        using var h = new Harness(); var options = Options; var store = new SyntheticSubscriptionStore(options); var runner = Runner(h);
        var service = new SubscriptionService(store, runner, options, h.Clock, h.Catalog, h.Parameters);
        var view = await service.CreateAsync(Owner, Definition(), default);
        var id = await Queue(store, Owner, view, h.Clock.GetUtcNow());
        await using (var lease = await store.AcquireAsync(id, h.Clock.GetUtcNow(), default)) { Assert.IsTrue(await lease!.BeginSendAsync(h.Clock.GetUtcNow(), default)); }
        Assert.IsNull(await store.AcquireAsync(id, h.Clock.GetUtcNow(), default));
        Assert.AreEqual("Unknown", (await store.HistoryAsync(view.Id, Owner, default)).Single().Status);
    }
    [TestMethod]
    public async Task OwnersCannotEnumerateReadEditPauseDeleteOrViewOtherHistory()
    {
        using var factory = Factory(); using var one = await factory.Login();
        using var created = await one.Post("/api/subscriptions", new SubscriptionWrite(Definition()));
        Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
        var view = (await created.Content.ReadFromJsonAsync<SubscriptionView>())!;
        foreach (var profile in new[] { "staff-two", "staff-other-tenant" })
        {
            using var other = await factory.Login(profile);
            Assert.AreEqual(0, (await other.Http.GetFromJsonAsync<SubscriptionView[]>("/api/subscriptions"))!.Length);
            foreach (var path in new[] { "/api/subscriptions/" + view.Id, "/api/subscriptions/" + view.Id + "/history" })
            { using var response = await other.Http.GetAsync(path); Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode); }
            using var pause = await other.Post($"/api/subscriptions/{view.Id}/state", new SubscriptionState(false, view.Revision));
            Assert.AreEqual(HttpStatusCode.NotFound, pause.StatusCode);
            using var update = await Send(other, HttpMethod.Put, $"/api/subscriptions/{view.Id}", new SubscriptionWrite(Definition(), view.Revision));
            Assert.AreEqual(HttpStatusCode.NotFound, update.StatusCode);
            using var delete = await Send(other, HttpMethod.Delete, $"/api/subscriptions/{view.Id}?revision={view.Revision}");
            Assert.AreEqual(HttpStatusCode.NotFound, delete.StatusCode);
        }
    }
    internal static async Task<HttpResponseMessage> Send(LoginClient client, HttpMethod method, string path, object? body = null)
    {
        var csrf = await client.Http.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/csrf");
        using var request = new HttpRequestMessage(method, path);
        if (body != null) { request.Content = JsonContent.Create(body); }
        request.Headers.Add("RequestVerificationToken", csrf.GetProperty("token").GetString());
        return await client.Http.SendAsync(request);
    }
    [TestMethod]
    public async Task ExpiredCreateResumeAndPreviewRejectWhileEligibleMissedOnceRemainsValid()
    {
        using var h = new Harness(); var options = Options; var store = new SyntheticSubscriptionStore(options); var runner = Runner(h);
        var service = new SubscriptionService(store, runner, options, h.Clock, h.Catalog, h.Parameters);
        var expired = new SubscriptionSchedule("once", new DateTime(2026, 10, 8, 8, 0, 0), new DateTime(2026, 10, 9, 8, 0, 0));
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => service.CreateAsync(Owner, Definition() with { Schedule = expired }, default));
        Assert.Throws<SubscriptionValidationException>(() => service.PreviewSchedule(expired));
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => service.CreateAsync(Owner, Definition() with { Schedule = expired with { Kind = "daily" } }, default));
        var eligible = await service.CreateAsync(Owner, Definition(), default); Assert.IsTrue(eligible.NextDueUtc < h.Clock.GetUtcNow());
        var valid = expired with { EndLocal = new DateTime(2026, 10, 11, 8, 0, 0) };
        var view = await service.CreateAsync(Owner, Definition() with { Schedule = valid }, default);
        view = await service.StateAsync(view.Id, Owner, new(false, view.Revision), false, default);
        h.Clock.Advance(TimeSpan.FromDays(3));
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => service.StateAsync(view.Id, Owner, new(true, view.Revision), false, default));
        Assert.IsFalse((await store.GetAsync(view.Id, Owner, default))!.Enabled);
    }
    [TestMethod]
    public async Task RenderingFailureNeverSendsAndTransientPreSendFailureCanRecover()
    {
        using var h = new Harness(); var options = Options; var store = new SyntheticSubscriptionStore(options); var runner = Runner(h);
        var service = new SubscriptionService(store, runner, options, h.Clock, h.Catalog, h.Parameters);
        var view = await service.CreateAsync(Owner, Definition(), default); var id = await Queue(store, Owner, view, h.Clock.GetUtcNow());
        var delivery = new SyntheticSubscriptionDelivery(); var worker = new SubscriptionWorker(store, runner, delivery, options, h.Clock, h.Catalog, NullLogger<SubscriptionWorker>.Instance);
        h.Mock.Failure = MockFailure.Unavailable; h.Mock.FailOperation = "Render";
        await worker.ExecuteAsync(id, default); Assert.AreEqual(0, delivery.Sent.Count);
        var failed = (await store.HistoryAsync(view.Id, Owner, default)).Single(); Assert.AreEqual("Pending", failed.Status); Assert.IsNull(failed.SendStartedUtc);
        h.Mock.Failure = MockFailure.None; h.Clock.Advance(TimeSpan.FromMinutes(5));
        await worker.ExecuteAsync(id, default); Assert.AreEqual(1, delivery.Sent.Count);
        Assert.AreEqual("Delivered", (await store.HistoryAsync(view.Id, Owner, default)).Single().Status);
    }
    [TestMethod]
    public async Task ApiRejectsRevokedOwnerWithoutChangingOtherUsers()
    {
        using var factory = Factory(); using var one = await factory.Login();
        await factory.Services.GetRequiredService<SyntheticSubscriptionStore>().RevokeAsync(Owner, default);
        using var denied = await one.Post("/api/subscriptions", new SubscriptionWrite(Definition())); Assert.AreEqual(HttpStatusCode.BadRequest, denied.StatusCode);
        using var other = await factory.Login("staff-two"); using var allowed = await other.Post("/api/subscriptions", new SubscriptionWrite(Definition())); Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
    }
    [TestMethod]
    public async Task AuthAntiforgeryAndDisabledFeatureAreEnforced()
    {
        using var factory = Factory(); using var anon = factory.Browser();
        using var denied = await anon.GetAsync("/api/subscriptions"); Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var owner = await factory.Login();
        using var missing = await owner.Http.PostAsJsonAsync("/api/subscriptions", new SubscriptionWrite(Definition())); Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
        using var disabled = new PortalFactory(); using var user = await disabled.Login();
        using var response = await user.Http.GetAsync("/api/subscriptions"); Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
    [TestMethod]
    public async Task CurrentOwnerCanEditPauseResumeDeleteAndSeeHistory()
    {
        using var factory = Factory(); using var one = await factory.Login();
        using var create = await one.Post("/api/subscriptions", new SubscriptionWrite(Definition()));
        var view = (await create.Content.ReadFromJsonAsync<SubscriptionView>())!;
        using var edit = await Send(one, HttpMethod.Put, $"/api/subscriptions/{view.Id}", new SubscriptionWrite(Definition() with { Name = "Edited" }, view.Revision));
        view = (await edit.Content.ReadFromJsonAsync<SubscriptionView>())!; Assert.AreEqual("Edited", view.Definition.Name);
        using var pause = await one.Post($"/api/subscriptions/{view.Id}/state", new SubscriptionState(false, view.Revision));
        view = (await pause.Content.ReadFromJsonAsync<SubscriptionView>())!; Assert.IsFalse(view.Enabled); Assert.IsNull(view.NextDueUtc);
        using var resume = await one.Post($"/api/subscriptions/{view.Id}/state", new SubscriptionState(true, view.Revision));
        view = (await resume.Content.ReadFromJsonAsync<SubscriptionView>())!; Assert.IsTrue(view.Enabled);
        var store = factory.Services.GetRequiredService<ISubscriptionStore>();
        var id = await Queue(store, Owner, view, factory.Clock.GetUtcNow());
        await factory.Services.GetRequiredService<SubscriptionWorker>().ExecuteAsync(id, default);
        var history = await one.Http.GetFromJsonAsync<SubscriptionExecution[]>($"/api/subscriptions/{view.Id}/history"); Assert.AreEqual("Delivered", history!.Single().Status);
        using var delete = await Send(one, HttpMethod.Delete, $"/api/subscriptions/{view.Id}?revision={view.Revision}"); Assert.AreEqual(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.AreEqual(0, (await one.Http.GetFromJsonAsync<SubscriptionView[]>("/api/subscriptions"))!.Length);
    }
}
