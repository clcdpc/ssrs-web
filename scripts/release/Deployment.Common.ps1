#Requires -Version 7.0
# Shared manual/automatic deployment safeguards. Load only from the trusted workflow commit.
$script:SsrsTargetId='ssrs-web-dev'
$script:SsrsEnvironmentName='DEV'
# SSRS-DEV-FUNCTIONS-BEGIN
function Stop-Ssrs([string] $Code) { throw [IO.InvalidDataException]::new($Code) }
function Assert-SsrsSelector([string] $Id) { if ($Id -cnotmatch '^[a-z0-9][a-z0-9-]{0,63}$') { Stop-Ssrs 'Ssrs.InvalidSelector' } }
function Assert-SsrsContext {
    if ($env:GITHUB_ACTIONS -cne 'true' -or $env:GITHUB_REPOSITORY -cne 'clcdpc/ssrs-web' -or $env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or
        $env:GITHUB_REF -cne 'refs/heads/master' -or $env:GITHUB_REF_PROTECTED -cne 'true' -or
        $env:GITHUB_WORKFLOW_REF -cne 'clcdpc/ssrs-web/.github/workflows/deploy-dev.yml@refs/heads/master' -or
        $env:GITHUB_WORKFLOW_SHA -cne $env:GITHUB_SHA -or $env:GITHUB_SHA -cnotmatch '^[a-f0-9]{40}$' -or
        $env:SSRS_DEV_ENABLED -cne 'true' -or $env:SSRS_EXTERNAL_RUNNER_ADMISSION -cne 'true') { Stop-Ssrs 'Ssrs.TrustedWorkflowRequired' }
    if (-not $IsWindows -or $PSVersionTable.PSVersion.ToString() -cne '7.6.6') { Stop-Ssrs 'Ssrs.Prerequisite.PinnedWindowsPowerShellRequired' }
    Assert-SsrsSelector $env:SSRS_AUTHORIZATION_ID; Assert-SsrsSelector $env:SSRS_RELEASE_APPROVAL_ID
    if ($env:SSRS_FIRST_CUTOVER -cnotin @('true','false')) { Stop-Ssrs 'Ssrs.InvalidCutoverSelection' }
}
function Read-SsrsJson([byte[]] $Bytes) {
    if ($Bytes.Length -eq 0 -or $Bytes.Length -gt 4194304) { Stop-Ssrs 'Ssrs.JsonBound' }
    $text=[Text.UTF8Encoding]::new($false,$true).GetString($Bytes)
    $doc=[Text.Json.JsonDocument]::Parse($text,[Text.Json.JsonDocumentOptions]@{MaxDepth=32})
    function Test-SsrsElement($Element) {
        if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
            $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($property in $Element.EnumerateObject()) { if (-not $names.Add($property.Name)) { Stop-Ssrs 'Ssrs.DuplicateJson' }; Test-SsrsElement $property.Value }
        } elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) { foreach ($item in $Element.EnumerateArray()) { Test-SsrsElement $item } }
    }
    try { Test-SsrsElement $doc.RootElement } finally { $doc.Dispose() }
    return ($text | ConvertFrom-Json -AsHashtable -Depth 32)
}
function Initialize-SsrsNative {
    if ('SsrsWorkflow.Native' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace SsrsWorkflow {
 public static class Native {
  [StructLayout(LayoutKind.Sequential)] struct Info { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation,Access,Write; public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow; }
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern SafeFileHandle CreateFileW(string p,uint access,uint share,IntPtr sa,uint disposition,uint flags,IntPtr template);
  [DllImport("kernel32.dll",SetLastError=true),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern bool GetFileInformationByHandle(SafeFileHandle h,out Info i);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern uint GetFinalPathNameByHandleW(SafeFileHandle h,StringBuilder b,uint n,uint flags);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern uint GetSystemWindowsDirectoryW(StringBuilder b,uint n);
  [DllImport("shell32.dll"),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern int SHGetKnownFolderPath(ref Guid id,uint flags,IntPtr token,out IntPtr path);
  public static string ProgramData() {
   var b=new StringBuilder(32768); var n=GetSystemWindowsDirectoryW(b,(uint)b.Capacity); if(n==0 || n>=b.Capacity) throw new IOException();
   string drive=Path.GetPathRoot(b.ToString())!.TrimEnd('\\'); string inherited=Environment.GetEnvironmentVariable("SystemDrive")??"";
   if(inherited.Length!=0 && !String.Equals(inherited.TrimEnd('\\'),drive,StringComparison.OrdinalIgnoreCase)) throw new IOException();
   var id=new Guid("62AB5D82-FDC1-4DC3-A9DD-070D1D495D97"); IntPtr p=IntPtr.Zero;
   try { if(SHGetKnownFolderPath(ref id,0x4000,IntPtr.Zero,out p)!=0) throw new IOException(); string path=Marshal.PtrToStringUni(p)??"";
    if(path.StartsWith("%SystemDrive%\\",StringComparison.OrdinalIgnoreCase)) path=drive+path.Substring(13);
    if(!Path.IsPathFullyQualified(path) || path.Contains('%')) throw new IOException(); return Path.GetFullPath(path).TrimEnd('\\');
   } finally { if(p!=IntPtr.Zero) Marshal.FreeCoTaskMem(p); }
  }
  public static SafeFileHandle Pin(string path,bool directory) {
   var h=CreateFileW(path,0x80000000,directory?3u:1u,IntPtr.Zero,3,0x00200000u|(directory?0x02000000u:0),IntPtr.Zero);
   try { Info i; if(h.IsInvalid || !GetFileInformationByHandle(h,out i) || (i.Attributes&0x400)!=0 || (!directory && i.Links!=1) || ((i.Attributes&0x10)!=0)!=directory) throw new IOException();
    var b=new StringBuilder(32768); uint n=GetFinalPathNameByHandleW(h,b,(uint)b.Capacity,0); if(n==0 || n>=b.Capacity || !String.Equals(b.ToString(),"\\\\?\\"+path,StringComparison.OrdinalIgnoreCase)) throw new IOException(); return h;
   } catch { h.Dispose(); throw; }
  }
 }
}
'@
}
function Assert-SsrsAcl([string] $Path,[bool] $Ancestor=$false) {
    $acl=Get-Acl -LiteralPath $Path -ErrorAction Stop
    if (-not $acl.AreAccessRulesCanonical) { Stop-Ssrs 'Ssrs.UnprotectedBootstrap' }
    $raw=[Security.AccessControl.RawSecurityDescriptor]::new($acl.GetSecurityDescriptorBinaryForm(),0)
    Assert-SsrsSecurityDescriptor $raw $Ancestor
}
function Assert-SsrsSecurityDescriptor($raw,[bool] $Ancestor=$false) {
    $trusted=@('S-1-5-18','S-1-5-32-544')
    if ($Ancestor) { $trusted+='S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464' }
    if ($null -eq $raw.DiscretionaryAcl -or $raw.Owner.Value -cnotin $trusted) { Stop-Ssrs 'Ssrs.UnprotectedBootstrap' }
    $mask=if ($Ancestor) {0x000D0040L} else {0x000D0156L}
    foreach ($ace in $raw.DiscretionaryAcl) {
        if ($ace -isnot [Security.AccessControl.CommonAce] -or $ace.IsCallback) { Stop-Ssrs 'Ssrs.UnprotectedBootstrap' }
        if ($ace.AceQualifier -eq [Security.AccessControl.AceQualifier]::AccessAllowed -and
            (-not $Ancestor -or ($ace.AceFlags -band [Security.AccessControl.AceFlags]::InheritOnly) -eq 0) -and
            (([long]$ace.AccessMask -band $mask) -ne 0 -or ([long]$ace.AccessMask -band 0x50000000L) -ne 0) -and $ace.SecurityIdentifier.Value -cnotin $trusted) { Stop-Ssrs 'Ssrs.UnprotectedBootstrap' }
    }
}
function Open-SsrsProtected([string] $Path,$Leases,[bool] $Directory=$false,[long] $Maximum=4194304) {
    if ($Path -cnotmatch '^[A-Za-z]:\\' -or [IO.Path]::GetFullPath($Path) -cne $Path -or $Path -match '(^|\\)\.{1,2}(\\|$)') { Stop-Ssrs 'Ssrs.UnsafeBootstrapPath' }
    $drive=[IO.DriveInfo]::new([IO.Path]::GetPathRoot($Path))
    if ($drive.DriveType -ne [IO.DriveType]::Fixed -or $drive.DriveFormat -cne 'NTFS') { Stop-Ssrs 'Ssrs.UnsafeBootstrapPath' }
    $cursor=[IO.Path]::GetPathRoot($Path)
    foreach ($part in $Path.Substring(3).Split('\')) {
        $cursor=Join-Path $cursor $part; $leaf=$cursor -ceq $Path
        $handle=[SsrsWorkflow.Native]::Pin($cursor,(-not $leaf -or $Directory)); $Leases.Add($handle)
        Assert-SsrsAcl $cursor (-not $leaf)
    }
    if ($Directory) { return }
    $stream=[IO.FileStream]::new($handle,[IO.FileAccess]::Read); $Leases.Add($stream)
    if ($stream.Length -gt $Maximum) { Stop-Ssrs 'Ssrs.BootstrapBound' }
    return $stream
}
function Get-SsrsStreamHash($Stream) { $Stream.Position=0; $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Stream)).ToLowerInvariant(); $Stream.Position=0; return $hash }
function Read-SsrsStream($Stream,[long] $Maximum=4194304) {
    if ($Stream.Length -gt $Maximum) { Stop-Ssrs 'Ssrs.InputBound' }
    $Stream.Position=0; $memory=[IO.MemoryStream]::new()
    try { $Stream.CopyTo($memory); if ($memory.Length -gt $Maximum) { Stop-Ssrs 'Ssrs.InputBound' }; return ,$memory.ToArray() }
    finally { $memory.Dispose(); $Stream.Position=0 }
}
function Get-SsrsInventoryHash($Files) {
    $names=[string[]]@($Files.path); [Array]::Sort($names,[StringComparer]::Ordinal); $lookup=@{}
    foreach ($file in $Files) { if ($lookup.ContainsKey($file.path)) { Stop-Ssrs 'Ssrs.BootstrapInventoryMismatch' }; $lookup[$file.path]=$file }
    $hash=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    try { foreach ($name in $names) { $f=$lookup[$name]; $hash.AppendData([Text.Encoding]::UTF8.GetBytes($name+[char]0+([long]$f.length).ToString([Globalization.CultureInfo]::InvariantCulture)+[char]0+$f.sha256+"`n")) }; return [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant() } finally { $hash.Dispose() }
}
function Get-SsrsProgramData { Initialize-SsrsNative; return [SsrsWorkflow.Native]::ProgramData() }
function Assert-SsrsProgramDataEnvironment([string] $Root) { if ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData) -cne $Root) { Stop-Ssrs 'Ssrs.Prerequisite.ProgramDataEnvironment' } }
function Assert-SsrsBootstrapEnvelope($Authorization,[string] $AuthorizationId) {
    if ($Authorization -isnot [Collections.IDictionary] -or $Authorization.bootstrap -isnot [Collections.IDictionary] -or
        $Authorization.hostPolicy -isnot [Collections.IDictionary] -or $Authorization.hostPolicy.activeToolkit -isnot [Collections.IDictionary]) { Stop-Ssrs 'Ssrs.IndependentHandoffInvalid' }
    if ($Authorization.contractFamily -isnot [string] -or $Authorization.contractFamily -cne 'clc-host-enrollment-v1' -or
        $Authorization.recordKind -isnot [string] -or $Authorization.recordKind -cne 'host-enrollment-authorization' -or
        $Authorization.schemaVersion -isnot [long] -or $Authorization.schemaVersion -ne 1 -or
        $Authorization.authorizationId -isnot [string] -or $Authorization.authorizationId -cne $AuthorizationId -or
        $Authorization.targetId -isnot [string] -or $Authorization.targetId -cne $script:SsrsTargetId -or
        $Authorization.bootstrap.sourceCommit -isnot [string] -or $Authorization.bootstrap.sourceCommit -cnotmatch '^[a-f0-9]{40}$' -or
        $Authorization.bootstrap.inventorySha256 -isnot [string] -or $Authorization.bootstrap.inventorySha256 -cnotmatch '^[a-f0-9]{64}$' -or
        $Authorization.hostPolicy.activeToolkit.distributionSha256 -isnot [string] -or $Authorization.hostPolicy.activeToolkit.distributionSha256 -cnotmatch '^[a-f0-9]{64}$') { Stop-Ssrs 'Ssrs.IndependentHandoffInvalid' }
}
function Open-SsrsBootstrap([string] $AuthorizationId) {
    Assert-SsrsSelector $AuthorizationId
    Initialize-SsrsNative
    $leases=[Collections.Generic.List[IDisposable]]::new()
    try {
        $programData=Get-SsrsProgramData
        Assert-SsrsProgramDataEnvironment $programData
        $authority=Join-Path $programData 'CLC\Deployment\host-authorizations'
        if (-not [IO.File]::Exists((Join-Path $authority ($AuthorizationId+'.json')))) { Stop-Ssrs 'Ssrs.Prerequisite.IndependentHostAuthorizationRequired' }
        $stream=Open-SsrsProtected (Join-Path $authority ($AuthorizationId+'.json')) $leases
        $auth=Read-SsrsJson (Read-SsrsStream $stream)
        Assert-SsrsBootstrapEnvelope $auth $AuthorizationId
        $root=Join-Path $programData 'CLC\Deployment\Bootstrap\ssrs-web'
        if (-not [IO.Directory]::Exists($root) -or -not [IO.File]::Exists((Join-Path $root 'distribution.zip'))) { Stop-Ssrs 'Ssrs.Prerequisite.IndependentBootstrapRequired' }
        $files=[Collections.Generic.List[object]]::new(); $queue=[Collections.Generic.Queue[string]]::new(); $queue.Enqueue($root)
        $count=0; $total=0L; $clock=[Diagnostics.Stopwatch]::StartNew()
        while ($queue.Count) {
            $directory=$queue.Dequeue(); Open-SsrsProtected $directory $leases $true
            foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
                $count++; if ($count -gt 20000 -or $clock.Elapsed.TotalSeconds -gt 300) { Stop-Ssrs 'Ssrs.BootstrapBound' }
                $relative=$path.Substring($root.Length+1).Replace('\','/')
                $isDirectory=([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::Directory) -ne 0
                if ($isDirectory) {
                    if ($relative -cnotmatch '^(CLC\.Deployment|CLC\.Deployment/(Private|Public)|tools|tools/host)$') { Stop-Ssrs 'Ssrs.BootstrapInventoryMismatch' }
                    $queue.Enqueue($path); continue
                }
                if ($relative -cin @('source.commit','distribution.zip','distribution.json')) { continue }
                if ($relative -cnotmatch '^(CLC\.Deployment/(CLC\.Deployment\.ps[dm]1|(Private|Public)/[A-Za-z0-9.-]+\.ps1)|tools/(Distribution.Common|Test-ToolkitDistribution|Expand-ToolkitDistribution)\.ps1|tools/host/(Get-ClcHostEnrollment|Initialize-ClcHostEnrollment|HostEnrollment.Common|HostEnrollment.Iis|HostEnrollment.Resources)\.ps1)$') { Stop-Ssrs 'Ssrs.BootstrapInventoryMismatch' }
                $file=Open-SsrsProtected $path $leases $false 268435456; $total+=$file.Length
                if ($total -gt 1073741824) { Stop-Ssrs 'Ssrs.BootstrapBound' }
                $files.Add(@{path=$relative;length=$file.Length;sha256=(Get-SsrsStreamHash $file)})
            }
        }
        if ((Get-SsrsInventoryHash $files) -cne $auth.bootstrap.inventorySha256) { Stop-Ssrs 'Ssrs.BootstrapInventoryMismatch' }
        $source=Open-SsrsProtected (Join-Path $root 'source.commit') $leases $false 40
        if ([Text.Encoding]::ASCII.GetString((Read-SsrsStream $source 40)) -cne $auth.bootstrap.sourceCommit) { Stop-Ssrs 'Ssrs.BootstrapSourceMismatch' }
        $distribution=Join-Path $root 'distribution.zip'; $archive=Open-SsrsProtected $distribution $leases $false 1073741824
        if ((Get-SsrsStreamHash $archive) -cne $auth.hostPolicy.activeToolkit.distributionSha256) { Stop-Ssrs 'Ssrs.ApprovedDistributionMismatch' }
        return @{root=$root;programData=$programData;authorization=$auth;leases=$leases;distribution=$distribution;configurationPath=(Join-Path $programData 'clc-ssrs-web\Config\deployment.json')}
    } catch { foreach ($lease in $leases) { $lease.Dispose() }; throw }
}
function Test-SsrsForbiddenEnvironmentName([string] $Name) {
    $keys=@($Name.Replace('__',':'))
    foreach ($prefix in @('DOTNET_','ASPNETCORE_')) { if ($Name.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { $keys+=$Name.Substring($prefix.Length).Replace('__',':') } }
    foreach ($key in $keys) { foreach ($forbidden in @('SsrsPortal:TestConfigurationFile','Portal:ApprovedReports','Entra:TenantId','Entra:GroupId')) {
        if ($key.Equals($forbidden,[StringComparison]::OrdinalIgnoreCase) -or $key.StartsWith($forbidden+':',[StringComparison]::OrdinalIgnoreCase)) { return $true }
    } }; return $false
}
function Assert-SsrsEnvironmentNames($Names) { foreach ($name in $Names) { if (Test-SsrsForbiddenEnvironmentName $name) { Stop-Ssrs 'Ssrs.Prerequisite.RemoveUnsupportedWorkerEnvironment' } } }
function Read-SsrsXml([byte[]] $Bytes) {
    if ($Bytes.Length -gt 4194304) { Stop-Ssrs 'Ssrs.IisConfigurationBound' }
    $settings=[Xml.XmlReaderSettings]::new(); $settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit; $settings.XmlResolver=$null; $settings.MaxCharactersInDocument=4194304
    $memory=[IO.MemoryStream]::new($Bytes,$false); $reader=[Xml.XmlReader]::Create($memory,$settings)
    try { $doc=[Xml.XmlDocument]::new(); $doc.XmlResolver=$null; $doc.Load($reader); return ,$doc } finally { $reader.Dispose(); $memory.Dispose() }
}
function Assert-SsrsInheritedGrace($Document,[string] $Site) {
    if ($Document.DocumentElement.Name -cne 'configuration' -or $Document.DocumentElement.NamespaceURI -or $Document.SelectNodes('//*[@configSource]').Count) { Stop-Ssrs 'Ssrs.Prerequisite.UnsupportedIisConfigurationLayout' }
    $locations=@($Document.SelectNodes('/configuration/location') | Where-Object { $_.GetAttribute('path').TrimEnd('/').Equals($Site,[StringComparison]::OrdinalIgnoreCase) })
    if ($locations.Count -ne 1 -or $locations[0].GetAttribute('path') -cne $Site -or $locations[0].GetAttribute('overrideMode') -cne 'Allow') { Stop-Ssrs 'Ssrs.Prerequisite.InheritedAncmGrace' }
    $nodes=@($locations[0].SelectNodes('system.webServer/aspNetCore'))
    if ($nodes.Count -ne 1 -or $nodes[0].GetAttribute('shutdownTimeLimit') -cne '60' -or $nodes[0].GetAttribute('lockAttributes') -cne 'shutdownTimeLimit') { Stop-Ssrs 'Ssrs.Prerequisite.InheritedAncmGrace' }
}
function Test-SsrsApplicationSubscriptions([byte[]] $Bytes) {
    # Only extract the hosting prerequisite. Full options/configuration policy
    # remains the compiled application's validator. Match its bounded BOM-aware
    # decoder and JSON-provider syntax without relaxing any host/approval JSON.
    $document=$null
    try {
        if ($Bytes.Length -eq 0 -or $Bytes.Length -gt 4194304) { Stop-Ssrs 'Ssrs.Prerequisite.InvalidApplicationSnapshot' }
        $offset=0; $encoding=[Text.UTF8Encoding]::new($false,$true)
        if ($Bytes.Length -ge 4 -and $Bytes[0] -eq 0 -and $Bytes[1] -eq 0 -and $Bytes[2] -eq 254 -and $Bytes[3] -eq 255) { $encoding=[Text.UTF32Encoding]::new($true,$false,$true); $offset=4 }
        elseif ($Bytes.Length -ge 4 -and $Bytes[0] -eq 255 -and $Bytes[1] -eq 254 -and $Bytes[2] -eq 0 -and $Bytes[3] -eq 0) { $encoding=[Text.UTF32Encoding]::new($false,$false,$true); $offset=4 }
        elseif ($Bytes.Length -ge 3 -and $Bytes[0] -eq 239 -and $Bytes[1] -eq 187 -and $Bytes[2] -eq 191) { $offset=3 }
        elseif ($Bytes.Length -ge 2 -and $Bytes[0] -eq 254 -and $Bytes[1] -eq 255) { $encoding=[Text.UnicodeEncoding]::new($true,$false,$true); $offset=2 }
        elseif ($Bytes.Length -ge 2 -and $Bytes[0] -eq 255 -and $Bytes[1] -eq 254) { $encoding=[Text.UnicodeEncoding]::new($false,$false,$true); $offset=2 }
        $text=$encoding.GetString($Bytes,$offset,$Bytes.Length-$offset)
        $document=[Text.Json.JsonDocument]::Parse($text,[Text.Json.JsonDocumentOptions]@{MaxDepth=32;CommentHandling=[Text.Json.JsonCommentHandling]::Skip;AllowTrailingCommas=$true})
        function Assert-SsrsApplicationElement($Element) {
            if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
                $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach ($p in $Element.EnumerateObject()) { if ($p.Name.Contains(':') -or -not $names.Add($p.Name)) { Stop-Ssrs 'Ssrs.Prerequisite.InvalidApplicationSnapshot' }; Assert-SsrsApplicationElement $p.Value }
            } elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) { foreach ($item in $Element.EnumerateArray()) { Assert-SsrsApplicationElement $item } }
        }
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) { Stop-Ssrs 'Ssrs.Prerequisite.InvalidApplicationSnapshot' }
        Assert-SsrsApplicationElement $document.RootElement
        $section=@($document.RootElement.EnumerateObject() | Where-Object { $_.Name.Equals('Subscriptions',[StringComparison]::OrdinalIgnoreCase) })
        if ($section.Count -eq 0 -or $section[0].Value.ValueKind -ne [Text.Json.JsonValueKind]::Object) { return $false }
        $setting=@($section[0].Value.EnumerateObject() | Where-Object { $_.Name.Equals('Enabled',[StringComparison]::OrdinalIgnoreCase) })
        if ($setting.Count -eq 0) { return $false }
        if ($setting[0].Value.ValueKind -eq [Text.Json.JsonValueKind]::True) { return $true }
        if ($setting[0].Value.ValueKind -eq [Text.Json.JsonValueKind]::String) { $enabled=$false; if ([bool]::TryParse($setting[0].Value.GetString(),[ref]$enabled)) { return $enabled } }
        return $false
    } catch { Stop-Ssrs 'Ssrs.Prerequisite.InvalidApplicationSnapshot' }
    finally { if ($document) { $document.Dispose() } }
}
function Close-SsrsSession($Session) {
    if ($Session) { Close-ClcTrustedContext $Session.context; foreach ($handle in $Session.handles) { $handle.Dispose() } }
}
function Assert-SsrsPoolAutoStart($Pool) {
    if ($null -eq $Pool -or $Pool.AutoStart -isnot [bool] -or -not $Pool.AutoStart) { Stop-Ssrs 'Ssrs.Prerequisite.PoolAutoStart' }
}
function Assert-SsrsApplicationInitializationModule($GlobalModules,$EffectiveSiteModules,[string] $ExpectedImagePath) {
    $registered=@($GlobalModules | Where-Object { (Get-SsrsRecordProperty $_ 'name') -is [string] -and (Get-SsrsRecordProperty $_ 'name').Equals('ApplicationInitializationModule',[StringComparison]::OrdinalIgnoreCase) })
    $enabled=@($EffectiveSiteModules | Where-Object { (Get-SsrsRecordProperty $_ 'name') -is [string] -and (Get-SsrsRecordProperty $_ 'name').Equals('ApplicationInitializationModule',[StringComparison]::OrdinalIgnoreCase) })
    if ($registered.Count -ne 1 -or $enabled.Count -ne 1 -or -not [IO.Path]::IsPathFullyQualified($ExpectedImagePath)) { Stop-Ssrs 'Ssrs.Prerequisite.ApplicationInitializationModule' }
    $image=Get-SsrsRecordProperty $registered[0] 'image'; $globalPrecondition=Get-SsrsRecordProperty $registered[0] 'preCondition'
    $siteType=Get-SsrsRecordProperty $enabled[0] 'type'; $sitePrecondition=Get-SsrsRecordProperty $enabled[0] 'preCondition'
    if ($image -isnot [string] -or $globalPrecondition -isnot [string] -or $globalPrecondition.Length -ne 0 -or
        $siteType -isnot [string] -or $siteType.Length -ne 0 -or $sitePrecondition -isnot [string] -or $sitePrecondition.Length -ne 0) { Stop-Ssrs 'Ssrs.Prerequisite.ApplicationInitializationModule' }
    if (-not [IO.Path]::IsPathFullyQualified($ExpectedImagePath)) { Stop-Ssrs 'Ssrs.Prerequisite.ApplicationInitializationModule' }
    $expected=[IO.Path]::GetFullPath($ExpectedImagePath)
    if (-not $ExpectedImagePath.Equals($expected,[StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::IsPathFullyQualified($image) -or -not $image.Equals($expected,[StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.File]::Exists($expected) -or (([IO.File]::GetAttributes($expected) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { Stop-Ssrs 'Ssrs.Prerequisite.ApplicationInitializationModule' }
}
function Assert-SsrsLocalIisConfiguration($Redirection) {
    $nodes=@($Redirection.SelectNodes('/configuration/configurationRedirection'))
    if ($nodes.Count -ne 1 -or ($nodes[0].HasAttribute('enabled') -and $nodes[0].GetAttribute('enabled') -cnotmatch '^(?i:false)$') -or
        $Redirection.SelectNodes('//*[@configSource]').Count) { Stop-Ssrs 'Ssrs.Prerequisite.UnsupportedIisConfigurationLayout' }
}
function Open-SsrsApplicationPreflight($Bootstrap,[string] $AuthorizationId,[bool] $AfterEnrollment) {
    $session=$null
    try {
        $session=Open-HSession $Bootstrap.configurationPath $AuthorizationId
        $config=$session.configuration
        if ($config.applicationId -cne 'ssrs-web' -or $config.targetId -cne $script:SsrsTargetId -or $config.environmentName -cne $script:SsrsEnvironmentName -or $config.runtimeEnvironment -cne 'Production' -or
            $config.operationalConfigPath -cne (Join-Path $Bootstrap.programData 'clc-ssrs-web\Config\application.json')) { Stop-Ssrs 'Ssrs.FixedTargetMismatch' }
        $logs=Join-Path $Bootstrap.programData 'clc-ssrs-web\Logs'; $physical=Get-HProspectivePhysicalPath $logs $session.handles
        $others=@($config.applicationRoot,[IO.Path]::GetDirectoryName($config.operationalConfigPath),$config.dataProtectionRoot,$config.artifactInboxRoot,$config.enrollmentRoot,$Bootstrap.root,(Get-HAuthorityRoot))
        if ($session.authorization.cutover) { $others+=$session.authorization.cutover.backupRoot }
        foreach ($path in $others) {
            $other=Get-HProspectivePhysicalPath $path $session.handles
            if ((Test-ClcPathAncestry $physical $other) -or (Test-ClcPathAncestry $other $physical)) { Stop-Ssrs 'Ssrs.LogRootOverlap' }
        }
        Assert-SsrsEnvironmentNames ([Environment]::GetEnvironmentVariables([EnvironmentVariableTarget]::Machine).Keys)
        $snapshot=Get-HIisSnapshot $session
        if ($snapshot.poolExists) { Assert-SsrsEnvironmentNames @($snapshot.pool.environment | ForEach-Object { $_.name }) }
        $iisDirectory=[IO.Path]::GetDirectoryName($session.authorization.hostPolicy.appcmdPath)
        $redirect=Read-ClcHeldFile (Join-Path $iisDirectory 'config\redirection.config') $session.handles 4194304 -SystemFile
        $redirection=Read-SsrsXml $redirect.bytes
        Assert-SsrsLocalIisConfiguration $redirection
        $hostFile=Read-ClcHeldFile (Join-Path $iisDirectory 'config\applicationHost.config') $session.handles 4194304 -SystemFile
        Assert-SsrsInheritedGrace (Read-SsrsXml $hostFile.bytes) $config.siteName
        if ($AfterEnrollment) {
            $appConfig=Read-ClcHeldFile $config.operationalConfigPath $session.handles
            $subscriptionsEnabled=Test-SsrsApplicationSubscriptions $appConfig.bytes
            $manager=Open-HIisManager $session
            try {
                $pool=$manager.ApplicationPools[$config.applicationPoolName]
                Assert-SsrsPoolAutoStart $pool
                if ($subscriptionsEnabled) {
                    $app=$manager.Sites[$config.siteName].Applications['/']
                    try {
                        $globalModules=$manager.GetApplicationHostConfiguration().GetSection('system.webServer/globalModules').GetCollection()
                        $siteModules=$manager.GetWebConfiguration($config.siteName).GetSection('system.webServer/modules').GetCollection()
                        $globalModuleRecords=@($globalModules | ForEach-Object { [pscustomobject]@{name=[string]$_['name'];image=[string]$_['image'];preCondition=[string]$_['preCondition']} })
                        $effectiveSiteModuleRecords=@($siteModules | ForEach-Object { [pscustomobject]@{name=[string]$_['name'];type=[string]$_['type'];preCondition=[string]$_['preCondition']} })
                        $warmupPath=Join-Path $iisDirectory 'warmup.dll'
                        Assert-SsrsApplicationInitializationModule $globalModuleRecords $effectiveSiteModuleRecords $warmupPath
                        $null=Read-ClcHeldFile $warmupPath $session.handles 268435456 -SystemFile -NoCapture
                        $initialization=$manager.GetWebConfiguration($config.siteName).GetSection('system.webServer/applicationInitialization')
                    } catch { Stop-Ssrs 'Ssrs.Prerequisite.ApplicationInitializationModule' }
                    if ($pool.StartMode.ToString() -cne 'AlwaysRunning' -or $pool.ProcessModel.IdleTimeout -ne [TimeSpan]::Zero -or
                        $pool.ProcessModel.ShutdownTimeLimit.TotalSeconds -lt 90 -or -not [bool]$app['preloadEnabled'] -or -not [bool]$initialization['doAppInitAfterRestart']) { Stop-Ssrs 'Ssrs.Prerequisite.SubscriptionIisSetup' }
                }
            } finally { $manager.Dispose() }
        }
        return $session
    } catch { Close-SsrsSession $session; throw }
}
function Get-SsrsEnrollment($Bootstrap,[string] $AuthorizationId) {
    & (Join-Path $Bootstrap.root 'tools/host/Get-ClcHostEnrollment.ps1') -DeploymentConfigurationPath $Bootstrap.configurationPath -AuthorizationId $AuthorizationId
}
function Initialize-SsrsEnrollment($Bootstrap,[string] $AuthorizationId,[string] $State) {
    $parameters=@{DeploymentConfigurationPath=$Bootstrap.configurationPath;AuthorizationId=$AuthorizationId;Operation='Apply'}
    if ($State -ceq 'ToolkitNotInitialized') { $parameters.DistributionPath=$Bootstrap.distribution }
    & (Join-Path $Bootstrap.root 'tools/host/Initialize-ClcHostEnrollment.ps1') @parameters
}
function Assert-SsrsEnrollmentResult($Result) {
    $validWorker=$null -eq $Result.WorkerCount -or (($Result.WorkerCount -is [int] -or $Result.WorkerCount -is [long]) -and $Result.WorkerCount -ge 0 -and $Result.WorkerCount -le 4096)
    if ($Result.TargetId -cne $script:SsrsTargetId -or $Result.HostState -cnotin @('Ready','ToolkitNotInitialized','TargetNotEnrolled','Incomplete','DriftDetected','RecoveryRequired') -or
        $Result.ReadyForDeployment -isnot [bool] -or ($Result.HostState -ceq 'Ready') -ne $Result.ReadyForDeployment -or
        $Result.InitializationPerformed -isnot [bool] -or $Result.RequiresOperatorIntervention -isnot [bool] -or
        $Result.ObservedPoolState -cnotin @('Unknown','Started','Stopped','Starting','Stopping') -or -not $validWorker -or
        $Result.Code -isnot [string] -or $Result.Code.Length -lt 1 -or $Result.Code.Length -gt 128 -or
        $Result.NextAction -isnot [string] -or $Result.NextAction.Length -lt 1 -or $Result.NextAction.Length -gt 128) { Stop-Ssrs 'Ssrs.EnrollmentResultInvalid' }
}
function Set-SsrsEnrollmentEvidence($Evidence,$Result) {
    Assert-SsrsEnrollmentResult $Result
    $Evidence.enrollmentState=$Result.HostState
    if ($Evidence.initializationPerformed -ne $true) { $Evidence.initializationPerformed=[bool]$Result.InitializationPerformed }
    $Evidence.requiresOperatorIntervention=[bool]$Result.RequiresOperatorIntervention
    $Evidence.observedPoolState=$Result.ObservedPoolState
    $Evidence.workerCount=if ($null -eq $Result.WorkerCount) {$null} else {[long]$Result.WorkerCount}
    if ($Result.NextAction -cin @('ReconcileRetainedInfrastructure','PlanAuthorizedInitialization','ReconcileCanonicalPoolState','ReconcileConfiguration','UseExistingExplicitRecovery','InvokeSeparatelyApprovedRelease','RetainEvidenceAndUseExplicitRecoveryOrMaintenance','RetainAuthorizationEvidenceAndResourcesForIndependentReconciliation')) {
        $Evidence.nextAction=$Result.NextAction
    } elseif ($Result.RequiresOperatorIntervention -or $Result.HostState -cin @('Incomplete','DriftDetected','RecoveryRequired')) {
        $Evidence.nextAction='RetainEvidenceAndUseExplicitRecoveryOrMaintenance'
    } else { $Evidence.nextAction='ReviewIndependentPrerequisitesAndRetainEvidence' }
    if ($Result.RequiresOperatorIntervention -or $Result.HostState -cin @('Incomplete','DriftDetected','RecoveryRequired')) { $Evidence.mutationScope='Unknown' }
}
function Open-SsrsSelection($Bootstrap,[string] $AuthorizationId,[string] $ApprovalId) {
    $session=$null; $selection=$null
    try {
        $session=Open-HSession $Bootstrap.configurationPath $AuthorizationId; Open-HInstalledContext $session
        $module=Import-Module $session.layout.manifestPath -Force -PassThru -Scope Local
        $selection=& $module {
            param($ApprovalId,$TargetId)
            $context=Open-ClcTrustedContext
            try { $target=Read-ClcProtectedTarget $context $TargetId; $approval=Read-ClcProtectedApproval $context $ApprovalId; return @{context=$context;target=$target.record;approval=$approval.record} }
            catch { Close-ClcTrustedContext $context; throw }
        } $ApprovalId $script:SsrsTargetId
        $selection.module=$module; $selection.session=$session
        if (-not (Test-ClcValueEqual $selection.target (Get-HTarget $session.configuration))) { Stop-Ssrs 'Ssrs.FixedTargetMismatch' }
        $null=& $module { param($Context,$Target) Add-ClcProtectedPathPin $Context $Target.artifactInboxRoot -Directory -Mutable -RuntimeSid $Target.runtimeIdentitySid } $selection.context $selection.target
        $a=$selection.approval
        if (-not $a.enabled -or $a.targetId -cne $script:SsrsTargetId -or $a.applicationId -cne 'ssrs-web' -or $a.sourceRepository -cne 'clcdpc/ssrs-web' -or
            $a.provenance.workflowPath -cne '.github/workflows/release.yml' -or $a.provenance.buildJob -cne 'release') { Stop-Ssrs 'Ssrs.IndependentReleaseApprovalRequired' }
        return $selection
    } catch { if ($selection) { & $selection.module {param($c) Close-ClcTrustedContext $c} $selection.context }; Close-SsrsSession $session; throw }
}
function Invoke-SsrsGitHub([string] $Path,[string] $OutputPath) {
    if ($Path -cnotmatch '^/repos/clcdpc/ssrs-web(/[A-Za-z0-9_./-]+)?(\?per_page=100)?$') { Stop-Ssrs 'Ssrs.ArtifactApiPathRejected' }
    $handler=[Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect=$false; $handler.UseCookies=$false
    $client=[Net.Http.HttpClient]::new($handler); $deadline=[Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(5))
    $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,('https://api.github.com'+$Path))
    $response=$null; $source=$null; $destination=$null
    try {
        $request.Headers.UserAgent.ParseAdd('ssrs-web-approved-deployment/1'); $request.Headers.Accept.ParseAdd('application/vnd.github+json')
        $request.Headers.Add('X-GitHub-Api-Version','2022-11-28')
        $request.Headers.Authorization=[Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$env:SSRS_ARTIFACT_READ_TOKEN)
        $response=$client.Send($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$deadline.Token)
        if ($OutputPath -and [int]$response.StatusCode -in @(302,303,307,308)) {
            $location=$response.Headers.Location
            if (-not $location.IsAbsoluteUri -or $location.Scheme -cne 'https' -or $location.Port -ne 443 -or $location.UserInfo -or $location.Fragment -or
                ($location.Host -cnotmatch '^[a-z0-9-]+\.blob\.core\.windows\.net$' -and $location.Host -cnotmatch '(^|\.)actions\.githubusercontent\.com$')) { Stop-Ssrs 'Ssrs.ArtifactRedirectRejected' }
            $response.Dispose(); $request.Dispose(); $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,$location)
            $response=$client.Send($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$deadline.Token)
        }
        $maximum=if ($OutputPath) {1073741824L} else {4194304L}
        if (-not $response.IsSuccessStatusCode -or $response.Content.Headers.ContentLength -gt $maximum) { Stop-Ssrs 'Ssrs.ArtifactApiUnavailable' }
        $source=$response.Content.ReadAsStream()
        $destination=if ($OutputPath) { [IO.FileStream]::new($OutputPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None) } else { [IO.MemoryStream]::new() }
        $buffer=[byte[]]::new(65536); $total=0L
        while (($read=$source.ReadAsync($buffer,0,$buffer.Length,$deadline.Token).GetAwaiter().GetResult()) -gt 0) { $total+=$read; if ($total -gt $maximum) { Stop-Ssrs 'Ssrs.ArtifactBound' }; $destination.Write($buffer,0,$read) }
        if ($OutputPath) { $destination.Flush($true); return }
        return Read-SsrsJson $destination.ToArray()
    } catch { Stop-Ssrs 'Ssrs.ArtifactAcquisitionFailed' }
    finally { if ($source) {$source.Dispose()}; if ($destination) {$destination.Dispose()}; if ($response) {$response.Dispose()}; $request.Dispose(); $client.Dispose(); $handler.Dispose(); $deadline.Dispose() }
}
function Assert-SsrsProvenance($Approval,$Repository,$Artifact,$Run,$Jobs) {
    $p=$Approval.provenance; $expectedName='ssrs-web-release-'+$p.runId+'-'+$p.runAttempt
    if ($Repository.full_name -cne 'clcdpc/ssrs-web' -or $Repository.id -lt 1 -or $Artifact.id -ne $p.artifactId -or $Artifact.name -cne $expectedName -or
        $Artifact.expired -isnot [bool] -or $Artifact.expired -or $Artifact.digest -cne ('sha256:'+$p.transportSha256) -or
        $Artifact.workflow_run.id -ne $p.runId -or $Artifact.workflow_run.repository_id -ne $Repository.id -or $Artifact.workflow_run.head_repository_id -ne $Repository.id -or
        $Artifact.workflow_run.head_branch -cne $Run.head_branch -or $Artifact.workflow_run.head_sha -cne $Approval.sourceCommit -or
        $Run.id -ne $p.runId -or $Run.run_attempt -ne $p.runAttempt -or $Run.head_sha -cne $Approval.sourceCommit -or $Run.head_branch -cne $Artifact.workflow_run.head_branch -or
        $Run.path -cne '.github/workflows/release.yml' -or -not (($Run.event -ceq 'workflow_dispatch' -and $Run.head_branch -ceq 'master') -or ($Run.event -ceq 'push' -and $Run.head_branch -cmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(dev|beta|rc)\.([1-9][0-9]*))?$')) -or $Run.status -cne 'completed' -or $Run.conclusion -cne 'success' -or
        $Run.repository.id -ne $Repository.id -or $Run.head_repository.id -ne $Repository.id -or $Jobs.total_count -gt 100 -or @($Jobs.jobs).Count -ne $Jobs.total_count) { Stop-Ssrs 'Ssrs.ArtifactProvenanceMismatch' }
    $job=@($Jobs.jobs | Where-Object { $_.name -ceq $p.buildJob })
    if ($job.Count -ne 1 -or $job[0].run_id -ne $p.runId -or $job[0].run_attempt -ne $p.runAttempt -or $job[0].head_sha -cne $Approval.sourceCommit -or $job[0].status -cne 'completed' -or $job[0].conclusion -cne 'success') { Stop-Ssrs 'Ssrs.ArtifactProvenanceMismatch' }
}
function Receive-SsrsArtifact($Selection) {
    $a=$Selection.approval; $inbox=$Selection.target.artifactInboxRoot; $final=Join-Path $inbox ($a.approvalId+'.zip')
    if ([IO.File]::Exists($final)) {
        $cached=& $Selection.module { param($Context,$Path) Read-ClcHeldFile $Path $Context.handles 1073741824 -NoCapture } $Selection.context $final
        if ($cached.sha256 -cne $a.releaseZipSha256) { Stop-Ssrs 'Ssrs.InboxCollision' }
        return @{path=$final;transport='NotDownloadedApprovedInboxReused'}
    }
    $repository=Invoke-SsrsGitHub '/repos/clcdpc/ssrs-web'
    $p=$a.provenance; $artifact=Invoke-SsrsGitHub "/repos/clcdpc/ssrs-web/actions/artifacts/$($p.artifactId)"
    $run=Invoke-SsrsGitHub "/repos/clcdpc/ssrs-web/actions/runs/$($p.runId)/attempts/$($p.runAttempt)"
    $jobs=Invoke-SsrsGitHub "/repos/clcdpc/ssrs-web/actions/runs/$($p.runId)/attempts/$($p.runAttempt)/jobs?per_page=100"
    Assert-SsrsProvenance $a $repository $artifact $run $jobs
    $transport=Join-Path $inbox ('incoming-'+[Guid]::NewGuid().ToString('N')+'.transport')
    Invoke-SsrsGitHub "/repos/clcdpc/ssrs-web/actions/artifacts/$($p.artifactId)/zip" $transport
    if ((Get-FileHash -LiteralPath $transport -Algorithm SHA256).Hash.ToLowerInvariant() -cne $p.transportSha256) { Stop-Ssrs 'Ssrs.ArtifactTransportMismatch' }
    $temporary=Join-Path $inbox ('incoming-'+[Guid]::NewGuid().ToString('N')+'.release')
    $zip=[IO.Compression.ZipFile]::OpenRead($transport)
    try {
        if ($zip.Entries.Count -ne 1 -or $zip.Entries[0].FullName -cne 'release.zip' -or $zip.Entries[0].Length -gt 1073741824) { Stop-Ssrs 'Ssrs.ArtifactContainerRejected' }
        $input=$zip.Entries[0].Open(); $output=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try {
            $buffer=[byte[]]::new(65536); $length=0L
            while (($read=$input.Read($buffer,0,$buffer.Length)) -gt 0) { $length+=$read; if ($length -gt 1073741824) { Stop-Ssrs 'Ssrs.ArtifactBound' }; $output.Write($buffer,0,$read) }
            if ($length -ne $zip.Entries[0].Length) { Stop-Ssrs 'Ssrs.ArtifactBound' }; $output.Flush($true)
        } finally { $input.Dispose(); $output.Dispose() }
    } finally { $zip.Dispose() }
    if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant() -cne $a.releaseZipSha256) { Stop-Ssrs 'Ssrs.InnerReleaseMismatch' }
    Test-SsrsSelectedRelease $Selection $temporary
    [IO.File]::Move($temporary,$final,$false)
    return @{path=$final;transport='IndependentlyPinnedTransportVerified'}
}
function Test-SsrsSelectedRelease($Selection,[string] $ArchivePath) {
    $verified=& $Selection.module { param($Path) Test-ClcRelease -ArchivePath $Path } $ArchivePath
    $a=$Selection.approval
    if ($verified.integrityVerified -ne $true -or $verified.authorizedForDeployment -ne $false -or $verified.applicationId -cne 'ssrs-web' -or
        $verified.releaseId -cne $a.releaseId -or $verified.sourceCommit -cne $a.sourceCommit -or $verified.archiveSha256 -cne $a.releaseZipSha256 -or $verified.manifestSha256 -cne $a.manifestSha256) { Stop-Ssrs 'Ssrs.ReleaseInspectionMismatch' }
}
function Invoke-SsrsApprovedDeployment($Selection,[string] $ApprovalId) { & $Selection.module { param($Id,$TargetId) Invoke-ClcIisDeployment -TargetId $TargetId -ReleaseApprovalId $Id } $ApprovalId $script:SsrsTargetId }
function Assert-SsrsDeploymentResult($Result,$Approval) {
    $attempt=[Guid]::Empty
    $validGeneration=($Result.currentGeneration -is [int] -or $Result.currentGeneration -is [long]) -and $Result.currentGeneration -ge 1
    $validAttempt=$Result.attemptId -is [string] -and [Guid]::TryParseExact($Result.attemptId,'D',[ref]$attempt) -and $attempt -ne [Guid]::Empty
    $toolkit=Get-SsrsRecordProperty $Result 'executingToolkit'; $toolkitVersion=Get-SsrsRecordProperty $toolkit 'version'; $toolkitCommit=Get-SsrsRecordProperty $toolkit 'sourceCommit'
    $validToolkit=$null -ne $toolkit -and $toolkitVersion -is [string] -and $toolkitVersion.Length -ge 1 -and
        $toolkitVersion.Length -le 128 -and $toolkitCommit -is [string] -and $toolkitCommit -cmatch '^[a-f0-9]{40}$'
    if ($Result.status -isnot [string] -or $Result.status -cnotin @('Deployed','NoOp') -or $Result.targetId -isnot [string] -or $Result.targetId -cne $script:SsrsTargetId -or
        $Result.releaseId -isnot [string] -or $Result.releaseId -cne $Approval.releaseId -or $Result.sourceCommit -isnot [string] -or $Result.sourceCommit -cne $Approval.sourceCommit -or
        -not $validAttempt -or -not $validGeneration -or $Result.observedPoolState -isnot [string] -or $Result.observedPoolState -cne 'Started' -or
        ($Result.workerCount -isnot [int] -and $Result.workerCount -isnot [long]) -or $Result.workerCount -ne 1 -or -not $validToolkit) { Stop-Ssrs 'Ssrs.DeploymentResultInvalid' }
}
function Set-SsrsCompletedDeploymentEvidence($Evidence,$Result) {
    $Evidence.outcome=$Result.status; $Evidence.attemptId=$Result.attemptId; $Evidence.observedPoolState=$Result.observedPoolState; $Evidence.workerCount=$Result.workerCount
    $Evidence.currentGeneration=$Result.currentGeneration
    $Evidence.toolkitVersion=$Result.executingToolkit.version; $Evidence.toolkitSourceCommit=$Result.executingToolkit.sourceCommit
    $Evidence.deploymentPhase='Completed'; $Evidence.mutationScope='None'; $Evidence.requiresOperatorIntervention=$false
    $Evidence.readiness='ToolkitVerified'; $Evidence.nextAction='None'
}
function Invoke-SsrsDevCore($Bootstrap,[string] $AuthorizationId,[string] $ApprovalId,[bool] $FirstCutover,$Evidence) {
    $selection=$null; $preflight=$null
    try {
        $state=Get-SsrsEnrollment $Bootstrap $AuthorizationId; Set-SsrsEnrollmentEvidence $Evidence $state
        if ($state.HostState -cin @('Incomplete','DriftDetected','RecoveryRequired')) { Stop-Ssrs ('Ssrs.Enrollment.'+$state.HostState) }
        if ($state.HostState -ceq 'Ready' -and $FirstCutover) { Stop-Ssrs 'Ssrs.FirstCutoverAlreadyCompleted' }
        $preflight=Open-SsrsApplicationPreflight $Bootstrap $AuthorizationId $false
        Close-SsrsSession $preflight; $preflight=$null
        if ($state.HostState -cne 'Ready') {
            if (($null -ne $Bootstrap.authorization.cutover) -ne $FirstCutover -or ($FirstCutover -and $Bootstrap.authorization.permittedOperations -cnotcontains 'first-cutover')) { Stop-Ssrs 'Ssrs.IndependentCutoverAuthorizationRequired' }
            $Evidence.enrollmentState='Unknown'; $Evidence.initializationPerformed=$null; $Evidence.requiresOperatorIntervention=$true
            $Evidence.nextAction='RetainEvidenceAndUseExplicitRecoveryOrMaintenance'
            $applied=Initialize-SsrsEnrollment $Bootstrap $AuthorizationId $state.HostState
            Set-SsrsEnrollmentEvidence $Evidence $applied
            $state=Get-SsrsEnrollment $Bootstrap $AuthorizationId; Set-SsrsEnrollmentEvidence $Evidence $state
            if (-not $state.ReadyForDeployment) { Stop-Ssrs 'Ssrs.EnrollmentReinspectionRequired' }
        }
        $preflight=Open-SsrsApplicationPreflight $Bootstrap $AuthorizationId $true
        $selection=Open-SsrsSelection $Bootstrap $AuthorizationId $ApprovalId
        $Evidence.releaseId=$selection.approval.releaseId; $Evidence.sourceCommit=$selection.approval.sourceCommit
        $received=Receive-SsrsArtifact $selection; Test-SsrsSelectedRelease $selection $received.path
        $Evidence.artifactVerification=$received.transport
        Assert-SsrsEnvironmentNames ([Environment]::GetEnvironmentVariables([EnvironmentVariableTarget]::Machine).Keys)
        $again=Get-SsrsEnrollment $Bootstrap $AuthorizationId; Set-SsrsEnrollmentEvidence $Evidence $again
        if (-not $again.ReadyForDeployment) { Stop-Ssrs 'Ssrs.EnrollmentReinspectionRequired' }
        $Evidence.deploymentPhase='Unknown'; $Evidence.mutationScope='Unknown'; $Evidence.requiresOperatorIntervention=$true
        $Evidence.outcome='Failed'; $Evidence.nextAction='PreserveEvidenceAndUseInfrastructureMaintenance'
        $result=Invoke-SsrsApprovedDeployment $selection $ApprovalId; Assert-SsrsDeploymentResult $result $selection.approval
        Set-SsrsCompletedDeploymentEvidence $Evidence $result
        return $Evidence
    } finally {
        if ($selection) { & $selection.module {param($c) Close-ClcTrustedContext $c} $selection.context; Close-SsrsSession $selection.session }
        Close-SsrsSession $preflight
    }
}
function New-SsrsEvidence {
    return [ordered]@{recordKind='ssrs-web-deployment-evidence';schemaVersion=1;targetId=$script:SsrsTargetId;environmentName=$script:SsrsEnvironmentName;runtimeEnvironment='Production';
        releaseId=$null;sourceCommit=$null;enrollmentState='Unknown';initializationPerformed=$null;requiresOperatorIntervention=$null;deploymentPhase=$null;mutationScope='None';
        artifactVerification='NotVerified';toolkitVersion=$null;toolkitSourceCommit=$null;outcome='Failed';attemptId=$null;currentGeneration=$null;observedPoolState='Unknown';workerCount=$null;
        readiness='NotVerified';code=$null;nextAction='ReviewIndependentPrerequisitesAndRetainEvidence'}
}
function Get-SsrsRecordProperty($Object,[string] $Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [Collections.IDictionary]) {
        foreach ($key in $Object.Keys) { if ($key -is [string] -and $key.Equals($Name,[StringComparison]::OrdinalIgnoreCase)) { return ,$Object[$key] } }
        return $null
    }
    $property=$Object.PSObject.Properties[$Name]
    if ($null -ne $property) { return ,$property.Value }
    return $null
}
function Test-SsrsRecordProperty($Object,[string] $Name) {
    if ($null -eq $Object) { return $false }
    if ($Object -is [Collections.IDictionary]) { foreach ($key in $Object.Keys) { if ($key -is [string] -and $key.Equals($Name,[StringComparison]::OrdinalIgnoreCase)) { return $true } }; return $false }
    return ($null -ne $Object.PSObject.Properties[$Name])
}
function Set-SsrsSafeFailure($Evidence,$Record) {
    $initializationWasPerformed=$Evidence.initializationPerformed -eq $true
    $code=($Record.FullyQualifiedErrorId -split ',')[0]
    if ($Record.Exception.Message -cmatch '^(Ssrs|Release\.Tag)\.[A-Za-z0-9.]{1,120}$') { $code=$Record.Exception.Message }
    if ($code -cnotmatch '^(Clc|Ssrs|Release\.Tag)\.[A-Za-z0-9.]{1,120}$') { $code='Ssrs.WorkflowFailed' }
    $Evidence.code=$code
    $safeActions=@('ReconcileRetainedInfrastructure','ReconcileCanonicalPoolState','ReconcileConfiguration','UseExistingExplicitRecovery',
        'RetainEvidenceAndUseExplicitRecoveryOrMaintenance','RetainAuthorizationEvidenceAndResourcesForIndependentReconciliation','ReviewProtectedInputsAndRetry',
        'RetainEvidenceAndSelectExplicitRecoveryOrMaintenance','PreserveEvidenceAndUseInfrastructureMaintenance','PublishIndependentReleaseApprovalThenRerun','ReviewIndependentPrerequisitesAndRetainEvidence')
    $obj=$Record.TargetObject
    if ($null -ne $obj) {
        if (Test-SsrsRecordProperty $obj 'attemptId') {
            $attempt=[Guid]::Empty; $value=Get-SsrsRecordProperty $obj 'attemptId'
            $Evidence.attemptId=if ($value -is [string] -and [Guid]::TryParseExact($value,'D',[ref]$attempt) -and $attempt -ne [Guid]::Empty) {$attempt.ToString('D')} else {$null}
        }
        if (Test-SsrsRecordProperty $obj 'HostState') {
            $value=Get-SsrsRecordProperty $obj 'HostState'
            $Evidence.enrollmentState=if ($value -is [string] -and $value -cin @('Ready','ToolkitNotInitialized','TargetNotEnrolled','Incomplete','DriftDetected','RecoveryRequired')) {$value} else {'Unknown'}
        }
        if (Test-SsrsRecordProperty $obj 'InitializationPerformed') {
            $value=Get-SsrsRecordProperty $obj 'InitializationPerformed'
            if (-not $initializationWasPerformed) { $Evidence.initializationPerformed=if ($value -is [bool]) {$value} else {$null} }
        }
        if (Test-SsrsRecordProperty $obj 'RequiresOperatorIntervention') {
            $value=Get-SsrsRecordProperty $obj 'RequiresOperatorIntervention'; $Evidence.requiresOperatorIntervention=if ($value -is [bool]) {$value} else {$null}
        }
        if (Test-SsrsRecordProperty $obj 'phase') {
            $value=Get-SsrsRecordProperty $obj 'phase'; $Evidence.deploymentPhase=if ($value -is [string] -and $value -cin @('Admission','Prepared','Mutating','RecoveryRequired','Completed','Unknown')) {$value} else {'Unknown'}
        }
        if (Test-SsrsRecordProperty $obj 'mutationScope') {
            $value=Get-SsrsRecordProperty $obj 'mutationScope'; $Evidence.mutationScope=if ($value -is [string] -and $value -cin @('None','PossiblyLive','Unknown')) {$value} else {'Unknown'}
        }
        if (Test-SsrsRecordProperty $obj 'observedPoolState') {
            $value=Get-SsrsRecordProperty $obj 'observedPoolState'; $Evidence.observedPoolState=if ($value -is [string] -and $value -cin @('Started','Stopped','Unknown','Starting','Stopping')) {$value} else {'Unknown'}
        }
        if (Test-SsrsRecordProperty $obj 'workerCount') {
            $value=Get-SsrsRecordProperty $obj 'workerCount'
            $Evidence.workerCount=if ($null -eq $value) {$null} elseif (($value -is [int] -or $value -is [long]) -and $value -ge 0 -and $value -le 4096) {[long]$value} else {$null}
        }
        if (Test-SsrsRecordProperty $obj 'nextAction') {
            $value=Get-SsrsRecordProperty $obj 'nextAction'
            if ($value -is [string] -and $value -cin $safeActions) { $Evidence.nextAction=$value }
        }
    }
    if ($initializationWasPerformed) { $Evidence.initializationPerformed=$true }
    $Evidence.requiresOperatorIntervention=$true
    $recoveryActions=@('ReconcileRetainedInfrastructure','ReconcileCanonicalPoolState','ReconcileConfiguration','UseExistingExplicitRecovery',
        'RetainEvidenceAndUseExplicitRecoveryOrMaintenance','RetainAuthorizationEvidenceAndResourcesForIndependentReconciliation','RetainEvidenceAndSelectExplicitRecoveryOrMaintenance',
        'PreserveEvidenceAndUseInfrastructureMaintenance')
    $hostRecoveryActions=@('ReconcileRetainedInfrastructure','ReconcileCanonicalPoolState','ReconcileConfiguration','UseExistingExplicitRecovery',
        'RetainEvidenceAndUseExplicitRecoveryOrMaintenance','RetainAuthorizationEvidenceAndResourcesForIndependentReconciliation')
    if ($Evidence.mutationScope -ceq 'Unknown' -or $Evidence.deploymentPhase -ceq 'Unknown') { $Evidence.nextAction='PreserveEvidenceAndUseInfrastructureMaintenance' }
    elseif (($Evidence.mutationScope -ceq 'PossiblyLive' -or $Evidence.deploymentPhase -ceq 'RecoveryRequired') -and $Evidence.nextAction -notin $recoveryActions) { $Evidence.nextAction='RetainEvidenceAndSelectExplicitRecoveryOrMaintenance' }
    elseif ($Evidence.enrollmentState -cin @('Incomplete','DriftDetected','RecoveryRequired') -and $Evidence.nextAction -notin $hostRecoveryActions) { $Evidence.nextAction='RetainEvidenceAndUseExplicitRecoveryOrMaintenance' }
    elseif ($Evidence.nextAction -notin $safeActions) {
        if ($code -cin @('Clc.ApprovalMissing','Ssrs.IndependentReleaseApprovalRequired')) { $Evidence.nextAction='PublishIndependentReleaseApprovalThenRerun' }
        else { $Evidence.nextAction='ReviewIndependentPrerequisitesAndRetainEvidence' }
    }
}
