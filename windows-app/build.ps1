# Builds DormVPN.exe and assembles the installable package in ..\dist\windows
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$dist = Join-Path $root 'dist\windows'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

# app icon (same shield as the tray icon, green)
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 64, 64
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.ScaleTransform(2, 2)
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddLine(16, 2, 29, 7); $path.AddBezier(29, 7, 29, 19, 24, 26, 16, 30); $path.AddBezier(16, 30, 8, 26, 3, 19, 3, 7); $path.CloseFigure()
$g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(34, 165, 89))), $path)
$w = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$g.FillEllipse($w, 12, 9, 8, 8)
$g.FillPolygon($w, [System.Drawing.Point[]]@((New-Object System.Drawing.Point 14, 15), (New-Object System.Drawing.Point 18, 15), (New-Object System.Drawing.Point 19, 23), (New-Object System.Drawing.Point 13, 23)))
$g.Dispose()
$icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
$fs = [IO.File]::Create("$here\dormvpn.ico"); $icon.Save($fs); $fs.Close()

# fresh client config with the real server IP
& "$root\make-configs.ps1" | Out-Null

# compile
& $csc /nologo /target:winexe /optimize+ /out:"$here\DormVPN.exe" /win32icon:"$here\dormvpn.ico" /win32manifest:"$here\app.manifest" `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll "$here\DormVPN.cs"
if ($LASTEXITCODE -ne 0) { throw "compile failed" }

# package
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force "$dist\app" | Out-Null
Copy-Item "$here\DormVPN.exe" "$dist\app\"
Copy-Item "$root\bin\sing-box.exe", "$root\bin\libcronet.dll", "$root\bin\LICENSE" "$dist\app\"
Copy-Item "$root\out\windows-client.json" "$dist\app\config.json"
Copy-Item "$here\install.cmd", "$here\install.ps1", "$here\uninstall.cmd", "$here\uninstall.ps1" "$dist\"
& "$root\bin\sing-box.exe" check -c "$dist\app\config.json"
if ($LASTEXITCODE -ne 0) { throw "config check failed" }
"built: $dist"
Get-ChildItem $dist -Recurse -File | Select-Object @{n='File';e={$_.FullName.Substring($dist.Length+1)}}, @{n='KB';e={[math]::Round($_.Length/1KB)}} | Format-Table -AutoSize
