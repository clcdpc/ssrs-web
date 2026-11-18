#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ToolkitRoot,
    [Parameter(Mandatory)][string] $Destination,
    [switch] $PackagingOnly
)
$ErrorActionPreference = 'Stop'
$lock = Get-Content -LiteralPath (Join-Path $ToolkitRoot 'tools/dependencies.lock.json') -Raw | ConvertFrom-Json -AsHashtable
$root = New-Item -ItemType Directory -Path $Destination
$downloads = @(
    @{ name = 'pwsh'; url = $lock.powershell.url; sha256 = $lock.powershell.sha256 }
)
if (-not $PackagingOnly) {
    $downloads += @(
        @{ name = 'pester'; url = $lock.pester.url; sha256 = $lock.pester.sha256 }
        @{
            name = 'actionlint'
            url = 'https://github.com/rhysd/actionlint/releases/download/v1.7.12/actionlint_1.7.12_windows_amd64.zip'
            sha256 = '6e7241b51e6817ea6a047693d8e6fed13b31819c9a0dd6c5a726e1592d22f6e9'
        }
    )
}
foreach ($download in $downloads) {
    $zip = Join-Path $root.FullName ($download.name + '.zip')
    Invoke-WebRequest -Uri $download.url -OutFile $zip -TimeoutSec 120
    if ((Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant() -cne $download.sha256) {
        throw "The $($download.name) tool failed its distribution digest check."
    }
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, (Join-Path $root.FullName $download.name))
}
