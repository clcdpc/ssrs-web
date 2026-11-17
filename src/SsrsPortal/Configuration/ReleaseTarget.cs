using System.Reflection;
using System.Text.RegularExpressions;

namespace SsrsPortal.Configuration;

// Consumer-owned release binding. Hosting environment and configuration never select a target.
internal static class ReleaseTarget
{
    internal static string ApplicationDirectoryName => DirectoryName(typeof(Program).Assembly);

    internal static string DirectoryName(Assembly assembly)
    {
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => attribute.Key.StartsWith("Clc.", StringComparison.Ordinal))
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);
        if (metadata.GetValueOrDefault("Clc.IdentityKind") == "Synthetic") { return "clc-ssrs-web"; }
        if (metadata.Count != 6 || metadata.GetValueOrDefault("Clc.IdentityKind") != "Release" ||
            metadata.GetValueOrDefault("Clc.ApplicationId") != "ssrs-web")
        { throw new InvalidOperationException("Invalid compiled release target binding."); }
        var tag = metadata.GetValueOrDefault("Clc.ReleaseTag") ?? "";
        var match = Regex.Match(tag, @"\Av(0|[1-9][0-9]{0,3})\.(0|[1-9][0-9]{0,3})\.(0|[1-9][0-9]{0,3})(?:-(dev|beta|rc)\.([1-9][0-9]{0,3}))?\z", RegexOptions.CultureInvariant);
        if (!match.Success || (match.Groups[5].Success && int.Parse(match.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture) > 9998))
        { throw new InvalidOperationException("Invalid compiled release tag."); }
        var expected = match.Groups[4].Value switch { "dev" => "ssrs-web-dev", "" => "ssrs-web-production", _ => "" };
        if (metadata.GetValueOrDefault("Clc.TargetId") != expected)
        { throw new InvalidOperationException("Compiled release tag and target disagree."); }
        if (expected.Length == 0) { throw new InvalidOperationException("Artifact-only releases cannot run on a deployment target."); }
        return "clc-" + expected;
    }

    internal static void ValidateKeyRoot(string root)
    {
        if (typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(attribute => attribute.Key == "Clc.IdentityKind" && attribute.Value == "Release") &&
            !string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
                Path.Combine(ProgramDataPath.Paths.ApplicationDataDirectory, "Keys"), StringComparison.OrdinalIgnoreCase))
        { throw new InvalidOperationException("Released Data Protection keys require the compiled target's Keys directory."); }
    }
}
