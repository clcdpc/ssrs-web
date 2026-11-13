using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SsrsPortal.Core;
using SsrsPortal.Security;

namespace SsrsPortal.Subscriptions;

public sealed class SubscriptionOptions
{
    public bool Enabled { get; set; }
    public string ConnectionString { get; set; } = "";
    public string PortalBaseUrl { get; set; } = "";
    public string PostmarkServerToken { get; set; } = "";
    public string From { get; set; } = "";
    public string MessageStream { get; set; } = "outbound";
    public int Workers { get; set; } = 2;
    public int ExecutionSeconds { get; set; } = 600;
    public int DeliverySeconds { get; set; } = 60;
    public int MaxRecipients { get; set; } = 20;
    public int MaxSubscriptionsPerOwner { get; set; } = 100;
    public int MaxAttachmentBytes { get; set; } = 6 * 1024 * 1024;
    public int MaxLinkCharacters { get; set; } = 16000;
    public int MaxAttempts { get; set; } = 3;

    public void Validate()
    {
        if (!Enabled) { return; }
        try
        {
            var sql = new SqlConnectionStringBuilder(ConnectionString);
            if (string.IsNullOrWhiteSpace(sql.DataSource) || sql.InitialCatalog != "SsrsWeb" ||
                !sql.IntegratedSecurity || sql.Authentication != SqlAuthenticationMethod.NotSpecified ||
                sql.UserID.Length != 0 || sql.Password.Length != 0 ||
                (sql.Encrypt != SqlConnectionEncryptOption.Mandatory && sql.Encrypt != SqlConnectionEncryptOption.Strict) ||
                sql.AttachDBFilename.Length != 0 || sql.UserInstance || sql.PersistSecurityInfo ||
                !Uri.TryCreate(PortalBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                !Recipients.Valid(From) || string.IsNullOrWhiteSpace(PostmarkServerToken) || PostmarkServerToken.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(MessageStream) || MessageStream.Length > 100 || MessageStream.Any(char.IsControl) ||
                Workers is < 1 or > 8 || ExecutionSeconds is < 30 or > 3600 || DeliverySeconds is < 5 or > 120 ||
                MaxRecipients is < 1 or > 50 || MaxSubscriptionsPerOwner is < 1 or > 1000 ||
                MaxAttachmentBytes is < 1024 or > 6 * 1024 * 1024 || MaxLinkCharacters is < 1024 or > 224 * 1024 || MaxAttempts is < 1 or > 5)
            { throw new ArgumentException(); }
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            throw new InvalidOperationException("Subscriptions configuration is invalid. SQL requires database SsrsWeb, Windows integrated authentication and Encrypt=Mandatory (true) or Strict, without SQL credentials, local attach/user instances or persisted security information. TrustServerCertificate may be true or false. Use the protected external application.json example and an HTTPS portal origin.");
        }
    }
}

public sealed record SubscriptionOwner(Guid TenantId, Guid UserObjectId, string? UserEmail)
{
    public string LockName => $"ssrs-owner:{TenantId:D}:{UserObjectId:D}";
    public static SubscriptionOwner FromPrincipal(ClaimsPrincipal principal)
    {
        var identity = StaffAuthorization.Owner(principal).Split(':');
        // Informational only. The identity has already passed the Staff policy.
        var emails = principal.FindAll("email").Select(c => c.Value).ToArray();
        var email = emails.Length == 1 ? emails[0] : null;
        if (email == null) { email = principal.FindAll("preferred_username").Select(c => c.Value).FirstOrDefault(); }
        return new(Guid.Parse(identity[0]), Guid.Parse(identity[1]), Recipients.Valid(email) ? email : null);
    }
}

public sealed record Recipients(string[] To, string[] Cc, string[] Bcc)
{
    internal static bool Valid(string? value) => value is { Length: > 0 and <= 254 } &&
        !value.Any(char.IsControl) && MailAddress.TryCreate(value, out var address) && address.Address == value && address.DisplayName.Length == 0;
    public Recipients Normalize(int limit)
    {
        if (To == null || Cc == null || Bcc == null || To.Length + Cc.Length + Bcc.Length > 150) { throw new SubscriptionValidationException("Enter valid recipient addresses."); }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] Clean(string[] addresses) => addresses.Select(a => a?.Trim() ?? "").Select(a =>
        {
            if (!Valid(a)) { throw new SubscriptionValidationException("Use plain email addresses without display names."); }
            return new MailAddress(a).Address;
        }).Where(seen.Add).ToArray();
        var result = new Recipients(Clean(To), Clean(Cc), Clean(Bcc));
        if (result.To.Length == 0 || seen.Count > limit) { throw new SubscriptionValidationException($"Enter at least one To recipient and at most {limit} unique recipients."); }
        return result;
    }
}

public sealed record SubscriptionParameter(bool UseDefault, string?[] Values);
public sealed record SubscriptionDefinition(string Name, string ReportPath, Recipients Recipients, string Delivery,
    string Format, SubscriptionSchedule Schedule, Dictionary<string, SubscriptionParameter> Parameters);
public sealed record SubscriptionWrite(SubscriptionDefinition Definition, int? Revision = null);
public sealed record SubscriptionState(bool Enabled, int Revision);
public sealed record SubscriptionView(Guid Id, int Revision, bool Enabled, SubscriptionDefinition Definition,
    DateTimeOffset? NextDueUtc, string? LastStatus, DateTimeOffset? LastOccurrenceUtc);
public sealed class SubscriptionRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserObjectId { get; set; }
    public string? UserEmail { get; set; }
    public int Revision { get; set; }
    public bool Enabled { get; set; }
    public bool Deleted { get; set; }
    public string DefinitionJson { get; set; } = "";
    public DateTimeOffset? NextDueUtc { get; set; }
    public string? LastStatus { get; set; }
    public DateTimeOffset? LastOccurrenceUtc { get; set; }
    public SubscriptionDefinition Definition => SubscriptionJson.Read<SubscriptionDefinition>(DefinitionJson);
    public SubscriptionOwner Owner => new(TenantId, UserObjectId, UserEmail);
    public SubscriptionView View => new(Id, Revision, Enabled, Definition, NextDueUtc, LastStatus, LastOccurrenceUtc);
}
public sealed class SubscriptionExecution
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset OccurrenceUtc { get; set; }
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public DateTimeOffset AvailableUtc { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public DateTimeOffset? SendStartedUtc { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? Diagnostic { get; set; }
}
public sealed class SubscriptionValidationException(string message) : Exception(message);
internal static class SubscriptionJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    internal static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    internal static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidOperationException("Invalid subscription state.");
}
