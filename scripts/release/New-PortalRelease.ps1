#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PublishRoot,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [Parameter(Mandatory)][string] $ToolkitLockPath,
    [Parameter(Mandatory)][string] $ToolkitDirectory,
    [string] $ToolkitArchivePath,
    [string] $ToolkitReadToken,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string] $SourceCommit,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')][string] $ReleaseId,
    [Parameter(Mandatory)][ValidateRange(1,[long]::MaxValue)][long] $RunId,
    [Parameter(Mandatory)][ValidateRange(1,[long]::MaxValue)][long] $RunAttempt,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string] $DependencyLockSha256,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string] $WorkflowCommit
)
. "$PSScriptRoot/Release.Common.ps1"
$PublishRoot=Assert-ReleasePath $PublishRoot; $OutputDirectory=Assert-ReleasePath $OutputDirectory
if (-not [IO.Directory]::Exists($OutputDirectory)) { Stop-Release 'Release.OutputParentMissing' }
Assert-PortalPublish $PublishRoot $ReleaseId $SourceCommit
$acquire=@{LockPath=$ToolkitLockPath;Destination=$ToolkitDirectory}
if ($ToolkitArchivePath) { $acquire.ArchivePath=$ToolkitArchivePath }
if ($ToolkitReadToken) { $acquire.ReadToken=$ToolkitReadToken }
$builder=& "$PSScriptRoot/Get-ReleaseToolkit.ps1" @acquire
Import-Module $builder.moduleManifest -Force
$metadata=[ordered]@{contractFamily='clc-file-iis-v1';schemaVersion=1;recordKind='release-metadata';applicationId='ssrs-web';releaseId=$ReleaseId;
    source=[ordered]@{repository='clcdpc/ssrs-web';commit=$SourceCommit};
    build=[ordered]@{workflowPath='.github/workflows/release.yml';runId=$RunId;runAttempt=$RunAttempt;job='release';sdkVersion='10.0.303';dependencyLockSha256=$DependencyLockSha256};
    builder=[ordered]@{module='CLC.Deployment';version=$builder.version;sourceCommit=$builder.sourceCommit;distributionSha256=$builder.distributionSha256};
    runtime=[ordered]@{framework='net10.0';rid='win-x64';architecture='x64';hostingModel='inprocess';workerLimit=1};
    changeKind='files-only';validation=[ordered]@{contractVersion=1;entryPoint='SsrsPortal.dll'};readinessContractVersion=1}
# Metadata must be outside the archive's output directory (toolkit path contract).
$metadataPath=Join-Path ([IO.Path]::GetDirectoryName($OutputDirectory)) ('metadata-'+[Guid]::NewGuid().ToString('N')+'.json')
$metadataBytes=[Text.Encoding]::UTF8.GetBytes(($metadata | ConvertTo-Json -Depth 16 -Compress))
$metadataStream=[IO.FileStream]::new($metadataPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try { $metadataStream.Write($metadataBytes); $metadataStream.Flush($true) } finally { $metadataStream.Dispose() }
$archive=Join-Path $OutputDirectory 'release.zip'
$created=New-ClcRelease -PublishRoot $PublishRoot -MetadataPath $metadataPath -OutputPath $archive
$verified=Test-ClcRelease -ArchivePath $archive
if (-not $verified.integrityVerified -or $verified.authorizedForDeployment -or $verified.applicationId -cne 'ssrs-web' -or
    $verified.releaseId -cne $ReleaseId -or $verified.sourceCommit -cne $SourceCommit -or $verified.archiveSha256 -cne $created.archiveSha256) { Stop-Release 'Release.ToolkitVerificationMismatch' }
$null=Assert-PortalReleaseArchive $archive $ReleaseId $SourceCommit
$evidence=[ordered]@{schemaVersion=1;recordKind='ssrs-web-build-evidence';repository='clcdpc/ssrs-web';sourceCommit=$SourceCommit;
    workflowPath='.github/workflows/release.yml';workflowCommit=$WorkflowCommit;runId=$RunId;runAttempt=$RunAttempt;buildJob='release';
    releaseId=$ReleaseId;innerReleaseSha256=$verified.archiveSha256;manifestSha256=$verified.manifestSha256;inventorySha256=$verified.inventorySha256;
    sdkVersion='10.0.303';dependencyLockSha256=$DependencyLockSha256;toolkitBuilder=$metadata.builder;moduleInventorySha256=$builder.moduleInventorySha256;
    fileCount=$verified.fileCount;totalBytes=$verified.totalBytes;integrityVerified=$true;deploymentApproval=$false;hostInstallationApproval=$false}
$evidencePath=Join-Path ([IO.Path]::GetDirectoryName($OutputDirectory)) 'build-evidence.json'
$evidenceStream=[IO.FileStream]::new($evidencePath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try { $evidenceStream.Write([Text.Encoding]::UTF8.GetBytes(($evidence | ConvertTo-Json -Depth 16 -Compress))); $evidenceStream.Flush($true) } finally { $evidenceStream.Dispose() }
[pscustomobject]$evidence
