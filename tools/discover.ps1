<#
.SYNOPSIS
  Read-only inventory of a Warhammer 40,000: Space Marine (2011) installation.

.DESCRIPTION
  Locates the game through Steam's library folders and app manifests (or -GameDir /
  config/local.json), then writes report.md and report.json describing files, extensions,
  signatures, OBPK (.oppc) archive contents, and likely gameplay-data files.

  Never writes inside the game folder. Reports use paths relative to the game folder and
  omit Steam account IDs, so the report can be shared for analysis.
  Runs on Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\discover.ps1
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$OutDir,
    # Override Steam root folder(s); normally read from the registry.
    [string[]]$SteamRoot,
    # Decompress archives to sample the first bytes of inner files. Skips archives above this size.
    [int]$MaxSampleMB = 4096,
    [switch]$NoInnerSamples
)

$ErrorActionPreference = 'Stop'
$ScriptVersion = '0.1.0'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$AppIds = [ordered]@{ '55150' = 'Space Marine (2011, original / Anniversary Edition)'; '3169520' = 'Space Marine - Master Crafted Edition (2025 remaster)' }

function Get-LocalDataRoot {
    if ($env:LOCALAPPDATA) { return Join-Path $env:LOCALAPPDATA 'sm-reimpl' }
    return Join-Path $HOME '.local/share/sm-reimpl'
}

function Read-LocalConfig {
    $p = Join-Path $RepoRoot 'config/local.json'
    if (Test-Path -LiteralPath $p) { return Get-Content -LiteralPath $p -Raw | ConvertFrom-Json }
    return $null
}

function Get-SteamRoots {
    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($k in @(@('HKCU:\Software\Valve\Steam', 'SteamPath'), @('HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'InstallPath'), @('HKLM:\SOFTWARE\Valve\Steam', 'InstallPath'))) {
        try { $v = (Get-ItemProperty -Path $k[0] -Name $k[1] -ErrorAction Stop).($k[1]); if ($v) { $roots.Add(($v -replace '/', '\')) } } catch { }
    }
    foreach ($d in @("${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam")) { if ($d -and (Test-Path -LiteralPath $d)) { $roots.Add($d) } }
    return $roots | Select-Object -Unique
}

function Get-SteamLibraries([string[]]$steamRoots) {
    $libs = New-Object System.Collections.Generic.List[string]
    foreach ($root in $steamRoots) {
        $libs.Add($root)
        $vdf = Join-Path $root 'steamapps/libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs.Add(($m.Groups[1].Value -replace '\\\\', '\')) }
        }
    }
    # Steam may list one library twice with different casing; PowerShell hashtable keys ignore case.
    $seen = @{}
    return $libs | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object {
        $key = [IO.Path]::GetFullPath($_).TrimEnd('\', '/')
        if (-not $seen.ContainsKey($key)) { $seen[$key] = $true; $_ }
    }
}

function Read-AppManifest([string]$path) {
    $text = Get-Content -LiteralPath $path -Raw
    $o = [ordered]@{}
    foreach ($key in 'appid', 'name', 'installdir', 'buildid', 'SizeOnDisk', 'LastUpdated', 'StateFlags', 'TargetBuildID') {
        $m = [regex]::Match($text, '"' + $key + '"\s+"([^"]*)"')
        if ($m.Success) { $o[$key] = $m.Groups[1].Value }
    }
    if ($o['LastUpdated']) { $o['LastUpdatedUtc'] = [DateTimeOffset]::FromUnixTimeSeconds([long]$o['LastUpdated']).UtcDateTime.ToString('u') }
    foreach ($key in 'language', 'BetaKey', 'betakey') {
        $m = [regex]::Match($text, '"' + $key + '"\s+"([^"]*)"')
        if ($m.Success) { $o["UserConfig.$key"] = $m.Groups[1].Value }
    }
    $depots = @()
    $block = [regex]::Match($text, '"InstalledDepots"\s*\{((?:[^{}]|\{[^{}]*\})*)\}')
    if ($block.Success) {
        foreach ($m in [regex]::Matches($block.Groups[1].Value, '"(\d+)"\s*\{([^}]*)\}')) {
            $man = [regex]::Match($m.Groups[2].Value, '"manifest"\s+"(\d+)"').Groups[1].Value
            $size = [regex]::Match($m.Groups[2].Value, '"size"\s+"(\d+)"').Groups[1].Value
            $depots += [ordered]@{ depot = $m.Groups[1].Value; manifest = $man; size = $size }
        }
    }
    $o['InstalledDepots'] = $depots
    return $o
}

function Open-ReadOnly([string]$path) {
    return [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
}

function Get-Head([string]$path, [int]$count = 32) {
    $fs = Open-ReadOnly $path
    try { $buf = New-Object byte[] $count; $n = $fs.Read($buf, 0, $count); if ($n -lt $count) { $buf = $buf[0..($n - 1)] }; return , $buf }
    finally { $fs.Dispose() }
}

function Format-Sig([byte[]]$b, [int]$n = 16) {
    if (-not $b -or $b.Length -eq 0) { return '(empty)' }
    $b = $b[0..([Math]::Min($n, $b.Length) - 1)]
    $hex = ($b | ForEach-Object { $_.ToString('X2') }) -join ''
    $ascii = -join ($b | ForEach-Object { if ($_ -ge 32 -and $_ -lt 127) { [char]$_ } else { '.' } })
    return "$hex  $ascii"
}

# OBPK reader; layout documented in docs/discovery.md. Mirrors src/SmFormats/Obpk/ObpkArchive.cs
# so the two implementations can cross-check each other on real files.
function Read-Obpk([string]$path) {
    $fs = Open-ReadOnly $path
    try {
        $len = $fs.Length
        $r = New-Object System.IO.BinaryReader($fs)
        function Need([long]$n) { if ($fs.Position + $n -gt $len) { throw "truncated at $($fs.Position) (need $n)" } }
        function Str { Need 4; $n = $r.ReadUInt32(); if ($n -gt 4096) { throw "string length $n at $($fs.Position - 4)" }; Need $n; return [Text.Encoding]::ASCII.GetString($r.ReadBytes([int]$n)) }
        function Count { Need 4; $n = $r.ReadUInt32(); if ($n -gt 1000000) { throw "implausible count $n at $($fs.Position - 4)" }; return $n }

        $magic = $r.ReadBytes(5)
        if ([Text.Encoding]::ASCII.GetString($magic) -ne "OBPK`0") { throw 'missing OBPK magic' }
        $h = [ordered]@{}
        foreach ($f in 'Version', 'Reserved0', 'FileCount', 'Unknown17', 'TableOffset', 'DataOffsetFromTable', 'DecompressedSize', 'Reserved1', 'Reserved2', 'Unknown41', 'Unknown45') { $h[$f] = $r.ReadUInt32() }
        $fs.Position = $h.TableOffset
        $idCount = Count
        for ($i = 0; $i -lt $idCount; $i++) { $n = Count; Need (4 * $n); $null = $r.ReadBytes([int](4 * $n)) }
        $nameCount = Count
        $names = New-Object string[] $nameCount
        for ($i = 0; $i -lt $nameCount; $i++) { $names[$i] = Str }
        $groupCount = Count
        $groups = @()
        for ($i = 0; $i -lt $groupCount; $i++) {
            Need 12; $lo = $r.ReadUInt32(); $hi = $r.ReadUInt32(); $files = $r.ReadUInt32()
            $groups += [pscustomobject]@{ Index = $i; Hash = ('{0:x8}{1:x8}' -f $hi, $lo); Files = $files; Name = "group$i" }
        }
        $entries = New-Object System.Collections.Generic.List[object]
        $warnings = New-Object System.Collections.Generic.List[string]
        [long]$offset = 0
        foreach ($g in $groups) {
            for ($k = 0; $k -lt $g.Files; $k++) {
                $fname = Str; $folder = Str
                Need 21; $size = $r.ReadUInt32(); $unk = $r.ReadUInt32(); $size2 = $r.ReadUInt32(); $res = $r.ReadUInt64(); $xl = $r.ReadByte(); Need $xl; $extra = $r.ReadBytes($xl)
                if ($size -ne $size2) { $warnings.Add("size mismatch on $fname") }
                $entries.Add([pscustomobject]@{ Path = ''; Name = $fname; Folder = $folder; Group = ''; GroupIndex = $g.Index; Size = $size; Offset = $offset; Unknown = ('0x{0:X8}' -f $unk); ExtraLen = $xl })
                $offset += $size
            }
        }
        # The name table is a string pool without duplicates: each group's type name, then its not-yet-seen
        # file names, in order. Walk it alongside the entries to recover each group's type name.
        # ObpkArchive.cs also resolves rare ambiguous cases (never seen in the real install); this walk only warns on them.
        $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        $cursor = 0; $first = 0; $inOrder = $true
        foreach ($g in $groups) {
            $files = @(); if ($g.Files -gt 0) { $files = @($entries.GetRange($first, [int]$g.Files)) }
            $first += $files.Count
            $firstNew = $null; foreach ($e in $files) { if (-not $seen.Contains($e.Name)) { $firstNew = $e.Name; break } }
            if ($cursor -lt $nameCount -and -not $seen.Contains($names[$cursor]) -and -not [string]::Equals($names[$cursor], $firstNew, [StringComparison]::Ordinal)) {
                $g.Name = $names[$cursor]; $null = $seen.Add($g.Name); $cursor++
            } else { $warnings.Add("group $($g.Index): type name not at its expected place in the name table") }
            foreach ($e in $files) {
                if (-not $seen.Add($e.Name)) { continue }
                if ($cursor -ge $nameCount -or -not [string]::Equals($names[$cursor], $e.Name, [StringComparison]::Ordinal)) { $inOrder = $false }
                $cursor++
            }
            foreach ($e in $files) {
                $dir = $e.Folder.Replace('\', '/').Trim('/')
                $prefix = if ($dir) { "$dir/" } else { '' }
                $e.Path = "$prefix$($e.Name).$($g.Name)"; $e.Group = $g.Name
            }
        }
        $tableEnd = $fs.Position
        $dataOffset = [long]$h.TableOffset + $h.DataOffsetFromTable
        if ($dataOffset -lt $tableEnd -or $dataOffset + 4 -gt $len) { throw "invalid data offset $dataOffset" }
        $fs.Position = $dataOffset
        $blockSize = $r.ReadUInt32()
        if ($entries.Count -ne $h.FileCount) { $warnings.Add("entries $($entries.Count) != header FileCount $($h.FileCount)") }
        if ($blockSize -ne $h.DecompressedSize) { $warnings.Add("data block size $blockSize != header $($h.DecompressedSize)") }
        if ($offset -ne $blockSize) { $warnings.Add("entries sum $offset != data block size $blockSize") }
        if (-not $inOrder -or $cursor -ne $nameCount) { $warnings.Add("name table $nameCount strings does not match string-pool layout ($cursor)") }
        if ($dataOffset -ne (($tableEnd + 0xFFF) -band (-bnot 0xFFF))) { $warnings.Add('data offset not 0x1000-aligned table end') }
        return [pscustomobject]@{ Header = $h; Groups = $groups; Entries = $entries; DataOffset = $dataOffset; TableEnd = $tableEnd; Warnings = $warnings; IdLists = $idCount; Names = $nameCount }
    }
    finally { $fs.Dispose() }
}

# Streams the zlib block once, capturing the first bytes of up to $perGroup entries per group.
function Get-InnerSamples([string]$path, $arc, [int]$perGroup = 3) {
    $want = @{}; $counts = @{}
    foreach ($e in $arc.Entries) { if (-not $counts[$e.Group]) { $counts[$e.Group] = 0 }; if ($counts[$e.Group] -lt $perGroup -and $e.Size -gt 0) { $want[$e.Offset] = $e; $counts[$e.Group]++ } }
    $samples = @{}
    $fs = Open-ReadOnly $path
    try {
        $fs.Position = $arc.DataOffset + 4 + 2   # skip size field and 2-byte zlib header; DeflateStream reads raw deflate
        $z = New-Object System.IO.Compression.DeflateStream($fs, [System.IO.Compression.CompressionMode]::Decompress)
        $buf = New-Object byte[] 65536; [long]$pos = 0
        foreach ($off in ($want.Keys | Sort-Object)) {
            while ($pos -lt $off) { $n = $z.Read($buf, 0, [int][Math]::Min($buf.Length, $off - $pos)); if ($n -le 0) { throw 'compressed data ended early' }; $pos += $n }
            $e = $want[$off]; $take = [int][Math]::Min(32, $e.Size); $head = New-Object byte[] $take; $got = 0
            while ($got -lt $take) { $n = $z.Read($head, $got, $take - $got); if ($n -le 0) { throw 'compressed data ended early' }; $got += $n }
            $pos += $take
            if (-not $samples[$e.Group]) { $samples[$e.Group] = @() }
            $samples[$e.Group] += [pscustomobject]@{ Path = $e.Path; Size = $e.Size; Sig = (Format-Sig $head) }
        }
    }
    finally { $fs.Dispose() }
    return $samples
}

# ---------------------------------------------------------------- locate installation
$config = Read-LocalConfig
$steamInfo = [ordered]@{ libraries = 0; manifests = @() }
$candidates = @()
if (-not $GameDir -and $config -and $config.gameDir) { $GameDir = $config.gameDir }
if (-not $GameDir) {
    $roots = if ($SteamRoot) { $SteamRoot } else { @(Get-SteamRoots) }
    $libs = @(Get-SteamLibraries $roots)
    $steamInfo.libraries = $libs.Count
    $libIndex = 0
    foreach ($lib in $libs) {
        foreach ($id in $AppIds.Keys) {
            $acf = Join-Path $lib "steamapps/appmanifest_$id.acf"
            if (Test-Path -LiteralPath $acf) {
                $m = Read-AppManifest $acf
                $m['library'] = "<steam library $libIndex>"
                $m['edition'] = $AppIds[$id]
                $steamInfo.manifests += $m
                $dir = Join-Path $lib ('steamapps/common/' + $m.installdir)
                if (Test-Path -LiteralPath $dir) { $candidates += [pscustomobject]@{ AppId = $id; Dir = $dir } }
            }
        }
        $libIndex++
    }
    $orig = $candidates | Where-Object AppId -eq '55150' | Select-Object -First 1
    if ($orig) { $GameDir = $orig.Dir } elseif ($candidates) { $GameDir = $candidates[0].Dir }
}
if (-not $GameDir -or -not (Test-Path -LiteralPath $GameDir)) {
    Write-Host 'Could not find the game. Re-run with:  -GameDir "<folder containing the game>"' -ForegroundColor Yellow
    Write-Host '(or copy config/local.example.json to config/local.json and set gameDir)'
    exit 3
}
$GameDir = (Resolve-Path -LiteralPath $GameDir).ProviderPath.TrimEnd('\', '/')
$edition = ($candidates | Where-Object { $_.Dir.TrimEnd('\', '/') -eq $GameDir } | Select-Object -First 1).AppId

if (-not $OutDir) { $OutDir = Join-Path (Get-LocalDataRoot) ('discovery/' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
if ($OutDir.StartsWith($GameDir, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output folder must not be inside the game folder.' }
New-Item -ItemType Directory -Force -Path (Join-Path $OutDir 'archives') | Out-Null
Write-Host "Game folder found (edition app id: $(if ($edition) { $edition } else { 'unknown/manual' })). Writing report to $OutDir"

function Rel([string]$full) { return $full.Substring($GameDir.Length).TrimStart('\', '/') -replace '\\', '/' }

# ---------------------------------------------------------------- inventory
$files = @(Get-ChildItem -LiteralPath $GameDir -Recurse -File -Force -ErrorAction SilentlyContinue)
$totalBytes = ($files | Measure-Object Length -Sum).Sum
$byExt = $files | Group-Object { if ($_.Extension) { $_.Extension.ToLowerInvariant() } else { '(none)' } } |
    ForEach-Object { [pscustomobject]@{ Ext = $_.Name; Count = $_.Count; Bytes = ($_.Group | Measure-Object Length -Sum).Sum } } | Sort-Object Bytes -Descending
$byTop = $files | Group-Object { $p = Rel $_.FullName; if ($p.Contains('/')) { $p.Split('/')[0] + '/' } else { '(root)' } } |
    ForEach-Object { [pscustomobject]@{ Dir = $_.Name; Count = $_.Count; Bytes = ($_.Group | Measure-Object Length -Sum).Sum } } | Sort-Object Bytes -Descending
$largest = $files | Sort-Object Length -Descending | Select-Object -First 25 | ForEach-Object { [pscustomobject]@{ Path = Rel $_.FullName; Bytes = $_.Length } }

$exeInfo = @($files | Where-Object { $_.Extension -in '.exe', '.dll' -and $_.DirectoryName -eq $GameDir -or $_.Extension -eq '.exe' } | Select-Object -First 40 | ForEach-Object {
        $v = $_.VersionInfo
        [pscustomobject]@{ Path = Rel $_.FullName; Bytes = $_.Length; FileVersion = $v.FileVersion; ProductVersion = $v.ProductVersion; ProductName = $v.ProductName; Company = $v.CompanyName; ModifiedUtc = $_.LastWriteTimeUtc.ToString('u') }
    })

$sigs = foreach ($g in ($files | Group-Object { $_.Extension.ToLowerInvariant() })) {
    foreach ($f in ($g.Group | Sort-Object Length | Select-Object -First 3)) {
        $s = try { Format-Sig (Get-Head $f.FullName) } catch { "(unreadable: $($_.Exception.Message))" }
        [pscustomobject]@{ Ext = $g.Name; Path = Rel $f.FullName; Bytes = $f.Length; Sig = $s }
    }
}

# ---------------------------------------------------------------- archives
$archives = @()
$innerExt = @{}
$innerSamples = @{}
$obpkFiles = $files | Where-Object { $_.Length -ge 49 } | Where-Object { $h = Get-Head $_.FullName 5; [Text.Encoding]::ASCII.GetString($h) -eq "OBPK`0" }
$i = 0
foreach ($f in $obpkFiles) {
    $i++; Write-Progress -Activity 'Reading OBPK archives' -Status (Rel $f.FullName) -PercentComplete (100 * $i / [Math]::Max(1, @($obpkFiles).Count))
    $rel = Rel $f.FullName
    try {
        $a = Read-Obpk $f.FullName
        $safe = ($rel -replace '[\\/:]', '_')
        $a.Entries | Select-Object Path, Group, Size, Offset, Unknown, ExtraLen | Export-Csv -NoTypeInformation -Path (Join-Path $OutDir "archives/$safe.csv")
        foreach ($e in $a.Entries) {
            if (-not $innerExt[$e.Group]) { $innerExt[$e.Group] = [pscustomobject]@{ Group = $e.Group; Count = 0; Bytes = [long]0; Archives = New-Object System.Collections.Generic.HashSet[string] } }
            $x = $innerExt[$e.Group]; $x.Count++; $x.Bytes += $e.Size; $null = $x.Archives.Add($rel)
        }
        $sampleNote = 'skipped'
        if (-not $NoInnerSamples -and $f.Length -le $MaxSampleMB * 1MB) {
            try {
                $s = Get-InnerSamples $f.FullName $a
                foreach ($k in $s.Keys) { if (-not $innerSamples[$k]) { $innerSamples[$k] = @() }; if ($innerSamples[$k].Count -lt 3) { $innerSamples[$k] += $s[$k] | Select-Object -First (3 - $innerSamples[$k].Count) } }
                $sampleNote = 'ok'
            }
            catch { $sampleNote = "failed: $($_.Exception.Message)" }
        }
        $archives += [pscustomobject]@{ Path = $rel; Bytes = $f.Length; Version = $a.Header.Version; Entries = $a.Entries.Count; Groups = (($a.Groups | ForEach-Object { "$($_.Name):$($_.Files)" }) -join ' '); Decompressed = $a.Header.DecompressedSize; DataOffset = $a.DataOffset; TableEnd = $a.TableEnd; Unknown17 = $a.Header.Unknown17; Unknown41 = $a.Header.Unknown41; Unknown45 = $a.Header.Unknown45; Warnings = (@($a.Warnings) -join '; '); InnerSamples = $sampleNote; Error = '' }
    }
    catch { $archives += [pscustomobject]@{ Path = $rel; Bytes = $f.Length; Error = $_.Exception.Message } }
}
Write-Progress -Activity 'Reading OBPK archives' -Completed

# ---------------------------------------------------------------- gameplay-data candidates
$pattern = '(?i)(attr|weapon|wargear|move|locomot|anim|event|camera|player|damage|melee|execut|fury|health|tuning|balance|ucs|\.lua$|\.scar$|\.xml$|\.ini$|\.cfg$|\.bod$|\.baf$|\.rgd$)'
$loose = @($files | Where-Object { (Rel $_.FullName) -match $pattern -and $_.Extension -ne '.oppc' } | Select-Object -First 200 | ForEach-Object { [pscustomobject]@{ Where = 'loose'; Path = Rel $_.FullName; Bytes = $_.Length } })
$inner = @()
foreach ($csv in Get-ChildItem -LiteralPath (Join-Path $OutDir 'archives') -Filter *.csv) {
    $arcName = $csv.BaseName
    $inner += Import-Csv -LiteralPath $csv.FullName | Where-Object { $_.Path -match $pattern } | Select-Object -First 60 | ForEach-Object { [pscustomobject]@{ Where = $arcName; Path = $_.Path; Bytes = [long]$_.Size } }
}

# ---------------------------------------------------------------- write reports
$report = [ordered]@{
    tool = "tools/discover.ps1 $ScriptVersion"; generatedUtc = (Get-Date).ToUniversalTime().ToString('u')
    os = [Environment]::OSVersion.VersionString; powershell = $PSVersionTable.PSVersion.ToString()
    editionAppId = $edition; steam = $steamInfo
    totals = [ordered]@{ files = $files.Count; bytes = $totalBytes; obpkArchives = @($archives).Count; innerEntries = ($innerExt.Values | Measure-Object Count -Sum).Sum }
    executables = $exeInfo; extensions = @($byExt); topDirs = @($byTop); largest = @($largest); signatures = @($sigs)
    archives = @($archives)
    innerGroups = @($innerExt.Values | Sort-Object Bytes -Descending | ForEach-Object { [ordered]@{ group = $_.Group; count = $_.Count; bytes = $_.Bytes; archives = $_.Archives.Count } })
    innerSamples = $innerSamples; gameplayCandidates = @($loose + $inner)
}
$report | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -Path (Join-Path $OutDir 'report.json')

function MdTable($rows, [string[]]$cols) {
    $lines = @(('| ' + ($cols -join ' | ') + ' |'), ('|' + (($cols | ForEach-Object { '---' }) -join '|') + '|'))
    foreach ($r in $rows) { $lines += '| ' + (($cols | ForEach-Object { "$($r.$_)" -replace '\|', '\|' }) -join ' | ') + ' |' }
    return $lines -join "`n"
}
$md = @"
# Space Marine installation inventory

Generated $($report.generatedUtc) by $($report.tool) on $($report.os), PowerShell $($report.powershell).
Paths are relative to the game folder. No original file contents beyond 16-32 byte signatures are included.

## Edition and build

Edition app id: $(if ($edition) { $edition } else { 'unknown (manual -GameDir)' }). Steam libraries scanned: $($steamInfo.libraries).

$(($steamInfo.manifests | ForEach-Object { "- **$($_.edition)**: appid $($_.appid), name '$($_.name)', buildid $($_.buildid), installdir '$($_.installdir)', SizeOnDisk $($_.SizeOnDisk), LastUpdated $($_.LastUpdatedUtc), language $($_['UserConfig.language']), beta '$($_['UserConfig.BetaKey'])$($_['UserConfig.betakey'])', depots: $(($_.InstalledDepots | ForEach-Object { "$($_.depot)@$($_.manifest)" }) -join ', ')" }) -join "`n")

## Executables

$(MdTable $exeInfo @('Path','Bytes','FileVersion','ProductVersion','ProductName','ModifiedUtc'))

## Totals

$($files.Count) files, $totalBytes bytes, $(@($archives).Count) OBPK archives, $($report.totals.innerEntries) archive entries.

## Top-level folders

$(MdTable $byTop @('Dir','Count','Bytes'))

## Extensions (loose files)

$(MdTable $byExt @('Ext','Count','Bytes'))

## Largest files

$(MdTable $largest @('Path','Bytes'))

## Signatures (up to 3 smallest files per extension)

$(MdTable $sigs @('Ext','Path','Bytes','Sig'))

## OBPK archives

$(MdTable $archives @('Path','Bytes','Version','Entries','Decompressed','DataOffset','Unknown17','Unknown41','Unknown45','InnerSamples','Warnings','Error'))

### Groups per archive

$(($archives | Where-Object Groups | ForEach-Object { "- $($_.Path): $($_.Groups)" }) -join "`n")

## Inner file types (all archives)

$(MdTable ($report.innerGroups | ForEach-Object { [pscustomobject]$_ }) @('group','count','bytes','archives'))

## Inner signatures

$(($innerSamples.Keys | Sort-Object | ForEach-Object { $k = $_; $innerSamples[$k] | ForEach-Object { "- ``$k`` $($_.Path) ($($_.Size) B): ``$($_.Sig)``" } }) -join "`n")

## Likely gameplay-data files

$(MdTable $report.gameplayCandidates @('Where','Path','Bytes'))
"@
$md | Set-Content -Encoding UTF8 -Path (Join-Path $OutDir 'report.md')
Write-Host "Done. $($files.Count) files, $(@($archives).Count) archives."
Write-Host "Report: $(Join-Path $OutDir 'report.md')"
# Private, local-only pointer for tools/m1.ps1 (the discovery folder is git-ignored and outside the repo).
Set-Content -Encoding UTF8 -Path (Join-Path $OutDir 'game-dir.txt') -Value $GameDir
