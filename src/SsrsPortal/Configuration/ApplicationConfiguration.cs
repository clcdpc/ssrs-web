using SsrsPortal.Core;
using SsrsPortal.Logging;
using SsrsPortal.Security;
using SsrsPortal.Subscriptions;

namespace SsrsPortal.Configuration;

// Pure binding/validation: no service registration, database, HTTP, logging or key writes.
internal sealed record ApplicationConfiguration(PortalOptions Portal, CatalogMonitorOptions Monitor, EntraOptions Entra,
    SubscriptionOptions Subscriptions, DeploymentOptions Deployment, PersistentKeyOptions Keys)
{
    internal static ApplicationConfiguration Read(IConfiguration configuration, bool development)
    {
        var portal = ExternalConfigurationLoader.Bind<PortalOptions>(configuration, "Portal");
        portal.Validate(development);
        var monitor = ExternalConfigurationLoader.Bind<CatalogMonitorOptions>(configuration, "CatalogMonitor", false);
        monitor.Validate();
        var entra = ExternalConfigurationLoader.Bind<EntraOptions>(configuration, "Entra");
        entra.Validate(portal.TestAuth);
        var subscriptions = ExternalConfigurationLoader.Bind<SubscriptionOptions>(configuration, "Subscriptions", false);
        subscriptions.Validate();
        _ = FileLoggingOptions.Read(configuration);
        var deployment = ExternalConfigurationLoader.Bind<DeploymentOptions>(configuration, "Deployment", !development);
        var keys = ExternalConfigurationLoader.Bind<PersistentKeyOptions>(configuration, "DataProtection", !development);
        if (!development || configuration.GetSection("Deployment").Exists()) { deployment.Validate(); }
        if (!development || configuration.GetSection("DataProtection").Exists()) { keys.Validate(); }
        return new(portal, monitor, entra, subscriptions, deployment, keys);
    }
}

internal sealed class DeploymentOptions
{
    public string ConfigurationRevision { get; set; } = "";
    internal void Validate()
    {
        if (!DeploymentContract.Revision(ConfigurationRevision)) { throw new InvalidOperationException("Deployment:ConfigurationRevision requires a nonsecret revision identifier; change it for every protected configuration edit."); }
    }
}

internal sealed class PersistentKeyOptions
{
    internal const string CompatibilityId = "ssrs-web-dpapi-user-v1";
    internal const string ApplicationName = "clc-ssrs-web";
    public string KeyRingPath { get; set; } = "";
    public string KeyStoreCompatibilityId { get; set; } = "";
    internal void Validate()
    {
        if (KeyStoreCompatibilityId != CompatibilityId || !Path.IsPathFullyQualified(KeyRingPath) ||
            (OperatingSystem.IsWindows() && !System.Text.RegularExpressions.Regex.IsMatch(KeyRingPath, "\\A[A-Za-z]:\\\\")))
        { throw new InvalidOperationException("DataProtection requires an absolute external KeyRingPath and the supported KeyStoreCompatibilityId."); }
    }
}
