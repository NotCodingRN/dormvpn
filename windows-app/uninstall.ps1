$dst = Join-Path $env:ProgramFiles 'DormVPN'
Get-Process DormVPN -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process sing-box -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dst*" } | Stop-Process -Force
Start-Sleep 1
schtasks.exe /Delete /TN DormVPN /F 2>$null | Out-Null
Remove-Item "$([Environment]::GetFolderPath('Desktop'))\DormVPN.lnk", "$([Environment]::GetFolderPath('Programs'))\DormVPN.lnk" -ErrorAction SilentlyContinue
Remove-Item $dst -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "DormVPN removed from this PC. (Your AWS server is still running - see the README to delete it.)"
