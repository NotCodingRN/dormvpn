$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'app'
$dst = Join-Path $env:ProgramFiles 'DormVPN'

Write-Host "`n=== Installing DormVPN ===`n"

# stop a running copy
Get-Process DormVPN -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process sing-box -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dst*" } | Stop-Process -Force
Start-Sleep 1

New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item "$src\*" $dst -Force
Write-Host "Installed to $dst"

# shortcuts
$sh = New-Object -ComObject WScript.Shell
foreach ($lnk in @("$([Environment]::GetFolderPath('Desktop'))\DormVPN.lnk", "$([Environment]::GetFolderPath('Programs'))\DormVPN.lnk")) {
  $s = $sh.CreateShortcut($lnk); $s.TargetPath = "$dst\DormVPN.exe"; $s.WorkingDirectory = $dst; $s.Description = 'DormVPN'; $s.Save()
}
Write-Host "Shortcuts added to Desktop and Start menu"

# start automatically at sign-in (no UAC prompt each time)
schtasks.exe /Create /TN DormVPN /TR "`"$dst\DormVPN.exe`"" /SC ONLOGON /RL HIGHEST /F | Out-Null
Write-Host "DormVPN will start when you sign in (turn off from its tray menu)"

# launch
Start-Process "$dst\DormVPN.exe"
Write-Host "`nDormVPN is running - look for the shield icon in the system tray."
Write-Host "Orange = connecting, green = connected. Right-click it for options.`n"
