using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SsrsPortal.Configuration;

namespace SsrsPortal.BrowserChecks;

internal static class DeploymentProcessProbe
{
    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 3 || !OperatingSystem.IsWindows()) { throw new InvalidOperationException("Invalid isolated Windows probe."); }
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", Args = [] });
        builder.Configuration.Sources.Clear();
        using var configuration = ExternalConfigurationLoader.Load(args[1]);
        var settings = ApplicationConfiguration.Read(configuration, false);
        PersistentKeys.Register(builder, settings);
        await using var app = builder.Build();
        PersistentKeys.VerifyUsable(app.Services, settings.Keys.KeyRingPath, builder.Environment.ContentRootPath);
        var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("restart-probe");
        if (args[0] == "protect") { File.WriteAllText(args[2], protector.Protect("synthetic-cookie")); }
        else if (args[0] == "unprotect")
        {
            if (protector.Unprotect(File.ReadAllText(args[2])) != "synthetic-cookie") { throw new InvalidOperationException("Roundtrip failed."); }
        }
        else { throw new InvalidOperationException("Unsupported probe operation."); }
    }
}
