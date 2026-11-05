# Deploys the DormVPN server to AWS (profile and region come from secrets.json).
# Creates: security group (TCP 443 open), Elastic IP (stable address), t3.micro instance.
# Re-running replaces the instance but keeps the same Elastic IP, so clients keep working.
$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$s = Get-Content "$root\secrets.json" -Raw | ConvertFrom-Json
$p = @('--profile', $s.aws_profile, '--region', $s.aws_region)
$stateFile = "$here\state.json"
$state = if (Test-Path $stateFile) { Get-Content $stateFile -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
function Save-State { $state | ConvertTo-Json | Set-Content $stateFile }
function Set-Prop($name, $val) { $state | Add-Member -NotePropertyName $name -NotePropertyValue $val -Force; Save-State }

# 1. server config + user-data
& "$root\make-configs.ps1" -ServerIp '0.0.0.0' | Out-Null
$serverJson = Get-Content "$root\out\server.json" -Raw
$ud = (Get-Content "$here\userdata.template.sh" -Raw).Replace('__SERVER_CONFIG__', $serverJson.Trim()).Replace("`r`n", "`n")
[IO.File]::WriteAllText("$here\userdata.sh", $ud)

# 2. security group
if (-not $state.sg) {
  $vpc = aws ec2 describe-vpcs @p --filters Name=isDefault,Values=true --query "Vpcs[0].VpcId" --output text
  $sg = aws ec2 create-security-group @p --group-name dormvpn-sg --description "DormVPN: TCP 443" --vpc-id $vpc `
        --tag-specifications "ResourceType=security-group,Tags=[{Key=Name,Value=dormvpn}]" --query GroupId --output text
  aws ec2 authorize-security-group-ingress @p --group-id $sg --ip-permissions "IpProtocol=tcp,FromPort=443,ToPort=443,IpRanges=[{CidrIp=0.0.0.0/0,Description=DormVPN}]" | Out-Null
  Set-Prop 'sg' $sg
}
"security group: $($state.sg)"

# 3. Elastic IP (stable public address)
if (-not $state.alloc) {
  $eip = aws ec2 allocate-address @p --domain vpc --tag-specifications "ResourceType=elastic-ip,Tags=[{Key=Name,Value=dormvpn}]" --output json | ConvertFrom-Json
  Set-Prop 'alloc' $eip.AllocationId
  Set-Prop 'ip' $eip.PublicIp
}
"elastic IP: $($state.ip) ($($state.alloc))"

# 4. instance (replace if one exists)
$old = $state.iid
$ami = aws ssm get-parameter @p --name /aws/service/ami-amazon-linux-latest/al2023-ami-kernel-default-x86_64 --query Parameter.Value --output text
$iid = aws ec2 run-instances @p --image-id $ami --instance-type t3.micro --security-group-ids $state.sg `
  --user-data "file://$here/userdata.sh" `
  --metadata-options "HttpTokens=required,HttpEndpoint=enabled" `
  --credit-specification "CpuCredits=standard" `
  --block-device-mappings "DeviceName=/dev/xvda,Ebs={VolumeSize=8,VolumeType=gp3,DeleteOnTermination=true}" `
  --tag-specifications "ResourceType=instance,Tags=[{Key=Name,Value=dormvpn-server}]" "ResourceType=volume,Tags=[{Key=Name,Value=dormvpn-server}]" `
  --query "Instances[0].InstanceId" --output text
if (-not $iid -or $iid -notmatch '^i-') { throw "run-instances failed" }
"instance: $iid (waiting for it to start)"
aws ec2 wait instance-running @p --instance-ids $iid
aws ec2 associate-address @p --allocation-id $state.alloc --instance-id $iid --allow-reassociation | Out-Null
Set-Prop 'iid' $iid
if ($old -and $old -ne $iid) {
  aws ec2 terminate-instances @p --instance-ids $old | Out-Null
  "old instance $old terminating"
}

# 5. client configs with the real IP
& "$root\make-configs.ps1" -ServerIp $state.ip
"DEPLOYED: $($state.ip):443"
