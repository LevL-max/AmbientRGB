# Builds AmbientRGB with the C# compiler that ships with Windows (.NET Framework 4.x) — nothing to install.
#   .\build.ps1          app + diagnostics
#   .\build.ps1 -Tools   also the developer tools (ListDevices, AudioProbe, ScreenProbe)
param([switch]$Tools)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $root 'bin'
$icon = Join-Path $root 'app.ico'
New-Item -ItemType Directory -Force $out | Out-Null

if (-not (Test-Path $icon)) {
    # Draw a simple ring icon (PNG-in-ICO, 16/32/48/256 px).
    Add-Type -AssemblyName System.Drawing
    $sizes = 16, 32, 48, 256
    $pngs = foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $rect = New-Object System.Drawing.RectangleF ($s * 0.08), ($s * 0.08), ($s * 0.84), ($s * 0.84)
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(0, 255, 110)), ([System.Drawing.Color]::FromArgb(0, 150, 255)), 45.0
        $g.FillEllipse($brush, $rect)
        $inner = New-Object System.Drawing.RectangleF ($s * 0.30), ($s * 0.30), ($s * 0.40), ($s * 0.40)
        $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(20, 24, 28))), $inner)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        , $ms.ToArray()
    }
    $fs = [System.IO.File]::Create($icon)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $bw.Write($p) }
    $bw.Close()
}

$src = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$refs = '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll'

& $csc /nologo /optimize+ /platform:x64 /target:winexe "/win32icon:$icon" "/out:$out\AmbientRGB.exe" $refs $src
if ($LASTEXITCODE -ne 0) { throw "Build failed (AmbientRGB.exe)" }
& $csc /nologo /optimize+ /platform:x64 /target:exe /define:DIAG "/out:$out\AmbientRGB-diag.exe" $refs $src
if ($LASTEXITCODE -ne 0) { throw "Build failed (AmbientRGB-diag.exe)" }

if ($Tools) {
    $lib = $src | Where-Object { (Split-Path $_ -Leaf) -notin 'Program.cs', 'TrayApp.cs', 'SettingsForm.cs' }
    foreach ($tool in Get-ChildItem (Join-Path $root 'tools') -Filter *.cs) {
        & $csc /nologo /optimize+ /platform:x64 /target:exe "/out:$out\$($tool.BaseName).exe" $refs $tool.FullName $lib
        if ($LASTEXITCODE -ne 0) { throw "Build failed ($($tool.Name))" }
    }
}
Write-Host "Built: $out\AmbientRGB.exe"
