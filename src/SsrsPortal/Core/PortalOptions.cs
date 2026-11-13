namespace SsrsPortal.Core;

public sealed class PortalOptions
{
    public bool UseMock { get; set; }
    public bool TestAuth { get; set; }
    public DiagnosticsOptions Diagnostics { get; set; } = new();
    public string ReportServer { get; set; } = "";
    public string ReportAccessPolicy { get; set; } = "";
    public string[] AllowedRoots { get; set; } = [];
    public string[] ExcludedPrefixes { get; set; } = [];
    public int RequestSeconds { get; set; } = 30;
    public int MaxSoapBytes { get; set; } = 24 * 1024 * 1024;
    public int MaxOutputBytes { get; set; } = 16 * 1024 * 1024;
    public int MaxMetadataItems { get; set; } = 6000;
    public int MaxOptions { get; set; } = 5000;
    public int MaxParameters { get; set; } = 100;
    public int MaxStreams { get; set; } = 100;
    public int MaxToggleActions { get; set; } = 1000;
    public int MaxSessions { get; set; } = 100;
    public int MaxSessionsPerUser { get; set; } = 3;
    public int SessionMinutes { get; set; } = 15;
    public int CatalogSeconds { get; set; } = 600;
    public long MaxSessionBytes { get; set; } = 32 * 1024 * 1024;
    public long MaxAggregateSessionBytes { get; set; } = 128 * 1024 * 1024;
    public int MaxConcurrentSoap { get; set; } = 8;

    public Uri Validate(bool development)
    {
        if (Diagnostics is null || (Diagnostics.ShowDetailsInBrowser && !development))
        {
            throw new InvalidOperationException("Portal:Diagnostics:ShowDetailsInBrowser requires Development and is forbidden in Production or Staging.");
        }
        if ((UseMock || TestAuth) && !development)
        {
            throw new InvalidOperationException("Synthetic SOAP and test authentication require Development.");
        }
        if (TestAuth && !UseMock)
        {
            throw new InvalidOperationException("Test authentication requires synthetic SOAP.");
        }
        var uri = ReportServerUri(ReportServer);
        if (RequestSeconds is < 1 or > 120 || MaxSoapBytes is < 1024 or > 64 * 1024 * 1024 ||
            MaxOutputBytes is < 128 or > 32 * 1024 * 1024 || MaxMetadataItems is < 1 or > 10000 ||
            MaxOptions is < 1 or > 10000 || MaxParameters is < 1 or > 200 || MaxStreams is < 1 or > 200 || MaxToggleActions is < 1 or > 2000 ||
            MaxSessions is < 1 or > 500 || MaxSessionsPerUser is < 1 or > 10 || SessionMinutes is < 1 or > 60 ||
            CatalogSeconds is < 1 or > 3600 || MaxSessionBytes < 4096 || MaxSessionBytes > 64 * 1024 * 1024 ||
            MaxAggregateSessionBytes < MaxSessionBytes || MaxAggregateSessionBytes > 512 * 1024 * 1024 ||
            MaxConcurrentSoap is < 1 or > 32)
        {
            throw new InvalidOperationException("Portal bounds are outside permitted ranges.");
        }
        if (ReportAccessPolicy != "GlobalBlocklist" || AllowedRoots is not { Length: > 0 } || ExcludedPrefixes is null ||
            AllowedRoots.Concat(ExcludedPrefixes).Any(p => !PathPolicy.Canonical(p)))
        {
            throw new InvalidOperationException("Portal:ReportAccessPolicy=GlobalBlocklist and canonical catalog roots/exclusions are required. An empty blocklist is permitted.");
        }
        return uri;
    }

    internal static Uri ReportServerUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath.TrimEnd('/') != "/ReportServer")
        {
            throw new InvalidOperationException("Portal:ReportServer must be an HTTPS origin with /ReportServer and no credentials/query/fragment.");
        }
        return uri;
    }
}

public sealed class DiagnosticsOptions
{
    public bool Enabled { get; set; }
    public bool DetailedFailuresAtDebug { get; set; } = true;
    public bool ShowDetailsInBrowser { get; set; }
    internal bool CaptureFailures => Enabled || DetailedFailuresAtDebug || ShowDetailsInBrowser;
}

public static class PathPolicy
{
    // Configuration roots retain their established conservative syntax. Actual catalog
    // names can include URL punctuation; the public route encodes each segment.
    public static bool Canonical(string? path) => path == "/" || CatalogPath(path) && !path!.Contains('%') && !path.Contains('?') && !path.Contains('#');

    public static bool CatalogPath(string? path) => path != null && path.Length is > 1 and <= 512 && path[0] == '/' &&
        !path.EndsWith('/') && !path.Contains('\\') && !path.Any(char.IsControl) &&
        path.Split('/').Skip(1).All(p => p.Length > 0 && p is not "." and not "..");

    public static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root == "/" ? "/" : root + "/", StringComparison.OrdinalIgnoreCase);

    public static bool Permitted(string path, PortalOptions options) => (path == "/" || CatalogPath(path)) &&
        options.AllowedRoots.Any(r => Within(path, r)) &&
        !options.ExcludedPrefixes.Any(r => Within(path, r));

    public static string Identity(PortalOptions options) =>
        string.Join('\n', options.AllowedRoots.Select(p => p.ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal)) +
        "\n|\n" + string.Join('\n', options.ExcludedPrefixes.Select(p => p.ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal));
}

public static class ConfigurationContract
{
    public static void RejectLegacy(IConfiguration configuration)
    {
        foreach (var key in new[] { "Portal:ApprovedReports", "Entra:TenantId", "Entra:GroupId" })
        {
            // Inspect providers as well as merged values, including obsolete empty JSON arrays.
            if (((IConfigurationRoot)configuration).Providers.Any(p => p.TryGet(key, out _) || p.GetChildKeys([], key).Any()))
            {
                throw new InvalidOperationException($"Obsolete configuration {key} must be removed. Configure allowed tenants, ReportViewer and the explicit GlobalBlocklist policy.");
            }
        }
    }
}
