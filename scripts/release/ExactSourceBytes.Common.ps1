function Invoke-ExactSourceGit {
    param(
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][string[]] $GitArguments
    )
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $gitCommand = Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $gitExecutable = [IO.Path]::GetFullPath($gitCommand.Source)
    $repositoryPrefix = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($gitExecutable.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'ExactSource.GitExecutableUntrusted' }
    $startInfo.FileName = $gitExecutable
    $root = [IO.Path]::GetFullPath($RepositoryRoot)
    $gitDirectory = Join-Path $root '.git'
    if ([IO.File]::Exists($gitDirectory)) { throw 'ExactSource.RepositoryMetadataInvalid' }
    $hasGitDirectory = [IO.Directory]::Exists($gitDirectory)
    if ($hasGitDirectory -and ([IO.File]::GetAttributes($gitDirectory) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'ExactSource.RepositoryMetadataInvalid'
    }
    if ($hasGitDirectory) {
        foreach ($name in @('config', 'index', 'HEAD')) {
            $metadataFile = Join-Path $gitDirectory $name
            if ([IO.File]::Exists($metadataFile) -and ([IO.File]::GetAttributes($metadataFile) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'ExactSource.RepositoryMetadataInvalid'
            }
        }

    }
    $startInfo.WorkingDirectory = $root
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($name in @($startInfo.Environment.Keys)) {
        if ($name.StartsWith('GIT_', [StringComparison]::OrdinalIgnoreCase)) {
            [void]$startInfo.Environment.Remove($name)
        }
    }
    $startInfo.Environment['GIT_CONFIG_NOSYSTEM'] = '1'
    $startInfo.Environment['GIT_ATTR_NOSYSTEM'] = '1'
    $startInfo.Environment['GIT_CONFIG_COUNT'] = '1'
    $startInfo.Environment['GIT_CONFIG_KEY_0'] = 'core.attributesFile'
    $startInfo.Environment['GIT_CONFIG_VALUE_0'] = Join-Path ([IO.Path]::GetTempPath()) ('clc-exact-source-' + [Guid]::NewGuid().ToString('N') + '.gitattributes')
    $startInfo.Environment['GIT_CONFIG_GLOBAL'] = Join-Path ([IO.Path]::GetTempPath()) ('clc-exact-source-' + [Guid]::NewGuid().ToString('N') + '.gitconfig')
    $startInfo.Environment['GIT_NO_REPLACE_OBJECTS'] = '1'
    $startInfo.Environment['GIT_NO_LAZY_FETCH'] = '1'
    $startInfo.Environment['GIT_OPTIONAL_LOCKS'] = '0'
    $startInfo.Environment['GIT_TERMINAL_PROMPT'] = '0'
    if ($hasGitDirectory) { $startInfo.Environment['GIT_INDEX_FILE'] = Join-Path $gitDirectory 'index' }
    $gitPrefix = @('-C', $root)
    if ($hasGitDirectory) { $gitPrefix += @('--git-dir', $gitDirectory, '--work-tree', $root) }
    foreach ($argument in $gitPrefix + @('--no-replace-objects', '-c', 'core.fsmonitor=false', '-c', 'core.untrackedCache=false') + $GitArguments) {
        $startInfo.ArgumentList.Add([string]$argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $output = [IO.MemoryStream]::new()
    try {
        if (-not $process.Start()) { throw 'ExactSource.GitUnavailable' }
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.StandardOutput.BaseStream.CopyTo($output)
        $process.WaitForExit()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'ExactSource.GitCommandFailed' }
        return ,$output.ToArray()
    } catch {
        if ($_.Exception.Message -like 'ExactSource.*') { throw }
        throw 'ExactSource.GitCommandFailed'
    } finally {
        $output.Dispose()
        $process.Dispose()

    }
}

function ConvertFrom-ExactSourceGitText {
    param([Parameter(Mandatory)][AllowEmptyCollection()][byte[]] $Bytes)
    try {
        $encoding = [Text.UTF8Encoding]::new($false, $true)
        return $encoding.GetString($Bytes).TrimEnd([char]13, [char]10)
    } catch {
        throw 'ExactSource.GitOutputInvalid'
    }
}

function ConvertFrom-ExactSourceNulRecords {
    param([Parameter(Mandatory)][AllowEmptyCollection()][byte[]] $Bytes)
    $records = [Collections.Generic.List[string]]::new()
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    $start = 0
    for ($index = 0; $index -lt $Bytes.Length; $index++) {
        if ($Bytes[$index] -eq 0) {
            if ($index -eq $start) { throw 'ExactSource.GitOutputInvalid' }
            try { $records.Add($encoding.GetString($Bytes, $start, $index - $start)) }
            catch { throw 'ExactSource.GitOutputInvalid' }
            $start = $index + 1
        }
    }
    if ($start -ne $Bytes.Length) { throw 'ExactSource.GitOutputInvalid' }
    return ,$records.ToArray()
}

function Assert-ExactSourcePath {
    param(
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][string] $RelativePath,
        [Parameter(Mandatory)][AllowEmptyCollection()][Collections.Generic.HashSet[string]] $WindowsPaths
    )
    if ([string]::IsNullOrEmpty($RelativePath) -or $RelativePath.StartsWith('/') -or $RelativePath.Contains('\') -or
        $RelativePath -match '(^|/)(\.|\.\.)(/|$)' -or $RelativePath -match '[\x00-\x1f<>:"|?*]' -or
        $RelativePath -match '(^|/)[^/]*[ .](/|$)' -or $RelativePath -match '(^|/)(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.[^/]*)?(/|$)') {
        throw 'ExactSource.UnsafePath'
    }
    $candidate = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot ($RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))))
    $prefix = $RepositoryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or -not $WindowsPaths.Add($candidate)) {
        throw 'ExactSource.UnsafePath'
    }
    return $candidate
}

function Get-ExactSourceGitObjectHash {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $ObjectFormat
    )
    $stream = $null
    $hash = $null
    try {
        $algorithm = if ($ObjectFormat -ceq 'sha1') { [Security.Cryptography.HashAlgorithmName]::SHA1 }
            elseif ($ObjectFormat -ceq 'sha256') { [Security.Cryptography.HashAlgorithmName]::SHA256 }
            else { throw 'ExactSource.ObjectFormatUnsupported' }
        $stream = [IO.File]::OpenRead($Path)
        $hash = [Security.Cryptography.IncrementalHash]::CreateHash($algorithm)
        $header = [Text.Encoding]::ASCII.GetBytes('blob ' + $stream.Length)
        $hash.AppendData($header)
        $hash.AppendData([byte[]]@(0))
        $buffer = [byte[]]::new(65536)
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $hash.AppendData($buffer, 0, $read)
        }
        return [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
    } finally {
        if ($hash) { $hash.Dispose() }
        if ($stream) { $stream.Dispose() }
    }
}

function Test-ExactSourceGeneratedPath {
    param(
        [Parameter(Mandatory)][string] $RelativePath,
        [Parameter(Mandatory)][Collections.Generic.HashSet[string]] $AllowedRoots
    )
    $path = $RelativePath.TrimEnd('/')
    foreach ($root in $AllowedRoots) {
        if ($path.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
            $path.StartsWith($root + '/', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Assert-ExactSourceBytes {
    param(
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string] $Commit
    )
    $root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not [IO.Directory]::Exists($root) -or
        ([IO.File]::GetAttributes($root) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'ExactSource.RepositoryRootInvalid' }
    $gitMetadata = Join-Path $root '.git'
    if (-not [IO.Directory]::Exists($gitMetadata) -or ([IO.File]::GetAttributes($gitMetadata) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'ExactSource.RepositoryMetadataInvalid'
    }
    foreach ($name in @('config', 'index', 'HEAD')) {
        $metadataPath = Join-Path $gitMetadata $name
        if (-not [IO.File]::Exists($metadataPath) -or ([IO.File]::GetAttributes($metadataPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'ExactSource.RepositoryMetadataInvalid'
        }
    }
    $absoluteGitDir = ConvertFrom-ExactSourceGitText (Invoke-ExactSourceGit $root @('rev-parse', '--absolute-git-dir'))
    if ([IO.Path]::GetFullPath($absoluteGitDir).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) -cne $gitMetadata) {
        throw 'ExactSource.RepositoryMetadataInvalid'
    }
    if ([IO.File]::Exists((Join-Path $gitMetadata 'objects/info/alternates'))) { throw 'ExactSource.RepositoryMetadataInvalid' }
    $head = ConvertFrom-ExactSourceGitText (Invoke-ExactSourceGit $root @('rev-parse', '--verify', 'HEAD'))
    if ($head -cne $Commit) { throw 'ExactSource.CommitMismatch' }
    $objectFormat = ConvertFrom-ExactSourceGitText (Invoke-ExactSourceGit $root @('rev-parse', '--show-object-format'))
    if ($objectFormat -cne 'sha1' -and $objectFormat -cne 'sha256') { throw 'ExactSource.ObjectFormatUnsupported' }

    $treeRecords = ConvertFrom-ExactSourceNulRecords (Invoke-ExactSourceGit $root @('ls-tree', '-r', '-z', '--full-tree', $Commit))
    $tree = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $windowsPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($record in $treeRecords) {
        $tab = $record.IndexOf([char]9)
        if ($tab -le 0) { throw 'ExactSource.GitOutputInvalid' }
        $metadata = $record.Substring(0, $tab).Split(' ')
        if ($metadata.Count -ne 3 -or $metadata[1] -cne 'blob' -or $metadata[0] -cnotin @('100644', '100755') -or
            $metadata[2] -cnotmatch '^[a-f0-9]{40}([a-f0-9]{24})?$') { throw 'ExactSource.UnsupportedTreeEntry' }
        $relativePath = $record.Substring($tab + 1)
        $fullPath = Assert-ExactSourcePath $root $relativePath $windowsPaths
        if ($tree.ContainsKey($relativePath)) { throw 'ExactSource.UnsupportedTreeEntry' }
        $tree.Add($relativePath, [pscustomobject]@{ mode = $metadata[0]; objectId = $metadata[2]; fullPath = $fullPath })
    }
    if ($tree.Count -eq 0) { throw 'ExactSource.EmptyTree' }

    $indexRecords = ConvertFrom-ExactSourceNulRecords (Invoke-ExactSourceGit $root @('ls-files', '--stage', '-z'))
    $index = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($record in $indexRecords) {
        $tab = $record.IndexOf([char]9)
        if ($tab -le 0) { throw 'ExactSource.GitOutputInvalid' }
        $metadata = $record.Substring(0, $tab).Split(' ')
        if ($metadata.Count -ne 3 -or $metadata[2] -cne '0' -or
            $metadata[0] -cnotin @('100644', '100755') -or $metadata[1] -cnotmatch '^[a-f0-9]{40}([a-f0-9]{24})?$') { throw 'ExactSource.IndexMismatch' }
        $relativePath = $record.Substring($tab + 1)
        if ($index.ContainsKey($relativePath)) { throw 'ExactSource.IndexMismatch' }
        $index.Add($relativePath, [pscustomobject]@{ mode = $metadata[0]; objectId = $metadata[1] })
    }
    if ($index.Count -ne $tree.Count) { throw 'ExactSource.IndexMismatch' }
    foreach ($relativePath in $tree.Keys) {
        if (-not $index.ContainsKey($relativePath) -or $index[$relativePath].mode -cne $tree[$relativePath].mode -or
            $index[$relativePath].objectId -cne $tree[$relativePath].objectId) { throw 'ExactSource.IndexMismatch' }
    }

    foreach ($relativePath in $tree.Keys) {
        $entry = $tree[$relativePath]
        $current = $root
        $parts = $relativePath.Split('/')
        for ($partIndex = 0; $partIndex -lt $parts.Length - 1; $partIndex++) {
            $current = Join-Path $current $parts[$partIndex]
            if (-not [IO.Directory]::Exists($current) -or
                ([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'ExactSource.WorkingTreeEntryInvalid' }
        }
        if (-not [IO.File]::Exists($entry.fullPath) -or
            ([IO.File]::GetAttributes($entry.fullPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'ExactSource.WorkingTreeEntryInvalid' }
        if ((Get-ExactSourceGitObjectHash $entry.fullPath $objectFormat) -cne $entry.objectId) { throw 'ExactSource.WorkingTreeBlobMismatch' }
    }

    $untracked = ConvertFrom-ExactSourceNulRecords (Invoke-ExactSourceGit $root @('ls-files', '--others', '-z'))
    $allowedGeneratedRoots = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in @('artifacts', 'node_modules')) {
        [void]$allowedGeneratedRoots.Add($path)
    }
    foreach ($project in @($tree.Keys | Where-Object { $_ -cmatch '(^|/)[^/]+\.csproj$' })) {
        $separator = $project.LastIndexOf('/')
        $projectDirectory = if ($separator -ge 0) { $project.Substring(0, $separator) } else { '' }
        foreach ($path in @('bin', 'obj', 'TestResults')) {
            $generated = if ($projectDirectory) { $projectDirectory + '/' + $path } else { $path }
            [void]$allowedGeneratedRoots.Add($generated)
        }
    }
    foreach ($relativePath in $untracked) {
        if (-not (Test-ExactSourceGeneratedPath $relativePath $allowedGeneratedRoots)) { throw 'ExactSource.UntrackedOrIgnoredSourceAmbiguity' }
    }
    return [pscustomobject]@{ commit = $Commit; trackedFileCount = $tree.Count; objectFormat = $objectFormat; exactSourceVerified = $true }
}
