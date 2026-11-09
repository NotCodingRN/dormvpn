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
function Initialize-Role([string]$name, [string]$service, [string[]]$managed, $inline, [switch]$InstanceProfile, $Condition) {
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

# ---------- spending limit ----------
# A monthly AWS budget for this region only, so it never counts or touches other projects in the
# account (keep DormVPN in a region they don't use). It emails at 80%, at 100% and when the forecast
# goes over, and at 100% an AWS Budgets action stops the VPN server.
# IncludeCredit is off on purpose: it counts real usage, otherwise it reads $0 until credits run out.
function Set-SpendLimit([string]$InstanceId) {
  $s = Get-Secrets
  if (-not $s.budget_email) { Write-Host "no alert email set: skipping the spending limit"; return }
  $usd = if ($s.budget_usd) { [double]$s.budget_usd } else { 15 }
  $name = 'DormVPN-Spend-Limit'
  $c = @('--profile', $s.aws_profile, '--region', 'us-east-1')
  $acct = (aws sts get-caller-identity --profile $s.aws_profile --query Account --output text).Trim()
  # only budgets in this account may use the role (AWS requires exactly this ARN form for Budgets)
  $role = Initialize-Role 'dormvpn-budget' 'budgets.amazonaws.com' @('arn:aws:iam::aws:policy/AWSBudgetsActions_RolePolicyForResourceAdministrationWithSSM') $null -Condition @{
    StringEquals = @{ 'aws:SourceAccount' = $acct }
    ArnLike = @{ 'aws:SourceArn' = "arn:aws:budgets::${acct}:budget/*" }
  }
  $budget = New-JsonArg ([ordered]@{
    BudgetName = $name; BudgetType = 'COST'; TimeUnit = 'MONTHLY'
    BudgetLimit = [ordered]@{ Amount = "$usd"; Unit = 'USD' }
    CostFilters = @{ Region = @($s.aws_region) }
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
  Write-Host "spending limit: `$$usd/month in $($s.aws_region), alerts at 80% and 100%"
  if (-not $InstanceId) { return }

  $def = New-JsonArg @{ SsmActionDefinition = [ordered]@{ ActionSubType = 'STOP_EC2_INSTANCES'; Region = $s.aws_region; InstanceIds = @($InstanceId) } }
  try {
    $action = aws budgets describe-budget-actions-for-budget @c --account-id $acct --budget-name $name --query "Actions[0].ActionId" --output text
    if ($action -and "$action" -ne 'None') {
      aws budgets update-budget-action @c --account-id $acct --budget-name $name --action-id $action --definition (UriOf $def) | Out-Null
    } else {
      $subs = New-JsonArg @([ordered]@{ SubscriptionType = 'EMAIL'; Address = $s.budget_email })
      try {
        aws budgets create-budget-action @c --account-id $acct --budget-name $name --notification-type ACTUAL --action-type RUN_SSM_DOCUMENTS `
          --action-threshold "ActionThresholdValue=100,ActionThresholdType=PERCENTAGE" --definition (UriOf $def) `
          --execution-role-arn $role --approval-model AUTOMATIC --subscribers (UriOf $subs) | Out-Null
      } finally { Remove-Item $subs -ErrorAction SilentlyContinue }
    }
    if ($LASTEXITCODE -ne 0) { throw "could not set the auto-stop" }
  } finally { Remove-Item $def -ErrorAction SilentlyContinue }
  Write-Host "auto-stop: server $InstanceId stops itself at 100% of the limit"
}

# ---------- IPv6 ----------
# Phones and other dual-stack clients send IPv6 destinations through the tunnel. Their VPN app accepts
# the connection locally, so if the server can't reach IPv6 the app sees a connection that dies and
# never falls back to IPv4: those apps just don't work. So the server gets real IPv6 outbound.
# IPv6 addresses are free on AWS. Safe to re-run. Inbound stays TCP 443 over IPv4 only.
function Initialize-IPv6([string]$SecurityGroup, [string]$InstanceId) {
  $p = Get-AwsArgs
  $vpc = (aws ec2 describe-vpcs @p --filters Name=isDefault,Values=true --query "Vpcs[0].VpcId" --output text).Trim()
  $q = "Vpcs[0].Ipv6CidrBlockAssociationSet[?Ipv6CidrBlockState.State=='associated'].Ipv6CidrBlock | [0]"
  $cidr = "$(aws ec2 describe-vpcs @p --vpc-ids $vpc --query $q --output text)".Trim()
  if (-not $cidr -or $cidr -eq 'None') {
    aws ec2 associate-vpc-cidr-block @p --vpc-id $vpc --amazon-provided-ipv6-cidr-block | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "could not add IPv6 to the VPC" }
    for ($i = 0; $i -lt 20; $i++) {
      Start-Sleep 3
      $cidr = "$(aws ec2 describe-vpcs @p --vpc-ids $vpc --query $q --output text)".Trim()
      if ($cidr -and $cidr -ne 'None') { break }
    }
    if (-not $cidr -or $cidr -eq 'None') { throw "the VPC's IPv6 range never became ready" }
  }
  Write-Host "IPv6 range: $cidr"

  # each subnet gets its own /64 out of the VPC's /56
  $base = $cidr -replace '00::/56$', ''
  $subnets = aws ec2 describe-subnets @p --filters "Name=vpc-id,Values=$vpc" --query "Subnets[].[SubnetId,Ipv6CidrBlockAssociationSet[?Ipv6CidrBlockState.State=='associated'].Ipv6CidrBlock | [0]]" --output text
  $used = @($subnets | ForEach-Object { ($_ -split '\s+')[1] } | Where-Object { $_ -and $_ -ne 'None' })
  $n = 0
  foreach ($line in $subnets) {
    $id, $has = $line -split '\s+'
    if (-not $has -or $has -eq 'None') {
      do { $block = "$base{0:x2}::/64" -f $n; $n++ } while ($used -contains $block)
      aws ec2 associate-subnet-cidr-block @p --subnet-id $id --ipv6-cidr-block $block | Out-Null
      if ($LASTEXITCODE -ne 0) { throw "could not add IPv6 to subnet $id" }
      Start-Sleep 2
    }
    aws ec2 modify-subnet-attribute @p --subnet-id $id --assign-ipv6-address-on-creation | Out-Null
  }

  $rt = (aws ec2 describe-route-tables @p --filters "Name=vpc-id,Values=$vpc" Name=association.main,Values=true --query "RouteTables[0].RouteTableId" --output text).Trim()
  $has = "$(aws ec2 describe-route-tables @p --route-table-ids $rt --query "RouteTables[0].Routes[?DestinationIpv6CidrBlock=='::/0'].State | [0]" --output text)".Trim()
  if ($has -eq 'None' -or -not $has) {
    $igw = (aws ec2 describe-internet-gateways @p --filters "Name=attachment.vpc-id,Values=$vpc" --query "InternetGateways[0].InternetGatewayId" --output text).Trim()
    aws ec2 create-route @p --route-table-id $rt --destination-ipv6-cidr-block '::/0' --gateway-id $igw | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "could not add the IPv6 route" }
  }

  $eg = "$(aws ec2 describe-security-groups @p --group-ids $SecurityGroup --query "SecurityGroups[0].IpPermissionsEgress[?Ipv6Ranges[?CidrIpv6=='::/0']] | [0].IpProtocol" --output text)".Trim()
  if ($eg -eq 'None' -or -not $eg) {
    aws ec2 authorize-security-group-egress @p --group-id $SecurityGroup --ip-permissions 'IpProtocol=-1,Ipv6Ranges=[{CidrIpv6=::/0,Description=DormVPN}]' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "could not allow IPv6 out of the firewall" }
  }

  # the default network ACL only gets IPv6 rules automatically if it was never edited
  $acl = (aws ec2 describe-network-acls @p --filters "Name=vpc-id,Values=$vpc" Name=default,Values=true --query "NetworkAcls[0].NetworkAclId" --output text).Trim()
  foreach ($dir in '--ingress', '--egress') {
    $flag = if ($dir -eq '--egress') { 'true' } else { 'false' }
    $rule = "$(aws ec2 describe-network-acls @p --network-acl-ids $acl --query "NetworkAcls[0].Entries[?Egress==``$flag`` && Ipv6CidrBlock=='::/0' && RuleAction=='allow'].RuleNumber | [0]" --output text)".Trim()
    if ($rule -eq 'None' -or -not $rule) {
      aws ec2 create-network-acl-entry @p --network-acl-id $acl $dir --rule-number 101 --protocol -1 --rule-action allow --ipv6-cidr-block '::/0' | Out-Null
    }
  }

  if ($InstanceId) {
    $q6 = "Reservations[0].Instances[0].NetworkInterfaces[0].[NetworkInterfaceId,Ipv6Addresses[0].Ipv6Address]"
    $eni, $addr = (aws ec2 describe-instances @p --instance-ids $InstanceId --query $q6 --output text) -split '\s+'
    if (-not $addr -or $addr -eq 'None') {
      aws ec2 assign-ipv6-addresses @p --network-interface-id $eni --ipv6-address-count 1 | Out-Null
      if ($LASTEXITCODE -ne 0) { throw "could not give the server an IPv6 address" }
      $addr = "$(aws ec2 describe-network-interfaces @p --network-interface-ids $eni --query "NetworkInterfaces[0].Ipv6Addresses[0].Ipv6Address" --output text)".Trim()
    }
    Write-Host "server IPv6 address: $addr"
  }
}
