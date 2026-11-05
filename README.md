# ZC-DormVPN

Your own VPN on your own AWS account, for you and the people you choose. Traffic looks like an ordinary HTTPS connection to a well-known website on TCP port 443 (VLESS + REALITY, using [sing-box](https://github.com/SagerNet/sing-box)), so it works on networks that block normal VPNs.

It comes as two Windows apps:

- **DormVPN Admin** (for you): a setup wizard that builds everything in AWS, then a dashboard with the link and login for your web admin panel.
- **DormVPN** (for your users): they sign in with the username and password you made for them, see how much data they've used against their limit, and connect. There's no sign-up.

In the **web admin panel** you add and remove users, set each user's monthly data limit, set a cap for everyone together, see usage, and pause or start the server.

## Getting started

You need Windows 10 or 11, [Git](https://git-scm.com/download/win), and an [AWS account](https://aws.amazon.com/). Nothing else: the apps build with the compiler that comes with Windows.

Everything stays inside the folder you clone into, so pick where you want it. In PowerShell:

```powershell
git clone https://github.com/NotCodingRN/dormvpn.git D:\DormVPN
cd D:\DormVPN
powershell -ExecutionPolicy Bypass -File build.ps1 -Install
```

That builds both apps into `D:\DormVPN\app` and adds **DormVPN Admin** to your Desktop and Start menu.

1. Open DormVPN Admin. The wizard walks you through:
   - installing the AWS CLI,
   - creating an AWS access key and picking a region (it can measure which one is fastest from where you are),
   - downloading sing-box 1.14.4 and putting its files in the folder it shows you,
   - creating the VPN (5–10 minutes).
2. At the end it shows your **admin panel link, username and generated password**. Open the panel and add users. Each new user gets a generated password to send them, and you add yourself the same way if you want to use the VPN.
3. Click **Build installer…**. It saves `DormVPN-Setup.zip` in the `installers` folder. Send it to your users. They unzip it, double-click `install.cmd` and sign in.

If you delete the folder, use **Delete everything** in the app first, or you lose the record of what to remove from AWS and it keeps billing.

Phones: in the panel, click **Phone** next to a user to get a QR code for Hiddify (Android/iOS). Phones can't show usage, but the user's limit still applies.

## How it fits together

```
 DormVPN Admin (your PC) ──aws cli──▶ creates everything below, shows the panel login
                                       
 Web admin panel ─┐                    
 DormVPN app ─────┼─HTTPS─▶ API (Lambda + function URL) ◀──▶ DynamoDB: users, limits, usage
 VPN server meter ┘                          │ start/stop
                                             ▼
 DormVPN app ═══════ VLESS+REALITY :443 ═══▶ VPN server (EC2 t3.micro, Elastic IP)
```

| Piece | What it does |
|---|---|
| API + panel (`server/api.py`, `server/panel.html`) | One Lambda function with a public HTTPS URL. Serves the admin panel, signs users in, hands the app its VPN settings and usage, and starts/stops the server. Keeps working while the VPN server is paused. |
| User database | DynamoDB table `dormvpn`: users (passwords stored as PBKDF2 hashes), limits, monthly usage. |
| VPN server | sing-box on Amazon Linux 2023 with BBR. No SSH port: it's managed with AWS SSM. |
| Usage meter (`server/dormvpn-meter.py`) | Runs on the server every minute. Pulls the user list from the API, rebuilds sing-box's user list when it changes, counts each user's traffic with nftables, blocks anyone over their limit, reports usage back, and can pause the server when idle. |
| `server/deploy.ps1` | Creates or updates all of the above. Safe to re-run: it only replaces the VPN instance when its setup changed, and the IP stays the same. |

## Limits and costs

- Usage is counted per calendar month (UTC) and resets on the 1st. "Used" is upload + download, because AWS charges both directions as data out.
- A user who goes over their limit is cut off within a minute. Raising their limit lets them straight back in.
- The **monthly cap** cuts everyone off once the total reaches it. Setting it at or under 100 GB keeps you in AWS's free data allowance.
- **Auto-pause** stops the server after a set time with no traffic. With "Users can start a paused server" on, the app offers a **Start server** button, so the server only runs while someone uses it.

AWS list prices in `eu-west-3` (Paris):

| Part | Per month |
|---|---|
| `t3.micro` instance, running all month | $8.61 (nothing while paused) |
| Public IPv4 address | $3.65 |
| 8 GB disk | $0.74 |
| API, panel, database | a few cents |
| Data out | first 100 GB free per account, then about $0.09/GB |

## Where things are

| Path | What it is |
|---|---|
| `admin-app/` | DormVPN Admin source (C#, built with the .NET Framework compiler in Windows) |
| `windows-app/` | DormVPN user app source, shared UI kit (`Ui.cs`), install/uninstall scripts |
| `server/` | Scripts the admin app runs, the API, the panel, the server bootstrap and meter |
| `app\` | The built admin app (created by `build.ps1`, replaced on every build) |
| `data\` | Your keys, AWS resource ids, admin login (password encrypted for your Windows user) and the sing-box engine. Created by the app, never in git. Back it up |
| `installers\` | The `DormVPN-Setup.zip` files you build for your users |

Two things live outside the folder because other programs own them: the AWS CLI keeps your access keys in `%USERPROFILE%\.aws`, and the DormVPN user app installs to `C:\Program Files\DormVPN`, since a VPN needs admin rights.

Nothing secret is in the repo or in the user installer: users get their VPN key only after signing in.

## Maintenance (in DormVPN Admin)

- **Update server**: re-runs the deploy. To get a new version first: `git pull`, then `powershell -ExecutionPolicy Bypass -File build.ps1`. Rebuilding never touches `data\`.
- **New admin password**: generates a new one. The old one stops working.
- **Change AWS keys**: back to the wizard's AWS step.
- **Delete everything**: removes the server, IP, panel, database, logs and roles from AWS, then checks that nothing is left. Quit DormVPN on this PC first, or it refuses: deleting the server you're connected through would cut your internet. Your AWS account and access keys aren't touched.
- A user's link or password leaked: **New password** in the panel signs them out everywhere. For the phone QR, delete and re-add the user.

## Use responsibly

This is for securing your own and your friends' traffic. Follow the rules of the networks you connect through and the [AWS Acceptable Use Policy](https://aws.amazon.com/aup/). Whatever goes through the server is tied to your AWS account.
