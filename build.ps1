# Builds both Windows apps with the C# compiler that ships with Windows (no SDK needed).
# Everything stays inside this folder:
#   app\          the built admin app, the scripts it runs and the user app it packages
#   data\         your keys, settings and the sing-box engine (created by the app, never in git)
#   installers\   the user installers you build from the app
# Usage: .\build.ps1            build only
#        .\build.ps1 -Install   also add DormVPN Admin shortcuts to the Desktop and Start menu
param([switch]$Install, [switch]$Package)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $root 'app'
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

# package (only app\ is replaced: data\ with your keys is never touched)
$running = Get-Process 'DormVPN Admin' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$out\*" }
if ($running) { $running | Stop-Process -Force; $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500 }
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force "$out\scripts", "$out\client" | Out-Null
Copy-Item "$obj\DormVPN Admin.exe" $out
Copy-Item "$root\server\*.ps1", "$root\server\*.py", "$root\server\*.sh", "$root\server\panel.html" "$out\scripts\"
Copy-Item "$obj\DormVPN.exe" "$out\client\"
Copy-Item "$root\windows-app\install.cmd", "$root\windows-app\install.ps1", "$root\windows-app\uninstall.cmd", "$root\windows-app\uninstall.ps1" "$out\client\"
"built: $out"

# -Package: the zip for GitHub Releases. Unzip, run the admin app, deploy. sing-box is included (GPL-3.0, source: github.com/SagerNet/sing-box).
if ($Package) {
  $eng = Join-Path $root 'data\engine'
  foreach ($f in 'sing-box.exe', 'libcronet.dll', 'LICENSE') { if (-not (Test-Path "$eng\$f")) { throw "Put the sing-box 1.14.4 files in data\engine first ($f is missing)." } }
  $ver = [regex]::Match((Get-Content "$root\admin-app\DormVPNAdmin.cs" -Raw), 'AssemblyVersion\("(\d+\.\d+\.\d+)').Groups[1].Value
  $stage = Join-Path $obj 'package'
  if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
  $dir = Join-Path $stage 'DormVPN Admin'
  Copy-Item $out $dir -Recurse
  Copy-Item $eng (Join-Path $dir 'engine') -Recurse
  [IO.File]::WriteAllText((Join-Path $dir 'READ ME.txt'), @"
DormVPN Admin $ver

1. Keep this folder somewhere permanent, e.g. Documents. Your keys and settings are saved in its data folder.
   Don't delete it while your VPN is running on AWS: it's how you manage and remove it.
2. Double-click "DormVPN Admin.exe". If Windows SmartScreen warns you, click More info, then Run anyway.
3. Follow the setup: it installs the AWS CLI, connects your AWS account and creates your VPN.

Source and docs: https://github.com/NotCodingRN/dormvpn
sing-box (in the engine folder) is GPL-3.0 software, source: https://github.com/SagerNet/sing-box
"@)
  New-Item -ItemType Directory -Force (Join-Path $root 'dist') | Out-Null
  $zip = Join-Path $root "dist\dormvpn-admin-v$ver.zip"
  if (Test-Path $zip) { Remove-Item $zip }
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
  Remove-Item $stage -Recurse -Force
  "package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}

if ($Install) {
  $sh = New-Object -ComObject WScript.Shell
  foreach ($lnk in "$([Environment]::GetFolderPath('Desktop'))\DormVPN Admin.lnk", "$([Environment]::GetFolderPath('Programs'))\DormVPN Admin.lnk") {
    $s = $sh.CreateShortcut($lnk); $s.TargetPath = "$out\DormVPN Admin.exe"; $s.WorkingDirectory = $out; $s.Description = 'DormVPN Admin'; $s.Save()
  }
  "shortcuts added to the Desktop and Start menu"
}
