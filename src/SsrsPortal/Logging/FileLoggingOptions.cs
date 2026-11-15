using System.Globalization;

namespace SsrsPortal.Logging;

// Read-only deployment validation. Clc.Logging alone binds/applies operational options.
internal static class FileLoggingOptions
{
    internal static void Validate(IConfiguration configuration)
    {
        var section = configuration.GetSection("Logging:File");
        if (!section.GetChildren().Any() && ((IConfigurationRoot)configuration).Providers.Any(provider => provider.TryGet("Logging:File", out _)))
        {
            throw new InvalidOperationException("Logging:File must be a nonempty options object; omit the section to use defaults.");
        }
        if (section.Value is not null) { throw new InvalidOperationException("Logging:File must be an options object."); }
        foreach (var child in section.GetChildren())
        {
            // Errors contain only known setting names, never submitted unknown keys or values.
            if (child.Key.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
            {
                if (child.GetChildren().Any() || !bool.TryParse(child.Value, out _))
                { throw new InvalidOperationException("Logging:File:Enabled must be true or false."); }
                continue;
            }
            var (name, minimum, maximum) = child.Key.ToUpperInvariant() switch
            {
                "MAXFILESIZEBYTES" => ("MaxFileSizeBytes", 1024L, 1073741824L),
                "RETENTIONDAYS" => ("RetentionDays", 1L, 3650L),
                "MAXARCHIVEFILES" => ("MaxArchiveFiles", 1L, 10000L),
                _ => throw new InvalidOperationException("Logging:File supports only Enabled, MaxFileSizeBytes, RetentionDays and MaxArchiveFiles. Remove legacy Rotation and unsupported settings; filesystem path overrides are not supported.")
            };
            if (child.GetChildren().Any() || !long.TryParse(child.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum)
            { throw new InvalidOperationException($"Logging:File:{name} must be an integer between {minimum} and {maximum}."); }
        }
    }
}
