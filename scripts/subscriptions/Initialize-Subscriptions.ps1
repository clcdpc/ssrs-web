#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SqlServer,
    [switch]$TrustServerCertificate
)
$ErrorActionPreference = 'Stop'
# Run the separate administrative setup artifact's Database copy as a migration administrator. Credentials come
# from the administrator's Windows identity, never command arguments or a file.
$migrationFiles = @('001-Subscriptions.sql', '002-Hangfire.sql', '003-SubscriptionPermissions.sql')
foreach ($migrationFile in $migrationFiles) {
    $migrationPath = Join-Path $PSScriptRoot $migrationFile
    if (!(Test-Path -LiteralPath $migrationPath -PathType Leaf)) {
        throw 'Run Database/Initialize-Subscriptions.ps1 from the separate administrative setup artifact, which includes the pinned Hangfire installation script.'
    }
}
# Filtered application indexes require QUOTED_IDENTIFIER ON in sqlcmd too.
$sqlArguments = @('-S', $SqlServer, '-d', 'SsrsWeb', '-E', '-I', '-b', '-r', '1')
if ($TrustServerCertificate) { $sqlArguments += '-C' }
foreach ($migrationFile in $migrationFiles) {
    & sqlcmd @sqlArguments -i (Join-Path $PSScriptRoot $migrationFile)
    if ($LASTEXITCODE -ne 0) { throw "Subscription migration failed at $migrationFile. Do not enable the feature." }
}
Write-Output 'Application schema 1 and pinned Hangfire schema installed. Assign runtime/admin database roles and provision external configuration before enabling.'
