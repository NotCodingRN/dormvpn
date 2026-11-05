# Deletes DormVPN from AWS: the VPN server, its address and firewall, the API/admin panel,
# the user database and the IAM roles. After this nothing is left billing.
# Run by the admin app with -Yes after it asked you; run by hand it asks first.
param([switch]$Yes)
$ErrorActionPreference = 'Continue'
. "$PSScriptRoot\common.ps1"
$s = Get-Secrets
$p = Get-AwsArgs
$ip = @('--profile', $s.aws_profile)
$state = Get-State

if (-not $Yes) {
  $answer = Read-Host "Delete DormVPN ($($state.ip)) and all its users from AWS? Type DELETE to confirm"
  if ($answer -ne 'DELETE') { "Cancelled."; return }
}

if ($state.iid) { aws ec2 terminate-instances @p --instance-ids $state.iid | Out-Null; Write-Host "terminating server $($state.iid)"; aws ec2 wait instance-terminated @p --instance-ids $state.iid }
if ($state.alloc) { aws ec2 release-address @p --allocation-id $state.alloc; Write-Host "address released" }
if ($state.sg) { aws ec2 delete-security-group @p --group-id $state.sg | Out-Null; Write-Host "firewall deleted" }

aws lambda delete-function @p --function-name dormvpn-api 2>$null; Write-Host "API deleted"
aws dynamodb delete-table @p --table-name dormvpn 2>$null | Out-Null; Write-Host "user database deleted"

aws iam remove-role-from-instance-profile @ip --instance-profile-name dormvpn-ssm --role-name dormvpn-ssm 2>$null
aws iam delete-instance-profile @ip --instance-profile-name dormvpn-ssm 2>$null
aws iam detach-role-policy @ip --role-name dormvpn-ssm --policy-arn arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore 2>$null
aws iam delete-role @ip --role-name dormvpn-ssm 2>$null
aws iam detach-role-policy @ip --role-name dormvpn-api --policy-arn arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole 2>$null
aws iam delete-role-policy @ip --role-name dormvpn-api --policy-name dormvpn-api-policy 2>$null
aws iam delete-role @ip --role-name dormvpn-api 2>$null
Write-Host "permissions removed"

foreach ($f in 'state.json', 'admin.json', 'userdata.sh') { Remove-Item "$script:data\$f" -ErrorAction SilentlyContinue }
Write-Host "`nDORMVPN_DESTROYED"
