# Creates or updates the whole DormVPN setup in AWS. Safe to re-run.
#   - VPN server: security group (TCP 443), Elastic IP (stable address), IPv6, t3.micro instance
#   - API + admin panel: DynamoDB table, Lambda function with a public HTTPS URL
#   - IAM roles for both, the admin login (first run), and keys (first run)
# The VPN instance is only replaced when its bootstrap changed (or with -ReplaceServer); the IP
# stays the same either way, and users, limits and this month's usage live in DynamoDB.
param([switch]$ReplaceServer)
$ErrorActionPreference = 'Continue'
. "$PSScriptRoot\common.ps1"
$s = Get-Secrets
$p = Get-AwsArgs
$state = Get-State
function Set-StateField($k, $v) { Set-Field $state $k $v; Save-State $state }
function Step($t) { Write-Host "`n== $t" }

$acct = (aws sts get-caller-identity --profile $s.aws_profile --query Account --output text)
if ($LASTEXITCODE -ne 0 -or -not $acct) { throw "AWS credentials for profile '$($s.aws_profile)' don't work" }
$acct = $acct.Trim()
Write-Host "AWS account $acct, region $($s.aws_region)"

# ---------------------------------------------------------------- 1. keys
Step "keys"
if (-not $s.reality_private_key -or -not $s.reality_public_key -or -not $s.short_id) {
  $sb = Get-Engine
  $kp = & $sb generate reality-keypair
  Set-Field $s 'reality_private_key' (($kp | Select-String 'PrivateKey:\s*(\S+)').Matches[0].Groups[1].Value)
  Set-Field $s 'reality_public_key' (($kp | Select-String 'PublicKey:\s*(\S+)').Matches[0].Groups[1].Value)
  Set-Field $s 'short_id' ((& $sb generate rand 8 --hex) | Out-String).Trim()
  if (-not $s.sni) { Set-Field $s 'sni' 'www.microsoft.com' }
  Save-Secrets $s
  Write-Host "generated new REALITY keys"
}
if (-not $s.token_key) { Set-Field $s 'token_key' (New-Token 32); Save-Secrets $s }
if (-not $s.server_token) { Set-Field $s 'server_token' (New-Token 32); Save-Secrets $s }
Write-Host "ok"

# ---------------------------------------------------------------- 2. network
Step "network"
if (-not $state.sg) {
  $vpc = aws ec2 describe-vpcs @p --filters Name=isDefault,Values=true --query "Vpcs[0].VpcId" --output text
  $sg = aws ec2 create-security-group @p --group-name dormvpn-sg --description "DormVPN: TCP 443" --vpc-id $vpc `
        --tag-specifications "ResourceType=security-group,Tags=[{Key=Name,Value=dormvpn}]" --query GroupId --output text
  aws ec2 authorize-security-group-ingress @p --group-id $sg --ip-permissions "IpProtocol=tcp,FromPort=443,ToPort=443,IpRanges=[{CidrIp=0.0.0.0/0,Description=DormVPN}]" | Out-Null
  Set-StateField 'sg' $sg
}
if (-not $state.alloc) {
  $eip = aws ec2 allocate-address @p --domain vpc --tag-specifications "ResourceType=elastic-ip,Tags=[{Key=Name,Value=dormvpn}]" --output json | Out-String | ConvertFrom-Json
  Set-StateField 'alloc' $eip.AllocationId
  Set-StateField 'ip' $eip.PublicIp
}
Write-Host "security group $($state.sg), address $($state.ip)"
Initialize-IPv6 $state.sg $(if ((Get-InstanceState) -in 'running', 'stopped') { $state.iid } else { '' })

# ---------------------------------------------------------------- 3. IAM roles
Step "permissions"
$null = Initialize-Role 'dormvpn-ssm' 'ec2.amazonaws.com' @('arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore') $null -InstanceProfile
$apiRole = Initialize-Role 'dormvpn-api' 'lambda.amazonaws.com' @('arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole') @{
  Version = '2012-10-17'
  Statement = @(
    @{ Effect = 'Allow'; Action = @('dynamodb:GetItem', 'dynamodb:PutItem', 'dynamodb:UpdateItem', 'dynamodb:DeleteItem', 'dynamodb:Scan')
       Resource = "arn:aws:dynamodb:$($s.aws_region):${acct}:table/dormvpn" },
    @{ Effect = 'Allow'; Action = 'ec2:DescribeInstances'; Resource = '*' },
    @{ Effect = 'Allow'; Action = @('ec2:StartInstances', 'ec2:StopInstances'); Resource = "arn:aws:ec2:$($s.aws_region):${acct}:instance/*"
       Condition = @{ StringEquals = @{ 'aws:ResourceTag/Name' = 'dormvpn-server' } } }
  )
}
Write-Host "ok"

# ---------------------------------------------------------------- 4. database
Step "database"
aws dynamodb describe-table @p --table-name dormvpn 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
  aws dynamodb create-table @p --table-name dormvpn --attribute-definitions AttributeName=pk,AttributeType=S `
    --key-schema AttributeName=pk,KeyType=HASH --billing-mode PAY_PER_REQUEST --tags Key=Name,Value=dormvpn | Out-Null
  aws dynamodb wait table-exists @p --table-name dormvpn
  Write-Host "created table dormvpn"
}
# sign-in lock records clean themselves up after a day
$ttl = aws dynamodb describe-time-to-live @p --table-name dormvpn --query TimeToLiveDescription.TimeToLiveStatus --output text 2>$null
if ($ttl -ne 'ENABLED' -and $ttl -ne 'ENABLING') {
  aws dynamodb update-time-to-live @p --table-name dormvpn --time-to-live-specification "Enabled=true,AttributeName=expires" | Out-Null
}
Write-Host "ok"

# ---------------------------------------------------------------- 5. API + admin panel
Step "API and admin panel"
$zipDir = Join-Path ([IO.Path]::GetTempPath()) "dormvpn-api-$(Get-Random)"
New-Item -ItemType Directory $zipDir | Out-Null
Copy-Item "$script:scripts\api.py", "$script:scripts\panel.html" $zipDir
$zip = "$zipDir.zip"
Compress-Archive -Path "$zipDir\*" -DestinationPath $zip -Force
$envFile = New-JsonArg @{ Variables = @{ TABLE = 'dormvpn'; TOKEN_KEY = $s.token_key; SERVER_TOKEN = $s.server_token } }
aws lambda get-function @p --function-name dormvpn-api 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
  for ($i = 1; $i -le 6; $i++) {  # a brand-new role can take a few seconds before Lambda accepts it
    aws lambda create-function @p --function-name dormvpn-api --runtime python3.13 --handler api.handler --role $apiRole `
      --zip-file "fileb://$($zip.Replace('\','/'))" --timeout 15 --memory-size 512 --environment (UriOf $envFile) `
      --description "DormVPN API and admin panel" --tags Name=dormvpn 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { break }
    Start-Sleep 8
  }
  if ($LASTEXITCODE -ne 0) { throw "could not create the Lambda function" }
  aws lambda wait function-active-v2 @p --function-name dormvpn-api
  Write-Host "created function dormvpn-api"
} else {
  aws lambda update-function-code @p --function-name dormvpn-api --zip-file "fileb://$($zip.Replace('\','/'))" | Out-Null
  aws lambda wait function-updated-v2 @p --function-name dormvpn-api
  aws lambda update-function-configuration @p --function-name dormvpn-api --environment (UriOf $envFile) --memory-size 512 | Out-Null
  aws lambda wait function-updated-v2 @p --function-name dormvpn-api
  Write-Host "updated function dormvpn-api"
}
Remove-Item $envFile, $zip -ErrorAction SilentlyContinue; Remove-Item $zipDir -Recurse -Force -ErrorAction SilentlyContinue

$url = aws lambda get-function-url-config @p --function-name dormvpn-api --query FunctionUrl --output text 2>$null
if ($LASTEXITCODE -ne 0 -or -not $url) {
  $url = aws lambda create-function-url-config @p --function-name dormvpn-api --auth-type NONE --query FunctionUrl --output text
  aws lambda add-permission @p --function-name dormvpn-api --statement-id public-url --action lambda:InvokeFunctionUrl `
    --principal '*' --function-url-auth-type NONE | Out-Null
  aws lambda add-permission @p --function-name dormvpn-api --statement-id public-url-invoke --action lambda:InvokeFunction `
    --principal '*' --invoked-via-function-url | Out-Null
}
$url = "$url".Trim().TrimEnd('/')
Set-StateField 'api_url' $url
Write-Host "panel: $url"

# ---------------------------------------------------------------- 6. admin login (first run only)
if (-not (Test-Path "$script:data\admin.json")) {
  $pw = New-Password
  Invoke-ApiDirect @{ admin_action = 'set_admin'; username = 'admin'; password = $pw } | Out-Null
  Save-AdminLogin 'admin' $pw $url
  Write-Host "created admin login (shown in the DormVPN Admin app)"
}

# ---------------------------------------------------------------- 7. users from the old command-line version
if (Test-Path "$script:data\users.json") {
  Step "moving users from users.json to the panel"
  $old = Get-Content "$script:data\users.json" -Raw | ConvertFrom-Json
  foreach ($u in @($old.users)) {
    $pw = New-Password
    try {
      Invoke-ApiDirect @{ admin_action = 'import_user'; name = $u.name; id = [int]$u.id; uuid = $u.uuid; limit_gb = $u.limit_gb; enabled = [bool]$u.enabled; password = $pw } | Out-Null
      Write-Host "  $($u.name): password $pw"
    } catch { Write-Host "  $($u.name): already in the panel" }
  }
  if ($old.monthly_cap_gb -or $old.idle_stop_minutes) {
    Write-Host "  (set the cap / auto-pause again in the panel)"
  }
  Move-Item "$script:data\users.json" "$script:data\users.migrated.json" -Force
}

# ---------------------------------------------------------------- 8. VPN server
Step "VPN server"
$base = [ordered]@{
  log = [ordered]@{ level = 'warn'; timestamp = $true }
  inbounds = @([ordered]@{
    type = 'vless'; tag = 'vless-in'; listen = '::'; listen_port = 443; users = @()
    tls = [ordered]@{
      enabled = $true; server_name = $s.sni
      reality = [ordered]@{ enabled = $true; handshake = [ordered]@{ server = $s.sni; server_port = 443 }
                            private_key = $s.reality_private_key; short_id = @($s.short_id) }
    }
  })
}
$meterConf = [ordered]@{ api_url = $url; server_token = $s.server_token }
$raw = $script:utf8.GetBytes((Get-Content "$script:scripts\dormvpn-meter.py" -Raw).Replace("`r`n", "`n"))
$ms = New-Object IO.MemoryStream
$gz = New-Object IO.Compression.GZipStream($ms, [IO.Compression.CompressionMode]::Compress)
$gz.Write($raw, 0, $raw.Length); $gz.Close()
$ud = (Get-Content "$script:scripts\userdata.template.sh" -Raw)
$ud = $ud.Replace('__SERVER_BASE__', ($base | ConvertTo-Json -Depth 20)).Replace('__METER_CONF__', ($meterConf | ConvertTo-Json))
$ud = $ud.Replace('__METER_SCRIPT_B64__', [Convert]::ToBase64String($ms.ToArray())).Replace("`r`n", "`n")
if ($ud.Length -gt 16384) { throw "user-data is $($ud.Length) bytes, AWS allows 16384" }
$udFile = "$script:data\userdata.sh"
[IO.File]::WriteAllText($udFile, $ud)
$sha = [Security.Cryptography.SHA256]::Create()
$udHash = -join ($sha.ComputeHash($script:utf8.GetBytes($ud)) | ForEach-Object { $_.ToString('x2') })

$current = Get-InstanceState
$needNew = $ReplaceServer -or $current -in 'none', 'terminated', 'shutting-down' -or $state.userdata_hash -ne $udHash
if (-not $needNew) {
  Write-Host "server $($state.iid) is up to date ($current)"
} else {
  $old = $state.iid
  $ami = aws ssm get-parameter @p --name /aws/service/ami-amazon-linux-latest/al2023-ami-kernel-default-x86_64 --query Parameter.Value --output text
  for ($i = 1; $i -le 6; $i++) {
    $iid = aws ec2 run-instances @p --image-id $ami --instance-type t3.micro --security-group-ids $state.sg `
      --iam-instance-profile "Name=dormvpn-ssm" --instance-initiated-shutdown-behavior stop `
      --user-data (UriOf $udFile) --metadata-options "HttpTokens=required,HttpEndpoint=enabled" `
      --credit-specification "CpuCredits=standard" `
      --block-device-mappings "DeviceName=/dev/xvda,Ebs={VolumeSize=8,VolumeType=gp3,DeleteOnTermination=true}" `
      --tag-specifications "ResourceType=instance,Tags=[{Key=Name,Value=dormvpn-server}]" "ResourceType=volume,Tags=[{Key=Name,Value=dormvpn-server}]" `
      --query "Instances[0].InstanceId" --output text
    if ($iid -match '^i-') { break }
    Start-Sleep 10
  }
  if ($iid -notmatch '^i-') { throw "run-instances failed" }
  Write-Host "new instance $iid, waiting for it to start"
  aws ec2 wait instance-running @p --instance-ids $iid
  for ($i = 1; $i -le 5; $i++) {
    aws ec2 associate-address @p --allocation-id $state.alloc --instance-id $iid --allow-reassociation | Out-Null
    if ($LASTEXITCODE -eq 0) { break }; Start-Sleep 5
  }
  Set-StateField 'iid' $iid
  Set-StateField 'userdata_hash' $udHash
  if ($old -and $old -ne $iid) {
    for ($i = 1; $i -le 5; $i++) {
      aws ec2 terminate-instances @p --instance-ids $old | Out-Null
      if ($LASTEXITCODE -eq 0) { Write-Host "old instance $old terminating"; break }; Start-Sleep 5
    }
  }
}

# ---------------------------------------------------------------- 9. tell the API about the server
Invoke-ApiDirect @{ admin_action = 'set_server'; instance_id = $state.iid; ip = $state.ip; public_key = $s.reality_public_key
                    short_id = $s.short_id; sni = $s.sni; region = $s.aws_region } | Out-Null

if ($needNew) {
  Step "waiting for the server to finish setting up (2-4 minutes)"
  try {
    Wait-ServerOnline $state.iid 420
    $deadline = (Get-Date).AddMinutes(8)
    do { Start-Sleep 10; $done = (Invoke-Server 'grep -c DORMVPN_SETUP_DONE /var/log/dormvpn-setup.log || true' -TimeoutSec 30).Trim() }
    while ($done -ne '1' -and (Get-Date) -lt $deadline)
    if ($done -eq '1') { Write-Host "server is ready" } else { Write-Host "the server hasn't finished yet; it keeps going on its own" }
  } catch { Write-Host "couldn't check on the server: $($_.Exception.Message)" }
}

# ---------------------------------------------------------------- 10. spending limit
Step "spending limit"
try { Set-SpendLimit $state.iid } catch { Write-Host "WARNING: the spending limit isn't set: $($_.Exception.Message)" }

Write-Host "`nDORMVPN_DEPLOYED $url"
