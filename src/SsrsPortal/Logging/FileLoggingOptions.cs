namespace SsrsPortal.Logging;

internal sealed class FileLoggingOptions
{
    public bool Enabled { get; set; } = true;
    public string Rotation { get; set; } = "Daily";
    public long MaxFileSizeBytes { get; set; } = 50 * 1024 * 1024;
    public int RetentionDays { get; set; } = 30;

    internal static FileLoggingOptions Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("Logging:File");
        if (!section.GetChildren().Any() && ((IConfigurationRoot)configuration).Providers.Any(provider => provider.TryGet("Logging:File", out _)))
        {
            throw new InvalidOperationException("Logging:File must be an options object; omit the section to use defaults.");
        }
        string[] keys = [nameof(Enabled), nameof(Rotation), nameof(MaxFileSizeBytes), nameof(RetentionDays)];
        if (section.Value is not null || section.GetChildren().Any(child => !keys.Contains(child.Key, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Logging:File supports only Enabled, Rotation, MaxFileSizeBytes and RetentionDays. Filesystem path overrides are not supported.");
        }
        foreach (var child in section.GetChildren())
        {
            if (child.Value is null || child.GetChildren().Any())
            {
                throw new InvalidOperationException($"Logging:File:{child.Key} must be a non-null scalar value.");
            }
        }
        var options = new FileLoggingOptions
        {
            Enabled = ReadValue(configuration, "Logging:File:Enabled", true),
            Rotation = ReadValue(configuration, "Logging:File:Rotation", "Daily"),
            MaxFileSizeBytes = ReadValue(configuration, "Logging:File:MaxFileSizeBytes", 50L * 1024 * 1024),
            RetentionDays = ReadValue(configuration, "Logging:File:RetentionDays", 30)
        };
        options.Validate();
        return options;
    }

    private static T ReadValue<T>(IConfiguration configuration, string key, T fallback)
    {
        if (configuration[key] is null) { return fallback; }
        try { return configuration.GetValue<T>(key)!; }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or ArgumentException)
        {
            throw new InvalidOperationException($"{key} has an invalid value type. Check application.Example.json and restart the application.");
        }
    }

    internal void Validate()
    {
        if (!string.Equals(Rotation, "Daily", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Logging:File:Rotation must be Daily.");
        }
        if (MaxFileSizeBytes is < 1024 or > 1024L * 1024 * 1024)
        {
            throw new InvalidOperationException("Logging:File:MaxFileSizeBytes must be between 1024 and 1073741824 bytes.");
        }
        if (RetentionDays is < 1 or > 3650)
        {
            throw new InvalidOperationException("Logging:File:RetentionDays must be between 1 and 3650 days.");
        }
    }
}
