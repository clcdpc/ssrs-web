#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PublishDirectory,
    [string] $NuGetPackagesDirectory
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$publishRoot = [IO.Path]::GetFullPath($PublishDirectory)
if (-not [IO.Directory]::Exists($publishRoot)) { throw 'ThirdParty.PublishDirectoryMissing' }
$dependenciesPath = Join-Path $publishRoot 'SsrsPortal.deps.json'
if (-not [IO.File]::Exists($dependenciesPath)) { throw 'ThirdParty.DependenciesFileMissing' }
$noticePath = Join-Path $publishRoot 'THIRD-PARTY-NOTICES.md'
$licensesPath = Join-Path $publishRoot 'THIRD-PARTY-LICENSES'
if ([IO.File]::Exists($noticePath) -or [IO.Directory]::Exists($licensesPath)) { throw 'ThirdParty.OutputAlreadyExists' }

foreach ($name in @('LICENSE', 'COPYRIGHT.md', 'BINARY-DISTRIBUTION-NOTICE.md')) {
    $sourcePath = Join-Path $repositoryRoot $name
    $publishedPath = Join-Path $publishRoot $name
    if (-not [IO.File]::Exists($sourcePath) -or -not [IO.File]::Exists($publishedPath) -or
        (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $publishedPath -Algorithm SHA256).Hash) {
        throw "ThirdParty.ProjectNoticeMissingOrChanged:$name"
    }
}

if ([string]::IsNullOrWhiteSpace($NuGetPackagesDirectory)) {
    if ($env:NUGET_PACKAGES) {
        $NuGetPackagesDirectory = $env:NUGET_PACKAGES
    } elseif ($env:USERPROFILE) {
        $NuGetPackagesDirectory = Join-Path $env:USERPROFILE '.nuget/packages'
    } elseif ($env:HOME) {
        $NuGetPackagesDirectory = Join-Path $env:HOME '.nuget/packages'
    } else {
        throw 'ThirdParty.NuGetPackageCachePathMissing'
    }
}
$packagesRoot = [IO.Path]::GetFullPath($NuGetPackagesDirectory)
if (-not [IO.Directory]::Exists($packagesRoot)) { throw 'ThirdParty.NuGetPackageCacheMissing' }
$dependencies = Get-Content -LiteralPath $dependenciesPath -Raw | ConvertFrom-Json -AsHashtable
$packageEntries = @($dependencies.libraries.GetEnumerator() | Where-Object { $_.Value.type -ceq 'package' } | Sort-Object Name)
if (-not $packageEntries.Count) { throw 'ThirdParty.PackageInventoryEmpty' }

$licenseSourceRoot = Join-Path $repositoryRoot 'THIRD-PARTY-LICENSES'
$licenseTemplates = @{
    'MIT' = Join-Path $licenseSourceRoot 'MIT.txt'
    'Apache-2.0' = Join-Path $licenseSourceRoot 'Apache-2.0.txt'
    'BSD-2-Clause' = Join-Path $licenseSourceRoot 'BSD-2-Clause.txt'
    'BSD-3-Clause' = Join-Path $licenseSourceRoot 'BSD-3-Clause.txt'
    'AGPL-3.0-only' = Join-Path $repositoryRoot 'LICENSE'
}
[void][IO.Directory]::CreateDirectory($licensesPath)
$licenseRows = [Collections.Generic.List[string]]::new()
$hangfirePackages = [Collections.Generic.List[string]]::new()
$sniPackage = $null
$sniPackageDirectory = $null
$sniLicensePath = $null

function Get-NuspecValue([System.Xml.XmlNode] $Metadata, [string] $Name) {
    $node = $Metadata.SelectSingleNode("./*[local-name()='$Name']")
    if ($node) { return $node.InnerText.Trim() }
    return ''
}

function ConvertTo-MarkdownCell([string] $Value) {
    return [regex]::Replace(($Value -replace '[\r\n\t]+', ' ').Trim().Replace('|', '\|'), '\s{2,}', ' ')
}

function Get-PackageNoticeName([string] $Id, [string] $Version, [string] $FileName) {
    if ($FileName -cnotmatch '\A[A-Za-z0-9._-]+\z') { throw 'ThirdParty.LicenseFileNameInvalid' }
    return "$Id-$Version-$FileName"
}

function Test-LicenseTextWithNormalizedLineEndings([string] $ExpectedPath, [string] $ActualPath) {
    if (-not [IO.File]::Exists($ExpectedPath) -or -not [IO.File]::Exists($ActualPath)) { return $false }
    $expectedText = [regex]::Replace([IO.File]::ReadAllText($ExpectedPath), "`r`n?", "`n")
    $actualText = [regex]::Replace([IO.File]::ReadAllText($ActualPath), "`r`n?", "`n")
    return [string]::Equals($expectedText, $actualText, [StringComparison]::Ordinal)
}

foreach ($entry in $packageEntries) {
    $parts = $entry.Name.Split('/')
    if ($parts.Count -ne 2 -or $parts[0] -cnotmatch '\A[A-Za-z0-9._-]+\z' -or $parts[1] -cnotmatch '\A[A-Za-z0-9.+-]+\z') {
        throw 'ThirdParty.PackageIdentityInvalid'
    }
    $packageId = $parts[0]
    $packageVersion = $parts[1]
    $packageDirectory = Join-Path (Join-Path $packagesRoot $packageId.ToLowerInvariant()) $packageVersion.ToLowerInvariant()
    if (-not [IO.Directory]::Exists($packageDirectory)) { throw "ThirdParty.RestoredPackageMissing:$packageId/$packageVersion" }
    $nuspecs = @(Get-ChildItem -LiteralPath $packageDirectory -File -Filter '*.nuspec')
    if ($nuspecs.Count -ne 1) { throw "ThirdParty.PackageMetadataAmbiguous:$packageId/$packageVersion" }
    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [System.Xml.XmlReader]::Create($nuspecs[0].FullName, $settings)
    try {
        $document = [System.Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
    } finally { $reader.Dispose() }
    $metadata = $document.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    $licenseNode = $metadata.SelectSingleNode("./*[local-name()='license']")
    if (-not $metadata -or -not $licenseNode) { throw "ThirdParty.PackageLicenseMissing:$packageId/$packageVersion" }
    $licenseType = $licenseNode.GetAttribute('type')
    $packageLicense = $licenseNode.InnerText.Trim()
    $copyright = Get-NuspecValue $metadata 'copyright'
    $authors = Get-NuspecValue $metadata 'authors'
    if (-not $copyright) { $copyright = "Copyright attribution is not specified in package metadata; package authors: $authors" }
    $projectUrl = Get-NuspecValue $metadata 'projectUrl'
    $licenseCell = ''

    if ($licenseType -ceq 'expression') {
        if (-not $licenseTemplates.ContainsKey($packageLicense)) { throw "ThirdParty.SPDXExpressionNeedsReview:$packageId/${packageVersion}:$packageLicense" }
        $templatePath = $licenseTemplates[$packageLicense]
        if (-not [IO.File]::Exists($templatePath)) { throw "ThirdParty.SPDXTextMissing:$packageLicense" }
        $licenseFileName = Get-PackageNoticeName $packageId $packageVersion "$packageLicense.txt"
        $licenseDestination = Join-Path $licensesPath $licenseFileName
        $licenseText = [IO.File]::ReadAllText($templatePath)
        if ($licenseText -match '(?m)^Copyright \(c\) <year> <(?:owner|copyright holders)>\.?\s*$') {
            $copyrightNotice = $copyright
            if ($copyrightNotice -notmatch '(?i)copyright|©') { $copyrightNotice = "Copyright (c) $copyrightNotice" }
            $licenseText = [regex]::Replace($licenseText, '(?m)^Copyright \(c\) <year> <(?:owner|copyright holders)>\.?\s*$', [System.Text.RegularExpressions.MatchEvaluator]{ param($match) $copyrightNotice }, 1)
        }
        [IO.File]::WriteAllText($licenseDestination, $licenseText, [Text.UTF8Encoding]::new($false))
        $licenseCell = "[$packageLicense](THIRD-PARTY-LICENSES/$licenseFileName)"
    } elseif ($licenseType -ceq 'file') {
        $relativeLicensePath = $packageLicense.Replace('\', '/')
        if (-not $relativeLicensePath -or $relativeLicensePath.StartsWith('/') -or $relativeLicensePath.Contains(':') -or
            @($relativeLicensePath.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count) { throw 'ThirdParty.PackageLicensePathInvalid' }
        $packagePrefix = [IO.Path]::GetFullPath($packageDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $packageLicensePath = [IO.Path]::GetFullPath((Join-Path $packageDirectory ($relativeLicensePath.Replace('/', [IO.Path]::DirectorySeparatorChar))))
        if (-not $packageLicensePath.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase) -or -not [IO.File]::Exists($packageLicensePath)) {
            throw "ThirdParty.PackageLicenseFileMissing:$packageId/$packageVersion"
        }
        if ($packageId -cmatch '\AHangfire\.') {
            $trackedHangfireLicense = Join-Path $licenseSourceRoot 'Hangfire-1.8.25-LICENSE.md'
            if ($packageVersion -cne '1.8.25' -or -not (Test-LicenseTextWithNormalizedLineEndings $trackedHangfireLicense $packageLicensePath)) {
                throw 'ThirdParty.HangfireLicenseTextMismatch'
            }
        }
        $licenseFileName = Get-PackageNoticeName $packageId $packageVersion ([IO.Path]::GetFileName($packageLicensePath))
        $licenseDestination = Join-Path $licensesPath $licenseFileName
        [IO.File]::Copy($packageLicensePath, $licenseDestination, $false)
        $licenseCell = "[package license file](THIRD-PARTY-LICENSES/$licenseFileName)"
    } else { throw "ThirdParty.PackageLicenseTypeUnsupported:$packageId/$packageVersion" }

    if ($packageId -cmatch '\AHangfire\.' -and $packageVersion -cne '1.8.25') { throw "ThirdParty.HangfireVersionNeedsReview:$packageId/$packageVersion" }
    if ($packageId -cmatch '\AHangfire\.') { $hangfirePackages.Add("$packageId/$packageVersion") }
    if ($packageId -ceq 'Microsoft.Data.SqlClient.SNI.runtime') {
        if ($packageVersion -cne '7.1.0') { throw "ThirdParty.SniVersionNeedsReview:$packageVersion" }
        $sniPackage = "$packageId/$packageVersion"
        $sniPackageDirectory = $packageDirectory
        $sniLicensePath = $packageLicensePath
    }
    $packageLink = "https://www.nuget.org/packages/$packageId/$packageVersion"
    $licenseRows.Add("| [$packageId/$packageVersion]($packageLink) | $(ConvertTo-MarkdownCell $copyright) | $(ConvertTo-MarkdownCell "${licenseType}: $packageLicense") | $licenseCell |")
}

if ($sniPackage) {
    $trackedSniLicense = Join-Path $licenseSourceRoot 'Microsoft.Data.SqlClient.SNI.runtime-7.1.0-LICENSE.txt'
    if (-not (Test-LicenseTextWithNormalizedLineEndings $trackedSniLicense $sniLicensePath)) { throw 'ThirdParty.SniLicenseTextMismatch' }
    if ($dependencies.runtimeTarget.name -ceq '.NETCoreApp,Version=v10.0/win-x64') {
        $packageNative = Join-Path $sniPackageDirectory 'runtimes/win-x64/native/Microsoft.Data.SqlClient.SNI.dll'
        $publishedNative = Join-Path $publishRoot 'Microsoft.Data.SqlClient.SNI.dll'
        if (-not [IO.File]::Exists($packageNative) -or -not [IO.File]::Exists($publishedNative) -or
            (Get-FileHash -LiteralPath $packageNative -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $publishedNative -Algorithm SHA256).Hash) {
            throw 'ThirdParty.SniPublishedBinaryMismatch'
        }
        $sniHash = (Get-FileHash -LiteralPath $publishedNative -Algorithm SHA256).Hash.ToLowerInvariant()
    } else { $sniHash = $null }
}

if ($hangfirePackages.Count) {
    foreach ($name in @('COPYING', 'COPYING.LESSER')) {
        $source = Join-Path (Join-Path $repositoryRoot 'scripts/subscriptions/vendor/hangfire') $name
        if (-not [IO.File]::Exists($source)) { throw "ThirdParty.HangfireSourceLicenseMissing:$name" }
        [IO.File]::Copy($source, (Join-Path $licensesPath "Hangfire-1.8.25-$name"), $false)
    }
}
$licenseReadme = Join-Path $licenseSourceRoot 'README.md'
if (-not [IO.File]::Exists($licenseReadme)) { throw 'ThirdParty.LicenseProvenanceMissing' }
[IO.File]::Copy($licenseReadme, (Join-Path $licensesPath 'README.md'), $false)

$notice = [Collections.Generic.List[string]]::new()
$notice.Add('# Third-Party Notices')
$notice.Add('')
$notice.Add('This inventory was generated from this application publish''s `SsrsPortal.deps.json` and the exact restored NuGet package metadata. It describes third-party packages included by this build; none of these components is relicensed under the project AGPL license. Keep this notice and the linked texts with any binary copy.')
$notice.Add('')
$notice.Add('| Package and version | Package copyright metadata | Package-declared license | Included license text |')
$notice.Add('| --- | --- | --- | --- |')
foreach ($row in $licenseRows) { $notice.Add($row) }
$notice.Add('')
if ($sniPackage) {
    $notice.Add('## Microsoft Windows-native SNI')
    $notice.Add('')
    $notice.Add('`Microsoft.Data.SqlClient.SNI.runtime` is a separate package from the MIT-licensed `Microsoft.Data.SqlClient` package. Its Microsoft Software License Terms are included above as the package license file. They permit object-code distribution only as part of an application, require downstream distributors and external end users to agree to terms protecting the component at least as much as the Microsoft terms, and require distributor indemnification as specified in the full text. Do not distribute the SNI binary as a standalone component.')
    if ($sniHash) { $notice.Add('The Windows x64 publish contains `Microsoft.Data.SqlClient.SNI.dll` (SHA-256 ' + $sniHash + ').') }
    $notice.Add('')
}
if ($hangfirePackages.Count) {
    $notice.Add('## Hangfire and the separate SQL setup artifact')
    $notice.Add('')
    $notice.Add('Hangfire 1.8.25 package license files are included above. The vendored Hangfire SQL installer is distributed only in the separate administrative setup artifact with its upstream attribution and full `COPYING` and `COPYING.LESSER` texts; it is excluded from this application publish.')
    $notice.Add('')
}
$notice.Add('This publish also contains the project `LICENSE`, `COPYRIGHT.md`, and `BINARY-DISTRIBUTION-NOTICE.md` at its root. Those files identify the project license and Microsoft SNI redistribution requirements.')
[IO.File]::WriteAllText($noticePath, ($notice -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
Write-Output "Third-party notices include $($packageEntries.Count) package libraries; license texts: $([IO.Directory]::GetFiles($licensesPath).Count)."
