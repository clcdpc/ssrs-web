#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string] $PublishDirectory,[Parameter(Mandatory)][string] $AdministrationArchive)
. "$PSScriptRoot/Release.Common.ps1"
foreach ($file in Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File) {
    if ($file.Extension -imatch '^\.(ps1|sql)$' -or $file.FullName -imatch '[/\\](Database|Operations)[/\\]') { Stop-Release 'Release.AdministrationInApplication' }
}
$subscriptionRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../subscriptions'))
$sources=@{}
foreach ($name in @('001-Subscriptions.sql','002-Hangfire.sql','003-SubscriptionPermissions.sql','Initialize-Subscriptions.ps1')) { $sources['Database/'+$name]=Join-Path $subscriptionRoot $name }
foreach ($name in @('LICENSE.md','COPYING','COPYING.LESSER','README.md')) { $sources['Database/Licenses/Hangfire/'+$name]=Join-Path $subscriptionRoot ('vendor/hangfire/'+$name) }
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
foreach ($name in @('LICENSE','COPYRIGHT.md')) { $sources['Database/Licenses/Project/'+$name]=Join-Path $repositoryRoot $name }
$sources['Operations/Configure-SubscriptionIis.ps1']=Join-Path $subscriptionRoot 'Configure-SubscriptionIis.ps1'
$zip=[IO.Compression.ZipFile]::OpenRead((Assert-ReleasePath ([IO.Path]::GetFullPath($AdministrationArchive))))
try {
    $manifest=Read-ReleaseJson (Read-ReleaseEntry ($zip.GetEntry('setup-manifest.json')) 4194304)
    if ($manifest.recordKind -cne 'ssrs-web-administrative-setup' -or $manifest.schemaVersion -ne 1 -or $manifest.automaticExecution -isnot [bool] -or $manifest.automaticExecution -or
        $manifest.deploymentApproval -isnot [bool] -or $manifest.deploymentApproval -or $manifest.files.Count -ne $sources.Count -or $zip.Entries.Count -ne $sources.Count+1 -or
        (Get-ReleaseInventoryHash $manifest.files) -cne $manifest.inventorySha256) { Stop-Release 'Release.AdministrationManifestMismatch' }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $zip.Entries) { Assert-ReleaseName $entry.FullName; if (-not $seen.Add($entry.FullName)) { Stop-Release 'Release.AdministrationInventoryMismatch' } }
    foreach ($name in $sources.Keys) {
        $record=@($manifest.files | Where-Object path -CEQ $name)
        $entry=$zip.GetEntry($name)
        if ($record.Count -ne 1 -or $null -eq $entry) { Stop-Release 'Release.AdministrationInventoryMismatch' }
        $bytes=Read-ReleaseEntry $entry
        if ($bytes.Length -ne $record[0].length -or (Get-ReleaseBytesHash $bytes) -cne $record[0].sha256 -or (Get-ReleaseHash $sources[$name]) -cne $record[0].sha256) { Stop-Release 'Release.AdministrationBytesMismatch' }
    }
} finally { $zip.Dispose() }
Write-Output 'Application excludes administrative tooling; separate setup archive exactly matches SQL, PowerShell, project notices and upstream license source bytes.'
