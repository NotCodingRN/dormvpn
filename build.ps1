# Builds both Windows apps with the C# compiler that ships with Windows (no SDK needed).
#   dist\DormVPN Admin\   the admin app (+ the scripts it runs and the user app it packages)
# Usage: .\build.ps1            build only
#        .\build.ps1 -Install   also install the admin app for this Windows user, with Desktop + Start menu shortcuts
param([switch]$Install)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $root 'dist\DormVPN Admin'
$obj = Join-Path $root 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null

# app icon: the green shield
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 64, 64
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.ScaleTransform(2, 2)
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddLine(16, 2, 29, 7); $path.AddBezier(29, 7, 29, 19, 24, 26, 16, 30); $path.AddBezier(16, 30, 8, 26, 3, 19, 3, 7); $path.CloseFigure()
$g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(31, 157, 85))), $path)
$w = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$g.FillEllipse($w, 12, 9, 8, 8)
$g.FillPolygon($w, [System.Drawing.Point[]]@((New-Object System.Drawing.Point 14, 15), (New-Object System.Drawing.Point 18, 15), (New-Object System.Drawing.Point 19, 23), (New-Object System.Drawing.Point 13, 23)))
$g.Dispose()
$icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
$fs = [IO.File]::Create("$obj\dormvpn.ico"); $icon.Save($fs); $fs.Close()

$refs = '/r:System.dll', '/r:System.Core.dll', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll', '/r:System.Security.dll'

"compiling DormVPN.exe (user app)"
& $csc /nologo /target:winexe /optimize+ /out:"$obj\DormVPN.exe" /win32icon:"$obj\dormvpn.ico" /win32manifest:"$root\windows-app\app.manifest" `
  @refs "$root\windows-app\DormVPN.cs" "$root\windows-app\Ui.cs"
if ($LASTEXITCODE -ne 0) { throw "DormVPN.exe failed to compile" }

"compiling DormVPN Admin.exe"
& $csc /nologo /target:winexe /optimize+ /out:"$obj\DormVPN Admin.exe" /win32icon:"$obj\dormvpn.ico" /win32manifest:"$root\admin-app\app.manifest" `
  @refs '/r:System.IO.Compression.dll' '/r:System.IO.Compression.FileSystem.dll' "$root\admin-app\DormVPNAdmin.cs" "$root\windows-app\Ui.cs"
if ($LASTEXITCODE -ne 0) { throw "DormVPN Admin.exe failed to compile" }

# package
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force "$out\scripts", "$out\client" | Out-Null
Copy-Item "$obj\DormVPN Admin.exe" $out
Copy-Item "$root\server\*.ps1", "$root\server\*.py", "$root\server\*.sh", "$root\server\panel.html" "$out\scripts\"
Copy-Item "$obj\DormVPN.exe" "$out\client\"
Copy-Item "$root\windows-app\install.cmd", "$root\windows-app\install.ps1", "$root\windows-app\uninstall.cmd", "$root\windows-app\uninstall.ps1" "$out\client\"
"built: $out"

if ($Install) {
  $dest = Join-Path $env:LOCALAPPDATA 'Programs\DormVPN Admin'
  $running = Get-Process 'DormVPN Admin' -ErrorAction SilentlyContinue
  if ($running) { $running | Stop-Process -Force; $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500 }
  if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
  Copy-Item $out $dest -Recurse
  $sh = New-Object -ComObject WScript.Shell
  foreach ($lnk in "$([Environment]::GetFolderPath('Desktop'))\DormVPN Admin.lnk", "$([Environment]::GetFolderPath('Programs'))\DormVPN Admin.lnk") {
    $s = $sh.CreateShortcut($lnk); $s.TargetPath = "$dest\DormVPN Admin.exe"; $s.WorkingDirectory = $dest; $s.Description = 'DormVPN Admin'; $s.Save()
  }
  "installed: $dest (shortcuts on the Desktop and in the Start menu)"
}
