# Creates or updates the whole DormVPN setup in AWS. Safe to re-run.
#   - VPN server: a $5 Lightsail instance (1 TB of data a month included) with a static IP,
#     IPv6 going out, and only TCP 443 open. It lives in the server region (Lightsail isn't in every region).
#   - API + admin panel: DynamoDB table, Lambda function with a public HTTPS URL, in aws_region
#   - the API's IAM role, the admin login (first run), and keys (first run)
# The VPN instance is only replaced when its bootstrap changed (or with -ReplaceServer); the IP
# stays the same either way, and users, limits and this month's usage live in DynamoDB.
# An install from an older version that ran on EC2 moves to Lightsail and its EC2 server is deleted.
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
$sr = Get-ServerRegion
$sp = Get-ServerArgs
aws lightsail get-regions @sp --query "regions[?name=='$sr'].name" --output text 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Lightsail isn't available in $sr for this account. Set server_region in secrets.json to a Lightsail region, e.g. eu-central-1." }
Write-Host "AWS account $acct, panel in $($s.aws_region), VPN server in $sr"

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

# ---------------------------------------------------------------- 2. address
Step "address"
aws lightsail get-static-ip @sp --static-ip-name dormvpn-ip 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
  aws lightsail allocate-static-ip @sp --static-ip-name dormvpn-ip | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "could not get a static IP" }
}
$newIp = "$(aws lightsail get-static-ip @sp --static-ip-name dormvpn-ip --query staticIp.ipAddress --output text)".Trim()
Write-Host "address $newIp"

# ---------------------------------------------------------------- 3. IAM roles
Step "permissions"
$apiRole = Initialize-Role 'dormvpn-api' 'lambda.amazonaws.com' @('arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole') @{
  Version = '2012-10-17'
  Statement = @(
    @{ Effect = 'Allow'; Action = @('dynamodb:GetItem', 'dynamodb:PutItem', 'dynamodb:UpdateItem', 'dynamodb:DeleteItem', 'dynamodb:Scan')
       Resource = "arn:aws:dynamodb:$($s.aws_region):${acct}:table/dormvpn" },
    @{ Effect = 'Allow'; Action = 'lightsail:GetInstanceState'; Resource = '*' },  # takes no resource ARN
    @{ Effect = 'Allow'; Action = @('lightsail:StartInstance', 'lightsail:StopInstance'); Resource = "arn:aws:lightsail:${sr}:${acct}:Instance/*" }
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

$old = $state.server
$current = Get-ServerState $old
$needNew = $ReplaceServer -or $current -in 'none', 'terminated', 'shutting-down' -or $state.userdata_hash -ne $udHash
if (-not $needNew) {
  Write-Host "server $old is up to date ($current)"
} else {
  $name = 'dormvpn-server-' + $udHash.Substring(0, 8) + '-' + (Get-Random -Maximum 9999)
  aws lightsail create-instances @sp --instance-names $name --availability-zone "${sr}a" --blueprint-id amazon_linux_2023 `
    --bundle-id nano_3_0 --ip-address-type dualstack --user-data (UriOf $udFile) --tags key=Name,value=dormvpn-server | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "could not create the Lightsail server" }
  Write-Host "new server $name, waiting for it to start"
  $deadline = (Get-Date).AddMinutes(5)
  while ((Get-ServerState $name) -ne 'running' -and (Get-Date) -lt $deadline) { Start-Sleep 5 }
  if ((Get-ServerState $name) -ne 'running') { throw "server $name didn't start" }
  # only the VPN port: this also closes the SSH and HTTP ports Lightsail opens by default
  for ($i = 1; $i -le 5; $i++) {
    aws lightsail put-instance-public-ports @sp --instance-name $name --port-infos "fromPort=443,toPort=443,protocol=tcp,cidrs=0.0.0.0/0" | Out-Null
    if ($LASTEXITCODE -eq 0) { break }; Start-Sleep 5
  }
  if ($LASTEXITCODE -ne 0) { throw "could not set the server's firewall" }
  $on = "$(aws lightsail get-static-ip @sp --static-ip-name dormvpn-ip --query staticIp.attachedTo --output text)".Trim()
  if ($on -and $on -ne 'None') { aws lightsail detach-static-ip @sp --static-ip-name dormvpn-ip | Out-Null }
  for ($i = 1; $i -le 5; $i++) {
    aws lightsail attach-static-ip @sp --static-ip-name dormvpn-ip --instance-name $name | Out-Null
    if ($LASTEXITCODE -eq 0) { break }; Start-Sleep 5
  }
  if ($LASTEXITCODE -ne 0) { throw "could not attach the static IP" }
  Set-StateField 'server' $name
  Set-StateField 'ip' $newIp
  Set-StateField 'userdata_hash' $udHash
  if ($old -and $old -ne $name -and (Get-ServerState $old) -ne 'none') {
    aws lightsail delete-instance @sp --instance-name $old | Out-Null
    if ($LASTEXITCODE -eq 0) { Write-Host "old server $old deleted" }
  }
}

if ($needNew) {
  # sing-box starts listening once the server has set itself up and pulled the users from the API
  Step "waiting for the server to finish setting up (2-4 minutes)"
  $deadline = (Get-Date).AddMinutes(8)
  do {
    Start-Sleep 10
    $c = New-Object Net.Sockets.TcpClient
    try { $up = $c.ConnectAsync($state.ip, 443).Wait(3000) -and $c.Connected } catch { $up = $false } finally { $c.Close() }
  } while (-not $up -and (Get-Date) -lt $deadline)
  if ($up) { Write-Host "server is ready" } else { Write-Host "the server hasn't answered yet; it keeps going on its own (run Update later to finish)" }
}

# ---------------------------------------------------------------- 9. tell the API about the server
# (after it's ready: until then the apps keep using the old server)
if (-not $needNew -or $up) {
  Invoke-ApiDirect @{ admin_action = 'set_server'; instance_id = $state.server; ip = $state.ip; public_key = $s.reality_public_key
                      short_id = $s.short_id; sni = $s.sni; region = $sr } | Out-Null
}

# an install from before the move to Lightsail: its EC2 server goes once the new one is in place
if (($state.iid -or $state.alloc -or $state.sg) -and (-not $needNew -or $up)) {
  Step "removing the old EC2 server"
  Remove-Ec2Server
  foreach ($k in 'iid', 'alloc', 'sg') { $state.PSObject.Properties.Remove($k) }
  Save-State $state
}

# ---------------------------------------------------------------- 10. spending limit
Step "spending limit"
try { Set-SpendLimit } catch { Write-Host "WARNING: the spending limit isn't set: $($_.Exception.Message)" }

Write-Host "`nDORMVPN_DEPLOYED $url"
