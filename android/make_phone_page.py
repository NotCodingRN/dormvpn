"""Builds dist/android/phone-setup.html: QR code for the VLESS share link + setup steps."""
import html, io, pathlib, sys
import qrcode
import qrcode.image.svg

root = pathlib.Path(__file__).resolve().parent.parent
link = (root / "out" / "share-link.txt").read_text().strip()
out = root / "dist" / "android" / "phone-setup.html"

qr = qrcode.QRCode(error_correction=qrcode.constants.ERROR_CORRECT_M, border=2, box_size=10)
qr.add_data(link)
qr.make(fit=True)
buf = io.BytesIO()
qr.make_image(image_factory=qrcode.image.svg.SvgPathImage).save(buf)
svg = buf.getvalue().decode()
svg = svg[svg.index("<svg"):]

page = f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>DormVPN Phone Setup</title>
<style>
:root {{ --bg:#f6f7f9; --card:#fff; --ink:#15181d; --muted:#5b6370; --line:#e3e6eb; --accent:#22a559; }}
@media (prefers-color-scheme: dark) {{ :root:not([data-theme="light"]) {{ --bg:#111317; --card:#1a1d22; --ink:#eef0f3; --muted:#9aa3ae; --line:#2a2f36; }} }}
body {{ margin:0; background:var(--bg); color:var(--ink); font:16px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif; }}
main {{ max-width:760px; margin:0 auto; padding:24px 16px 48px; }}
h1 {{ font-size:26px; margin:0 0 4px; }} h2 {{ font-size:19px; margin:0 0 10px; }}
.sub {{ color:var(--muted); margin:0 0 20px; }}
.card {{ background:var(--card); border:1px solid var(--line); border-radius:14px; padding:20px; margin:0 0 16px; }}
.qr {{ background:#fff; border-radius:12px; padding:14px; width:min(340px,100%); box-sizing:border-box; margin:6px auto 12px; }}
.qr svg {{ width:100%; height:auto; display:block; }}
ol {{ padding-left:22px; margin:0; }} li {{ margin:6px 0; }}
.tag {{ display:inline-block; font-size:12px; font-weight:600; color:#fff; background:var(--accent); border-radius:99px; padding:2px 10px; margin-left:6px; vertical-align:middle; }}
code, .link {{ font-family:ui-monospace,Consolas,monospace; font-size:12.5px; }}
.link {{ display:block; word-break:break-all; background:var(--bg); border:1px solid var(--line); border-radius:8px; padding:10px; color:var(--muted); }}
.warn {{ color:var(--muted); font-size:14px; }}
</style></head>
<body><main>
<h1>DormVPN on your Android phone</h1>
<p class="sub">Same server as the Windows app: AWS Paris, disguised as microsoft.com traffic on port 443.</p>

<div class="card">
<h2>Option A: Hiddify <span class="tag">Easiest</span></h2>
<ol>
<li>On your phone, install <b>Hiddify</b> from the Google Play Store.</li>
<li>Open it, tap <b>+</b> (New profile) &rarr; <b>Scan QR code</b>.</li>
<li>Point the camera at this code:</li>
</ol>
<div class="qr">{svg}</div>
<ol start="4">
<li>Tap the big connect button and allow the VPN request.</li>
<li>Check it worked: open <code>ipinfo.io</code> in the browser. It should say <b>Paris, Amazon</b>.</li>
</ol>
</div>

<div class="card">
<h2>Option B: official sing-box app</h2>
<p>Uses exactly the same engine and settings as the Windows app.</p>
<ol>
<li>Copy <code>sing-box-1.14.4-android-arm64.apk</code> and <code>DormVPN.json</code> from this folder to your phone (USB cable, Google Drive, or Telegram "Saved Messages").</li>
<li>Open the APK to install it (allow "install unknown apps" for your file manager when asked).</li>
<li>In sing-box: <b>Profiles</b> &rarr; <b>+</b> &rarr; <b>Import from file</b> &rarr; pick <code>DormVPN.json</code>.</li>
<li>Go to <b>Dashboard</b> and tap <b>Start</b>, then allow the VPN request.</li>
</ol>
</div>

<div class="card">
<h2>Link (if you can't scan)</h2>
<span class="link">{html.escape(link)}</span>
<p class="warn">Anyone with this code or link can use your server and your AWS data allowance. Don't post it or share it.</p>
</div>
</main></body></html>
"""
out.write_text(page, encoding="utf-8")
print("wrote", out)
