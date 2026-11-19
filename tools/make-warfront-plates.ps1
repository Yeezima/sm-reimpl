<#
Makes the main menu's WARFRONT and STOP WARFRONT SERVER plates (client/assets/ui/btn-musou-*.png and btn-stop-*.png, the files the ids "musou" and "stop" name) from the
plates that carried MUSOU MODE and STOP MUSOU SERVER: the old label is painted out of each plate (each row between the label's ends is the plate's own pixels just left and right of it,
blended across; the same way UiTextureBuilder.FaithfulButtons paints a label out) and the new words are drawn in the menu's own font (Oswald, bold, spaced) in the label's ink
colour with a short top-to-bottom shade and a soft shadow, at the old label's height and centre. The owner's concept art is not needed (it is outside the repository).
Owner decision 2026-11-19: WARFRONT replaces Musou Mode in the menu. Run from the repository root (Windows PowerShell 5.1):  powershell -File tools\make-warfront-plates.ps1 [-Preview <dir>]
It reads the plates it finds in client/assets/ui; run it once on the Musou plates (a second run repaints the Warfront ones with the same text).
#>
param([string]$Preview = "")
Add-Type -AssemblyName System.Drawing
$ui = Join-Path $PSScriptRoot "..\client\assets\ui" | Resolve-Path
$fontFile = (Join-Path $PSScriptRoot "..\client\assets\fonts\Oswald-wght.ttf" | Resolve-Path).Path
$pfc = New-Object System.Drawing.Text.PrivateFontCollection
$pfc.AddFontFile($fontFile)
$family = $pfc.Families[0]

function Bright($c) { return ($c.R -gt 215 -and $c.G -gt 205 -and $c.B -gt 175) }

function Make($file, $text, [double]$widthLimit) {
    $path = Join-Path $ui $file
    $src = [System.Drawing.Bitmap]::FromFile($path)
    $bmp = New-Object System.Drawing.Bitmap $src.Width, $src.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g0 = [System.Drawing.Graphics]::FromImage($bmp); $g0.DrawImage($src, 0, 0, $src.Width, $src.Height); $g0.Dispose(); $src.Dispose()
    # the old label's box and ink
    $minx = 99999; $maxx = 0; $miny = 99999; $maxy = 0; $r = 0.0; $gg = 0.0; $b = 0.0; $n = 0
    for ($y = 30; $y -lt $bmp.Height - 30; $y++) { for ($x = 120; $x -lt $bmp.Width - 120; $x++) { $c = $bmp.GetPixel($x, $y); if (Bright $c) {
        if ($x -lt $minx) { $minx = $x }; if ($x -gt $maxx) { $maxx = $x }; if ($y -lt $miny) { $miny = $y }; if ($y -gt $maxy) { $maxy = $y }; $r += $c.R; $gg += $c.G; $b += $c.B; $n++ } } }
    if ($n -lt 500) { throw "$file : no label found" }
    $ink = [System.Drawing.Color]::FromArgb(255, [int]($r / $n), [int]($gg / $n), [int]($b / $n))
    $cx = ($minx + $maxx) / 2.0; $cy = ($miny + $maxy) / 2.0; $h = $maxy - $miny + 1; $w = $maxx - $minx + 1
    # paint it out: each row from the plate just left of the label to the plate just right of it
    $pad = 6
    for ($y = $miny - $pad; $y -le $maxy + $pad; $y++) {
        $xa = $minx - 34; $xb = $maxx + 34
        $la = 0.0; $lb = 0.0; $lc = 0.0; $ra = 0.0; $rb = 0.0; $rc = 0.0
        for ($k = 0; $k -lt 10; $k++) { $p = $bmp.GetPixel($xa + $k, $y); $la += $p.R; $lb += $p.G; $lc += $p.B; $q = $bmp.GetPixel($xb - $k, $y); $ra += $q.R; $rb += $q.G; $rc += $q.B }
        for ($x = $minx - $pad; $x -le $maxx + $pad; $x++) {
            $t = ($x - ($minx - $pad)) / [double]($w + 2 * $pad)
            $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, [int](($la + ($ra - $la) * $t) / 10), [int](($lb + ($rb - $lb) * $t) / 10), [int](($lc + ($rc - $lc) * $t) / 10)))
        }
    }
    # the new words: Oswald bold, spaced, at the old height; narrower than the old label's width limit (longer words are made smaller)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = [System.Drawing.StringAlignment]::Near
    $size = 100.0
    $tracking = 0.0
    for ($pass = 0; $pass -lt 6; $pass++) {
        $path0 = New-Object System.Drawing.Drawing2D.GraphicsPath
        $x0 = 0.0
        foreach ($ch in $text.ToCharArray()) {
            if ($ch -eq ' ') { $x0 += $size * 0.34 + $tracking; continue }
            $p1 = New-Object System.Drawing.Drawing2D.GraphicsPath
            $p1.AddString([string]$ch, $family, [int][System.Drawing.FontStyle]::Bold, [single]$size, (New-Object System.Drawing.PointF([single]$x0, [single]0)), $sf)
            $bb = $p1.GetBounds()
            $mv = New-Object System.Drawing.Drawing2D.Matrix; $mv.Translate([single]($x0 - $bb.X), 0); $p1.Transform($mv)
            $bb = $p1.GetBounds()
            $path0.AddPath($p1, $false)
            $x0 = $bb.Right + $size * 0.15 + $tracking
        }
        $bb = $path0.GetBounds()
        $scale = $h / $bb.Height
        if ($bb.Width * $scale -gt $widthLimit) { $scale = $widthLimit / $bb.Width }
        $size = $size * $scale; if ($pass -ge 1) { break }
    }
    $m = New-Object System.Drawing.Drawing2D.Matrix
    $bb = $path0.GetBounds()
    $m.Translate([single](-($bb.X + $bb.Width / 2)), [single](-($bb.Y + $bb.Height / 2)), [System.Drawing.Drawing2D.MatrixOrder]::Append)
    $m.Scale([single]$scale, [single]$scale, [System.Drawing.Drawing2D.MatrixOrder]::Append)
    $m.Translate([single]$cx, [single]$cy, [System.Drawing.Drawing2D.MatrixOrder]::Append)
    $path0.Transform($m)
    $fb = $path0.GetBounds()
    # a soft shadow, then the letters shaded from the ink at the top to 82 percent of it at the bottom
    $shadow = New-Object System.Drawing.Drawing2D.Matrix; $shadow.Translate(1.5, 2.5)
    $ps = $path0.Clone(); $ps.Transform($shadow)
    $sb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(110, 10, 8, 6))
    $g.FillPath($sb, $ps)
    $dark = [System.Drawing.Color]::FromArgb(255, [int]($ink.R * 0.90), [int]($ink.G * 0.90), [int]($ink.B * 0.90))
    $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF(0, [single]$fb.Top)), (New-Object System.Drawing.PointF(0, [single]$fb.Bottom)), $ink, $dark)
    $g.FillPath($lg, $path0)
    $pen = New-Object System.Drawing.Pen ($lg, [single]($h * 0.022)); $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($pen, $path0)   # the letters a little heavier, as the concept's painted ones are
    $g.Dispose()
    $out = if ($Preview) { Join-Path $Preview $file } else { $path }
    if ($Preview) { New-Item -ItemType Directory -Force $Preview | Out-Null }
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "{0}: old label {1}x{2} at {3:N0},{4:N0}; ink {5}; new {6:N0}x{7:N0}" -f $file, $w, $h, $cx, $cy, $ink.Name, $fb.Width, $fb.Height
}

# the widths allowed: the old labels' (MUSOU MODE 484 px, STOP MUSOU SERVER the same plate, the longer words smaller)
Make "btn-musou-gold.png" "WARFRONT" 520
Make "btn-musou-dark.png" "WARFRONT" 520
Make "btn-stop-gold.png" "STOP WARFRONT SERVER" 880
Make "btn-stop-dark.png" "STOP WARFRONT SERVER" 880

