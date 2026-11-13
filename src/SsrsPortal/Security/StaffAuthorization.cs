using System.Security.Claims;

namespace SsrsPortal.Security;

public static class StaffAuthorization
{
    public const string RequiredRole = "ReportViewer";
    public const string Authority = "https://login.microsoftonline.com/organizations/v2.0";
    public static readonly Guid ConsumerTenant = Guid.Parse("9188040d-6c67-4c5b-b112-36a304b66dad");
    public static string Issuer(string tenant) => "https://login.microsoftonline.com/" + tenant + "/v2.0";

    public static bool Approved(ClaimsPrincipal? user, EntraOptions options)
    {
        if (user?.Identity is not ClaimsIdentity { IsAuthenticated: true } identity || user.Identities.Count() != 1 ||
            !Identity(identity, out var tenant, out _) || tenant == ConsumerTenant ||
            !options.AllowedTenantIds.Any(t => Guid.TryParseExact(t, "D", out var allowed) && allowed == tenant))
        {
            return false;
        }
        // These are claims from the validated portal ID token (or protected cookie). Group claims,
        // including overage hints, have no bearing on the application's role assignment.
        return Single(identity, "iss") == Issuer(Single(identity, "tid")!) &&
            Single(identity, "aud") == options.ClientId &&
            identity.HasClaim(c => c.Type == "roles" && c.Value == RequiredRole);
    }

    private static string? Single(ClaimsIdentity identity, string type)
    {
        var claims = identity.Claims.Where(c => c.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).ToArray();
        return claims.Length == 1 && claims[0].Type == type ? claims[0].Value : null;
    }

    private static bool Identity(ClaimsIdentity identity, out Guid tenant, out Guid user)
    {
        user = default;
        return Guid.TryParseExact(Single(identity, "tid"), "D", out tenant) && tenant != Guid.Empty &&
            Guid.TryParseExact(Single(identity, "oid"), "D", out user) && user != Guid.Empty;
    }

    public static string? TryOwner(ClaimsPrincipal? user) =>
        user?.Identity is ClaimsIdentity identity && Identity(identity, out var tenant, out var subject)
            ? tenant.ToString("D") + ":" + subject.ToString("D") : null;

    public static string Owner(ClaimsPrincipal user) => TryOwner(user) ?? throw new InvalidOperationException("Validated tenant/object identity is required.");
}

public sealed class EntraOptions
{
    public string[] AllowedTenantIds { get; set; } = [];
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public void Validate(bool test)
    {
        if (AllowedTenantIds is not { Length: > 0 } || AllowedTenantIds.Any(t => !Guid.TryParseExact(t, "D", out var id) ||
                id == Guid.Empty || id == StaffAuthorization.ConsumerTenant) ||
            AllowedTenantIds.Select(Guid.Parse).Distinct().Count() != AllowedTenantIds.Length ||
            !Guid.TryParseExact(ClientId, "D", out var client) || client == Guid.Empty ||
            (!test && string.IsNullOrWhiteSpace(ClientSecret)))
        {
            throw new InvalidOperationException("Entra requires an explicit organizational tenant GUID allowlist, one application ClientId and a protected server-side ClientSecret (except synthetic Development authentication).");
        }
        AllowedTenantIds = AllowedTenantIds.Select(t => Guid.Parse(t).ToString("D")).ToArray();
        ClientId = client.ToString("D");
    }
}
