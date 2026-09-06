# gen_icon.ps1 — 生成 Assets/app.ico (暖色圆 + 中心暗点, PNG-in-ICO 多尺寸)
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "app.ico"
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$entries = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $rect = New-Object System.Drawing.Rectangle(1, 1, ($s - 2), ($s - 2))
    $c1 = [System.Drawing.Color]::FromArgb(255, 255, 150, 40)
    $c2 = [System.Drawing.Color]::FromArgb(255, 255, 60, 110)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 45.0)
    $g.FillEllipse($brush, $rect)
    $dr = [Math]::Max(2, [int]($s * 0.18))
    $dark = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(235, 28, 28, 34))
    $g.FillEllipse($dark, [int]($s * 0.5 - $dr / 2), [int]($s * 0.5 - $dr / 2), $dr, $dr)
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $entries += , @($s, $ms.ToArray())
    $g.Dispose(); $bmp.Dispose()
}
$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ico)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $s = [int]$e[0]; $data = [byte[]]$e[1]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($e in $entries) { $bw.Write([byte[]]$e[1]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($out, $ico.ToArray())
Write-Host "icon written: $out ($($ico.Length) bytes)"
