#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string] $OutputPath,[Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string] $SourceCommit)
. "$PSScriptRoot/Release.Common.ps1"
$OutputPath=Assert-ReleasePath $OutputPath
$subscriptionRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../subscriptions'))
$payload=@{}
foreach ($name in @('001-Subscriptions.sql','002-Hangfire.sql','003-SubscriptionPermissions.sql','Initialize-Subscriptions.ps1')) { $payload['Database/'+$name]=[IO.File]::ReadAllBytes((Join-Path $subscriptionRoot $name)) }
foreach ($name in @('LICENSE.md','COPYING','COPYING.LESSER','README.md')) { $payload['Database/Licenses/Hangfire/'+$name]=[IO.File]::ReadAllBytes((Join-Path $subscriptionRoot ('vendor/hangfire/'+$name))) }
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
foreach ($name in @('LICENSE','COPYRIGHT.md')) { $payload['Database/Licenses/Project/'+$name]=[IO.File]::ReadAllBytes((Join-Path $repositoryRoot $name)) }
$payload['Operations/Configure-SubscriptionIis.ps1']=[IO.File]::ReadAllBytes((Join-Path $subscriptionRoot 'Configure-SubscriptionIis.ps1'))
$names=[string[]]@($payload.Keys); [Array]::Sort($names,[StringComparer]::Ordinal)
$files=@(foreach ($name in $names) { [ordered]@{path=$name;length=[long]$payload[$name].Length;sha256=(Get-ReleaseBytesHash $payload[$name])} })
$manifest=[ordered]@{recordKind='ssrs-web-administrative-setup';schemaVersion=1;sourceRepository='clcdpc/ssrs-web';sourceCommit=$SourceCommit;automaticExecution=$false;deploymentApproval=$false;inventorySha256=(Get-ReleaseInventoryHash $files);files=$files}
$payload['setup-manifest.json']=[Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 16 -Compress))
$stream=[IO.FileStream]::new($OutputPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $zip=$null
try {
    $zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$true)
    $names=[string[]]@($payload.Keys); [Array]::Sort($names,[StringComparer]::Ordinal)
    foreach ($name in $names) {
        $entry=$zip.CreateEntry($name,[IO.Compression.CompressionLevel]::NoCompression)
        $entry.LastWriteTime=[DateTimeOffset]::new(1980,1,1,0,0,0,[TimeSpan]::Zero); $entry.ExternalAttributes=0
        $output=$entry.Open(); try { $output.Write($payload[$name]) } finally { $output.Dispose() }
    }
    $zip.Dispose(); $zip=$null; $stream.Flush($true)
} finally { if ($zip) { $zip.Dispose() }; $stream.Dispose() }
[pscustomobject]@{archivePath=$OutputPath;sha256=(Get-ReleaseHash $OutputPath);sourceCommit=$SourceCommit;authorizedForDeployment=$false;automaticExecution=$false}
