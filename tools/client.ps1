<#
.SYNOPSIS
  Builds and runs the Godot client. Downloads Godot 4.7.2-stable .NET into %LOCALAPPDATA%\sm-reimpl\tools\godot
  on first use (official GitHub release, SHA-512 checked). Read-only against the game install.
.EXAMPLE
  client.cmd                       (the front end: Join screen)
  client.cmd --viewer              (the animated marine viewer)
  client.cmd --archive data/chunks/mp_tdm_17__r000_main.oppc --mesh WalkwayA
  client.cmd --viewer --screenshot "%LOCALAPPDATA%\sm-reimpl\screenshots\marine.png" --quit
#>
$ErrorActionPreference = 'Stop'
$Version = '4.7.2-stable'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Tools = Join-Path $env:LOCALAPPDATA 'sm-reimpl\tools'
$Name = "Godot_v$($Version)_mono_win64"
# SHA-512 of the official zip, pinned so a changed release asset is refused (also checked against SHA512-SUMS.txt).
$Sha512 = '79229FD112B0C9CBEAB82363A4EF7BE18EA70F1CAF86BF912789335B136FBE7E01DB0053A33461438C5DA1C680C17BBB10040BD09BEDC51221CD4423D0367757'
$Godot = Join-Path $Tools "godot\$Name\$($Name)_console.exe"
$Marker = Join-Path $Tools "godot\$Name.complete"    # written only after a full, verified extraction

if (-not (Test-Path -LiteralPath $Marker)) {
    Write-Host "Downloading Godot $Version (.NET, about 117 MB)..." -ForegroundColor Yellow
    $dl = Join-Path $Tools 'download'
    New-Item -ItemType Directory -Force -Path $dl | Out-Null
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $base = "https://github.com/godotengine/godot/releases/download/$Version"
    $zip = Join-Path $dl "$Name.zip"
    Invoke-WebRequest -UseBasicParsing -Uri "$base/$Name.zip" -OutFile $zip
    $sums = (Invoke-WebRequest -UseBasicParsing -Uri "$base/SHA512-SUMS.txt").Content
    $expected = ($sums -split "`n" | Where-Object { $_ -match [regex]::Escape("$Name.zip") } | Select-Object -First 1) -split '\s+' | Select-Object -First 1
    $actual = (Get-FileHash -Algorithm SHA512 -LiteralPath $zip).Hash
    if (-not $expected -or $actual -ne $expected.ToUpperInvariant() -or $actual -ne $Sha512) { throw "Godot download failed its SHA-512 check." }
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $Tools 'godot') -Force
    Set-Content -LiteralPath $Marker -Value $Version -Encoding ascii
    Remove-Item -LiteralPath $zip
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET 10 SDK is required: run m1.cmd once, or winget install Microsoft.DotNet.SDK.10' }
$client = Join-Path $RepoRoot 'client'
dotnet build (Join-Path $client 'SmClient.csproj') -nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not (Test-Path -LiteralPath (Join-Path $client '.godot'))) { & $Godot --headless --path $client --import | Out-Null }

# Quoted so the separator always reaches Godot; arguments after it go to the client (OS.GetCmdlineUserArgs).
& $Godot --path $client '--' @args
exit $LASTEXITCODE
