using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SsrsPortal.Security;
using SsrsPortal.Services;

namespace SsrsPortal.Tests;

public sealed class AuthenticationTests
{
    internal const string Tenant = "00000000-0000-0000-0000-000000000001";
    internal const string SecondTenant = "00000000-0000-0000-0000-000000000004";
    internal const string Client = "00000000-0000-0000-0000-000000000003";
    internal const string Subject = "00000000-0000-0000-0000-000000000011";

    internal static Dictionary<string, string?> RealAuthSettings() => new()
    {
        ["Portal:UseMock"] = "false", ["Portal:TestAuth"] = "false",
        ["Portal:ReportServer"] = "https://synthetic.invalid/ReportServer",
        ["Portal:AllowedRoots:0"] = "/Training", ["Portal:ReportAccessPolicy"] = "GlobalBlocklist",
        ["Entra:AllowedTenantIds:0"] = Tenant, ["Entra:AllowedTenantIds:1"] = SecondTenant,
        ["Entra:ClientId"] = Client, ["Entra:ClientSecret"] = "synthetic-test-credential"
    };

    internal static ClaimsPrincipal Principal(string tenant = Tenant, params Claim[] additional) => new(new ClaimsIdentity(
        new Claim[] { new("tid", tenant), new("oid", Subject), new("iss", StaffAuthorization.Issuer(tenant)), new("aud", Client), new("roles", "ReportViewer") }
            .Concat(additional), CookieAuthenticationDefaults.AuthenticationScheme, "name", "roles"));

    [Theory]
    [InlineData("valid", true)]
    [InlineData("second-tenant", true)]
    [InlineData("unknown-tenant", false)]
    [InlineData("no-role", false)]
    [InlineData("group-only", false)]
    [InlineData("wrong-role-case", false)]
    [InlineData("overage-no-role", false)]
    [InlineData("overage-role", true)]
    [InlineData("missing-tid", false)]
    [InlineData("malformed-tid", false)]
    [InlineData("empty-tid", false)]
    [InlineData("missing-oid", false)]
    [InlineData("malformed-oid", false)]
    [InlineData("empty-oid", false)]
    [InlineData("missing-sub", false)]
    [InlineData("issuer-tenant-mismatch", false)]
    [InlineData("untrusted-issuer", false)]
    [InlineData("consumer", false)]
    [InlineData("wrong-audience", false)]
    [InlineData("wrong-signature", false)]
    [InlineData("wrong-key-issuer", false)]
    [InlineData("wrong-algorithm", false)]
    [InlineData("unsigned", false)]
    [InlineData("expired", false)]
    [InlineData("wrong-nonce", false)]
    public async Task RealOidcCodeCallbackEnforcesTokenTenantRoleAndSigningAuthority(string scenario, bool accepted)
    {
        using var identityProvider = new SyntheticOidc(scenario);
        await using var f = new PortalFactory(RealAuthSettings(), configureServices: identityProvider.Configure);
        using var browser = f.Browser();
        var options = f.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        Assert.Equal(StaffAuthorization.Authority, options.Authority);
        Assert.True(options.RequireHttpsMetadata); Assert.True(options.UsePkce);
        Assert.Equal("code", options.ResponseType); Assert.False(options.SaveTokens); Assert.False(options.MapInboundClaims);
        Assert.True(options.ProtocolValidator.RequireNonce);
        Assert.True(options.TokenValidationParameters.ValidateIssuer);
        Assert.True(options.TokenValidationParameters.ValidateIssuerSigningKey);
        Assert.True(options.TokenValidationParameters.ValidateAudience);

        var challenge = await browser.GetAsync("/signin");
        Assert.Equal(HttpStatusCode.Found, challenge.StatusCode);
        var location = challenge.Headers.Location!;
        Assert.Equal("login.microsoftonline.com", location.Host);
        Assert.StartsWith("/organizations/", location.AbsolutePath);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        identityProvider.Nonce = query["nonce"].ToString();
        var callback = await browser.PostAsync("/signin-oidc", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = "synthetic-authorization-code", ["state"] = query["state"].ToString()
        }));
        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Equal(accepted ? "/" : "/Denied", callback.Headers.Location!.OriginalString);
        Assert.Equal(1, identityProvider.TokenRequests);
        Assert.True(identityProvider.SawPkceVerifier);
        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/csrf")).StatusCode);
        Assert.Equal(accepted, callback.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("__Host-Portal=", StringComparison.Ordinal)));
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/TestLogin")).StatusCode);
    }

    [Theory]
    [InlineData("missing-state")]
    [InlineData("tampered-state")]
    [InlineData("missing-correlation")]
    public async Task OidcStateAndCorrelationCannotBeSkipped(string scenario)
    {
        using var provider = new SyntheticOidc("valid");
        await using var f = new PortalFactory(RealAuthSettings(), configureServices: provider.Configure);
        using var original = f.Browser();
        var challenge = await original.GetAsync("/signin");
        var query = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
        provider.Nonce = query["nonce"].ToString();
        using var browser = scenario == "missing-correlation" ? f.Browser() : original;
        var callback = await browser.PostAsync("/signin-oidc", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = "synthetic-authorization-code",
            ["state"] = scenario switch { "missing-state" => "", "tampered-state" => "invalid", _ => query["state"].ToString() }
        }));
        Assert.Equal("/Denied", callback.Headers.Location!.OriginalString);
        Assert.Equal(0, provider.TokenRequests);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/csrf")).StatusCode);
    }

    [Theory]
    [InlineData("no-role")]
    [InlineData("group-only")]
    [InlineData("unknown-tenant")]
    [InlineData("duplicate-tid")]
    [InlineData("duplicate-oid")]
    [InlineData("malformed-oid")]
    [InlineData("issuer-mismatch")]
    [InlineData("audience-mismatch")]
    public async Task ProtectedCookiesAreReauthorizedOnEveryRequest(string scenario)
    {
        await using var f = new PortalFactory(); using var browser = f.Browser();
        var principal = Principal();
        var identity = (ClaimsIdentity)principal.Identity!;
        if (scenario is "no-role" or "group-only")
        {
            identity.RemoveClaim(identity.FindFirst("roles")!);
            identity.AddClaim(new Claim("groups", "old-group"));
        }
        else if (scenario is "duplicate-tid" or "duplicate-oid")
        {
            identity.AddClaim(new Claim(scenario == "duplicate-tid" ? "tid" : "oid", Subject));
        }
        else
        {
            var type = scenario switch { "unknown-tenant" => "tid", "malformed-oid" => "oid", "issuer-mismatch" => "iss", _ => "aud" };
            identity.RemoveClaim(identity.FindFirst(type)!);
            identity.AddClaim(new Claim(type, scenario == "malformed-oid" ? "malformed" : "00000000-0000-0000-0000-000000000099"));
        }
        var cookie = f.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var protectedTicket = cookie.TicketDataFormat.Protect(new AuthenticationTicket(principal, new AuthenticationProperties(), CookieAuthenticationDefaults.AuthenticationScheme));
        browser.DefaultRequestHeaders.Add("Cookie", "__Host-Portal=" + protectedTicket);
        var denied = await browser.GetAsync("/api/catalog");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Contains(denied.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-Portal=;", StringComparison.Ordinal));
        Assert.Empty(f.Mock.Observations);
    }

    [Fact]
    public async Task RemovingAnAllowedTenantRejectsExistingCookieAndCancelsItsExecutions()
    {
        await using var f = new PortalFactory(); using var user = await f.Login();
        await user.Run();
        Assert.Equal(1, f.Services.GetRequiredService<SessionStore>().Snapshot().Count);
        f.Services.GetRequiredService<EntraOptions>().AllowedTenantIds = [SecondTenant];
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Http.GetAsync("/api/catalog")).StatusCode);
        Assert.Equal(0, f.Services.GetRequiredService<SessionStore>().Snapshot().Count);
        // The synthetic fixture does not impersonate whichever tenant is currently allowed.
        using var rejected = await f.Login();
        Assert.Equal(HttpStatusCode.Unauthorized, (await rejected.Http.GetAsync("/api/catalog")).StatusCode);
    }

    [Theory]
    [InlineData("staff-other-tenant")]
    [InlineData("role-overage")]
    public async Task RoleAuthorizedProfilesShareTheSameCatalog(string profile)
    {
        await using var f = new PortalFactory(); using var first = await f.Login(); using var second = await f.Login(profile);
        var a = await first.Http.GetFromJsonAsync<SsrsPortal.Core.CatalogEntry[]>("/api/catalog");
        var b = await second.Http.GetFromJsonAsync<SsrsPortal.Core.CatalogEntry[]>("/api/catalog");
        Assert.Equal(a, b);
        if (profile == "staff-other-tenant")
        {
            Assert.NotEqual(StaffAuthorization.Owner(Principal()), StaffAuthorization.Owner(Principal(SecondTenant)));
            var execution = await first.Run();
            foreach (var route in new[] { "info", "viewer", "streams/x", "exports/pdf" })
            {
                Assert.Equal(HttpStatusCode.NotFound, (await second.Http.GetAsync("/api/executions/" + execution.Token + "/" + route)).StatusCode);
            }
            Assert.Equal(HttpStatusCode.NotFound, (await second.Post("/api/executions/" + execution.Token + "/page/2", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await second.Post("/api/executions/" + execution.Token + "/cancel", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await first.Http.GetAsync("/api/executions/" + execution.Token + "/viewer")).StatusCode);
        }
    }

    [Theory]
    [InlineData("Portal:ApprovedReports", "[]")]
    [InlineData("Portal:ApprovedReports", "[\"/Training/A\"]")]
    [InlineData("Entra:TenantId", "\"00000000-0000-0000-0000-000000000001\"")]
    [InlineData("Entra:GroupId", "\"00000000-0000-0000-0000-000000000002\"")]
    public void LegacyKeysIncludingEmptyArraysCannotSilentlyActivateTheNewModel(string key, string value)
    {
        var parts = key.Split(':');
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"" + parts[0] + "\":{\"" + parts[1] + "\":" + value + "}}"));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        Assert.Contains(key, Assert.Throws<InvalidOperationException>(() => SsrsPortal.Core.ConfigurationContract.RejectLegacy(configuration)).Message);
    }

    [Theory]
    [InlineData("Portal:ApprovedReports:0", "/Training/A")]
    [InlineData("Portal:ReportAccessPolicy", "")]
    [InlineData("Entra:AllowedTenantIds:0", "malformed")]
    [InlineData("Entra:AllowedTenantIds:0", "00000000-0000-0000-0000-000000000000")]
    [InlineData("Entra:AllowedTenantIds:0", "9188040d-6c67-4c5b-b112-36a304b66dad")]
    public async Task InvalidOrLegacyStartupConfigurationIsRejected(string key, string value)
    {
        await using var f = new PortalFactory(new() { [key] = value });
        Assert.Throws<InvalidOperationException>(() => f.Browser());
    }

    private sealed class SyntheticOidc(string scenario) : HttpMessageHandler
    {
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly RSA _other = RSA.Create(2048);
        public string Nonce { get; set; } = "";
        public int TokenRequests { get; private set; }
        public bool SawPkceVerifier { get; private set; }

        public void Configure(IServiceCollection services)
        {
            services.PostConfigure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, o => o.TimeProvider = TimeProvider.System);
            services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
            {
                // Correlation/nonce cookies must be current to HttpClient's real-time cookie jar;
                // the catalog fixture clock intentionally stays fixed for cache/monitor tests.
                o.TimeProvider = TimeProvider.System;
                var key = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_rsa.ExportParameters(false)) { KeyId = "synthetic-key" });
                key.AdditionalData["issuer"] = scenario == "wrong-key-issuer" ? StaffAuthorization.Issuer(StaffAuthorization.ConsumerTenant.ToString()) : "https://login.microsoftonline.com/{tenantid}/v2.0";
                var configuration = new OpenIdConnectConfiguration
                {
                    Issuer = "https://login.microsoftonline.com/{tenantid}/v2.0",
                    AuthorizationEndpoint = "https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize",
                    TokenEndpoint = "https://synthetic.invalid/token", JsonWebKeySet = new JsonWebKeySet()
                };
                configuration.JsonWebKeySet.Keys.Add(key); configuration.SigningKeys.Add(key);
                // Use the actual BaseConfigurationManager path so signing-key issuer validation
                // receives discovery JWK metadata exactly as it does in the deployed OIDC handler.
                o.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    StaffAuthorization.Authority + "/.well-known/openid-configuration", new FixtureConfiguration(configuration), new NoNetworkDocuments());
                o.Backchannel = new HttpClient(this, false);
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://synthetic.invalid/token", request.RequestUri!.AbsoluteUri);
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("authorization_code", form["grant_type"].ToString());
            Assert.Equal("synthetic-authorization-code", form["code"].ToString());
            SawPkceVerifier = form["code_verifier"].ToString().Length >= 43;
            TokenRequests++;
            var tenant = scenario switch
            {
                "second-tenant" => SecondTenant, "unknown-tenant" => "00000000-0000-0000-0000-000000000099",
                "consumer" => StaffAuthorization.ConsumerTenant.ToString(), "malformed-tid" => "malformed",
                "empty-tid" => Guid.Empty.ToString(), _ => Tenant
            };
            var claims = new Dictionary<string, object>
            {
                ["tid"] = tenant, ["oid"] = Subject, ["sub"] = "synthetic-subject", ["name"] = "Synthetic staff",
                ["nonce"] = scenario == "wrong-nonce" ? "invalid" : Nonce, ["roles"] = new[] { "ReportViewer" }, ["ver"] = "2.0"
            };
            if (scenario is "no-role" or "group-only" or "overage-no-role") { claims.Remove("roles"); }
            if (scenario == "wrong-role-case") { claims["roles"] = new[] { "reportviewer" }; }
            if (scenario == "group-only") { claims["groups"] = new[] { "old-group" }; }
            if (scenario is "overage-role" or "overage-no-role")
            {
                claims["_claim_names"] = new Dictionary<string, string> { ["groups"] = "src1" }; claims["hasgroups"] = true;
            }
            if (scenario == "missing-tid") { claims.Remove("tid"); }
            if (scenario == "missing-oid") { claims.Remove("oid"); }
            if (scenario == "malformed-oid") { claims["oid"] = "malformed"; }
            if (scenario == "empty-oid") { claims["oid"] = Guid.Empty.ToString(); }
            if (scenario == "missing-sub") { claims.Remove("sub"); }
            var issuer = scenario switch
            {
                "issuer-tenant-mismatch" => StaffAuthorization.Issuer(SecondTenant),
                "untrusted-issuer" => "https://untrusted.invalid/" + tenant + "/v2.0", _ => StaffAuthorization.Issuer(tenant)
            };
            var now = DateTime.UtcNow;
            var signed = new SecurityTokenDescriptor
            {
                Claims = claims, Issuer = issuer, Audience = scenario == "wrong-audience" ? SecondTenant : Client,
                NotBefore = now.AddMinutes(-20), IssuedAt = now.AddMinutes(-20), Expires = scenario == "expired" ? now.AddMinutes(-10) : now.AddMinutes(30),
                SigningCredentials = scenario == "unsigned" ? null : new SigningCredentials(
                    new RsaSecurityKey(scenario == "wrong-signature" ? _other : _rsa) { KeyId = "synthetic-key" },
                    scenario == "wrong-algorithm" ? SecurityAlgorithms.RsaSha512 : SecurityAlgorithms.RsaSha256)
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id_token = new JsonWebTokenHandler().CreateToken(signed), access_token = "synthetic-access-token", token_type = "Bearer", expires_in = 1800 })
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _rsa.Dispose(); _other.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private sealed class FixtureConfiguration(OpenIdConnectConfiguration configuration) : IConfigurationRetriever<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel) => Task.FromResult(configuration);
    }
    private sealed class NoNetworkDocuments : IDocumentRetriever
    {
        public Task<string> GetDocumentAsync(string address, CancellationToken cancel) => throw new InvalidOperationException("Authentication tests must not use network discovery.");
    }
}
