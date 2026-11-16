#Requires -Version 7.0
[CmdletBinding()]
param()
. "$PSScriptRoot/Release.Common.ps1"
. "$PSScriptRoot/TagRelease.Common.ps1"
$context=Get-SsrsTagWorkflowContext
if ($env:GITHUB_JOB -cne 'archive') { Stop-TagRelease 'ArchiveJobRequired' }
$directory=Join-Path $env:RUNNER_TEMP ('ssrs-release-archive-'+[Guid]::NewGuid().ToString('N'))
$release=Get-SsrsAuthenticatedTagRelease $context $env:GITHUB_WORKFLOW_SHA $directory
# Authenticate the tagged packaging inspector, then inspect package data only.
$builder=& "$PSScriptRoot/Get-ReleaseToolkit.ps1" -LockPath "$PSScriptRoot/../../deployment/toolkit.lock.json" -Destination (Join-Path $directory 'toolkit')
$module=Import-Module $builder.moduleManifest -Force -PassThru
$verified=& $module { param($Path) Test-ClcRelease -ArchivePath $Path } $release.archivePath
$p=$release.provenance
if ($verified.integrityVerified -ne $true -or $verified.authorizedForDeployment -ne $false -or $verified.applicationId -cne 'ssrs-web' -or
    $verified.releaseId -cne $p.releaseId -or $verified.sourceCommit -cne $release.sourceCommit -or $verified.archiveSha256 -cne $p.innerReleaseSha256 -or
    $verified.manifestSha256 -cne $p.manifestSha256) { Stop-TagRelease 'ArchivePackageMismatch' }
$zip=[IO.Compression.ZipFile]::OpenRead($release.archivePath)
try {
    $manifest=Read-ReleaseJson (Read-ReleaseEntry ($zip.GetEntry('manifest.json')) 4194304)
    if ($manifest.build.workflowPath -cne '.github/workflows/release.yml' -or $manifest.build.job -cne 'release' -or
        $manifest.build.runId -ne $release.run.id -or $manifest.build.runAttempt -ne $release.run.run_attempt) { Stop-TagRelease 'ArchivePackageMismatch' }
    Assert-CompiledReleaseIdentity (Read-CompiledReleaseIdentity (Read-ReleaseEntry ($zip.GetEntry('application/SsrsPortal.dll')))) $p.releaseId $release.sourceCommit
} finally { $zip.Dispose() }
$publication=Publish-SsrsCanonicalRelease $release (Join-Path $directory 'publication')
$publication | ConvertTo-Json -Compress | Write-Output
if ($env:GITHUB_OUTPUT) {
    $target=if ($release.targetId -and -not (Test-SsrsTagSuperseded $release)) { $release.targetId } else { '' }
    "target=$target" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
