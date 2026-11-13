using System.Net;
using Clc.Deployment.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Validators;
using SsrsPortal.Configuration;
using SsrsPortal.Core;
using SsrsPortal.Logging;
#if !CLC_RELEASE
using SsrsPortal.Mock;
#endif
using SsrsPortal.Security;
using SsrsPortal.Services;
using SsrsPortal.Soap;
using SsrsPortal.Subscriptions;

// Deployment validation must precede host/logging construction and all hosted services.
if (DeploymentContract.TryRun(args, typeof(Program).Assembly, Console.Out, out var validationExitCode))
{
    Environment.ExitCode = validationExitCode;
    return;
}
if (ApplicationPathCommandLine.TryHandle(args, out var pathCommandExitCode))
{
    Environment.ExitCode = pathCommandExitCode;
    return;
}

ApplicationLogging? fileLogging = null;
WebApplicationBuilder? startupBuilder = null;
try
{
var builder = WebApplication.CreateBuilder(args);
startupBuilder = builder;
fileLogging = new ApplicationLogging(ApplicationLogging.ResolveDirectory(builder));
ExternalConfigurationLoader.AddTo(builder);
fileLogging.Configure(builder.Configuration);
fileLogging.Register(builder);
var settings = ApplicationConfiguration.Read(builder.Configuration, builder.Environment.IsDevelopment());
var portal = settings.Portal;
var monitor = settings.Monitor;
var entra = settings.Entra;
var deploymentIdentity = ClcApplicationIdentity.ReadFromAssembly(typeof(Program).Assembly, "ssrs-web");
PersistentKeys.Register(builder, settings);
var readiness = new DeploymentReadinessGate();
builder.Services.AddSingleton(deploymentIdentity);
builder.Services.AddSingleton(portal);
builder.Services.AddSingleton(monitor);
builder.Services.AddSingleton(entra);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<IISServerOptions>(o => o.MaxRequestBodySize = 256 * 1024);
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 256 * 1024;
    o.Limits.MaxRequestLineSize = 256 * 1024;
    // HTTP/2 carries the URL in :path rather than a request line. Keep both
    // protocol envelopes finite and aligned with the existing request-body bound.
    o.Limits.MaxRequestHeadersTotalSize = 256 * 1024;
    o.Limits.Http2.MaxRequestHeaderFieldSize = 256 * 1024;
});
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/", "Staff");
    o.Conventions.AddPageRoute("/Report", "/report/{**catalogPath}");
});
builder.Services.AddControllers(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
builder.Services.Configure<ApiBehaviorOptions>(o => o.InvalidModelStateResponseFactory = c =>
    new BadRequestObjectResult(new { code = "values", message = PortalException.Description("values"), trace = c.HttpContext.TraceIdentifier }));
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "RequestVerificationToken";
    o.Cookie.Name = "__Host-PortalCsrf";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});
var auth = builder.Services.AddAuthentication(o =>
{
    o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    o.DefaultChallengeScheme = portal.TestAuth ? CookieAuthenticationDefaults.AuthenticationScheme : OpenIdConnectDefaults.AuthenticationScheme;
});
auth.AddCookie(o =>
{
    o.Cookie.Name = "__Host-Portal";
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.ExpireTimeSpan = TimeSpan.FromMinutes(30);
    o.SlidingExpiration = false;
    o.LoginPath = "/TestLogin";
    o.AccessDeniedPath = "/Denied";
    o.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            AuthenticationEvent(context.HttpContext, "Application session missing or expired", false);
            context.Response.StatusCode = 401;
        }
        else
        {
            context.Response.Redirect(context.RedirectUri);
        }
        return Task.CompletedTask;
    };
    o.Events.OnRedirectToAccessDenied = context =>
    {
        AuthenticationEvent(context.HttpContext, "Tenant or ReportViewer authorization denied", false);
        context.Response.StatusCode = 403;
        return context.Response.WriteAsync("Access requires an approved organizational tenant and the ReportViewer application role.");
    };
    o.Events.OnValidatePrincipal = async context =>
    {
        if (!StaffAuthorization.Approved(context.Principal, entra))
        {
            AuthenticationEvent(context.HttpContext, "Application session rejected by tenant/role validation", false);
            if (StaffAuthorization.TryOwner(context.Principal) is { } owner)
            {
                context.HttpContext.RequestServices.GetRequiredService<SessionStore>().CancelOwner(owner);
            }
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    };
    o.Events.OnSignedIn = context =>
    {
        AuthenticationEvent(context.HttpContext, "Application sign-in completed", true, context.Principal);
        return Task.CompletedTask;
    };
});
if (!portal.TestAuth)
{
    auth.AddOpenIdConnect(o =>
    {
        o.Authority = StaffAuthorization.Authority;
        o.RequireHttpsMetadata = true;
        o.ClientId = entra.ClientId;
        o.ClientSecret = entra.ClientSecret;
        o.ResponseType = "code";
        o.UsePkce = true;
        o.SaveTokens = false;
        o.GetClaimsFromUserInfoEndpoint = false;
        o.MapInboundClaims = false;
        // Keep these validated claims in the protected ticket for issuer/audience rechecks.
        o.ClaimActions.Remove("iss");
        o.ClaimActions.Remove("aud");
        o.Scope.Clear();
        o.Scope.Add("openid");
        o.Scope.Add("profile");
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuers = entra.AllowedTenantIds.Select(StaffAuthorization.Issuer).ToArray(),
            ValidateAudience = true, ValidAudience = entra.ClientId,
            RequireSignedTokens = true, ValidateIssuerSigningKey = true,
            ValidateLifetime = true, RequireExpirationTime = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256], NameClaimType = "name", RoleClaimType = "roles"
        };
        // Microsoft identity's supported validator binds a discovery signing key's issuer to
        // the token tenant/issuer, including tenant-specific keys in multitenant metadata.
        o.TokenValidationParameters.EnableAadSigningKeyIssuerValidation();
        o.Events.OnRedirectToIdentityProvider = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = 401;
                context.HandleResponse();
            }
            return Task.CompletedTask;
        };
        o.Events.OnTokenValidated = context =>
        {
            // The code-flow handler permits unsigned ID tokens received over its TLS backchannel.
            // This portal requires a signature actually verified by the Microsoft token handler.
            if (string.IsNullOrEmpty(context.SecurityToken.RawSignature) || !StaffAuthorization.Approved(context.Principal, entra))
            {
                AuthenticationEvent(context.HttpContext, "Entra sign-in rejected: tenant, signature or ReportViewer role", false, context.Principal);
                context.Fail("Approved tenant and ReportViewer application role cannot be established.");
            }
            return Task.CompletedTask;
        };
        o.Events.OnRemoteFailure = context =>
        {
            // Never pass the remote protocol failure/payload to the log.
            AuthenticationEvent(context.HttpContext, "Entra sign-in rejected by authentication handler", false);
            context.HandleResponse();
            context.Response.Redirect("/Denied");
            return Task.CompletedTask;
        };
    });
}
builder.Services.AddAuthorization(o => o.AddPolicy("Staff", p => p.RequireAuthenticatedUser()
    .RequireAssertion(c => StaffAuthorization.Approved(c.User, entra))));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
    o.ForwardLimit = 1;
});
if (portal.UseMock)
{
#if CLC_RELEASE
    throw new InvalidOperationException("Synthetic SOAP is not included in release builds.");
#else
    builder.Services.AddSingleton<SyntheticSsrsHandler>();
    builder.Services.AddSingleton<HttpMessageHandler>(s => s.GetRequiredService<SyntheticSsrsHandler>());
#endif
}
else
{
    builder.Services.AddSingleton<HttpMessageHandler>(_ => new HttpClientHandler
    {
        UseDefaultCredentials = true, AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None, MaxConnectionsPerServer = portal.MaxConcurrentSoap
    });
}
builder.Services.AddSingleton(s => new HttpClient(s.GetRequiredService<HttpMessageHandler>(), false) { Timeout = Timeout.InfiniteTimeSpan });
builder.Services.AddSingleton<SoapTransport>();
builder.Services.AddSingleton<SsrsClient>();
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddSingleton<SsrsBrandingService>();
builder.Services.AddHostedService(s => s.GetRequiredService<SsrsBrandingService>());
builder.Services.AddSingleton<ParameterService>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<HtmlSafety>();
builder.Services.AddSingleton<ExecutionService>();
builder.Services.AddHostedService<SessionReaper>();
builder.Services.AddSingleton<ICatalogSnapshotProvider, SqlCatalogSnapshotProvider>();
builder.Services.AddHostedService<CatalogMonitor>();
builder.AddSubscriptions(portal);
var app = builder.Build();
startupBuilder = null; // The built host now owns and disposes configuration/providers.
fileLogging.ObserveLifetime(app);
if (!app.Environment.IsDevelopment())
{
    if (!deploymentIdentity.IsRelease || !DeploymentContract.EnvironmentSupported(app.Environment.EnvironmentName))
    { throw new InvalidOperationException("Deployed startup requires a compiled release identity and Production or Staging."); }
    PersistentKeys.VerifyUsable(app.Services, settings.Keys.KeyRingPath, app.Environment.ContentRootPath);
    readiness.MarkReady();
}
var requests = new SemaphoreSlim(portal.MaxConcurrentSoap);
app.Lifetime.ApplicationStopped.Register(requests.Dispose);
// Keep the actual connection peer as well as the forwarded address. A remote
// client arriving through a local reverse proxy must never gain raw diagnostics.
app.Use(async (context, next) =>
{
    using var scope = app.Logger.BeginScope(new Dictionary<string, object?> { ["Trace"] = context.TraceIdentifier });
    context.Items[FailureDiagnostics.PeerAddressKey] = context.Connection.RemoteIpAddress;
    await next(context);
});
app.UseForwardedHeaders();
// Handle deployment readiness before redirects and authentication so the probe is anonymous,
// cookie-free, and independent of the incoming staff authentication cookie.
app.UseClcDeploymentReadiness(deploymentIdentity, app.Environment.EnvironmentName,
    () => settings.Deployment.ConfigurationRevision, readiness);
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseRouting();
app.Use(async (context, next) =>
{
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    deadline.CancelAfter(TimeSpan.FromSeconds(portal.RequestSeconds));
    var originalAbort = context.RequestAborted;
    context.RequestAborted = deadline.Token;
    var admitted = false;
    var action = context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>();
    var viewer = action?.ControllerTypeInfo.AsType() == typeof(SsrsPortal.Controllers.ReportsController) &&
        action.MethodInfo.Name == nameof(SsrsPortal.Controllers.ReportsController.Viewer);
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = viewer ? "SAMEORIGIN" : "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    context.Response.Headers["Permissions-Policy"] = "camera=(),microphone=(),geolocation=()";
    context.Response.Headers.ContentSecurityPolicy = viewer
        ? "default-src 'none'; img-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'self'; sandbox allow-same-origin"
        : "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; frame-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
    try
    {
        if (context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.Value!.EndsWith("/cancel", StringComparison.Ordinal))
        {
            admitted = await requests.WaitAsync(0, deadline.Token);
            if (!admitted)
            {
                throw new PortalException("capacity", 429);
            }
        }
        await next(context);
    }
    catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge && !context.Response.HasStarted)
    {
        FailureDiagnostics.Log(app.Logger, portal, ex, "selection-limit", context.TraceIdentifier, entra.ClientSecret, context);
        await FailureAsync(context, "selection-limit", ex.StatusCode, ex, originalAbort);
    }
    catch (SubscriptionValidationException ex) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { code = "subscription-values", message = ex.Message, trace = context.TraceIdentifier }, originalAbort);
    }
    catch (PortalException ex) when (!context.Response.HasStarted)
    {
        FailureDiagnostics.Log(app.Logger, portal, ex, ex.Code, context.TraceIdentifier, entra.ClientSecret, context);
        await FailureAsync(context, ex.Code, ex.Status, ex, originalAbort);
    }
    catch (OperationCanceledException ex)
    {
        var code = !originalAbort.IsCancellationRequested && deadline.IsCancellationRequested ? "timeout" : "cancelled";
        app.Logger.LogInformation("Portal request stopped with {Code}; trace {Trace}", code, context.TraceIdentifier);
        if (context.Response.HasStarted)
        {
            context.Abort();
        }
        else if (!originalAbort.IsCancellationRequested)
        {
            FailureDiagnostics.Log(app.Logger, portal, ex, code, context.TraceIdentifier, entra.ClientSecret, context);
            await FailureAsync(context, code, code == "timeout" ? 504 : 409, ex, originalAbort);
        }
    }
    catch (Exception ex)
    {
        FailureDiagnostics.Log(app.Logger, portal, ex, "service", context.TraceIdentifier, entra.ClientSecret, context);
        if (context.Response.HasStarted) { context.Abort(); }
        else { await FailureAsync(context, "service", 500, ex, originalAbort); }
    }
    finally
    {
        context.RequestAborted = originalAbort;
        if (admitted)
        {
            requests.Release();
        }
    }
});
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapRazorPages();
await app.RunAsync();
}
catch (Exception exception) when (exception is not HostAbortedException)
{
    if (fileLogging is not null) { fileLogging.Fatal(exception); }
    else { ApplicationLogging.Fallback(exception); }
    // The original chain is preserved in the file. Do not let the runtime print an
    // unfiltered dependency exception (for example a password) to hosting stderr.
    throw new InvalidOperationException(fileLogging?.SafeStartupMessage(exception) ?? FailureDiagnostics.Filter(exception.Message));
}
finally
{
    fileLogging?.Dispose();
    if (startupBuilder is not null) { ((IDisposable)startupBuilder.Configuration).Dispose(); }
}

static void AuthenticationEvent(HttpContext context, string outcome, bool success, System.Security.Claims.ClaimsPrincipal? principal = null)
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SsrsPortal.Authentication");
    logger.Log(success ? LogLevel.Information : LogLevel.Warning,
        "{Outcome}; identity {Identity}; trace {Trace}.", outcome, StaffAuthorization.TryOwner(principal ?? context.User), context.TraceIdentifier);
}

static async Task FailureAsync(HttpContext context, string code, int status, Exception exception, CancellationToken ct)
{
    context.Response.StatusCode = status;
    var message = PortalException.Description(code);
    var details = FailureDiagnostics.BrowserAllowed(context, context.RequestServices.GetRequiredService<IWebHostEnvironment>(),
        context.RequestServices.GetRequiredService<PortalOptions>(), context.RequestServices.GetRequiredService<EntraOptions>())
        ? FailureDiagnostics.Format(exception, context.TraceIdentifier, context.RequestServices.GetRequiredService<EntraOptions>().ClientSecret, context) : null;
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        if (details is null)
        {
            await context.Response.WriteAsJsonAsync(new { code, message, trace = context.TraceIdentifier }, ct);
        }
        else
        {
            await context.Response.WriteAsJsonAsync(new { code, message, trace = context.TraceIdentifier, diagnostics = details }, ct);
        }
        return;
    }
    var encode = System.Text.Encodings.Web.HtmlEncoder.Default;
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Report request unavailable</title><link rel=\"stylesheet\" href=\"/css/site.css\"></head><body><main><h1>Report request unavailable</h1><p>" +
        encode.Encode(message) + "</p><p>Reference: " + encode.Encode(context.TraceIdentifier) + "</p>" +
        (details is null ? "" : "<h2>Diagnostic details</h2><pre>" + encode.Encode(details) + "</pre>") +
        "<a href=\"/\">Return to report library</a></main></body></html>", ct);
}

public partial class Program
{
    internal const string ApplicationDirectoryName = "clc-ssrs-web";
}
