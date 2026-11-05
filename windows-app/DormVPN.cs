// DormVPN - Windows client. Sign in with the account the admin gave you, see your usage, connect.
// Build: build.ps1 at the repo root (uses the .NET Framework 4 C# compiler that ships with Windows).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using DormVPN.Shared;

[assembly: System.Reflection.AssemblyTitle("DormVPN")]
[assembly: System.Reflection.AssemblyProduct("DormVPN")]
[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.0.0.0")]

namespace DormVPN
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (var m = new Mutex(true, "Global\\DormVPN-single-instance", out created))
            {
                if (!created)
                {
                    // already running: ask that copy to show its window
                    try { EventWaitHandle.OpenExisting("Global\\DormVPN-show").Set(); } catch { }
                    return;
                }
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool background = Array.Exists(args, a => a.Equals("/background", StringComparison.OrdinalIgnoreCase));
                Application.Run(new ClientApp(background));
            }
        }
    }

    enum VpnState { Disconnected, Connecting, Connected, Reconnecting }

    class ClientApp : ApplicationContext
    {
        readonly string dir = AppDomain.CurrentDomain.BaseDirectory;
        string SingBox { get { return Path.Combine(dir, "sing-box.exe"); } }
        string ConfigPath { get { return Path.Combine(dir, "config.json"); } }
        string LogPath { get { return Path.Combine(dir, "dormvpn.log"); } }
        string SettingsPath { get { return Path.Combine(dir, "settings.ini"); } }
        string SessionPath { get { return Path.Combine(dir, "session.dat"); } }

        public string ApiUrl = "";
        public string Token = "";
        public string UserName = "";
        public Dictionary<string, object> Me;      // last /api/me answer
        public DateTime MeTime = DateTime.MinValue;
        public bool Offline;

        MainForm form;
        NotifyIcon tray;
        ToolStripMenuItem miOpen, miToggle, miAutoConnect, miStartup;
        System.Windows.Forms.Timer timer;
        Icon icoOff, icoWait, icoOn;
        bool hintedTray;

        Process proc;
        public bool WantConnected;
        public VpnState State = VpnState.Disconnected;
        public string ExitInfo = "";
        string serverIp = "";
        int failCount, restartCount;
        DateTime lastStart = DateTime.MinValue, nextHealth = DateTime.MinValue, connectingSince = DateTime.MinValue;
        DateTime nextMe = DateTime.MinValue;
        volatile bool healthBusy, meBusy;
        volatile int healthResult;          // 0 none, 1 ok, 2 fail
        volatile string healthDetail = "";

        readonly object logLock = new object();
        static readonly Regex Ansi = new Regex("\x1B\\[[0-9;]*m", RegexOptions.Compiled);

        public ClientApp(bool background)
        {
            icoOff = Theme.MakeIcon(Theme.Grey);
            icoWait = Theme.MakeIcon(Color.FromArgb(240, 160, 32));
            icoOn = Theme.MakeIcon(Theme.Accent);

            try { ApiUrl = Json.Str(Json.Parse(File.ReadAllText(Path.Combine(dir, "api.json"))), "api").TrimEnd('/'); } catch { }
            var sess = Json.Parse(Secret.Load(SessionPath) ?? "");
            Token = Json.Str(sess, "token"); UserName = Json.Str(sess, "name");

            var menu = new ContextMenuStrip();
            miOpen = new ToolStripMenuItem("Open DormVPN", null, delegate { ShowWindow(); }) { Font = Theme.B(9) };
            miToggle = new ToolStripMenuItem("Connect", null, delegate { Toggle(); });
            miAutoConnect = new ToolStripMenuItem("Connect when DormVPN opens", null, delegate { miAutoConnect.Checked = !miAutoConnect.Checked; WriteSetting("autoconnect", miAutoConnect.Checked ? "1" : "0"); });
            miStartup = new ToolStripMenuItem("Start DormVPN when I sign in", null, delegate { ToggleStartup(); });
            menu.Items.AddRange(new ToolStripItem[] { miOpen, miToggle, new ToolStripSeparator(), miAutoConnect, miStartup,
                new ToolStripMenuItem("Open log", null, delegate { Ui.Open(LogPath); }), new ToolStripSeparator(),
                new ToolStripMenuItem("Quit DormVPN", null, delegate { Quit(); }) });
            menu.Opening += delegate { miStartup.Checked = StartupTaskExists(); miToggle.Enabled = Token != ""; };
            miAutoConnect.Checked = ReadSetting("autoconnect", "1") == "1";

            tray = new NotifyIcon { Icon = icoOff, Text = "DormVPN", ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

            form = new MainForm(this);
            form.FormClosing += (s, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true; form.Hide();
                    if (!hintedTray) { hintedTray = true; Balloon("DormVPN is still running", WantConnected ? "You stay connected. Click the shield icon to open it again." : "Click the shield icon to open it again.", ToolTipIcon.Info); }
                }
            };

            var show = new EventWaitHandle(false, EventResetMode.AutoReset, "Global\\DormVPN-show");
            var t = new Thread(() => { while (true) { show.WaitOne(); try { form.BeginInvoke((Action)ShowWindow); } catch { } } }) { IsBackground = true };
            t.Start();

            timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += delegate { Tick(); };
            timer.Start();

            Log("[app] DormVPN started");
            if (Token != "") { RefreshMe(true); }
            Refresh();
            if (!background || Token == "") ShowWindow();
        }

        public void ShowWindow()
        {
            if (!form.Visible) form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
            if (Token != "" && (DateTime.Now - MeTime).TotalSeconds > 15) RefreshMe(false);
        }

        // ---------------------------------------------------------------- account

        public void SignIn(string user, string password, Action<string> done)
        {
            if (ApiUrl == "") { done("This copy of DormVPN has no server address. Ask for a new installer."); return; }
            ThreadPool.QueueUserWorkItem(delegate
            {
                var r = Http.Call("POST", ApiUrl + "/api/login", null, new Dictionary<string, object> { { "username", user.Trim().ToLowerInvariant() }, { "password", password } });
                form.BeginInvoke((Action)delegate
                {
                    if (!r.Ok) { done(r.Error); return; }
                    Token = Json.Str(r.Data, "token"); UserName = Json.Str(r.Data, "name");
                    try { Secret.Save(SessionPath, Json.Write(new Dictionary<string, object> { { "token", Token }, { "name", UserName } })); } catch { }
                    Log("[app] signed in as " + UserName);
                    done(null);
                    Me = null;
                    RefreshMe(miAutoConnect.Checked);
                    Refresh();
                });
            });
        }

        public void SignOut(string why)
        {
            Log("[app] signed out" + (why != null ? ": " + why : ""));
            if (WantConnected) { WantConnected = false; StopCore(); SetState(VpnState.Disconnected); }
            Token = ""; UserName = ""; Me = null; ExitInfo = "";
            try { File.Delete(SessionPath); } catch { }
            try { File.Delete(ConfigPath); } catch { }
            form.ShowSignIn(why);
            Refresh();
            if (why != null) { ShowWindow(); }
        }

        /// Fetches /api/me in the background: usage, limit, server state and VPN settings.
        public void RefreshMe(bool connectAfter)
        {
            if (meBusy || Token == "" || ApiUrl == "") return;
            meBusy = true;
            nextMe = DateTime.Now.AddSeconds(60);
            ThreadPool.QueueUserWorkItem(delegate
            {
                var r = Http.Call("GET", ApiUrl + "/api/me", Token, null);
                try
                {
                    form.BeginInvoke((Action)delegate
                    {
                        meBusy = false;
                        if (r.Status == 401 || r.Status == 403) { SignOut(r.Error); return; }
                        if (!r.Ok) { Offline = true; Refresh(); return; }
                        Offline = false;
                        Me = r.Data; MeTime = DateTime.Now;
                        bool changed = WriteConfig();
                        if (changed && WantConnected) RestartCore("server settings changed");
                        if (ServerState == "pending" || ServerState == "stopping") nextMe = DateTime.Now.AddSeconds(5);
                        if (connectAfter && !WantConnected && CanConnect() == null && ServerState == "running") Connect();
                        Refresh();
                    });
                }
                catch { meBusy = false; }
            });
        }

        public string ServerState { get { return Me == null ? "" : Json.Str(Json.Obj(Me, "server"), "state"); } }
        public bool Blocked { get { return Me != null && Json.Bool(Me, "blocked"); } }

        /// null when connecting is possible, otherwise the reason it isn't
        public string CanConnect()
        {
            if (Me == null) return "Still loading your account…";
            if (Blocked) return "You've used all your data for this month. It resets on " + ResetText() + ".";
            return null;
        }

        public string ResetText()
        {
            DateTime d;
            return DateTime.TryParse(Json.Str(Me, "resets"), out d) ? d.ToString("MMMM d") : "the 1st";
        }

        public void Wake()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                var r = Http.Call("POST", ApiUrl + "/api/wake", Token, new Dictionary<string, object>());
                form.BeginInvoke((Action)delegate
                {
                    if (!r.Ok) { form.ShowError(r.Error); return; }
                    if (Me != null) Json.Obj(Me, "server")["state"] = Json.Str(r.Data, "state");
                    nextMe = DateTime.Now.AddSeconds(5);
                    if (!WantConnected) { WantConnected = true; SetState(VpnState.Connecting); }
                    Refresh();
                });
            });
        }

        bool WriteConfig()
        {
            var vpn = Json.Obj(Me, "vpn");
            serverIp = Json.Str(vpn, "server");
            if (serverIp == "" || Json.Str(vpn, "uuid") == "") return false;
            var proxy = new Dictionary<string, object> {
                { "type", "vless" }, { "tag", "proxy" }, { "server", serverIp }, { "server_port", (int)Json.Num(vpn, "port") },
                { "uuid", Json.Str(vpn, "uuid") }, { "flow", "xtls-rprx-vision" },
                { "tls", new Dictionary<string, object> {
                    { "enabled", true }, { "server_name", Json.Str(vpn, "sni") },
                    { "utls", new Dictionary<string, object> { { "enabled", true }, { "fingerprint", "chrome" } } },
                    { "reality", new Dictionary<string, object> { { "enabled", true }, { "public_key", Json.Str(vpn, "public_key") }, { "short_id", Json.Str(vpn, "short_id") } } } } } };
            var cfg = new Dictionary<string, object> {
                { "log", new Dictionary<string, object> { { "level", "warn" }, { "timestamp", true } } },
                { "dns", new Dictionary<string, object> {
                    { "servers", new object[] {
                        new Dictionary<string, object> { { "type", "https" }, { "tag", "dns-remote" }, { "server", "1.1.1.1" }, { "detour", "proxy" } },
                        new Dictionary<string, object> { { "type", "local" }, { "tag", "dns-local" } } } },
                    { "final", "dns-remote" }, { "strategy", "ipv4_only" } } },
                { "inbounds", new object[] { new Dictionary<string, object> {
                    { "type", "tun" }, { "tag", "tun-in" }, { "interface_name", "DormVPN" }, { "address", new[] { "172.19.0.1/30" } },
                    { "mtu", 1400 }, { "auto_route", true }, { "strict_route", true }, { "stack", "mixed" } } } },
                { "outbounds", new object[] { proxy, new Dictionary<string, object> { { "type", "direct" }, { "tag", "direct" } } } },
                { "route", new Dictionary<string, object> {
                    { "rules", new object[] {
                        new Dictionary<string, object> { { "action", "sniff" } },
                        new Dictionary<string, object> { { "protocol", "dns" }, { "action", "hijack-dns" } },
                        new Dictionary<string, object> { { "ip_is_private", true }, { "outbound", "direct" } } } },
                    { "final", "proxy" }, { "auto_detect_interface", true }, { "default_domain_resolver", "dns-local" } } } };
            var json = Json.Write(cfg);
            try { if (File.Exists(ConfigPath) && File.ReadAllText(ConfigPath) == json) return false; } catch { }
            File.WriteAllText(ConfigPath, json);
            return true;
        }

        // ---------------------------------------------------------------- connection control

        public void Toggle() { if (WantConnected) Disconnect(); else Connect(); }

        public void Connect()
        {
            var why = CanConnect();
            if (why != null) { form.ShowError(why); return; }
            if (!File.Exists(SingBox)) { form.ShowError("sing-box.exe is missing from " + dir + ". Reinstall DormVPN."); return; }
            if (!File.Exists(ConfigPath) && !WriteConfig()) { form.ShowError("Couldn't get your VPN settings yet. Check your internet and try again."); return; }
            WantConnected = true;
            restartCount = 0;
            SetState(VpnState.Connecting);
            if (ServerState == "stopped" && Json.Bool(Me, "allow_wake")) { Wake(); }
            StartCore();
        }

        public void Disconnect()
        {
            WantConnected = false;
            StopCore();
            SetState(VpnState.Disconnected);
        }

        void StartCore()
        {
            if (proc != null && !proc.HasExited) return;
            RotateLog();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = SingBox,
                    Arguments = "run -c \"" + ConfigPath + "\" -D \"" + dir.TrimEnd('\\') + "\"",
                    WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) Log(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Log(e.Data); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                lastStart = DateTime.Now; connectingSince = DateTime.Now;
                nextHealth = DateTime.Now.AddSeconds(3);
                failCount = 0;
                Log("[app] engine started (pid " + proc.Id + ")");
            }
            catch (Exception ex)
            {
                Log("[app] failed to start engine: " + ex.Message);
                form.ShowError("DormVPN couldn't start: " + ex.Message);
            }
        }

        void StopCore()
        {
            try { if (proc != null && !proc.HasExited) { proc.Kill(); proc.WaitForExit(4000); Log("[app] engine stopped"); } }
            catch (Exception ex) { Log("[app] stop error: " + ex.Message); }
            proc = null;
        }

        void RestartCore(string why)
        {
            Log("[app] restarting engine: " + why);
            StopCore();
            restartCount++;
            SetState(VpnState.Reconnecting);
            StartCore();
        }

        // ---------------------------------------------------------------- watchdog

        void Tick()
        {
            if (Token != "" && DateTime.Now >= nextMe) RefreshMe(false);
            if (WantConnected)
            {
                if (Blocked) { Disconnect(); form.ShowError(CanConnect()); }
                bool dead = proc == null || proc.HasExited;
                if (dead)
                {
                    int backoff = Math.Min(30, 1 << Math.Min(5, restartCount + 1));
                    if (State != VpnState.Reconnecting && State != VpnState.Connecting) { SetState(VpnState.Reconnecting); Log("[app] engine exited unexpectedly"); }
                    if ((DateTime.Now - lastStart).TotalSeconds >= backoff) { restartCount++; StartCore(); }
                }
                else if (!healthBusy && healthResult == 0 && DateTime.Now >= nextHealth)
                {
                    healthBusy = true;
                    ThreadPool.QueueUserWorkItem(delegate { HealthCheck(); });
                }

                int r = healthResult;
                if (r != 0)
                {
                    healthResult = 0;
                    if (r == 1)
                    {
                        failCount = 0; restartCount = 0; ExitInfo = healthDetail;
                        if (State != VpnState.Connected) { SetState(VpnState.Connected); if (!form.Visible) Balloon("DormVPN connected", "Exit: " + ExitInfo, ToolTipIcon.Info); }
                        nextHealth = DateTime.Now.AddSeconds(30);
                    }
                    else
                    {
                        failCount++;
                        Log("[app] health check failed (" + failCount + "): " + healthDetail);
                        if (State == VpnState.Connected)
                        {
                            if (failCount >= 2) SetState(VpnState.Reconnecting);
                            nextHealth = DateTime.Now.AddSeconds(5);
                            if (failCount >= 4) { RestartCore("server not reachable"); RefreshMe(false); }
                        }
                        else
                        {
                            nextHealth = DateTime.Now.AddSeconds(3);
                            // a server that's still starting takes ~1 min; don't thrash the engine meanwhile
                            if ((DateTime.Now - connectingSince).TotalSeconds > 45 && ServerState == "running") RestartCore("no connection after 45s");
                        }
                    }
                }
            }
            Refresh();
        }

        void HealthCheck()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://www.cloudflare.com/cdn-cgi/trace");
                req.Proxy = null; req.Timeout = 8000; req.ReadWriteTimeout = 8000; req.UserAgent = "DormVPN-health";
                string body;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream())) body = sr.ReadToEnd();
                var ip = Regex.Match(body, "(?m)^ip=(.+)$").Groups[1].Value.Trim();
                var loc = Regex.Match(body, "(?m)^loc=(.+)$").Groups[1].Value.Trim();
                if (ip == serverIp) { healthDetail = ip + " (" + loc + ")"; healthResult = 1; }
                else { healthDetail = "traffic not going through VPN (seen IP " + ip + ")"; healthResult = 2; }
            }
            catch (Exception ex) { healthDetail = ex.Message; healthResult = 2; }
            finally { healthBusy = false; }
        }

        void SetState(VpnState s)
        {
            if (State == s) return;
            State = s;
            Log("[app] state -> " + s);
            Refresh();
        }

        void Refresh()
        {
            tray.Icon = State == VpnState.Connected ? icoOn : State == VpnState.Disconnected ? icoOff : icoWait;
            string tip = "DormVPN: " + (Token == "" ? "signed out" : State == VpnState.Connected ? "connected" : WantConnected ? "connecting" : "not connected");
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            miToggle.Text = WantConnected ? "Disconnect" : "Connect";
            form.Render();
        }

        void Balloon(string title, string text, ToolTipIcon icon) { try { tray.ShowBalloonTip(3000, title, text, icon); } catch { } }

        // ---------------------------------------------------------------- settings, startup, log

        string ReadSetting(string key, string def)
        {
            try
            {
                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    var i = line.IndexOf('=');
                    if (i > 0 && line.Substring(0, i).Trim() == key) return line.Substring(i + 1).Trim();
                }
            }
            catch { }
            return def;
        }

        void WriteSetting(string key, string val) { try { File.WriteAllText(SettingsPath, key + "=" + val + Environment.NewLine); } catch { } }

        bool StartupTaskExists() { return RunHidden("schtasks.exe", "/Query /TN DormVPN") == 0; }

        void ToggleStartup()
        {
            if (StartupTaskExists()) RunHidden("schtasks.exe", "/Delete /TN DormVPN /F");
            else RunHidden("schtasks.exe", "/Create /TN DormVPN /TR \"\\\"" + Application.ExecutablePath + "\\\" /background\" /SC ONLOGON /RL HIGHEST /F");
        }

        static int RunHidden(string file, string args)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                return p.ExitCode;
            }
            catch { return -1; }
        }

        void RotateLog()
        {
            lock (logLock)
            {
                try
                {
                    var fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    {
                        var old = Path.Combine(dir, "dormvpn.old.log");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(LogPath, old);
                    }
                }
                catch { }
            }
        }

        public void Log(string line)
        {
            lock (logLock)
            {
                try { File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + Ansi.Replace(line, "") + Environment.NewLine); } catch { }
            }
        }

        public void Quit()
        {
            WantConnected = false;
            timer.Stop();
            StopCore();
            Log("[app] quit");
            tray.Visible = false;
            tray.Dispose();
            form.Dispose();
            ExitThread();
        }
    }

    /// Big round connect button.
    class PowerButton : Control
    {
        public Color Tone = Theme.Grey;
        public bool Busy;
        float pulse;
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 40 };
        bool hover;

        public PowerButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            anim.Tick += delegate { pulse = (pulse + 0.035f) % 1f; Invalidate(); };
        }
        public void SetTone(Color c, bool busy)
        {
            Tone = c; Busy = busy;
            if (busy && !anim.Enabled) anim.Start();
            if (!busy && anim.Enabled) { anim.Stop(); pulse = 0; }
            Invalidate();
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent.BackColor);
            float size = Math.Min(Width, Height) - 4;
            var c = new PointF(Width / 2f, Height / 2f);
            // soft halo (pulses while connecting)
            float halo = size / 2f * (Busy ? 0.86f + 0.14f * pulse : 1f);
            int alpha = Busy ? (int)(70 * (1 - pulse)) + 20 : 38;
            using (var b = new SolidBrush(Color.FromArgb(alpha, Tone))) g.FillEllipse(b, c.X - halo, c.Y - halo, halo * 2, halo * 2);
            // disc
            float r = size * 0.36f * (hover ? 1.03f : 1f);
            using (var b = new SolidBrush(Tone)) g.FillEllipse(b, c.X - r, c.Y - r, r * 2, r * 2);
            // power symbol
            float pr = r * 0.42f;
            using (var p = new Pen(Color.White, r * 0.11f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(p, c.X - pr, c.Y - pr, pr * 2, pr * 2, -60, 300);
                g.DrawLine(p, c.X, c.Y - pr * 1.25f, c.X, c.Y - pr * 0.2f);
            }
        }
    }

    class MainForm : Form
    {
        readonly ClientApp app;
        readonly Panel signIn = new Panel(), home = new Panel();

        // sign-in view
        InputBox inUser, inPass;
        FlatButton btnSignIn;
        Label lblSignInError, lblSignInNote;

        // home view
        Label lblUser, lblStatus, lblDetail, lblUsed, lblOf, lblUsageFoot, lblBanner;
        FlatButton btnSignOut, btnBanner;
        PowerButton power;
        Card usageCard, banner;
        UsageBar bar;
        string error;
        DateTime errorUntil = DateTime.MinValue;

        public MainForm(ClientApp app)
        {
            this.app = app;
            Text = "DormVPN";
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            ClientSize = new Size(380, 600);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            Font = Theme.F(9.5f);
            Icon = Theme.MakeIcon(Theme.Accent);

            foreach (var p in new[] { signIn, home }) { p.Dock = DockStyle.Fill; p.BackColor = Theme.Bg; Controls.Add(p); }
            BuildSignIn();
            BuildHome();
            if (app.Token == "") ShowSignIn(null); else { signIn.Visible = false; home.Visible = true; }
        }

        void BuildSignIn()
        {
            int x = 40, w = 300;
            var logo = new Panel { Bounds = new Rectangle(162, 64, 56, 56), BackColor = Theme.Bg };
            logo.Paint += (s, e) => Theme.DrawShield(e.Graphics, new RectangleF(0, 0, 56, 56), Theme.Accent);
            signIn.Controls.Add(logo);
            var title = Ui.Text("DormVPN", Theme.B(20), Theme.Ink);
            var sub = Ui.Text("Sign in with the account you were given.", Theme.F(10), Theme.Muted);
            signIn.Controls.Add(title); signIn.Controls.Add(sub);
            title.Location = new Point((380 - title.PreferredWidth) / 2, 128);
            sub.Location = new Point((380 - sub.PreferredWidth) / 2, 170);

            var lu = Ui.Text("Username", Theme.F(9.5f), Theme.Muted); lu.Location = new Point(x, 218);
            inUser = new InputBox(false) { Bounds = new Rectangle(x, 240, w, 38) };
            var lp = Ui.Text("Password", Theme.F(9.5f), Theme.Muted); lp.Location = new Point(x, 290);
            inPass = new InputBox(true) { Bounds = new Rectangle(x, 312, w, 38) };
            lblSignInError = Ui.Wrapped("", Theme.F(9.5f), Theme.Bad, w); lblSignInError.Location = new Point(x, 358);
            btnSignIn = new FlatButton("Sign in", ButtonKind.Primary) { Bounds = new Rectangle(x, 398, w, 40) };
            btnSignIn.Click += delegate { DoSignIn(); };
            inPass.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoSignIn(); } };
            inUser.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; inPass.Box.Focus(); } };
            lblSignInNote = Ui.Wrapped("No account? Ask the person who runs this VPN to make one for you.", Theme.F(9), Theme.Muted, w);
            lblSignInNote.Location = new Point(x, 540);
            signIn.Controls.AddRange(new Control[] { lu, inUser, lp, inPass, lblSignInError, btnSignIn, lblSignInNote });
        }

        void DoSignIn()
        {
            if (inUser.Text.Trim() == "" || inPass.Text == "") { lblSignInError.Text = "Enter your username and password."; return; }
            btnSignIn.Enabled = false; btnSignIn.Text = "Signing in…"; lblSignInError.Text = "";
            app.SignIn(inUser.Text, inPass.Text, err =>
            {
                btnSignIn.Enabled = true; btnSignIn.Text = "Sign in";
                if (err != null) { lblSignInError.Text = err; inPass.Text = ""; inPass.Box.Focus(); return; }
                inPass.Text = "";
                signIn.Visible = false; home.Visible = true;
                Render();
            });
        }

        public void ShowSignIn(string message)
        {
            home.Visible = false; signIn.Visible = true;
            lblSignInError.Text = message ?? "";
            inPass.Text = "";
            if (inUser.Text == "") inUser.Box.Focus(); else inPass.Box.Focus();
        }

        void BuildHome()
        {
            lblUser = Ui.Text("", Theme.F(9.5f), Theme.Muted); lblUser.Location = new Point(24, 20);
            btnSignOut = new FlatButton("Sign out", ButtonKind.Link) { Bounds = new Rectangle(296, 14, 64, 28) };
            btnSignOut.Click += delegate
            {
                if (MessageBox.Show(this, "Sign out of DormVPN? You'll be disconnected.", "DormVPN", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                    app.SignOut(null);
            };
            power = new PowerButton { Bounds = new Rectangle(100, 58, 180, 180) };
            power.Click += delegate { app.Toggle(); };
            lblStatus = new Label { Font = Theme.B(17), ForeColor = Theme.Ink, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(20, 244, 340, 36), BackColor = Color.Transparent };
            lblDetail = new Label { Font = Theme.F(9.5f), ForeColor = Theme.Muted, TextAlign = ContentAlignment.TopCenter, Bounds = new Rectangle(20, 282, 340, 40), BackColor = Color.Transparent };

            banner = new Card { Bounds = new Rectangle(20, 326, 340, 64), Visible = false };
            lblBanner = Ui.Wrapped("", Theme.F(9.5f), Theme.Ink, 210); lblBanner.Location = new Point(14, 12);
            btnBanner = new FlatButton("Start server", ButtonKind.Primary) { Bounds = new Rectangle(232, 14, 94, 34), Font = Theme.B(9) };
            btnBanner.Click += delegate { app.Wake(); };
            banner.Controls.Add(lblBanner); banner.Controls.Add(btnBanner);

            usageCard = new Card { Bounds = new Rectangle(20, 400, 340, 150) };
            var lt = Ui.Text("Data used this month", Theme.F(9.5f), Theme.Muted); lt.Location = new Point(18, 16);
            lblUsed = Ui.Text("–", Theme.B(22), Theme.Ink); lblUsed.Location = new Point(14, 40);
            lblOf = Ui.Text("", Theme.F(11), Theme.Muted);
            bar = new UsageBar { Bounds = new Rectangle(18, 94, 304, 8) };
            lblUsageFoot = Ui.Text("", Theme.F(9), Theme.Muted); lblUsageFoot.Location = new Point(18, 112);
            usageCard.Controls.AddRange(new Control[] { lt, lblUsed, lblOf, bar, lblUsageFoot });

            home.Controls.AddRange(new Control[] { lblUser, btnSignOut, power, lblStatus, lblDetail, banner, usageCard });
        }

        public void ShowError(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return;
            error = msg; errorUntil = DateTime.Now.AddSeconds(8);
            Render();
            if (!Visible) app.ShowWindow();
        }

        public void Render()
        {
            if (IsDisposed || app.Token == "") return;
            if (signIn.Visible) { signIn.Visible = false; home.Visible = true; }
            lblUser.Text = "Signed in as " + app.UserName;

            string server = app.ServerState;
            Color tone; string status, detail; bool busy = false;
            switch (app.State)
            {
                case VpnState.Connected: tone = Theme.Accent; status = "Connected"; detail = "Your traffic goes through DormVPN.\n" + app.ExitInfo; break;
                case VpnState.Connecting:
                case VpnState.Reconnecting:
                    tone = Color.FromArgb(240, 160, 32); busy = true;
                    status = app.State == VpnState.Connecting ? "Connecting…" : "Reconnecting…";
                    detail = server == "pending" ? "The server is starting up. This takes about a minute." :
                             server == "stopped" ? "The server is paused." : "Your traffic is held until the connection is back.";
                    break;
                default: tone = Theme.Grey; status = "Not connected"; detail = "Click the button to connect."; break;
            }
            if (app.Offline && app.State != VpnState.Connected) detail = "Can't reach DormVPN right now. Check your internet.";
            if (error != null && DateTime.Now < errorUntil) detail = error; else error = null;
            power.SetTone(tone, busy);
            lblStatus.Text = status;
            lblDetail.ForeColor = error != null ? Theme.Bad : Theme.Muted;
            lblDetail.Text = detail;

            // banner: paused server / used up / starting
            string bannerText = null; bool bannerButton = false; Color bannerFill = Color.FromArgb(255, 247, 230), bannerBorder = Color.FromArgb(242, 220, 170);
            if (app.Blocked) { bannerText = "You've used all your data for this month. It comes back on " + app.ResetText() + "."; bannerFill = Color.FromArgb(253, 238, 237); bannerBorder = Color.FromArgb(240, 200, 196); }
            else if (server == "stopped") { bannerText = Json.Bool(app.Me, "allow_wake") ? "The server is paused to save money." : "The server is paused. Ask the admin to start it."; bannerButton = Json.Bool(app.Me, "allow_wake"); }
            else if (server == "pending") bannerText = "The server is starting. You'll be connected in about a minute.";
            else if (server == "stopping") bannerText = "The server is being paused.";
            banner.Visible = bannerText != null;
            if (bannerText != null)
            {
                banner.Fill = bannerFill; banner.Border = bannerBorder;
                lblBanner.MaximumSize = new Size(bannerButton ? 210 : 312, 0);
                lblBanner.Text = bannerText;
                btnBanner.Visible = bannerButton;
                lblBanner.Top = Math.Max(8, (banner.Height - lblBanner.PreferredHeight) / 2);
                banner.Invalidate();
            }

            // usage
            if (app.Me != null)
            {
                double used = Json.Num(app.Me, "used"), limit = Json.Num(app.Me, "limit_gb");
                lblUsed.Text = Ui.Gb(used) + " GB";
                lblOf.Text = limit > 0 ? "of " + limit.ToString("0.##") + " GB" : "no limit";
                lblOf.Location = new Point(lblUsed.Right + 2, lblUsed.Top + 15);
                bar.Visible = limit > 0;
                bar.Value = limit > 0 ? used / (limit * 1e9) : 0;
                string left = limit > 0 ? Math.Max(0, limit - used / 1e9).ToString("0.00") + " GB left  ·  " : "";
                lblUsageFoot.Text = left + "resets " + app.ResetText();
                lblUsageFoot.Top = limit > 0 ? 112 : 96;
            }
            else { lblUsed.Text = "–"; lblOf.Text = ""; lblUsageFoot.Text = app.Offline ? "Can't load your usage right now." : "Loading…"; bar.Visible = false; }
        }
    }
}
