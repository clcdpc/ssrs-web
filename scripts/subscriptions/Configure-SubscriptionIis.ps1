#Requires -Version 7.0
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9 _.-]+$')][string]$SiteName,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9 _.-]+$')][string]$ApplicationPool
)
$ErrorActionPreference = 'Stop'
$appCmd = Join-Path $env:windir 'System32/inetsrv/appcmd.exe'
if (!(Test-Path -LiteralPath $appCmd -PathType Leaf)) { throw 'IIS appcmd.exe is required.' }
$modules = & $appCmd list modules /text:name
if ($LASTEXITCODE -ne 0 -or $modules -notcontains 'ApplicationInitializationModule') { throw 'Install the IIS Web-AppInit (Application Initialization) role service before enabling subscriptions.' }
$assignedPool = & $appCmd list app "$SiteName/" /text:applicationPool
if ($LASTEXITCODE -ne 0 -or $assignedPool -ne $ApplicationPool) { throw 'The site root must use the specified dedicated application pool.' }
$commands = @(
    @('set', 'apppool', $ApplicationPool, '/startMode:AlwaysRunning', '/processModel.idleTimeout:00:00:00', '/processModel.shutdownTimeLimit:00:01:30'),
    @('set', 'app', "$SiteName/", '/preloadEnabled:true'),
    @('set', 'config', $SiteName, '/section:system.webServer/applicationInitialization', '/doAppInitAfterRestart:true', '/commit:apphost')
)
foreach ($commandArguments in $commands) {
    & $appCmd @commandArguments
    if ($LASTEXITCODE -ne 0) { throw 'IIS subscription configuration failed. Review applicationHost.config and the published operations instructions.' }
}
Write-Output 'IIS subscription hosting configured. Verify cold start, recycle, app-offline deployment and failure diagnostics before operational acceptance.'
