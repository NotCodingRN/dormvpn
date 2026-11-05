# Deletes the DormVPN server from AWS: instance, Elastic IP and security group.
# After this the apps stop working and AWS stops charging for it.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$s = Get-Content "$root\secrets.json" -Raw | ConvertFrom-Json
$p = @('--profile', $s.aws_profile, '--region', $s.aws_region)
$state = Get-Content "$here\state.json" -Raw | ConvertFrom-Json

$answer = Read-Host "Delete DormVPN server $($state.ip) ($($state.iid)) from AWS? Type DELETE to confirm"
if ($answer -ne 'DELETE') { "Cancelled."; return }

if ($state.iid) { aws ec2 terminate-instances @p --instance-ids $state.iid | Out-Null; aws ec2 wait instance-terminated @p --instance-ids $state.iid; "instance terminated" }
if ($state.alloc) { aws ec2 release-address @p --allocation-id $state.alloc; "Elastic IP released" }
if ($state.sg) { aws ec2 delete-security-group @p --group-id $state.sg | Out-Null; "security group deleted" }
Rename-Item "$here\state.json" "state.deleted.json" -Force
"Done. Run deploy.ps1 to create a new server later (it will get a new IP, so re-import the phone profile)."
