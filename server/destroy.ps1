# Deletes DormVPN from AWS: the VPN server, its address and firewall, the API/admin panel,
# the user database, logs and IAM roles. Then it checks that nothing is left billing.
# Run by the admin app with -Yes after it asked you; run by hand it asks first.
# Your AWS account and access keys are not touched. Safe to run again if it stops partway.
param([switch]$Yes)
$ErrorActionPreference = 'Continue'
. "$PSScriptRoot\common.ps1"
$s = Get-Secrets
$p = Get-AwsArgs
$ip = @('--profile', $s.aws_profile)
$state = Get-State

# The app's kill switch holds all traffic while the VPN is down, so deleting the server from a
# PC that's connected through it would cut this PC's internet and stop the delete halfway.
try {
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  $mine = (Invoke-RestMethod https://checkip.amazonaws.com -TimeoutSec 15).Trim()
} catch { $mine = '' }
if ($state.ip -and $mine -eq $state.ip) {
  Write-Host "STOPPED: this PC is connected through DormVPN ($mine)."
  Write-Host "Nothing was deleted. Quit DormVPN first: right-click the shield icon in the system tray, then Quit DormVPN."
  exit 1
}

if (-not $Yes) {
  $answer = Read-Host "Delete DormVPN ($($state.ip)) and all its users from AWS? Type DELETE to confirm"
  if ($answer -ne 'DELETE') { "Cancelled."; return }
}

function Retry([scriptblock]$cmd) {
  for ($i = 1; $i -le 5; $i++) { & $cmd; if ($LASTEXITCODE -eq 0) { return $true }; Start-Sleep 6 }
  $false
}

# ---------- VPN server (found by tag too, in case state.json is out of date)
$ids = aws ec2 describe-instances @p --filters Name=tag:Name,Values=dormvpn-server "Name=instance-state-name,Values=pending,running,stopping,stopped" --query "Reservations[].Instances[].InstanceId" --output text
$ids = @("$ids" -split '\s+' | Where-Object { $_ })
if ($ids.Count) {
  Write-Host "deleting server $($ids -join ', ') (takes a minute or two)"
  $null = Retry { aws ec2 terminate-instances @p --instance-ids $ids | Out-Null }
  $null = Retry { aws ec2 wait instance-terminated @p --instance-ids $ids }
}

# ---------- address and firewall
$allocs = aws ec2 describe-addresses @p --filters Name=tag:Name,Values=dormvpn --query "Addresses[].AllocationId" --output text
foreach ($a in @("$allocs" -split '\s+' | Where-Object { $_ })) {
  if (Retry { aws ec2 release-address @p --allocation-id $a }) { Write-Host "address released" }
}
$sgs = aws ec2 describe-security-groups @p --filters Name=group-name,Values=dormvpn-sg --query "SecurityGroups[].GroupId" --output text
foreach ($g in @("$sgs" -split '\s+' | Where-Object { $_ })) {
  if (Retry { aws ec2 delete-security-group @p --group-id $g | Out-Null }) { Write-Host "firewall deleted" }
}

# ---------- API, database, logs
aws lambda get-function @p --function-name dormvpn-api 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { if (Retry { aws lambda delete-function @p --function-name dormvpn-api }) { Write-Host "admin panel deleted" } }
aws dynamodb describe-table @p --table-name dormvpn 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { if (Retry { aws dynamodb delete-table @p --table-name dormvpn | Out-Null }) { Write-Host "user database deleted" } }
aws logs delete-log-group @p --log-group-name /aws/lambda/dormvpn-api 2>$null

# ---------- IAM roles (free, but no reason to leave them behind)
aws iam remove-role-from-instance-profile @ip --instance-profile-name dormvpn-ssm --role-name dormvpn-ssm 2>$null
aws iam delete-instance-profile @ip --instance-profile-name dormvpn-ssm 2>$null
aws iam detach-role-policy @ip --role-name dormvpn-ssm --policy-arn arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore 2>$null
aws iam delete-role @ip --role-name dormvpn-ssm 2>$null
aws iam detach-role-policy @ip --role-name dormvpn-api --policy-arn arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole 2>$null
aws iam delete-role-policy @ip --role-name dormvpn-api --policy-name dormvpn-api-policy 2>$null
aws iam delete-role @ip --role-name dormvpn-api 2>$null

# ---------- check, and only then forget this install
Write-Host "`nchecking that nothing is left..."
$left = @()
$v = aws ec2 describe-instances @p --filters Name=tag:Name,Values=dormvpn-server "Name=instance-state-name,Values=pending,running,stopping,stopped" --query "Reservations[].Instances[].InstanceId" --output text
if ($LASTEXITCODE -ne 0) { $left += 'server (could not check)' } elseif ("$v".Trim()) { $left += "server $v" }
$v = aws ec2 describe-addresses @p --filters Name=tag:Name,Values=dormvpn --query "Addresses[].PublicIp" --output text
if ($LASTEXITCODE -ne 0) { $left += 'address (could not check)' } elseif ("$v".Trim()) { $left += "address $v" }
$v = aws ec2 describe-security-groups @p --filters Name=group-name,Values=dormvpn-sg --query "SecurityGroups[].GroupId" --output text
if ($LASTEXITCODE -ne 0) { $left += 'firewall (could not check)' } elseif ("$v".Trim()) { $left += "firewall $v" }
aws lambda get-function @p --function-name dormvpn-api 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { $left += 'admin panel' }
$v = aws dynamodb list-tables @p --query "TableNames[?@=='dormvpn']" --output text
if ("$v".Trim()) {
  $st = aws dynamodb describe-table @p --table-name dormvpn --query Table.TableStatus --output text 2>$null
  if ("$st".Trim() -ne 'DELETING' -and $LASTEXITCODE -eq 0) { $left += 'user database' }
}
if ($left.Count) {
  Write-Host "NOT finished, still there: $($left -join ', ')"
  Write-Host "Run this again in a minute. Your local settings were kept so it can finish the job."
  exit 1
}

foreach ($f in 'state.json', 'admin.json', 'userdata.sh', 'secrets.json', 'users.migrated.json') { Remove-Item "$script:data\$f" -ErrorAction SilentlyContinue }
Write-Host "everything is gone from AWS"
Write-Host "`nDORMVPN_DESTROYED"
