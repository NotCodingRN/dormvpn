# Shared helpers for deploy.ps1 / destroy.ps1 / admin-password.ps1. Dot-source it: . "$PSScriptRoot\common.ps1"
#
# Scripts live next to this file. Per-install data (keys, AWS resource ids, admin login) lives in
# $env:DORMVPN_DATA, which the admin app sets to the data folder inside the cloned repo (<repo>\data).
# Running a script straight from the repo's server folder uses that same folder.
$script:scripts = $PSScriptRoot
$script:data = if ($env:DORMVPN_DATA) { $env:DORMVPN_DATA } else { Join-Path (Split-Path -Parent $PSScriptRoot) 'data' }
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

# sing-box.exe, which the setup wizard has you put in data\engine
function Get-Engine {
  foreach ($p in "$script:data\engine\sing-box.exe", "$(Split-Path -Parent $script:scripts)\engine\sing-box.exe") { if (Test-Path $p) { return $p } }
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
  [IO.File]::WriteAllText($tmp, (ConvertTo-Json -InputObject $obj -Depth 20 -Compress), $script:utf8)
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

# ---------- VPN server on Lightsail ----------
# The server is a $5 Lightsail instance (1 TB of data included) in secrets.server_region. The API and
# the database stay in aws_region, so an install can move its server without changing the panel link.
function Get-ServerRegion { $s = Get-Secrets; if ($s.server_region) { $s.server_region } else { $s.aws_region } }
function Get-ServerArgs { @('--profile', (Get-Secrets).aws_profile, '--region', (Get-ServerRegion)) }

# running, stopped, pending, stopping, ... or 'none'
function Get-ServerState([string]$Name) {
  if (-not $Name) { $Name = (Get-State).server }
  if (-not $Name) { return 'none' }
  $p = Get-ServerArgs
  $r = aws lightsail get-instance-state @p --instance-name $Name --query state.name --output text 2>$null
  if ($LASTEXITCODE -ne 0 -or -not $r) { 'none' } else { "$r".Trim() }
}

# Removes the EC2 server older versions made (instance, Elastic IP, firewall, its IAM role). Safe to re-run.
function Remove-Ec2Server {
  $p = Get-AwsArgs
  $ip = @('--profile', (Get-Secrets).aws_profile)
  $ids = aws ec2 describe-instances @p --filters Name=tag:Name,Values=dormvpn-server "Name=instance-state-name,Values=pending,running,stopping,stopped" --query "Reservations[].Instances[].InstanceId" --output text
  $ids = @("$ids" -split '\s+' | Where-Object { $_ })
  if ($ids.Count) {
    Write-Host "deleting the old EC2 server $($ids -join ', ') (takes a minute or two)"
    aws ec2 terminate-instances @p --instance-ids $ids | Out-Null
    aws ec2 wait instance-terminated @p --instance-ids $ids
  }
  $allocs = aws ec2 describe-addresses @p --filters Name=tag:Name,Values=dormvpn --query "Addresses[].AllocationId" --output text
  foreach ($a in @("$allocs" -split '\s+' | Where-Object { $_ })) { aws ec2 release-address @p --allocation-id $a; if ($LASTEXITCODE -eq 0) { Write-Host "old address released" } }
  $sgs = aws ec2 describe-security-groups @p --filters Name=group-name,Values=dormvpn-sg --query "SecurityGroups[].GroupId" --output text
  foreach ($g in @("$sgs" -split '\s+' | Where-Object { $_ })) { aws ec2 delete-security-group @p --group-id $g | Out-Null; if ($LASTEXITCODE -eq 0) { Write-Host "old firewall deleted" } }
  aws iam remove-role-from-instance-profile @ip --instance-profile-name dormvpn-ssm --role-name dormvpn-ssm 2>$null
  aws iam delete-instance-profile @ip --instance-profile-name dormvpn-ssm 2>$null
  aws iam detach-role-policy @ip --role-name dormvpn-ssm --policy-arn arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore 2>$null
  aws iam delete-role @ip --role-name dormvpn-ssm 2>$null
}

# ---------- IAM ----------
function Initialize-Role([string]$name, [string]$service, [string[]]$managed, $inline, $Condition) {
  $ip = @('--profile', (Get-Secrets).aws_profile)
  $statement = @{ Effect = 'Allow'; Principal = @{ Service = $service }; Action = 'sts:AssumeRole' }
  if ($Condition) { $statement.Condition = $Condition }
  $trust = New-JsonArg @{ Version = '2012-10-17'; Statement = @($statement) }
  aws iam get-role @ip --role-name $name 2>$null | Out-Null
  $exists = $LASTEXITCODE -eq 0
  $created = $false
  if ($exists) {
    # keeps the trust condition right on roles made by an older version
    if ($Condition) {
      aws iam update-assume-role-policy @ip --role-name $name --policy-document (UriOf $trust)
      if ($LASTEXITCODE -ne 0) { Remove-Item $trust; throw "could not update the trust policy of $name" }
    }
    Remove-Item $trust
  } else {
    aws iam create-role @ip --role-name $name --assume-role-policy-document (UriOf $trust) --description "DormVPN" | Out-Null
    $rc = $LASTEXITCODE
    Remove-Item $trust
    if ($rc -ne 0) { throw "could not create IAM role $name" }
    $created = $true
  }
  foreach ($arn in $managed) { aws iam attach-role-policy @ip --role-name $name --policy-arn $arn }
  if ($inline) {
    $pol = New-JsonArg $inline
    aws iam put-role-policy @ip --role-name $name --policy-name "$name-policy" --policy-document (UriOf $pol)
    Remove-Item $pol
  }
  if ($created) { Write-Host "created IAM role $name (waiting for it to propagate)"; Start-Sleep 12 }
  (aws iam get-role @ip --role-name $name --query Role.Arn --output text).Trim()
}

# ---------- spending limit ----------
# A monthly AWS budget for DormVPN's regions only, so it never counts other projects in the account
# (keep DormVPN in regions they don't use). It emails at 80%, at 100% and when the forecast goes over.
# Nothing gets stopped: the server is a flat $5, and the monthly cap keeps data inside the 1 TB.
# IncludeCredit is off on purpose: it counts real usage, otherwise it reads $0 until credits run out.
function Set-SpendLimit {
  $s = Get-Secrets
  if (-not $s.budget_email) { Write-Host "no alert email set: skipping the spending limit"; return }
  $usd = if ($s.budget_usd) { [double]$s.budget_usd } else { 15 }
  $name = 'DormVPN-Spend-Limit'
  $c = @('--profile', $s.aws_profile, '--region', 'us-east-1')
  $acct = (aws sts get-caller-identity --profile $s.aws_profile --query Account --output text).Trim()
  $budget = New-JsonArg ([ordered]@{
    BudgetName = $name; BudgetType = 'COST'; TimeUnit = 'MONTHLY'
    BudgetLimit = [ordered]@{ Amount = "$usd"; Unit = 'USD' }
    CostFilters = @{ Region = @(@($s.aws_region, (Get-ServerRegion)) | Select-Object -Unique) }
    CostTypes = @{ IncludeCredit = $false; IncludeRefund = $false }
  })
  try {
    aws budgets describe-budget @c --account-id $acct --budget-name $name 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
      aws budgets update-budget @c --account-id $acct --new-budget (UriOf $budget) | Out-Null
    } else {
      $who = @([ordered]@{ SubscriptionType = 'EMAIL'; Address = $s.budget_email })
      $rule = { param($type, $pct) [ordered]@{ Notification = [ordered]@{ NotificationType = $type; ComparisonOperator = 'GREATER_THAN'; Threshold = $pct; ThresholdType = 'PERCENTAGE' }; Subscribers = $who } }
      $notes = New-JsonArg @((& $rule 'ACTUAL' 80), (& $rule 'ACTUAL' 100), (& $rule 'FORECASTED' 100))
      aws budgets create-budget @c --account-id $acct --budget (UriOf $budget) --notifications-with-subscribers (UriOf $notes) | Out-Null
      Remove-Item $notes
    }
    if ($LASTEXITCODE -ne 0) { throw "could not save the budget" }
  } finally { Remove-Item $budget -ErrorAction SilentlyContinue }
  # older versions stopped the EC2 server at 100%
  $acts = aws budgets describe-budget-actions-for-budget @c --account-id $acct --budget-name $name --query "Actions[].ActionId" --output text 2>$null
  foreach ($x in @("$acts" -split '\s+' | Where-Object { $_ -and $_ -ne 'None' })) { aws budgets delete-budget-action @c --account-id $acct --budget-name $name --action-id $x | Out-Null }
  Write-Host "spending limit: `$$usd/month, alerts at 80% and 100%"
}
