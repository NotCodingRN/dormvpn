# Shared helpers for deploy.ps1 / destroy.ps1 / admin-password.ps1. Dot-source it: . "$PSScriptRoot\common.ps1"
#
# Scripts live next to this file. Per-install data (keys, AWS resource ids, admin login) lives in
# $env:DORMVPN_DATA, which the admin app sets to %APPDATA%\DormVPN Admin. Without it (running from
# the repo) the repo root is used.
$script:scripts = $PSScriptRoot
$script:data = if ($env:DORMVPN_DATA) { $env:DORMVPN_DATA } else { Split-Path -Parent $PSScriptRoot }
$script:utf8 = New-Object Text.UTF8Encoding($false)
New-Item -ItemType Directory -Force $script:data | Out-Null

function Save-Json($obj, $path) { [IO.File]::WriteAllText($path, ($obj | ConvertTo-Json -Depth 20), $script:utf8) }
function Read-Json($path) { if (Test-Path $path) { Get-Content $path -Raw | ConvertFrom-Json } else { $null } }

function Get-Secrets {
  $s = Read-Json "$script:data\secrets.json"
  if (-not $s) { throw "secrets.json not found in $script:data. Run the setup in the DormVPN Admin app first." }
  $s
}
function Save-Secrets($s) { Save-Json $s "$script:data\secrets.json" }
function Set-Field($obj, $name, $val) { $obj | Add-Member -NotePropertyName $name -NotePropertyValue $val -Force }

function Get-AwsArgs { $s = Get-Secrets; @('--profile', $s.aws_profile, '--region', $s.aws_region) }

function Get-State { $st = Read-Json "$script:data\state.json"; if ($st) { $st } else { [pscustomobject]@{} } }
function Save-State($st) { Save-Json $st "$script:data\state.json" }

# sing-box.exe: the admin app's engine folder, or bin\ in the repo
function Get-Engine {
  foreach ($p in "$script:data\engine\sing-box.exe", "$(Split-Path -Parent $script:scripts)\bin\sing-box.exe") { if (Test-Path $p) { return $p } }
  throw "sing-box.exe not found. Put it in $script:data\engine (the setup wizard explains how)."
}

# Random URL-safe token
function New-Token([int]$bytes = 32) {
  $b = New-Object byte[] $bytes
  [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
  [Convert]::ToBase64String($b).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

# Readable password like abcd-EFGH-jk23-mnpq
function New-Password {
  $alphabet = 'abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'.ToCharArray()
  $b = New-Object byte[] 16
  [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
  $chars = $b | ForEach-Object { $alphabet[$_ % $alphabet.Length] }
  (0..3 | ForEach-Object { -join $chars[($_ * 4)..($_ * 4 + 3)] }) -join '-'
}

# Writes a temp JSON file and returns a file:// URI for the AWS CLI (deleted by the caller)
function New-JsonArg($obj) {
  $tmp = [IO.Path]::GetTempFileName()
  [IO.File]::WriteAllText($tmp, ($obj | ConvertTo-Json -Depth 20 -Compress), $script:utf8)
  $tmp
}
function UriOf($path) { "file://$($path.Replace('\','/'))" }

# Calls the API Lambda directly (IAM-protected, used for admin-only actions)
function Invoke-ApiDirect($payload) {
  $p = Get-AwsArgs
  $in = New-JsonArg $payload; $out = [IO.Path]::GetTempFileName()
  try {
    aws lambda invoke @p --function-name dormvpn-api --cli-binary-format raw-in-base64-out --payload (UriOf $in) $out | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "lambda invoke failed" }
    $r = Get-Content $out -Raw | ConvertFrom-Json
    if ($r.errorMessage -or $r.error) { throw "API: $($r.errorMessage)$($r.error)" }
    $r
  } finally { Remove-Item $in, $out -ErrorAction SilentlyContinue }
}

# admin.json keeps the panel login; the password is DPAPI-encrypted for this Windows user
function Save-AdminLogin($user, $password, $url) {
  $enc = ConvertTo-SecureString $password -AsPlainText -Force | ConvertFrom-SecureString
  Save-Json ([ordered]@{ username = $user; password = $enc; url = $url }) "$script:data\admin.json"
}

# ---------- server state over EC2 / SSM ----------
function Get-InstanceState {
  $st = Get-State
  if (-not $st.iid) { return 'none' }
  $p = Get-AwsArgs
  $r = aws ec2 describe-instances @p --instance-ids $st.iid --query "Reservations[0].Instances[0].State.Name" --output text 2>$null
  if ($LASTEXITCODE -ne 0 -or -not $r) { 'none' } else { "$r".Trim() }
}

function Wait-ServerOnline([string]$iid, [int]$TimeoutSec = 300) {
  $p = Get-AwsArgs
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  while ((Get-Date) -lt $deadline) {
    $ping = aws ssm describe-instance-information @p --filters "Key=InstanceIds,Values=$iid" --query "InstanceInformationList[0].PingStatus" --output text 2>$null
    if ($ping -eq 'Online') { return }
    Start-Sleep 5
  }
  throw "server $iid did not come online in SSM within $TimeoutSec s"
}

# Runs a bash script on the server as root over SSM and returns its stdout.
function Invoke-Server([string]$Script, [string]$InstanceId, [int]$TimeoutSec = 120) {
  $p = Get-AwsArgs
  if (-not $InstanceId) { $InstanceId = (Get-State).iid }
  $b64 = [Convert]::ToBase64String($script:utf8.GetBytes($Script.Replace("`r`n", "`n")))
  # Output comes back base64-encoded: the AWS CLI on Windows crashes printing non-ASCII characters
  $tmp = New-JsonArg @{ commands = @("cd /root && echo $b64 | base64 -d > dvpn-cmd.sh && bash dvpn-cmd.sh > dvpn.out 2> dvpn.err; rc=`$?; base64 -w0 dvpn.out; base64 -w0 dvpn.err >&2; rm -f dvpn-cmd.sh dvpn.out dvpn.err; exit `$rc") }
  try {
    $cid = aws ssm send-command @p --instance-ids $InstanceId --document-name AWS-RunShellScript `
      --parameters (UriOf $tmp) --comment dormvpn --query Command.CommandId --output text
    if ($LASTEXITCODE -ne 0 -or -not $cid) { throw "ssm send-command failed" }
  } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  do {
    Start-Sleep 2
    # Out-String first: Windows PowerShell's ConvertFrom-Json would otherwise parse line by line
    $inv = $null
    $json = aws ssm get-command-invocation @p --command-id $cid --instance-id $InstanceId --output json 2>$null | Out-String
    if ($LASTEXITCODE -eq 0 -and $json.Trim()) { $inv = $json | ConvertFrom-Json }
  } while ((-not $inv -or $inv.Status -in 'Pending', 'InProgress', 'Delayed') -and (Get-Date) -lt $deadline)
  if (-not $inv) { throw "no result from the server within $TimeoutSec s" }
  $out = ConvertFrom-B64Text $inv.StandardOutputContent
  if ($inv.Status -ne 'Success') { throw "server command $($inv.Status): $(ConvertFrom-B64Text $inv.StandardErrorContent)$out" }
  $out
}

function ConvertFrom-B64Text([string]$text) {
  $text = "$text".Trim()
  if (-not $text) { return '' }
  # on failure SSM appends its own message after our base64, e.g. "<b64>failed to run commands: exit status 3"
  $m = [regex]::Match($text, '^([A-Za-z0-9+/]+={0,2})(.*)$', 'Singleline')
  try { $script:utf8.GetString([Convert]::FromBase64String($m.Groups[1].Value)) + $(if ($m.Groups[2].Value) { " ($($m.Groups[2].Value.Trim()))" }) }
  catch { $text }
}

# ---------- IAM ----------
function Initialize-Role([string]$name, [string]$service, [string[]]$managed, $inline, [switch]$InstanceProfile) {
  $ip = @('--profile', (Get-Secrets).aws_profile)
  aws iam get-role @ip --role-name $name 2>$null | Out-Null
  $created = $false
  if ($LASTEXITCODE -ne 0) {
    $trust = New-JsonArg @{ Version = '2012-10-17'; Statement = @(@{ Effect = 'Allow'; Principal = @{ Service = $service }; Action = 'sts:AssumeRole' }) }
    aws iam create-role @ip --role-name $name --assume-role-policy-document (UriOf $trust) --description "DormVPN" | Out-Null
    Remove-Item $trust
    if ($LASTEXITCODE -ne 0) { throw "could not create IAM role $name" }
    $created = $true
  }
  foreach ($arn in $managed) { aws iam attach-role-policy @ip --role-name $name --policy-arn $arn }
  if ($inline) {
    $pol = New-JsonArg $inline
    aws iam put-role-policy @ip --role-name $name --policy-name "$name-policy" --policy-document (UriOf $pol)
    Remove-Item $pol
  }
  if ($InstanceProfile) {
    aws iam get-instance-profile @ip --instance-profile-name $name 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
      aws iam create-instance-profile @ip --instance-profile-name $name | Out-Null
      aws iam add-role-to-instance-profile @ip --instance-profile-name $name --role-name $name
      $created = $true
    }
  }
  if ($created) { Write-Host "created IAM role $name (waiting for it to propagate)"; Start-Sleep 12 }
  (aws iam get-role @ip --role-name $name --query Role.Arn --output text).Trim()
}
