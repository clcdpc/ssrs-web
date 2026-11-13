using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SsrsPortal.Core;
using SsrsPortal.Subscriptions;

namespace SsrsPortal.SubscriptionTests;

[TestClass]
public sealed class PostmarkTests
{
    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public JsonElement Payload { get; private set; }
        public string? Token { get; private set; }
        public string? Uri { get; private set; }
        public int Calls { get; private set; }
        public bool Throw { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Uri = request.RequestUri!.AbsoluteUri; Token = request.Headers.GetValues("X-Postmark-Server-Token").Single();
            Payload = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
            if (Throw) { throw new HttpRequestException("Provider may have accepted; do not persist this message or sensitive data."); }
            return new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
    [TestMethod]
    public async Task PayloadUsesCorrectPostmarkNamesRecipientsAndAttachment()
    {
        using var handler = new Handler(HttpStatusCode.OK, "{\"ErrorCode\":0,\"MessageID\":\"00000000-0000-0000-0000-000000000001\"}");
        using var http = new HttpClient(handler);
        var options = SubscriptionTests.Options;
        var result = await new PostmarkDelivery(http, options).SendAsync(Guid.NewGuid(), SubscriptionTests.Definition(),
            new("Circulation", "https://portal.example.invalid/report/Test?A=1", new RenderOutput("%PDF-test"u8.ToArray(), "application/pdf", "pdf", "", [])), default);
        Assert.AreEqual("Accepted", result.Outcome); Assert.AreEqual("https://api.postmarkapp.com/email", handler.Uri);
        Assert.AreEqual(options.PostmarkServerToken, handler.Token);
        var payload = handler.Payload;
        Assert.AreEqual("staff@example.invalid", payload.GetProperty("To").GetString());
        Assert.AreEqual("copy@example.invalid", payload.GetProperty("Cc").GetString());
        Assert.AreEqual("blind@example.invalid", payload.GetProperty("Bcc").GetString());
        Assert.AreEqual("outbound", payload.GetProperty("MessageStream").GetString()); Assert.IsFalse(payload.GetProperty("TrackOpens").GetBoolean());
        Assert.AreEqual("None", payload.GetProperty("TrackLinks").GetString());
        var attachment = payload.GetProperty("Attachments")[0]; Assert.AreEqual("Circulation.pdf", attachment.GetProperty("Name").GetString());
        Assert.AreEqual(Convert.ToBase64String("%PDF-test"u8), attachment.GetProperty("Content").GetString());
        Assert.IsFalse(payload.GetProperty("TextBody").GetString()!.Contains("blind@example.invalid"));
    }
    [TestMethod]
    [DataRow(429, "{}", "TransientRejected")]
    [DataRow(422, "{}", "Rejected")]
    [DataRow(401, "{}", "Rejected")]
    [DataRow(413, "{}", "Rejected")]
    [DataRow(500, "{}", "Unknown")]
    [DataRow(302, "{}", "Unknown")]
    [DataRow(200, "not-json", "Unknown")]
    [DataRow(200, "null", "Unknown")]
    [DataRow(200, "[]", "Unknown")]
    [DataRow(200, "{\"ErrorCode\":\"0\"}", "Unknown")]
    [DataRow(200, "{\"ErrorCode\":0}", "Unknown")]
    [DataRow(200, "{\"ErrorCode\":0,\"MessageID\":null}", "Unknown")]
    public async Task UncertainResponsesNeverRetryInsideAdapter(int status, string body, string expected)
    {
        using var handler = new Handler((HttpStatusCode)status, body); using var http = new HttpClient(handler);
        var result = await new PostmarkDelivery(http, SubscriptionTests.Options).SendAsync(Guid.NewGuid(), SubscriptionTests.Definition("link"), new("Report", "https://portal.example.invalid/report/Test", null), default);
        Assert.AreEqual(expected, result.Outcome); Assert.AreEqual(1, handler.Calls);
    }
    [TestMethod]
    public async Task TransportFailureAndOversizedResponseAreUnknown()
    {
        using var handler = new Handler(HttpStatusCode.OK, new string('x', 17000)); using var http = new HttpClient(handler);
        var sender = new PostmarkDelivery(http, SubscriptionTests.Options);
        Assert.AreEqual("Unknown", (await sender.SendAsync(Guid.NewGuid(), SubscriptionTests.Definition(), new("Report", null, null), default)).Outcome);
        handler.Throw = true;
        Assert.AreEqual("Unknown", (await sender.SendAsync(Guid.NewGuid(), SubscriptionTests.Definition(), new("Report", null, null), default)).Outcome);
        Assert.AreEqual(2, handler.Calls);
    }
}
