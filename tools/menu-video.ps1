<#
.SYNOPSIS
  Converts the owner's main menu video (his own cinematic of the Ashfall Citadel map, 2026-11-10) into the looping Ogg Theora file the client plays
  behind the main menu (client\assets\menu\menu-loop.ogv). Developer tool, never shipped; reads the source, writes only the output file.
.DESCRIPTION
  tools\menu-video.ps1 -Source <video> [-Out <file.ogv>] [-Quality 7] [-Crossfade 0.5] [-Fps 30] [-Width 1920] [-Height 1080] [-FFmpeg <ffmpeg.exe>]

  Godot 4.7 plays Ogg Theora only (VideoStreamTheora), so the source (any format ffmpeg reads, e.g. the owner's H.264 MP4 with a variable frame rate)
  becomes a constant -Fps, -Width x -Height (Lanczos), 4:2:0, Theora at -Quality (0 to 10; 7 gave about 20 MB and SSIM 0.987 against the uncompressed
  loop for the 31.8 s source), without audio. The loop seam: the source's last frames do not meet its first (debris and embers jump), so the first
  -Crossfade seconds are cut from the start and faded in over the last -Crossfade seconds; the file then ends on the source frame just before its
  own first one and loops without a jump (the result is -Crossfade seconds shorter than the source). -Crossfade 0 keeps the cut as it is.
  At the end it prints the file's size and frame count and the SSIM of its last frame against its first (the seam; the owner's 2026-11-10 source:
  0.94 uncut, where one frame step of its slow start is about 0.997 and of its middle about 0.98).
  ffmpeg: -FFmpeg, else %SMREIMPL_FFMPEG%, else ffmpeg on PATH (it needs the libtheora encoder, as the gyan.dev "essentials" builds have).
#>
param(
    [Parameter(Mandatory)][string]$Source,
    [string]$Out = '',
    [ValidateRange(0, 10)][int]$Quality = 7,
    [ValidateRange(0.0, 5.0)][double]$Crossfade = 0.5,
    [ValidateRange(1, 120)][int]$Fps = 30,
    [int]$Width = 1920,
    [int]$Height = 1080,
    [string]$FFmpeg = ''
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $RepoRoot 'client\assets\menu\menu-loop.ogv' }
if (-not $FFmpeg) { $FFmpeg = if ($env:SMREIMPL_FFMPEG) { $env:SMREIMPL_FFMPEG } else { 'ffmpeg' } }
if (-not (Get-Command $FFmpeg -ErrorAction SilentlyContinue)) { throw "ffmpeg not found ($FFmpeg): pass -FFmpeg <path to ffmpeg.exe> or set SMREIMPL_FFMPEG." }
if (-not (Test-Path -LiteralPath $Source)) { throw "source not found: $Source" }
$inv = [Globalization.CultureInfo]::InvariantCulture

function Invoke-FFmpeg([string[]]$Arguments) {
    # ffmpeg reports on stderr; the lines are returned (as text) for the counts and the SSIM.
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $lines = & $FFmpeg @Arguments 2>&1 | ForEach-Object { "$_" } } finally { $ErrorActionPreference = $old }
    if ($LASTEXITCODE -ne 0) { $lines | Select-Object -Last 15 | Write-Host; throw "ffmpeg failed (exit $LASTEXITCODE)" }
    return $lines
}

function Get-FrameCount([string]$Path, [string]$Filter) {
    $lines = Invoke-FFmpeg @('-hide_banner', '-nostats', '-i', $Path, '-vf', $Filter, '-an', '-f', 'null', '-')
    $last = ($lines | Select-String -Pattern 'frame=\s*(\d+)' -AllMatches | Select-Object -Last 1)
    if (-not $last) { throw "could not count the frames of $Path" }
    return [int]$last.Matches[-1].Groups[1].Value
}

$scale = "fps=$Fps,scale=${Width}:${Height}:flags=lanczos,format=yuv420p"
$frames = Get-FrameCount $Source "fps=$Fps"
$fade = [int][Math]::Round($Crossfade * $Fps)
if ($fade -gt 0 -and $frames -le 3 * $fade) { throw "the source is too short ($frames frames at $Fps fps) for a $Crossfade s crossfade" }
Write-Host ("source: {0} frames at {1} fps ({2:F2} s); crossfade {3} frames" -f $frames, $Fps, ($frames / $Fps), $fade)

if ($fade -gt 0) {
    # [h]: the source's first $fade + 1 frames; [m]: the rest. xfade blends [m]'s last $fade frames into [h]'s first $fade (its last blended frame
    # still holds 1/$fade of [m]), then [h]'s last frame plays unblended, so the file ends on the source frame just before [m]'s first one, where
    # the loop starts again: the seam is one of the source's own frame steps. $frames - $fade frames in all.
    $head = $fade + 1
    $offset = (($frames - $head - $fade) / $Fps).ToString('0.######', $inv)
    $duration = ($fade / $Fps).ToString('0.######', $inv)
    $graph = "[0:v]$scale,split[a][b];[a]trim=start_frame=$head,setpts=PTS-STARTPTS[m];[b]trim=end_frame=$head,setpts=PTS-STARTPTS[h];" +
             "[m][h]xfade=transition=fade:duration=${duration}:offset=$offset,format=yuv420p[v]"
} else {
    $graph = "[0:v]$scale[v]"
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent ([IO.Path]::GetFullPath($Out))) | Out-Null
Write-Host "encoding $Out (Theora q $Quality, no audio)..."
$null = Invoke-FFmpeg @('-hide_banner', '-nostats', '-y', '-i', $Source, '-filter_complex', $graph, '-map', '[v]', '-an', '-c:v', 'libtheora', '-pix_fmt', 'yuv420p', '-q:v', "$Quality", $Out)

$outFrames = Get-FrameCount $Out 'null'
$last = $outFrames - 1
$ssim = Invoke-FFmpeg @('-hide_banner', '-nostats', '-i', $Out, '-filter_complex', "[0:v]split[x][y];[x]select=eq(n\,0),setpts=0[first];[y]select=eq(n\,$last),setpts=0[end];[end][first]ssim", '-f', 'null', '-')
$seam = ($ssim | Select-String -Pattern 'All:([0-9.]+)' | Select-Object -Last 1)
$size = (Get-Item -LiteralPath $Out).Length
Write-Host ("done: {0} ({1:N1} MB, {2} frames, {3:F2} s at {4} fps, {5}x{6}); loop seam SSIM last->first {7}" -f $Out, ($size / 1MB), $outFrames, ($outFrames / $Fps), $Fps, $Width, $Height, $(if ($seam) { $seam.Matches[0].Groups[1].Value } else { '?' }))
