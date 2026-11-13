using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Services;
using SsrsPortal.Tests;

namespace SsrsPortal.BrowserChecks;

internal static class BreadcrumbChecks
{
    public static async Task RunAsync(string root)
    {
        await using var factory = new PortalFactory(new() { ["Portal:AllowedRoots:1"] = "/Other" }, configureServices: services =>
            services.AddSingleton(new SyntheticSsrsHandler { CatalogItems = [new("/Training/Reports", "Folder"), new("/Training/Reports/One Input"),
                new("/Other", "Folder"), new("/Other/Other report", "LinkedReport")] }));
        await using var host = factory.WithWebHostBuilder(builder => builder.UseContentRoot(Path.Combine(root, "src", "SsrsPortal")));
        host.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps()));
        var server = host.Services.GetRequiredService<IServer>();
        var service = host.Services.GetRequiredService<CatalogService>();
        var catalog = await service.BrowseAsync(default);
        var report = catalog.Single(e => e.Name == "One Input");
        var folder = catalog.Single(e => e.Name == "Training");
        var direct = new CatalogEntry(report.Id, null, report.Name, "Report");
        var link = new CatalogEntry("synthetic-root-link", null, "Root linked report", "LinkedReport");
        var fixtures = new List<object>();
        foreach (var (name, entries) in new (string, CatalogEntry[])[]
        {
            ("one-folder-and-report", [folder, direct]), ("one-folder-and-link", [folder, direct with { Type = "LinkedReport" }]),
            ("reports-only", [direct, link]), ("empty", []), ("single-folder", [folder])
        })
        {
            foreach (var search in new[] { false, true })
            {
                var navigation = search ? BreadcrumbNavigation.ForCatalog(entries, folder.Id, true)
                    : name == "empty" ? BreadcrumbNavigation.ForCatalog(entries, null, false)
                    : BreadcrumbNavigation.ForReport(entries, new(report.Id, report.Name, "/synthetic", "fixture", name == "single-folder" ? folder.Id : null));
                fixtures.Add(new { name = name + (search ? "-search" : "-report"), html = await BreadcrumbRendering.RenderAsync(host.Services, navigation),
                    labels = navigation.Items.Select(e => e.Name).ToArray() });
            }
        }
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
        var path = Path.Combine(root, "artifacts", "browser", "breadcrumb-root-fixtures.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { baseURL = address, reportId = report.Id, fixtures }));
        var start = new ProcessStartInfo("node") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "tests", "browser", "browser-breadcrumb-roots.cjs")); start.ArgumentList.Add(path);
        var edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PORTAL_BROWSER_EXECUTABLE")) && File.Exists(edge))
        { start.Environment["PORTAL_BROWSER_EXECUTABLE"] = edge; }
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(deadline.Token)) is not null) { Console.WriteLine(line); }
            await process.WaitForExitAsync(deadline.Token);
            if (process.ExitCode != 0) { throw new InvalidOperationException(await stderr); }
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
}
