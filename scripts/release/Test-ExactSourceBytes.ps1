#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $RepositoryRoot,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string] $Commit
)
. "$PSScriptRoot/ExactSourceBytes.Common.ps1"
$result = Assert-ExactSourceBytes -RepositoryRoot $RepositoryRoot -Commit $Commit
Write-Output "Exact source bytes verified for $($result.trackedFileCount) tracked files."