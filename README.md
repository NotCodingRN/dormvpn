# DormVPN

A personal VPN on your own AWS server. Traffic looks like an ordinary HTTPS connection to a well-known website on TCP port 443 (VLESS + REALITY, using [sing-box](https://github.com/SagerNet/sing-box)). The server is tuned with BBR so it stays fast on lossy, long-distance routes. It comes with a Windows tray app and an Android setup page.

It's meant for one person: you, your laptop and your phone.

## Design

| Choice | Why |
|---|---|
| TCP on port 443 only | Many restrictive networks drop UDP and unusual ports. WireGuard and other UDP VPNs fail there. |
| VLESS + REALITY with a real site's name (default `www.microsoft.com`) | The connection presents a genuine TLS handshake for that site. Probes without your key are passed through to the real site. |
| BBR congestion control on the server | Default TCP backs off hard on lossy routes. In testing on a ~50 ms lossy route, one connection went from 2–6 Mbps to about 26 Mbps. |
| Elastic IP | The server address stays the same across redeploys, so clients don't need updating. |
| `t3.micro` on Amazon Linux 2023 | Cheap, plenty for one user. Automatic security updates are on. |

## Setup

1. Install the [AWS CLI](https://aws.amazon.com/cli/) and configure a profile.
2. Download [sing-box 1.14.4](https://github.com/SagerNet/sing-box/releases/tag/v1.14.4) (`windows-amd64.zip`) and put `sing-box.exe`, `libcronet.dll` and `LICENSE` in `bin/`.
3. Copy `secrets.example.json` to `secrets.json` and fill it in. Generate the values with `bin\sing-box.exe generate reality-keypair`, `generate uuid` and `generate rand 8 --hex`. Set `aws_profile` and `aws_region` to yours (pick the region with the lowest ping from where you are).
4. Run `server\deploy.ps1` to create the server.
5. Run `windows-app\build.ps1`, then double-click `dist\windows\install.cmd`.
6. For a phone, run `python android\make_phone_page.py` (needs `pip install qrcode`) and open `dist\android\phone-setup.html`.

`secrets.json`, `server/state.json`, `server/userdata.sh`, `out/` and `dist/` contain your keys and server address. They're in `.gitignore`. Don't commit or share them: anyone who has them can use your server.

## Repository layout

| Path | What it is |
|---|---|
| `server/userdata.template.sh` | Server bootstrap: installs a pinned, checksum-verified sing-box, sets up BBR and runs sing-box as an unprivileged service |
| `server/deploy.ps1` | Creates or replaces the AWS server (security group, Elastic IP, instance). Re-running keeps the same IP |
| `server/destroy.ps1` | Deletes the server, Elastic IP and security group |
| `make-configs.ps1` | Generates the server, Windows, Android and test-client configs and the share link from `secrets.json` |
| `windows-app/` | Tray app source (C#, builds with the .NET Framework compiler included in Windows), build and install scripts |
| `android/make_phone_page.py` | Builds a phone setup page with a QR code for Hiddify, and steps for the official sing-box app |

## Windows app

The tray icon is a shield: **grey** = off, **orange** = connecting or reconnecting, **green** = connected. Double-click it to connect or disconnect. Right-click for options.

- **Auto-reconnect:** every 30 seconds it checks that your public IP is the server's. If not, it reconnects. If the engine crashes, it restarts it with backoff.
- **No leaks while reconnecting:** strict routing keeps traffic inside the VPN adapter while it reconnects. The only gap is a crash of the engine itself, which is restarted within about 2 seconds.
- **Start at sign-in:** a Task Scheduler task, so there's no UAC prompt each time. Toggle it from the menu.
- Don't run another VPN at the same time; two VPNs fight over routing.
- Log: right-click → Open log (`C:\Program Files\DormVPN\dormvpn.log`).

## Android

- **Hiddify** (Google Play): scan the QR code on the setup page.
- **Official sing-box app:** install it from the [sing-box releases](https://github.com/SagerNet/sing-box/releases/tag/v1.14.4), then import `dist/android/DormVPN.json`.

## Cost

AWS list prices in `eu-west-3` (Paris); other regions are similar:

| Part | Per month |
|---|---|
| `t3.micro` instance ($0.0118/hour) | $8.61 |
| Public IPv4 address ($0.005/hour) | $3.65 |
| 8 GB gp3 disk | $0.74 |
| Data out to the internet | First 100 GB free per account, then about $0.09/GB |

You can stop the instance in the EC2 console when you're not using it, which removes the instance charge. The IP and disk keep billing.

## Maintenance

- **Server IP blocked:** run `server\destroy.ps1` then `server\deploy.ps1` to get a new IP. Then rebuild and reinstall the Windows app, and re-scan the QR code on your phone.
- **Rotating keys** (if a link leaked): generate new values, put them in `secrets.json`, run `server\deploy.ps1`, rebuild, reinstall and re-scan.

## Use responsibly

This is for securing your own traffic. Follow the rules of the networks you connect through, and the [AWS Acceptable Use Policy](https://aws.amazon.com/aup/). Whatever goes through the server is tied to your AWS account.
