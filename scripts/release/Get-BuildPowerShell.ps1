#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string] $Destination)
# Public upstream archive pin independently recorded by merged clc-deployment
# tools/dependencies.lock.json at d45e305; this authenticates execution, not a host install.
$ErrorActionPreference='Stop'
$Destination=[IO.Path]::GetFullPath($Destination)
if (-not $IsWindows -or [IO.Path]::Exists($Destination) -or -not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($Destination))) { throw 'Release.NewWindowsBuildToolDirectoryRequired' }
$archive=$Destination+'.zip'
$client=[Net.Http.HttpClient]::new(); $deadline=[Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(5)); $response=$null; $source=$null; $output=$null
try {
    $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,'https://github.com/PowerShell/PowerShell/releases/download/v7.6.6/PowerShell-7.6.6-win-x64.zip')
    $response=$client.Send($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$deadline.Token)
    if (-not $response.IsSuccessStatusCode -or $response.Content.Headers.ContentLength -gt 536870912) { throw 'Release.BuildToolDownloadFailed' }
    $source=$response.Content.ReadAsStream(); $output=[IO.FileStream]::new($archive,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $buffer=[byte[]]::new(65536); $total=0L
    while (($read=$source.ReadAsync($buffer,0,$buffer.Length,$deadline.Token).GetAwaiter().GetResult()) -gt 0) {
        $total+=$read; if ($total -gt 536870912) { throw 'Release.BuildToolDownloadBound' }; $output.Write($buffer,0,$read)
    }
    $output.Flush($true); $output.Dispose(); $output=$null
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -cne '02fe458be20493fbdf43f61ea20610b811ee6c738ab1676c61b9cfcd1a33c860') { throw 'Release.BuildToolPinMismatch' }
    [IO.Compression.ZipFile]::ExtractToDirectory($archive,$Destination,$false)
    if ($env:GITHUB_PATH) { $Destination | Out-File -LiteralPath $env:GITHUB_PATH -Encoding utf8 -Append }
    Write-Output 'Authenticated job-local PowerShell7.6.6 is ready.'
} catch { throw 'Release.BuildToolAcquisitionFailed' }
finally { if ($output) { $output.Dispose() }; if ($source) { $source.Dispose() }; if ($response) { $response.Dispose() }; if ($request) { $request.Dispose() }; $client.Dispose(); $deadline.Dispose() }
