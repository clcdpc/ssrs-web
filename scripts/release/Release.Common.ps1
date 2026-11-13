Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:ReleaseRepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))

function Stop-Release([string] $Code) { throw [IO.InvalidDataException]::new($Code) }
function Get-ReleaseHash([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Get-ReleaseBytesHash([byte[]] $Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }
function Read-ReleaseJson([byte[]] $Bytes) {
    if ($Bytes.Length -gt 4194304) { Stop-Release 'Release.JsonBound' }
    $value = [Text.UTF8Encoding]::new($false,$true).GetString($Bytes)
    $document = [Text.Json.JsonDocument]::Parse($value, [Text.Json.JsonDocumentOptions]@{MaxDepth=32})
    function Test-ReleaseJsonElement($Element) {
        if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($property in $Element.EnumerateObject()) {
                if (-not $names.Add($property.Name)) { Stop-Release 'Release.DuplicateJson' }
                Test-ReleaseJsonElement $property.Value
            }
        } elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
            foreach ($item in $Element.EnumerateArray()) { Test-ReleaseJsonElement $item }
        }
    }
    try { Test-ReleaseJsonElement $document.RootElement } finally { $document.Dispose() }
    $value | ConvertFrom-Json -AsHashtable -Depth 32
}
function Assert-ReleasePath([string] $Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { Stop-Release 'Release.AbsolutePathRequired' }
    $full = [IO.Path]::GetFullPath($Path)
    $cursor = [IO.Path]::GetPathRoot($full)
    foreach ($part in $full.Substring($cursor.Length).Split([char[]]@([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar),[StringSplitOptions]::RemoveEmptyEntries)) {
        $cursor = Join-Path $cursor $part
        if (([IO.File]::Exists($cursor) -or [IO.Directory]::Exists($cursor)) -and
            ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint)) { Stop-Release 'Release.ReparsePath' }
    }
    $full
}
function Assert-ReleaseName([string] $Name) {
    if ($Name.Length -gt 240 -or $Name -cnotmatch '^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$') { Stop-Release 'Release.UnsafeName' }
    foreach ($part in $Name.Split('/')) {
        if ($part -in @('.','..') -or $part.EndsWith('.') -or $part -imatch '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)' -or $part -imatch '^\.clc-') { Stop-Release 'Release.UnsafeName' }
    }
}
function Read-ReleaseEntry($Entry,[long] $Maximum=268435456) {
    if ($Entry.Length -gt $Maximum) { Stop-Release 'Release.EntryBound' }
    $source = $Entry.Open(); $memory = [IO.MemoryStream]::new(); $buffer = [byte[]]::new(65536)
    try {
        while (($read=$source.Read($buffer,0,$buffer.Length)) -gt 0) {
            if ($memory.Length+$read -gt $Maximum) { Stop-Release 'Release.EntryBound' }
            $memory.Write($buffer,0,$read)
        }
        if ($memory.Length -ne $Entry.Length) { Stop-Release 'Release.EntryBound' }
        return ,$memory.ToArray()
    } finally { $source.Dispose(); $memory.Dispose() }
}
function Get-ReleaseInventoryHash([object[]] $Files) {
    $names=[string[]]@($Files.path); [Array]::Sort($names,[StringComparer]::Ordinal)
    $lookup=@{}; foreach ($file in $Files) { $lookup[$file.path]=$file }
    $text=[Text.StringBuilder]::new()
    foreach ($name in $names) {
        $file=$lookup[$name]
        [void]$text.Append($name).Append([char]0).Append(([long]$file.length).ToString([Globalization.CultureInfo]::InvariantCulture)).Append([char]0).Append($file.sha256).Append("`n")
    }
    Get-ReleaseBytesHash ([Text.Encoding]::UTF8.GetBytes($text.ToString()))
}
function Get-ReleaseDependencyLockHash([string] $RepositoryRoot) {
    $files=@(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'src') -Recurse -File -Filter 'packages*.lock.json') +
        @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'tests') -Recurse -File -Filter 'packages*.lock.json') +
        @(Get-Item -LiteralPath (Join-Path $RepositoryRoot 'package-lock.json'))
    if ($files.Count -lt 3) { Stop-Release 'Release.DependencyLocksMissing' }
    $records=@($files | ForEach-Object { @{path=[IO.Path]::GetRelativePath($RepositoryRoot,$_.FullName).Replace('\','/');length=$_.Length;sha256=(Get-ReleaseHash $_.FullName)} })
    Get-ReleaseInventoryHash $records
}
function Read-CompiledReleaseIdentity([byte[]] $Bytes) {
    if ($null -eq ('SsrsRelease.Metadata' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
namespace SsrsRelease {
 public static class Metadata {
  public static Dictionary<string,string> Read(byte[] bytes) {
   using var stream = new MemoryStream(bytes, false);
   using var pe = new PEReader(stream);
   var reader = pe.GetMetadataReader();
   var result = new Dictionary<string,string>(StringComparer.Ordinal);
   foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes()) {
    var attribute = reader.GetCustomAttribute(handle);
    if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
    var member = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
    if (member.Parent.Kind != HandleKind.TypeReference) continue;
    var type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
    if (reader.GetString(type.Namespace) != "System.Reflection" || reader.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
    var blob = reader.GetBlobReader(attribute.Value);
    if (blob.ReadUInt16() != 1) throw new InvalidDataException();
    var key = blob.ReadSerializedString(); var value = blob.ReadSerializedString();
    if (key != null && key.StartsWith("Clc.", StringComparison.Ordinal)) {
     if (value == null || value.Length > 256 || !result.TryAdd(key,value)) throw new InvalidDataException();
    }
   }
   return result;
  }
 }
}
'@
    }
    if ($Bytes.Length -gt 268435456) { Stop-Release 'Release.IdentityBound' }
    [SsrsRelease.Metadata]::Read($Bytes)
}
function Assert-CompiledReleaseIdentity($Identity,[string] $ReleaseId,[string] $SourceCommit) {
    if ($Identity.Count -ne 4 -or $Identity['Clc.ApplicationId'] -cne 'ssrs-web' -or $Identity['Clc.IdentityKind'] -cne 'Release' -or
        $Identity['Clc.ReleaseId'] -cne $ReleaseId -or $Identity['Clc.SourceCommit'] -cne $SourceCommit -or
        $ReleaseId -cnotmatch '^[a-z0-9][a-z0-9-]{0,63}$' -or $ReleaseId.StartsWith('synthetic-',[StringComparison]::Ordinal) -or
        $SourceCommit -cnotmatch '^[a-f0-9]{40}$' -or $SourceCommit -ceq ('0'*40)) { Stop-Release 'Release.CompiledIdentityMismatch' }
}
function Assert-PortalContentName([string] $relative) {
        Assert-ReleaseName $relative
        $name=($relative -split '/')[-1]; $extension=[IO.Path]::GetExtension($name)
        if ($relative -imatch '(^|/)(Database|Operations|toolkit|deployment|Fixtures|TestResults|Logs|Properties|Keys|Certificates|Secrets|tests?|\.git|\.github|artifacts|node_modules|obj)(/|$)' -or
            $name -ieq 'web.clc-release.config' -or ($name -imatch '^application.*\.json$' -and $relative -cne 'application.Example.json') -or
            $name -imatch '^(appsettings.*|deployment|launchSettings|packages.*\.lock)\.json$' -or
            $name -imatch '^(key-[a-f0-9-]+|revocation.*)\.xml$|^\.env($|\.)' -or
            $name -imatch '(Tests|BrowserChecks|SsrsPortal\.Mock)\.(dll|json)$' -or $extension -imatch '^\.(ps1|psm1|psd1|sql|pfx|p12|pem|key|crt|cer|log|trx|pdb)$') {
            Stop-Release 'Release.ForbiddenPublishContent'
        }
}
function Get-PortalPublishedAspNetCoreElement([string] $PublishRoot) {
    $path=Join-Path $PublishRoot 'web.config'
    if (-not [IO.File]::Exists($path)) { Stop-Release 'Release.WebConfigMissing' }
    try {
        $document=[Xml.XmlDocument]::new()
        $document.XmlResolver=$null
        $document.Load($path)
        $nodes=$document.SelectNodes('/configuration/location[@path="."]/system.webServer/aspNetCore')
        if ($nodes.Count -ne 1) { Stop-Release 'Release.WebConfigInvalid' }
        return $nodes[0]
    } catch { Stop-Release 'Release.WebConfigInvalid' }
}
function Assert-OrdinaryPublishShutdownTimeLimit([string] $PublishRoot) {
    $aspNetCore=Get-PortalPublishedAspNetCoreElement $PublishRoot
    if (-not $aspNetCore.HasAttribute('shutdownTimeLimit') -or $aspNetCore.GetAttribute('shutdownTimeLimit') -cne '60') {
        Stop-Release 'Release.OrdinaryShutdownGraceMismatch'
    }
}
function Assert-ReleasePublishShutdownTimeLimit([string] $PublishRoot) {
    $aspNetCore=Get-PortalPublishedAspNetCoreElement $PublishRoot
    if ($aspNetCore.HasAttribute('shutdownTimeLimit')) { Stop-Release 'Release.ToolkitWebConfigShutdownAttributeForbidden' }
}
function Assert-PortalDependencies($dependencies) {
    if ($dependencies.runtimeTarget.name -cne '.NETCoreApp,Version=v10.0/win-x64' -or @($dependencies.libraries.Keys | Where-Object { $_ -clike 'SsrsPortal.Mock/*' }).Count) { Stop-Release 'Release.RuntimeDependenciesMismatch' }
}
function Assert-ThirdPartyNoticeInventory([byte[]] $DependencyBytes,[byte[]] $NoticeBytes,[string[]] $LicenseRelativePaths) {
    $dependencies=Read-ReleaseJson $DependencyBytes
    $notice=[Text.UTF8Encoding]::new($false,$true).GetString($NoticeBytes)
    if ($notice -notmatch '(?m)^# Third-Party Notices$' -or $notice -notmatch 'none of these components is relicensed under the project AGPL license') {
        Stop-Release 'Release.ThirdPartyNoticeInvalid'
    }
    $licenseSet=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $LicenseRelativePaths) {
        Assert-ReleaseName $path
        if (-not $path.StartsWith('THIRD-PARTY-LICENSES/',[StringComparison]::Ordinal)) { Stop-Release 'Release.ThirdPartyLicensePathInvalid' }
        [void]$licenseSet.Add($path.Substring('THIRD-PARTY-LICENSES/'.Length))
    }
    if (-not $licenseSet.Contains('README.md')) { Stop-Release 'Release.ThirdPartyLicenseProvenanceMissing' }
    $lines=$notice -split "`r?`n"
    $packageEntries=@($dependencies.libraries.GetEnumerator() | Where-Object { $_.Value.type -ceq 'package' } | Sort-Object Name)
    $packageRows=@($lines | Where-Object { $_.StartsWith('| [',[StringComparison]::Ordinal) })
    if ($packageRows.Count -ne $packageEntries.Count) { Stop-Release 'Release.ThirdPartyPackageInventoryMismatch' }
    foreach ($entry in $packageEntries) {
        $parts=$entry.Name.Split('/')
        if ($parts.Count -ne 2) { Stop-Release 'Release.ThirdPartyPackageIdentityInvalid' }
        $label="$($parts[0])/$($parts[1])"
        $matchingRows=@($packageRows | Where-Object { $_.StartsWith("| [$label](",[StringComparison]::Ordinal) })
        if ($matchingRows.Count -ne 1) { Stop-Release 'Release.ThirdPartyPackageNoticeMissing' }
        $licenseReference=[regex]::Match($matchingRows[0], '\]\(THIRD-PARTY-LICENSES/([A-Za-z0-9_.-]+)\)')
        if (-not $licenseReference.Success -or -not $licenseSet.Contains($licenseReference.Groups[1].Value)) { Stop-Release 'Release.ThirdPartyLicenseTextMissing' }
    }
    $sni=@($packageEntries | Where-Object { $_.Name -ceq 'Microsoft.Data.SqlClient.SNI.runtime/7.1.0' })
    if ($sni.Count -and ($notice -notmatch 'Microsoft.Data.SqlClient.SNI.runtime' -or $notice -notmatch 'Microsoft.Data.SqlClient.SNI.dll' -or $notice -notmatch 'SHA-256')) {
        Stop-Release 'Release.SniNoticeMissing'
    }
    $hangfire=@($packageEntries | Where-Object { $_.Name -cmatch '^Hangfire\.' })
    if ($hangfire.Count -and (-not $licenseSet.Contains('Hangfire-1.8.25-COPYING') -or -not $licenseSet.Contains('Hangfire-1.8.25-COPYING.LESSER') -or
        $notice -notmatch 'vendored Hangfire SQL installer')) { Stop-Release 'Release.HangfireLicenseNoticeMissing' }
}
function Assert-PortalPublish([string] $PublishRoot,[string] $ReleaseId,[string] $SourceCommit) {
    $PublishRoot=Assert-ReleasePath $PublishRoot
    $files=@(Get-ChildItem -LiteralPath $PublishRoot -Recurse -File -Force)
    if (-not $files.Count) { Stop-Release 'Release.EmptyPublish' }
    foreach ($file in $files) {
        Assert-PortalContentName ([IO.Path]::GetRelativePath($PublishRoot,$file.FullName).Replace('\','/'))
        $null=Assert-ReleasePath $file.FullName
    }
    $dll=Join-Path $PublishRoot 'SsrsPortal.dll'
    Assert-CompiledReleaseIdentity (Read-CompiledReleaseIdentity ([IO.File]::ReadAllBytes($dll))) $ReleaseId $SourceCommit
    $dependenciesBytes=[IO.File]::ReadAllBytes((Join-Path $PublishRoot 'SsrsPortal.deps.json'))
    $dependencies=Read-ReleaseJson $dependenciesBytes
    Assert-PortalDependencies $dependencies
    foreach ($name in @('LICENSE','COPYRIGHT.md','BINARY-DISTRIBUTION-NOTICE.md')) {
        $source=Join-Path $script:ReleaseRepositoryRoot $name; $published=Join-Path $PublishRoot $name
        if (-not [IO.File]::Exists($source) -or -not [IO.File]::Exists($published) -or (Get-ReleaseHash $source) -cne (Get-ReleaseHash $published)) { Stop-Release 'Release.ProjectLicenseFileMismatch' }
    }
    $exampleSource=Join-Path $script:ReleaseRepositoryRoot 'src/SsrsPortal/application.Example.json'
    $examplePublished=Join-Path $PublishRoot 'application.Example.json'
    if (-not [IO.File]::Exists($examplePublished) -or (Get-ReleaseHash $examplePublished) -cne (Get-ReleaseHash $exampleSource)) {
        Stop-Release 'Release.ConfigurationExampleMismatch'
    }
    $noticePath=Join-Path $PublishRoot 'THIRD-PARTY-NOTICES.md'; $licensesPath=Join-Path $PublishRoot 'THIRD-PARTY-LICENSES'
    if (-not [IO.File]::Exists($noticePath) -or -not [IO.Directory]::Exists($licensesPath)) { Stop-Release 'Release.ThirdPartyNoticeMissing' }
    $licensePaths=@(Get-ChildItem -LiteralPath $licensesPath -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($PublishRoot,$_.FullName).Replace('\','/') })
    Assert-ThirdPartyNoticeInventory $dependenciesBytes ([IO.File]::ReadAllBytes($noticePath)) $licensePaths
    # Shared toolkit performs authoritative XML/runtime/ZIP inventory validation.
    Assert-ReleasePublishShutdownTimeLimit $PublishRoot
}
function Assert-PortalReleaseArchive([string] $ArchivePath,[string] $ReleaseId,[string] $SourceCommit) {
    $zip=[IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $manifestEntries=@($zip.Entries | Where-Object FullName -CEQ 'manifest.json')
        $dllEntries=@($zip.Entries | Where-Object FullName -CEQ 'application/SsrsPortal.dll')
        if ($manifestEntries.Count -ne 1 -or $dllEntries.Count -ne 1) { Stop-Release 'Release.InventoryMismatch' }
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.StartsWith('application/',[StringComparison]::Ordinal)) { Assert-PortalContentName $entry.FullName.Substring(12) }
        }
        $manifest=Read-ReleaseJson (Read-ReleaseEntry $manifestEntries[0] 4194304)
        if ($manifest.applicationId -cne 'ssrs-web' -or $manifest.releaseId -cne $ReleaseId -or $manifest.source.commit -cne $SourceCommit -or
            $manifest.source.repository -cne 'clcdpc/ssrs-web' -or $manifest.runtime.framework -cne 'net10.0' -or
            $manifest.runtime.rid -cne 'win-x64' -or $manifest.runtime.architecture -cne 'x64' -or
            $manifest.runtime.hostingModel -cne 'inprocess' -or $manifest.runtime.workerLimit -ne 1 -or
            $manifest.validation.entryPoint -cne 'SsrsPortal.dll' -or $manifest.validation.contractVersion -ne 1 -or $manifest.readinessContractVersion -ne 1) { Stop-Release 'Release.ManifestMismatch' }
        Assert-CompiledReleaseIdentity (Read-CompiledReleaseIdentity (Read-ReleaseEntry $dllEntries[0])) $ReleaseId $SourceCommit
        $dependencyBytes=Read-ReleaseEntry ($zip.GetEntry('application/SsrsPortal.deps.json')) 4194304
        Assert-PortalDependencies (Read-ReleaseJson $dependencyBytes)
        foreach ($name in @('LICENSE','COPYRIGHT.md','BINARY-DISTRIBUTION-NOTICE.md')) {
            $entry=$zip.GetEntry("application/$name"); $source=Join-Path $script:ReleaseRepositoryRoot $name
            if ($null -eq $entry -or -not [IO.File]::Exists($source) -or (Get-ReleaseBytesHash (Read-ReleaseEntry $entry)) -cne (Get-ReleaseHash $source)) { Stop-Release 'Release.ProjectLicenseFileMismatch' }
        }
        $exampleEntry=$zip.GetEntry('application/application.Example.json')
        $exampleSource=Join-Path $script:ReleaseRepositoryRoot 'src/SsrsPortal/application.Example.json'
        if ($null -eq $exampleEntry -or (Get-ReleaseBytesHash (Read-ReleaseEntry $exampleEntry 4194304)) -cne (Get-ReleaseHash $exampleSource)) {
            Stop-Release 'Release.ConfigurationExampleMismatch'
        }
        $noticeEntry=$zip.GetEntry('application/THIRD-PARTY-NOTICES.md')
        $licenseEntries=@($zip.Entries | Where-Object { $_.FullName.StartsWith('application/THIRD-PARTY-LICENSES/',[StringComparison]::Ordinal) })
        if ($null -eq $noticeEntry -or -not $licenseEntries.Count -or @($licenseEntries | Where-Object { $_.Length -le 0 }).Count) { Stop-Release 'Release.ThirdPartyNoticeMissing' }
        $licensePaths=@($licenseEntries | ForEach-Object { $_.FullName.Substring('application/'.Length) })
        Assert-ThirdPartyNoticeInventory $dependencyBytes (Read-ReleaseEntry $noticeEntry) $licensePaths
        return $manifest
    } finally { $zip.Dispose() }
}
