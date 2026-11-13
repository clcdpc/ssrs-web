using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SsrsPortal.Controllers;

namespace SsrsPortal.Subscriptions;

public sealed record DeliveryResult(string Outcome, string? MessageId = null);
public interface ISubscriptionDelivery
{
    Task<DeliveryResult> SendAsync(Guid executionId, SubscriptionDefinition definition, PreparedSubscriptionReport report, CancellationToken ct);
}
public sealed class PostmarkDelivery(HttpClient http, SubscriptionOptions options) : ISubscriptionDelivery
{
    public async Task<DeliveryResult> SendAsync(Guid executionId, SubscriptionDefinition definition, PreparedSubscriptionReport report, CancellationToken ct)
    {
        var text = $"{report.Name}\nSubscription: {definition.Name}\n" +
            (report.Link == null ? "" : $"\nOpen report: {report.Link}\nSign in with an authorized portal account. This link runs fresh data with this execution's inputs.\n") +
            (report.Attachment == null ? "" : "\nThe attached report can be opened without portal authentication.\n");
        var payload = new
        {
            options.From, To = string.Join(',', definition.Recipients.To), Cc = string.Join(',', definition.Recipients.Cc), Bcc = string.Join(',', definition.Recipients.Bcc),
            Subject = $"{report.Name} — {definition.Name}", TextBody = text, options.MessageStream,
            TrackOpens = false, TrackLinks = "None", Tag = "ssrs-web-subscription",
            Metadata = new { executionId = executionId.ToString("D") },
            Attachments = report.Attachment == null ? [] : new[] { new
            {
                Name = ReportsController.SafeFilename(report.Name) + "." + report.Attachment.Extension,
                Content = Convert.ToBase64String(report.Attachment.Bytes), ContentType = report.Attachment.MimeType
            } }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.postmarkapp.com/email");
        request.Headers.Add("X-Postmark-Server-Token", options.PostmarkServerToken);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = JsonContent.Create(payload, options: new JsonSerializerOptions { PropertyNamingPolicy = null });
        // Never retry this HTTP request. After it leaves the process, a timeout,
        // malformed reply, redirect or server error may hide provider acceptance.
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.DeliverySeconds));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) { return new("TransientRejected"); }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.UnprocessableEntity or HttpStatusCode.RequestEntityTooLarge)
            { return new("Rejected"); }
            if (!response.IsSuccessStatusCode) { return new("Unknown"); }
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = new byte[16385];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), deadline.Token);
                if (read == 0) { break; }
                length += read;
            }
            if (length == bytes.Length) { return new("Unknown"); }
            using var result = JsonDocument.Parse(bytes.AsMemory(0, length));
            if (result.RootElement.ValueKind == JsonValueKind.Object && result.RootElement.TryGetProperty("ErrorCode", out var error) && error.ValueKind == JsonValueKind.Number && error.TryGetInt32(out var code) && code == 0 &&
                result.RootElement.TryGetProperty("MessageID", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var messageId))
            { return new("Accepted", messageId.ToString("D")); }
            return new("Unknown");
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or IOException)
        { return new("Unknown"); }
    }
}
