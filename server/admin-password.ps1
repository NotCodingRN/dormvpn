# Gives the admin panel a new generated password (the old one stops working, open panels sign out).
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"
$state = Get-State
$admin = Read-Json "$script:data\admin.json"
$user = if ($admin) { $admin.username } else { 'admin' }
$pw = New-Password
Invoke-ApiDirect @{ admin_action = 'set_admin'; username = $user; password = $pw } | Out-Null
Save-AdminLogin $user $pw $state.api_url
Write-Host "DORMVPN_ADMIN_RESET"
