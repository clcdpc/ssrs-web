#Requires -Version 7.0
[CmdletBinding()]
param()
. "$PSScriptRoot/Release.Common.ps1"
. "$PSScriptRoot/TagRelease.Common.ps1"
if ($env:GITHUB_REPOSITORY -cne 'clcdpc/ssrs-web' -or $env:GITHUB_EVENT_NAME -cne 'workflow_run' -or $env:GITHUB_REF -cne 'refs/heads/master' -or
    $env:GITHUB_REF_PROTECTED -cne 'true' -or $env:GITHUB_WORKFLOW_SHA -cne $env:GITHUB_SHA -or
    $env:GITHUB_WORKFLOW_REF -cne 'clcdpc/ssrs-web/.github/workflows/deploy-release.yml@refs/heads/master') { Stop-TagRelease 'TrustedConsumerRequired' }
$event=Read-TagJson ([IO.File]::ReadAllBytes($env:GITHUB_EVENT_PATH))
$directory=Join-Path $env:RUNNER_TEMP ('ssrs-release-archive-'+[Guid]::NewGuid().ToString('N'))
$release=Get-SsrsAuthenticatedTagRelease $event.workflow_run $env:GITHUB_WORKFLOW_SHA $directory
# Authenticate a master-pinned packaging inspector, then inspect data only.
# No application code or tagged producer script runs with contents:write.
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
Publish-SsrsCanonicalRelease $release (Join-Path $directory 'publication') | ConvertTo-Json -Compress | Write-Output
