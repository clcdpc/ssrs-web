#Requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string] $ActionlintPath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$workflow=[IO.File]::ReadAllText((Join-Path $repository '.github/workflows/diagnose-deployment.yml')).Replace("`r`n","`n")
function Assert-True([bool] $Value) { if (-not $Value) { throw 'Diagnostic contract assertion failed.' } }
$version=& $ActionlintPath -version 2>&1
Assert-True ($LASTEXITCODE -eq 0 -and $version[0] -ceq '1.7.12')
& $ActionlintPath @((Get-ChildItem -LiteralPath (Join-Path $repository '.github/workflows') -Filter '*.yml').FullName)
Assert-True ($LASTEXITCODE -eq 0)
Assert-True ($workflow.Contains('check-github-setup.yml@33777ec2c4cfc70c86c102f5cb38502d0a8f4f1d'))
Assert-True ($workflow.Contains("on:`n  workflow_dispatch:"))
Assert-True ($workflow.Contains("permissions:`n  contents: read`n  actions: read"))
foreach ($setting in @('application_id: ssrs-web','channel: DEV','integration_branch: master','check_release_immutability: true')) { Assert-True ($workflow.Contains($setting)) }
Assert-True ($workflow -cnotmatch 'self-hosted|host:|report:|activation|vars\.|secrets\.|ToolkitManifestPath|validate-deployment.yml|: write|runner_group')
foreach ($name in @('release.yml','verify.yml')) {
    $text=[IO.File]::ReadAllText((Join-Path $repository ".github/workflows/$name"))
    Assert-True ($text -cnotmatch 'diagnose-deployment.yml|SSRS_DEV_DIAGNOSTIC_RUNNER_READY|validate-deployment.yml')
}
Write-Output 'Deployment diagnostic integration: current pinned GET-only GitHub checker; no host access or deployment admission gate.'
