param([string]$BrowserExecutable = $env:PORTAL_BROWSER_EXECUTABLE, [ValidateRange(0, 65535)][int]$Port = 0)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'Test-Portal.ps1 requires PowerShell 7 or later. Run: pwsh -NoProfile -File ./scripts/Test-Portal.ps1'
}
$taskWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($BrowserExecutable) -and !$PSBoundParameters.ContainsKey('BrowserExecutable') -and $IsWindows) {
    $edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
    if (Test-Path -LiteralPath $edge -PathType Leaf) { $BrowserExecutable = $edge }
}
if (![string]::IsNullOrWhiteSpace($BrowserExecutable)) {
    if (!(Test-Path -LiteralPath $BrowserExecutable -PathType Leaf)) { throw "Missing browser executable: $BrowserExecutable" }
    $BrowserExecutable = (Resolve-Path -LiteralPath $BrowserExecutable).ProviderPath
}
$testConfigDirectory = Join-Path ([IO.Path]::GetTempPath()) ('SsrsPortal.Browser/' + [Guid]::NewGuid().ToString('N'))
$savedEnvironment = @{}
foreach ($variable in @('ASPNETCORE_ENVIRONMENT', 'DOTNET_ENVIRONMENT', 'PORTAL_BROWSER_EXECUTABLE', 'PORTAL_BASE_URL')) {
    $savedEnvironment[$variable] = [Environment]::GetEnvironmentVariable($variable, 'Process')
}
$runDirectory = $null; $stdoutPath = $null; $stderrPath = $null
$ready = $false
Push-Location $taskWorkspace
try {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:DOTNET_ENVIRONMENT = 'Development'
    # An absent override lets Playwright select its managed Chromium on Linux.
    $browserEnvironment = if ([string]::IsNullOrWhiteSpace($BrowserExecutable)) { [NullString]::Value } else { $BrowserExecutable }
    [Environment]::SetEnvironmentVariable('PORTAL_BROWSER_EXECUTABLE', $browserEnvironment, 'Process')
    if ($Port -eq 0) {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        try { $Port = $listener.LocalEndpoint.Port } finally { $listener.Stop() }
    }
    $env:PORTAL_BASE_URL = 'https://localhost:' + $Port
    $runDirectory = Join-Path $taskWorkspace ('artifacts/browser-runner/' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
    node tests/browser/browser-preflight.cjs 2>&1 | Tee-Object -FilePath (Join-Path $runDirectory 'browser-preflight.log')
    if ($LASTEXITCODE -ne 0) { throw "Browser infrastructure preflight failed; see $runDirectory/browser-preflight.log." }
    New-Item -ItemType Directory -Path $testConfigDirectory -Force | Out-Null
    $testConfigFile = Join-Path $testConfigDirectory 'application.json'
    @{
        AllowedHosts = 'localhost'
        Portal = @{
            UseMock = $true; TestAuth = $true; ReportServer = 'https://synthetic.invalid/ReportServer'
            ReportAccessPolicy = 'GlobalBlocklist'; AllowedRoots = @('/Training')
            ExcludedPrefixes = @('/Training/Custom_Restored', '/Training/Internal')
        }
        Entra = @{
            ClientId = '00000000-0000-0000-0000-000000000003'
            AllowedTenantIds = @('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000004')
        }
        CatalogMonitor = @{ Enabled = $false }
        Subscriptions = @{
            Enabled = ($env:PORTAL_BROWSER_SUITE -eq 'subscriptions')
            ConnectionString = 'Server=localhost;Database=SsrsWeb;Integrated Security=true;Encrypt=true'
            PortalBaseUrl = $env:PORTAL_BASE_URL
            From = 'reports@example.invalid'; PostmarkServerToken = 'synthetic-only'
        }
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $testConfigFile -Encoding utf8
    dotnet dev-certs https
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic HTTPS setup failed: dotnet dev-certs https could not generate a development certificate.' }
    $taskAppDirectory = Join-Path $taskWorkspace 'src/SsrsPortal'
    $stdoutPath = Join-Path $runDirectory 'server.stdout.log'
    $stderrPath = Join-Path $runDirectory 'server.stderr.log'
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
    $startInfo.WorkingDirectory = $taskAppDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @('bin/Release/net10.0/SsrsPortal.dll', '--urls', $env:PORTAL_BASE_URL, '--SsrsPortal:TestConfigurationFile', $testConfigFile)) {
        $startInfo.ArgumentList.Add($argument)
    }
    $server = [Diagnostics.Process]::new()
    $server.StartInfo = $startInfo
    $serverStarted = $false
    $stdoutLog = $null; $stderrLog = $null
    $stdoutCopy = $null; $stderrCopy = $null
    try {
        $stdoutLog = [IO.File]::Open($stdoutPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
        $stderrLog = [IO.File]::Open($stderrPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
        $serverStarted = $server.Start()
        if (!$serverStarted) { throw 'Synthetic application process could not be started.' }
        # Drain both pipes concurrently so verbose startup failures cannot block.
        $stdoutCopy = $server.StandardOutput.BaseStream.CopyToAsync($stdoutLog)
        $stderrCopy = $server.StandardError.BaseStream.CopyToAsync($stderrLog)
        Write-Host "Synthetic HTTPS portal: $($env:PORTAL_BASE_URL); process $($server.Id); logs: $runDirectory"
        for ($attempt = 0; $attempt -lt 100; $attempt++) {
            if ($server.HasExited) { throw "Synthetic application startup failed (exit $($server.ExitCode))." }
            try { $response = Invoke-WebRequest ($env:PORTAL_BASE_URL + '/TestLogin') -SkipCertificateCheck -TimeoutSec 1; $ready = $response.StatusCode -eq 200 } catch { }
            if ($ready) { break }
            Start-Sleep -Milliseconds 200
        }
        if (!$ready) { throw 'Synthetic portal did not become ready before the readiness timeout.' }
        npm run test:browser 2>&1 | Tee-Object -FilePath (Join-Path $runDirectory 'browser.log')
        if ($LASTEXITCODE -ne 0) { throw "Browser assertions/runtime checks failed; see $runDirectory/browser.log." }
    } finally {
        try {
            if ($serverStarted -and !$server.HasExited) {
                try { $server.Kill($true) } catch { if (!$server.HasExited) { throw } }
                if (!$server.WaitForExit(10000)) { throw 'Synthetic application process did not terminate within 10 seconds.' }
            }
        } finally {
            try {
                foreach ($copy in @($stdoutCopy, $stderrCopy)) {
                    if ($copy) {
                        if (!$copy.Wait(10000)) { throw 'Timed out draining synthetic application logs during cleanup.' }
                        $null = $copy.GetAwaiter().GetResult()
                    }
                }
            } finally {
                if ($stdoutLog) { $stdoutLog.Dispose() }
                if ($stderrLog) { $stderrLog.Dispose() }
                $server.Dispose()
            }
        }
    }
} catch {
    $failure = $_
    if ($runDirectory) {
        $startupLogs = @($stdoutPath, $stderrPath) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
        $startupText = ($startupLogs | ForEach-Object { Get-Content -LiteralPath $_ -Raw }) -join "`n"
        if (!$ready -and $startupText -match 'address already in use|Failed to bind|EADDRINUSE') {
            Write-Host 'Infrastructure failure: synthetic HTTPS port binding failed.'
        } elseif (!$ready -and $startupText -match 'Unable to configure HTTPS|No server certificate was specified|developer certificate could not be found') {
            Write-Host 'Infrastructure failure: synthetic HTTPS certificate setup failed.'
        }
        foreach ($log in $startupLogs) {
            Write-Host "Last startup output from ${log}:"
            Get-Content -LiteralPath $log -Tail 20 | Write-Host
        }
        Write-Host "Synthetic diagnostics retained in $runDirectory"
    }
    throw $failure
} finally {
    try {
        if (Test-Path -LiteralPath $testConfigDirectory) {
            $resolvedTestDirectory = [IO.Path]::GetFullPath($testConfigDirectory)
            $expectedTestRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'SsrsPortal.Browser')) + [IO.Path]::DirectorySeparatorChar
            if (!$resolvedTestDirectory.StartsWith($expectedTestRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing unexpected test configuration cleanup path.' }
            Remove-Item -LiteralPath $resolvedTestDirectory -Recurse -Force
        }
    } finally {
        foreach ($variable in $savedEnvironment.Keys) {
            # PowerShell coerces $null to an empty string when binding a .NET
            # string parameter. Preserve absence as well as existing emptiness.
            $value = if ($null -eq $savedEnvironment[$variable]) { [NullString]::Value } else { $savedEnvironment[$variable] }
            [Environment]::SetEnvironmentVariable($variable, $value, 'Process')
        }
        Pop-Location
    }
}
