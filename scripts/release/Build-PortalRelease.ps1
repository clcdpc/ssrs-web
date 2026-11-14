#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string] $SourceCommit)
. "$PSScriptRoot/Release.Common.ps1"
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:GITHUB_REPOSITORY -cne 'clcdpc/ssrs-web' -or
    $env:GITHUB_EVENT_NAME -cnotin @('push','workflow_dispatch') -or $env:GITHUB_SHA -cne $SourceCommit -or $env:GITHUB_REF_TYPE -cne 'tag' -or
    $env:GITHUB_WORKFLOW_REF -cne ('clcdpc/ssrs-web/.github/workflows/release.yml@refs/tags/'+$env:GITHUB_REF_NAME) -or
    $env:GITHUB_WORKFLOW_SHA -cne $SourceCommit -or $env:GITHUB_JOB -cne 'release' -or
    $env:GITHUB_RUN_ID -cnotmatch '^[1-9][0-9]*$' -or $env:GITHUB_RUN_ATTEMPT -cnotmatch '^[1-9][0-9]*$') { Stop-Release 'Release.TrustedProducerContextRequired' }
. "$PSScriptRoot/TagRelease.Common.ps1"
$null=Get-SsrsReleaseTag $env:GITHUB_REF_NAME
& "$PSScriptRoot/Test-ExactSourceBytes.ps1" -RepositoryRoot $repositoryRoot -Commit $SourceCommit
$sdk=(Read-ReleaseJson ([IO.File]::ReadAllBytes((Join-Path $repositoryRoot 'global.json')))).sdk.version
if ($PSVersionTable.PSVersion.ToString() -cne '7.6.6') { Stop-Release 'Release.ExactPowerShellRequired' }
if ($sdk -cne '10.0.303' -or (& dotnet --version) -cne $sdk -or $LASTEXITCODE -ne 0) { Stop-Release 'Release.ExactSdkRequired' }
$releaseId='ssrs-web-'+$env:GITHUB_RUN_ID+'-'+$env:GITHUB_RUN_ATTEMPT
$work=Join-Path $repositoryRoot ('artifacts/release-'+$env:GITHUB_RUN_ID+'-'+$env:GITHUB_RUN_ATTEMPT)
if ([IO.Path]::Exists($work)) { Stop-Release 'Release.NewWorkDirectoryRequired' }
[void][IO.Directory]::CreateDirectory($work)
$publish=Join-Path $work 'publish'; $output=Join-Path $work 'artifact'
[void][IO.Directory]::CreateDirectory($output)
& "$PSScriptRoot/Test-ExactSourceBytes.ps1" -RepositoryRoot $repositoryRoot -Commit $SourceCommit
& dotnet restore (Join-Path $repositoryRoot 'src/SsrsPortal/SsrsPortal.csproj') --locked-mode --runtime win-x64 -p:ClcIdentityKind=Release
if ($LASTEXITCODE -ne 0) { Stop-Release 'Release.LockedRestoreFailed' }
& "$PSScriptRoot/Test-ExactSourceBytes.ps1" -RepositoryRoot $repositoryRoot -Commit $SourceCommit
& dotnet publish (Join-Path $repositoryRoot 'src/SsrsPortal/SsrsPortal.csproj') -c Release --runtime win-x64 --self-contained false --no-restore -p:UseAppHost=false -p:DebugType=None -p:DebugSymbols=false "-p:ClcReleaseId=$releaseId" "-p:ClcSourceCommit=$SourceCommit" -p:ClcIdentityKind=Release --output $publish
if ($LASTEXITCODE -ne 0) { Stop-Release 'Release.PublishFailed' }
& "$PSScriptRoot/Export-ThirdPartyNotices.ps1" -PublishDirectory $publish
if ($LASTEXITCODE -ne 0) { Stop-Release 'Release.ThirdPartyNoticeExportFailed' }
& "$PSScriptRoot/Test-ExactSourceBytes.ps1" -RepositoryRoot $repositoryRoot -Commit $SourceCommit
$args=@{PublishRoot=$publish;OutputDirectory=$output;ToolkitLockPath=(Join-Path $repositoryRoot 'deployment/toolkit.lock.json');ToolkitDirectory=(Join-Path $work 'toolkit');
    SourceCommit=$SourceCommit;ReleaseId=$releaseId;RunId=[long]$env:GITHUB_RUN_ID;RunAttempt=[long]$env:GITHUB_RUN_ATTEMPT;
    DependencyLockSha256=(Get-ReleaseDependencyLockHash $repositoryRoot);WorkflowCommit=$env:GITHUB_WORKFLOW_SHA;ToolkitReadToken=$env:TOOLKIT_READ_TOKEN}
$null=& "$PSScriptRoot/New-PortalRelease.ps1" @args
$admin=Join-Path $work 'administrative-setup.zip'
$null=& "$PSScriptRoot/New-SubscriptionSetupArtifact.ps1" -OutputPath $admin -SourceCommit $SourceCommit
& "$PSScriptRoot/Test-SubscriptionPublish.ps1" -PublishDirectory $publish -AdministrationArchive $admin
& "$PSScriptRoot/Test-ExactSourceBytes.ps1" -RepositoryRoot $repositoryRoot -Commit $SourceCommit
if ($env:GITHUB_OUTPUT) { "artifact-directory=$output`nbuild-evidence=$(Join-Path $work 'build-evidence.json')`nadministrative-archive=$admin" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8 -Append }
