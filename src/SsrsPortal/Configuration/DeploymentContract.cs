using System.Reflection;
using System.Text.Json;
using Clc.Deployment.AspNetCore;

namespace SsrsPortal.Configuration;

// This adapter supplies SSRS configuration and persistent-key policy to the shared
// version-1 protocol. Argument parsing and JSON serialization live in the package.
internal static class DeploymentContract
{
    private const string ApplicationId = "ssrs-web";

    internal static JsonSerializerOptions Json => ClcDeploymentValidator.JsonOptions;
    internal static bool Revision(string value) => ClcDeploymentValidator.IsValidConfigurationRevision(value);
    internal static bool EnvironmentSupported(string value) => ClcDeploymentValidator.IsSupportedEnvironment(value);

    internal static bool TryRun(string[] args, Assembly applicationAssembly, TextWriter output, out int exitCode) =>
        ClcDeploymentValidator.TryRun(args, Options(applicationAssembly, () => ExternalConfigurationLoader.ConventionalPath), output, out exitCode);

    internal static int Run(string[] args, TextWriter output) =>
        ClcDeploymentValidator.Run(args, Options(typeof(Program).Assembly, () => ExternalConfigurationLoader.ConventionalPath), output);

    internal static DeploymentValidationResponse Validate(string[] args, Assembly applicationAssembly) =>
        ClcDeploymentValidator.Validate(args, Options(applicationAssembly, () => ExternalConfigurationLoader.ConventionalPath));

    // The expected path parameter is an isolated contract-test seam. Production always uses ConventionalPath.
    internal static DeploymentValidationResponse ValidateCore(string[] args, Assembly applicationAssembly, string expectedConfigurationPath) =>
        ClcDeploymentValidator.Validate(args, Options(applicationAssembly, () => expectedConfigurationPath));

    private static DeploymentValidationOptions Options(Assembly applicationAssembly, Func<string> expectedConfigurationPath) =>
        new(applicationAssembly, ApplicationId, PersistentKeyOptions.CompatibilityId,
            request => ValidateRequest(request, expectedConfigurationPath));

    private static string ValidateRequest(DeploymentValidationRequest request, Func<string> expectedConfigurationPath)
    {
        try
        {
            ExternalConfigurationLoader.RejectPreLoadSources(null, development: false, validation: true);
        }
        catch (InvalidOperationException)
        {
            throw new DeploymentValidationException(DeploymentValidationErrorCode.AmbiguousConfigurationSource);
        }
        catch (Exception)
        {
            throw new DeploymentValidationException(DeploymentValidationErrorCode.InvalidDeploymentInput);
        }

        string expectedPath;
        try { expectedPath = expectedConfigurationPath(); }
        catch (Exception) { throw new DeploymentValidationException(DeploymentValidationErrorCode.InvalidDeploymentInput); }

        bool pathsMatch;
        try
        {
            pathsMatch = Path.IsPathFullyQualified(request.ConfigurationPath) && Path.IsPathFullyQualified(request.DataProtectionRoot) &&
                string.Equals(Path.GetFullPath(request.ConfigurationPath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            throw new DeploymentValidationException(DeploymentValidationErrorCode.InvalidDeploymentInput);
        }

        if (!pathsMatch)
        {
            throw new DeploymentValidationException(DeploymentValidationErrorCode.ConfigurationPathMismatch);
        }

        return ValidateConfiguration(request.ConfigurationPath, request.DataProtectionRoot, AppContext.BaseDirectory);
    }

    internal static string ValidateConfiguration(string path, string root, string applicationRoot)
    {
        using var configuration = ExternalConfigurationLoader.Load(path);
        var settings = ApplicationConfiguration.Read(configuration, development: false);
        if (!string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(settings.Keys.KeyRingPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Key root mismatch.");
        }
        PersistentKeys.ValidateDirectory(root, applicationRoot);
        return settings.Deployment.ConfigurationRevision;
    }
}
