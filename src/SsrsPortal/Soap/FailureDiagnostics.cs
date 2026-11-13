using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SsrsPortal.Core;
using SsrsPortal.Security;

namespace SsrsPortal.Soap;

internal sealed record SoapDiagnostic(string Operation, string Endpoint, string Trace)
{
    public int? HttpStatus { get; init; }
    public string? Reason { get; init; }
    public string? ContentType { get; init; }
    public string Failure { get; init; } = "service";
    public long Milliseconds { get; init; }
    public string? FaultCode { get; init; }
    public string? FaultMessage { get; init; }
    public string? FaultDetail { get; init; }
    public string? FaultXml { get; init; }
    public string? ResponseExcerpt { get; init; }
    public bool SuppressContent { get; init; }
}

internal static class FailureDiagnostics
{
    internal const int CaptureCharacters = 8192;
    internal const int MaximumCharacters = 32768;
    internal const string PeerAddressKey = "SsrsPortal.OriginalPeerAddress";
    private const string LoggedKey = "SsrsPortal.DetailedFailureLogged";
    private const string Truncated = "\n[truncated]";
    private const string AuthenticationKeys = "Password|Pwd|UserName|ClientSecret|client_secret|access_token|AccessToken|refresh_token|RefreshToken|id_token|IdToken|Authorization|Proxy-Authorization|Cookie|Cookies|Set-Cookie|AuthenticationCookie|AuthCookie|__Host-PortalCsrf|__Host-Portal|__RequestVerificationToken|RequestVerificationToken|AntiforgeryToken|csrf_token|CsrfToken|X-CSRF-TOKEN|csrf|RequestToken|client_assertion|ClientAssertion|code_verifier|CodeVerifier|authorization_code|AuthorizationCode";
    // Linear-time, reusable expressions keep large ordinary faults useful under load.
    private static readonly (Regex Pattern, string Replacement)[] AuthenticationFilters =
    [
        (Pattern(@"(<(?:[\w.-]+:)?(?:" + AuthenticationKeys + @")\b[^>]*>)[\s\S]*?(</(?:[\w.-]+:)?[\w.-]+\s*>|$)"), "$1[redacted]$2"),
        (Pattern(@"(&lt;(?:[\w.-]+:)?(?:" + AuthenticationKeys + @")\b[^&]*&gt;)[\s\S]*?(&lt;/(?:[\w.-]+:)?[\w.-]+\s*&gt;|$)"), "$1[redacted]$2"),
        (Pattern(@"\b(Authorization|Proxy-Authorization|Cookie|Set-Cookie)\s*[:=]\s*[^\r\n<]*"), "$1: [redacted]"),
        (Pattern("([\"']?(?:" + AuthenticationKeys + ")[\"']?\\s*[:=]\\s*)(?:\"[^\"]*(?:\"|$)|'[^']*(?:'|$)|[^\\s<>&;,]+)"), "$1[redacted]"),
        (Pattern(@"\b(Bearer|Basic|Negotiate|NTLM)\s+[^\s<>\""']+"), "$1 [redacted]"),
        (Pattern(@"(https?://)[^/\s<>]+@"), "$1[redacted]@"),
        (Pattern(@"\beyJ[\w-]+\.[\w-]+\.[\w-]*"), "[redacted token]")
    ];

    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(200));

    internal static T Attach<T>(T exception, SoapDiagnostic diagnostic) where T : Exception
    {
        exception.Data[typeof(SoapDiagnostic)] = diagnostic;
        return exception;
    }

    internal static SoapDiagnostic? Find(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current.Data[typeof(SoapDiagnostic)] is SoapDiagnostic diagnostic)
            {
                return diagnostic;
            }
        }
        return null;
    }

    internal static string Bound(string? value, int maximum = CaptureCharacters) => value is null ? "" :
        value.Length <= maximum ? value : value[..(maximum - Truncated.Length)] + Truncated;

    internal static bool BrowserAllowed(HttpContext context, IWebHostEnvironment environment, PortalOptions options, EntraOptions entra) =>
        options.Diagnostics.ShowDetailsInBrowser && environment.IsDevelopment() &&
        context.User.Identity?.IsAuthenticated == true && StaffAuthorization.Approved(context.User, entra) &&
        context.Connection.RemoteIpAddress is { } remote && IsLoopback(remote) &&
        context.Items[PeerAddressKey] is IPAddress peer && IsLoopback(peer);

    private static bool IsLoopback(IPAddress address) => IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    internal static string Format(Exception exception, string trace, string? clientSecret = null, HttpContext? request = null, int maximum = MaximumCharacters)
    {
        var text = new StringBuilder();
        text.AppendLine("Reference: " + Bound(trace, 256));
        if (Find(exception) is { } diagnostic)
        {
            text.AppendLine("Operation: " + diagnostic.Operation);
            text.AppendLine("Endpoint: " + (diagnostic.SuppressContent ? "[report execution endpoint]" : diagnostic.Endpoint));
            text.AppendLine($"HTTP Status: {diagnostic.HttpStatus?.ToString() ?? "unavailable"} {diagnostic.Reason}");
            text.AppendLine("Content-Type: " + diagnostic.ContentType);
            text.AppendLine("Failure: " + diagnostic.Failure);
            text.AppendLine($"Elapsed: {diagnostic.Milliseconds} ms");
            text.AppendLine("SOAP reference: " + diagnostic.Trace);
            if (!diagnostic.SuppressContent)
            {
                Add("Fault Code", diagnostic.FaultCode);
                Add("Fault Message", diagnostic.FaultMessage);
                Add("Fault Detail", diagnostic.FaultDetail);
                Add("Fault XML", diagnostic.FaultXml);
                Add("Response excerpt", diagnostic.ResponseExcerpt);
            }
        }
        var context = Bound(Filter(text.ToString(), clientSecret, request), maximum / 2);
        text.Clear().AppendLine(context).AppendLine("Exception:");
        if (Find(exception)?.SuppressContent == true)
        {
            text.AppendLine("Exception content omitted for report interaction privacy.");
        }
        else { AppendException(exception, text, 0, maximum, value => Filter(value, clientSecret, request)); }
        // Raw exception objects remain intact. Only the bounded presentation is filtered;
        // passing the original to ILogger would let providers print unbounded secrets.
        return Bound(Filter(text.ToString(), clientSecret, request), maximum);

        void Add(string label, string? value)
        {
            if (!string.IsNullOrEmpty(value)) { text.AppendLine(label + ": " + value); }
        }
    }

    internal static string Filter(string? text, string? clientSecret = null, HttpContext? request = null, IEnumerable<string>? secrets = null)
    {
        var presentation = text ?? "";
        // These values are used only for removal, never added to captured context.
        if (request is not null)
        {
            foreach (var value in request.Request.Cookies.Select(cookie => cookie.Value)
                .Concat(request.Request.Query.Where(pair => SecretKey(pair.Key) ||
                    (request.Request.Path == "/signin-oidc" && pair.Key is "code" or "state")).SelectMany(pair => pair.Value).OfType<string>())
                .Append(request.Request.Headers.Authorization.ToString().Split(' ').Last())
                .Append(request.Request.Headers["RequestVerificationToken"].ToString()).Where(value => !string.IsNullOrEmpty(value)))
            {
                presentation = presentation.Replace(value, "[redacted]", StringComparison.Ordinal);
            }
        }
        foreach (var secret in secrets ?? [])
        {
            if (!string.IsNullOrEmpty(secret)) { presentation = presentation.Replace(secret, "[redacted]", StringComparison.Ordinal); }
        }
        return Redact(presentation, clientSecret);
    }

    internal static string ExceptionText(Exception exception, int maximum, Func<string, string>? filter = null)
    {
        var text = new StringBuilder();
        AppendException(exception, text, 0, maximum, filter);
        return Bound(text.ToString(), maximum);
    }

    private static void AppendException(Exception exception, StringBuilder text, int depth, int maximum, Func<string, string>? filter = null)
    {
        if (depth >= 16 || text.Length >= maximum)
        {
            text.AppendLine(Truncated);
            return;
        }
        // AggregateException.Message recursively formats ALL child messages. Check
        // its graph before reading that property so presentation limits also bound work.
        var message = exception is AggregateException group && !CanExpandAggregateMessage(group, 16 - depth)
            ? $"{group.InnerExceptions.Count} inner exceptions; automatic message expansion [truncated]. See the bounded inner exception list."
            : exception.Message;
        text.AppendLine(exception.GetType().FullName + ": " + Bound(filter is null ? Filter(message) : filter(message), maximum));
        if (exception.StackTrace is { } stack) { text.AppendLine(Bound(filter is null ? Filter(stack) : filter(stack), maximum)); }
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions.Take(16))
            {
                text.AppendLine("Inner exception:");
                AppendException(inner, text, depth + 1, maximum, filter);
            }
            if (aggregate.InnerExceptions.Count > 16) { text.AppendLine(Truncated); }
        }
        else if (exception.InnerException is { } inner)
        {
            text.AppendLine("Inner exception:");
            AppendException(inner, text, depth + 1, maximum, filter);
        }
    }

    private static bool CanExpandAggregateMessage(AggregateException exception, int maximumDepth)
    {
        var pending = new Stack<(Exception Exception, int Depth)>();
        pending.Push((exception, 0));
        var remaining = 256;
        while (pending.TryPop(out var current))
        {
            if (--remaining < 0 || current.Depth >= maximumDepth) { return false; }
            if (current.Exception is not AggregateException aggregate) { continue; }
            if (aggregate.InnerExceptions.Count > 16) { return false; }
            foreach (var inner in aggregate.InnerExceptions) { pending.Push((inner, current.Depth + 1)); }
        }
        return true;
    }

    internal static void Log(ILogger logger, PortalOptions options, Exception exception, string code, string trace, string? clientSecret = null, HttpContext? request = null)
    {
        var diagnostic = Find(exception);
        bool detailed;
        lock (exception)
        {
            detailed = (options.Diagnostics.Enabled || (options.Diagnostics.DetailedFailuresAtDebug && logger.IsEnabled(LogLevel.Debug))) && exception.Data[LoggedKey] is not true;
            if (detailed) { exception.Data[LoggedKey] = true; }
        }
        if (detailed)
        {
            logger.Log(options.Diagnostics.Enabled ? LogLevel.Warning : LogLevel.Debug,
                "Portal failure {Code}; trace {Trace}; SOAP {Operation}; endpoint {Endpoint}; HTTP {HttpStatus}; failure {Failure}; elapsed {Milliseconds}ms; details {DiagnosticDetails}",
                code, trace, diagnostic?.Operation, diagnostic?.Endpoint, diagnostic?.HttpStatus,
                diagnostic?.Failure ?? exception.GetType().Name, diagnostic?.Milliseconds, Format(exception, trace, clientSecret, request, 98304));
        }
        if (!detailed || !options.Diagnostics.Enabled)
        {
            logger.LogWarning("Portal failure {Code}; trace {Trace}; SOAP {Operation}; HTTP {HttpStatus}; failure {Failure}; elapsed {Milliseconds}ms; SOAP reference {SoapTrace}",
                code, trace, diagnostic?.Operation, diagnostic?.HttpStatus, diagnostic?.Failure, diagnostic?.Milliseconds, diagnostic?.Trace);
        }
    }

    internal static bool SecretKey(string key) => new string(key.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant() is
        "password" or "pwd" or "clientsecret" or "accesstoken" or "refreshtoken" or "idtoken" or "authorization" or
        "proxyauthorization" or "cookie" or "cookies" or "setcookie" or "authenticationcookie" or "authcookie" or "hostportal" or "hostportalcsrf" or
        "antiforgerytoken" or "requestverificationtoken" or "csrf" or "csrftoken" or "xcsrftoken" or "requesttoken" or "clientassertion" or "codeverifier" or "authorizationcode" or "connectionstring";

    private static string Redact(string text, string? clientSecret)
    {
        if (!string.IsNullOrEmpty(clientSecret))
        {
            foreach (var value in new[] { clientSecret, System.Security.SecurityElement.Escape(clientSecret)!, WebUtility.HtmlEncode(clientSecret) })
            {
                text = text.Replace(value, "[redacted]", StringComparison.Ordinal);
            }
        }
        try
        {
            foreach (var filter in AuthenticationFilters) { text = filter.Pattern.Replace(text, filter.Replacement); }
            return text;
        }
        catch (RegexMatchTimeoutException)
        {
            return "[truncated] Diagnostic content omitted because authentication filtering exceeded its processing bound.";
        }
    }
}
