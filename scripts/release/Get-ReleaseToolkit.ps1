#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $LockPath,
    [Parameter(Mandatory)][string] $Destination,
    [string] $ArchivePath,
    [string] $ReadToken
)
. "$PSScriptRoot/Release.Common.ps1"
$lock=Read-ReleaseJson ([IO.File]::ReadAllBytes((Assert-ReleasePath $LockPath)))
if ($lock.schemaVersion -ne 1 -or $lock.repository -cne 'clcdpc/clc-deployment' -or
    $lock.sourceCommit -cnotmatch '^[a-f0-9]{40}$' -or $lock.version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+-[a-z0-9.-]+$') { Stop-Release 'Release.ToolkitLockInvalid' }
if ($lock.distributionSha256 -cnotmatch '^[a-f0-9]{64}$' -or $lock.moduleInventorySha256 -cnotmatch '^[a-f0-9]{64}$') {
    Stop-Release 'Release.Prerequisite.IndependentToolkitDistributionPinRequired'
}
$Destination=Assert-ReleasePath $Destination
if ([IO.Path]::Exists($Destination) -or -not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($Destination))) { Stop-Release 'Release.ToolkitDestinationMustBeNew' }
if (-not $ArchivePath) {
    $uri=$null
    if (-not [Uri]::TryCreate($lock.distributionUri,[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -cne 'https' -or
        $uri.Host -cne 'api.github.com' -or $uri.Port -ne 443 -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
        $uri.AbsolutePath -cnotmatch '^/repos/clcdpc/clc-deployment/releases/assets/[1-9][0-9]*$') { Stop-Release 'Release.ToolkitAssetUriInvalid' }
    $ArchivePath=$Destination+'.download.zip'
    $handler=[Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect=$false
    $client=[Net.Http.HttpClient]::new($handler); $client.Timeout=[TimeSpan]::FromMinutes(5)
    $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,$uri)
    $request.Headers.UserAgent.ParseAdd('ssrs-web-release/1'); $request.Headers.Accept.ParseAdd('application/octet-stream')
    if ($ReadToken) { $request.Headers.Authorization=[Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$ReadToken) }
    $response=$null; $output=$null; $source=$null; $deadline=[Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(5))
    try {
        $response=$client.Send($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$deadline.Token)
        if ([int]$response.StatusCode -in @(302,303,307,308)) {
            $redirect=$response.Headers.Location
            if ($null -eq $redirect -or -not $redirect.IsAbsoluteUri -or $redirect.Scheme -cne 'https' -or
                $redirect.Host -cne 'release-assets.githubusercontent.com' -or $redirect.Port -ne 443 -or $redirect.UserInfo -or $redirect.Fragment) { Stop-Release 'Release.ToolkitAssetRedirectInvalid' }
            $response.Dispose(); $request.Dispose()
            $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,$redirect)
            $request.Headers.UserAgent.ParseAdd('ssrs-web-release/1')
            # Never forward repository credentials to the asset/CDN origin.
            $response=$client.Send($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$deadline.Token)
        }
        if (-not $response.IsSuccessStatusCode -or $response.Content.Headers.ContentLength -gt 1073741824) { Stop-Release 'Release.ToolkitDownloadFailed' }
        $source=$response.Content.ReadAsStream(); $output=[IO.FileStream]::new($ArchivePath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        $buffer=[byte[]]::new(65536); $total=0L; $timer=[Diagnostics.Stopwatch]::StartNew()
        while (($read=$source.ReadAsync($buffer,0,$buffer.Length,$deadline.Token).GetAwaiter().GetResult()) -gt 0) {
            $total+=$read
            if ($total -gt 1073741824 -or $timer.Elapsed.TotalMinutes -gt 5) { Stop-Release 'Release.ToolkitDownloadBound' }
            $output.Write($buffer,0,$read)
        }
        $output.Flush($true)
    } catch { Stop-Release 'Release.ToolkitDownloadFailed' }
    finally {
        if ($output) { $output.Dispose() }; if ($source) { $source.Dispose() }; if ($response) { $response.Dispose() }
        $request.Dispose(); $client.Dispose(); $handler.Dispose(); $deadline.Dispose()
    }
}
$ArchivePath=Assert-ReleasePath $ArchivePath
$stream=[IO.FileStream]::new($ArchivePath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read); $zip=$null
try {
    # An independent whole-archive pin authenticates every executable byte before
    # the archive's verifier or module is ever evaluated.
    if ($stream.Length -gt 1073741824 -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() -cne $lock.distributionSha256) { Stop-Release 'Release.ToolkitArchivePinMismatch' }
    $stream.Position=0; $zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Read,$true)
    if ($zip.Entries.Count -gt 20000) { Stop-Release 'Release.ToolkitArchiveBound' }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase); $total=0L
    foreach ($entry in $zip.Entries) {
        Assert-ReleaseName $entry.FullName
        if (-not $seen.Add($entry.FullName) -or $entry.ExternalAttributes -ne 0 -or
            $entry.FullName -cnotmatch '^(CLC\.Deployment/[A-Za-z0-9_./-]+|tools/[A-Za-z0-9_./-]+|distribution\.json|source\.commit)$') { Stop-Release 'Release.ToolkitArchiveShape' }
        $total+=$entry.Length
        if ($entry.Length -gt 268435456 -or $total -gt 1073741824) { Stop-Release 'Release.ToolkitArchiveBound' }
    }
    [void][IO.Directory]::CreateDirectory($Destination)
    foreach ($entry in $zip.Entries) {
        $target=Join-Path $Destination $entry.FullName
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        $bytes=Read-ReleaseEntry $entry
        $file=[IO.FileStream]::new($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $file.Write($bytes); $file.Flush($true) } finally { $file.Dispose() }
    }
    $stream.Position=0
    $retained=[IO.FileStream]::new((Join-Path $Destination 'distribution.zip'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.CopyTo($retained); $retained.Flush($true) } finally { $retained.Dispose() }
    $moduleRoot=Join-Path $Destination 'CLC.Deployment'
    $checked=& (Join-Path $Destination 'tools/Test-ToolkitDistribution.ps1') -ArchivePath (Join-Path $Destination 'distribution.zip') -ExpectedSha256 $lock.distributionSha256 -ExpectedSourceCommit $lock.sourceCommit -ExpectedVersion $lock.version -InstalledModuleRoot $moduleRoot -RequireInfrastructureTools
    # The installed toolkit uses the existing infrastructure distribution format.
    if (-not $checked.integrityVerified -or $checked.formatVersion -ne 2 -or $checked.inventorySha256 -cne $lock.moduleInventorySha256) { Stop-Release 'Release.ToolkitInventoryPinMismatch' }
    [pscustomobject]@{moduleManifest=(Join-Path $moduleRoot 'CLC.Deployment.psd1');version=$lock.version;sourceCommit=$lock.sourceCommit;
        distributionSha256=$lock.distributionSha256;moduleInventorySha256=$lock.moduleInventorySha256;executionPinVerified=$true;
        hostInstallationAuthorized=$false;applicationDeploymentAuthorized=$false}
} finally { if ($zip) { $zip.Dispose() }; $stream.Dispose() }
