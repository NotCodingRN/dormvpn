# Generates server + client sing-box configs from secrets.json.
# Usage: .\make-configs.ps1 [-ServerIp 1.2.3.4]   (server IP is read from server\state.json if omitted)
param([string]$ServerIp)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$s = Get-Content "$root\secrets.json" -Raw | ConvertFrom-Json
if (-not $ServerIp -and (Test-Path "$root\server\state.json")) { $ServerIp = (Get-Content "$root\server\state.json" -Raw | ConvertFrom-Json).ip }
if (-not $ServerIp) { $ServerIp = '0.0.0.0' }
New-Item -ItemType Directory -Force "$root\out" | Out-Null

function Save-Json($obj, $path) {
  $json = $obj | ConvertTo-Json -Depth 20
  [IO.File]::WriteAllText($path, $json, (New-Object Text.UTF8Encoding($false)))
}

# ---------- server ----------
$server = [ordered]@{
  log       = [ordered]@{ level = 'warn'; timestamp = $true }
  inbounds  = @([ordered]@{
    type = 'vless'; tag = 'vless-in'; listen = '::'; listen_port = 443
    users = @([ordered]@{ name = 'me'; uuid = $s.uuid; flow = 'xtls-rprx-vision' })
    tls = [ordered]@{
      enabled = $true; server_name = $s.sni
      reality = [ordered]@{
        enabled = $true
        handshake = [ordered]@{ server = $s.sni; server_port = 443 }
        private_key = $s.reality_private_key
        short_id = @($s.short_id)
      }
    }
  })
  outbounds = @([ordered]@{ type = 'direct'; tag = 'direct' })
}
Save-Json $server "$root\out\server.json"

# ---------- shared client outbound ----------
function New-ProxyOutbound {
  [ordered]@{
    type = 'vless'; tag = 'proxy'; server = $ServerIp; server_port = 443
    uuid = $s.uuid; flow = 'xtls-rprx-vision'
    tls = [ordered]@{
      enabled = $true; server_name = $s.sni
      utls = [ordered]@{ enabled = $true; fingerprint = 'chrome' }
      reality = [ordered]@{ enabled = $true; public_key = $s.reality_public_key; short_id = $s.short_id }
    }
  }
}
function New-Dns {
  [ordered]@{
    servers = @(
      [ordered]@{ type = 'https'; tag = 'dns-remote'; server = '1.1.1.1'; detour = 'proxy' },
      [ordered]@{ type = 'local'; tag = 'dns-local' }
    )
    final = 'dns-remote'
    strategy = 'ipv4_only'
  }
}
function New-Route {
  [ordered]@{
    rules = @(
      [ordered]@{ action = 'sniff' },
      [ordered]@{ protocol = 'dns'; action = 'hijack-dns' },
      [ordered]@{ ip_is_private = $true; outbound = 'direct' }
    )
    final = 'proxy'
    auto_detect_interface = $true
    default_domain_resolver = 'dns-local'
  }
}

# ---------- Windows (TUN, whole-PC VPN) ----------
$win = [ordered]@{
  log = [ordered]@{ level = 'warn'; timestamp = $true }
  dns = New-Dns
  inbounds = @([ordered]@{
    type = 'tun'; tag = 'tun-in'; interface_name = 'DormVPN'
    address = @('172.19.0.1/30'); mtu = 1400
    auto_route = $true; strict_route = $true; stack = 'mixed'
  })
  outbounds = @((New-ProxyOutbound), [ordered]@{ type = 'direct'; tag = 'direct' })
  route = New-Route
}
Save-Json $win "$root\out\windows-client.json"

# ---------- Android (sing-box app / TUN via VpnService) ----------
$android = [ordered]@{
  log = [ordered]@{ level = 'warn' }
  dns = New-Dns
  inbounds = @([ordered]@{
    type = 'tun'; tag = 'tun-in'
    address = @('172.19.0.1/30'); mtu = 1400
    auto_route = $true; strict_route = $true; stack = 'mixed'
  })
  outbounds = @((New-ProxyOutbound), [ordered]@{ type = 'direct'; tag = 'direct' })
  route = New-Route
}
Save-Json $android "$root\out\android-profile.json"

# ---------- local test client (SOCKS on 127.0.0.1:10808, no TUN, no admin) ----------
$test = [ordered]@{
  log = [ordered]@{ level = 'info' }
  inbounds = @([ordered]@{ type = 'mixed'; tag = 'mixed-in'; listen = '127.0.0.1'; listen_port = 10808 })
  outbounds = @((New-ProxyOutbound))
}
Save-Json $test "$root\out\test-client.json"

# ---------- share link (Hiddify / v2rayNG / NekoBox) ----------
$link = "vless://$($s.uuid)@${ServerIp}:443?encryption=none&flow=xtls-rprx-vision&security=reality&sni=$($s.sni)&fp=chrome&pbk=$($s.reality_public_key)&sid=$($s.short_id)&type=tcp#DormVPN"
[IO.File]::WriteAllText("$root\out\share-link.txt", $link)

"configs written to $root\out (server IP $ServerIp)"
