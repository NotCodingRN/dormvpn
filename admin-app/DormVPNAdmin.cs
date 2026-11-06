// DormVPN Admin - sets up the VPN in your AWS account and gives you the admin panel link.
// First run: a wizard (AWS CLI -> AWS keys -> sing-box engine -> create). After that: a dashboard.
// The AWS work itself is done by the PowerShell scripts in .\scripts (deploy.ps1, destroy.ps1, ...).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using DormVPN.Shared;

[assembly: System.Reflection.AssemblyTitle("DormVPN Admin")]
[assembly: System.Reflection.AssemblyProduct("DormVPN")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace DormVPN.Admin
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (new Mutex(true, "DormVPN-Admin-single-instance", out created))
            {
                if (!created) { MessageBox.Show("DormVPN Admin is already open.", "DormVPN Admin"); return; }
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new AdminForm());
            }
        }
    }

    static class Paths
    {
        public static readonly string App = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        // everything stays inside the cloned folder: <folder>pp (this exe), <folder>\data, <folder>\installers
        public static readonly string Root = Path.GetDirectoryName(App);
        public static readonly string Data = Path.Combine(Root, "data");
        public static string Installers { get { return Path.Combine(Root, "installers"); } }
        public static string Scripts { get { return Path.Combine(App, "scripts"); } }
        public static string Client { get { return Path.Combine(App, "client"); } }
        public static string Engine { get { return Path.Combine(Data, "engine"); } }
        public static string File(string name) { return Path.Combine(Data, name); }
    }

    class CmdResult { public int Code; public string Out = "", Err = ""; }

    static class Aws
    {
        static string exe;
        public static string Exe
        {
            get
            {
                if (exe != null && System.IO.File.Exists(exe)) return exe;
                var candidates = new List<string> { @"C:\Program Files\Amazon\AWSCLIV2\aws.exe" };
                foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                    if (dir.Trim() != "") candidates.Add(Path.Combine(dir.Trim(), "aws.exe"));
                exe = candidates.FirstOrDefault(System.IO.File.Exists);
                return exe;
            }
        }

        public static CmdResult Run(string args, int timeoutMs = 60000)
        {
            if (Exe == null) return new CmdResult { Code = -1, Err = "AWS CLI not found" };
            return Proc.Run(Exe, args, timeoutMs);
        }

        public static string Q(string s) { return "\"" + (s ?? "").Replace("\"", "\\\"") + "\""; }
    }

    static class Proc
    {
        public static CmdResult Run(string file, string args, int timeoutMs)
        {
            var r = new CmdResult();
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    r.Out = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } r.Code = -2; r.Err = "timed out"; return r; }
                    r.Err = err.Result;
                    r.Code = p.ExitCode;
                }
            }
            catch (Exception ex) { r.Code = -1; r.Err = ex.Message; }
            return r;
        }

        /// Runs one of the setup scripts and streams its output line by line.
        public static Process Script(string name, string args, Action<string> line, Action<int> done)
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(Paths.Scripts, name) + "\" " + args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Paths.Scripts
            };
            psi.EnvironmentVariables["DORMVPN_DATA"] = Paths.Data;
            if (Aws.Exe != null) psi.EnvironmentVariables["PATH"] = Path.GetDirectoryName(Aws.Exe) + ";" + psi.EnvironmentVariables["PATH"];
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) line(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null && e.Data.Trim() != "" && !e.Data.Contains("RemoteException")) line(e.Data); };
            p.Exited += delegate { p.WaitForExit(); done(p.ExitCode); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }
    }

    /// secrets.json / state.json / admin.json in %APPDATA%\DormVPN Admin
    static class Store
    {
        public static Dictionary<string, object> Read(string name)
        {
            try { return Json.Parse(System.IO.File.ReadAllText(Paths.File(name))); } catch { return new Dictionary<string, object>(); }
        }
        public static void Write(string name, Dictionary<string, object> d)
        {
            Directory.CreateDirectory(Paths.Data);
            System.IO.File.WriteAllText(Paths.File(name), Json.Write(d), new UTF8Encoding(false));
        }
        public static bool Deployed { get { return Json.Str(Read("state.json"), "api_url") != "" && System.IO.File.Exists(Paths.File("admin.json")); } }
        public static string PanelUrl { get { return Json.Str(Read("state.json"), "api_url"); } }
        public static string AdminUser { get { return Json.Str(Read("admin.json"), "username"); } }
        public static string AdminPassword { get { return Secret.FromPowerShell(Json.Str(Read("admin.json"), "password")) ?? "(can't read: make a new password)"; } }
        public static string Profile { get { return Json.Str(Read("secrets.json"), "aws_profile"); } }
        public static string Region { get { return Json.Str(Read("secrets.json"), "aws_region"); } }
        public static string AwsArgs { get { return " --profile " + Aws.Q(Profile) + " --region " + Aws.Q(Region); } }
    }

    static class Regions
    {
        public static readonly string[][] All = {
            new[] { "eu-west-3", "Europe (Paris)" }, new[] { "eu-west-1", "Europe (Ireland)" }, new[] { "eu-west-2", "Europe (London)" },
            new[] { "eu-central-1", "Europe (Frankfurt)" }, new[] { "eu-north-1", "Europe (Stockholm)" }, new[] { "eu-south-1", "Europe (Milan)" },
            new[] { "me-central-1", "Middle East (UAE)" }, new[] { "me-south-1", "Middle East (Bahrain)" }, new[] { "il-central-1", "Israel (Tel Aviv)" },
            new[] { "ap-south-1", "Asia Pacific (Mumbai)" }, new[] { "ap-southeast-1", "Asia Pacific (Singapore)" }, new[] { "ap-northeast-1", "Asia Pacific (Tokyo)" },
            new[] { "us-east-1", "US East (N. Virginia)" }, new[] { "us-west-2", "US West (Oregon)" }, new[] { "ca-central-1", "Canada (Central)" },
            new[] { "sa-east-1", "South America (Sao Paulo)" }, new[] { "af-south-1", "Africa (Cape Town)" } };
        public static string Label(string code) { var r = All.FirstOrDefault(x => x[0] == code); return r == null ? code : r[1] + "  ·  " + code; }
    }

    class AdminForm : Form
    {
        const int W = 820, H = 620, Side = 220;
        readonly Panel sidebar = new Panel(), content = new Panel();
        readonly string[] steps = { "Welcome", "AWS CLI", "AWS account", "VPN engine", "Create", "Done" };
        int step;
        bool wizard;
        Process running;

        public AdminForm()
        {
            Text = "DormVPN Admin";
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            ClientSize = new Size(W, H);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            Font = Theme.F(9.5f);
            Icon = Theme.MakeIcon(Theme.Accent);
            sidebar.SetBounds(0, 0, Side, H); sidebar.BackColor = Theme.Card;
            sidebar.Paint += (s, e) => { using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, Side - 1, 0, Side - 1, H); };
            content.SetBounds(Side, 0, W - Side, H); content.BackColor = Theme.Bg;
            Controls.Add(content); Controls.Add(sidebar);
            FormClosing += (s, e) =>
            {
                if (running != null && !running.HasExited &&
                    MessageBox.Show(this, "Setup is still running. Closing now can leave things half-made (running setup again fixes it). Close anyway?",
                        "DormVPN Admin", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) e.Cancel = true;
            };
            if (Store.Deployed) ShowDashboard(); else StartWizard();
        }

        // ================================================================ layout helpers

        int y;
        Panel page;

        Panel NewPage(string title, string subtitle)
        {
            content.SuspendLayout();
            foreach (Control c in content.Controls.Cast<Control>().ToList()) c.Dispose();
            content.Controls.Clear();
            page = new Panel { Bounds = new Rectangle(0, 0, W - Side, H), BackColor = Theme.Bg, AutoScroll = true };
            content.Controls.Add(page);
            y = 34;
            Add(Ui.Text(title, Theme.B(17), Theme.Ink), 36);
            y += 6;
            if (subtitle != null) Para(subtitle, Theme.Muted);
            y += 8;
            content.ResumeLayout();
            return page;
        }

        T Add<T>(T c, int x) where T : Control
        {
            c.Location = new Point(x, y);
            page.Controls.Add(c);
            y = c.Bottom + 8;
            return c;
        }

        Label Para(string text, Color color, float size = 10f, int x = 36, int width = 0)
        {
            var l = Ui.Wrapped(text, Theme.F(size), color, width > 0 ? width : W - Side - x - 36);
            return Add(l, x);
        }

        Label Step(int n, string text)
        {
            var num = new Label { Text = n.ToString(), Font = Theme.B(9), ForeColor = Color.White, BackColor = Theme.Accent, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(22, 22), Location = new Point(36, y) };
            page.Controls.Add(num);
            var l = Ui.Wrapped(text, Theme.F(10), Theme.Ink, W - Side - 72 - 36);
            l.Location = new Point(68, y + 1);
            page.Controls.Add(l);
            y = Math.Max(num.Bottom, l.Bottom) + 10;
            return l;
        }

        FlatButton Button(string text, ButtonKind kind, int x, int width, EventHandler click)
        {
            var b = new FlatButton(text, kind) { Bounds = new Rectangle(x, y, width, 36) };
            b.Click += click;
            page.Controls.Add(b);
            return b;
        }

        FlatButton footerNext, footerBack;

        void Footer(string next, EventHandler onNext, bool back = true)
        {
            int fy = H - 64;
            var line = new Panel { Bounds = new Rectangle(0, fy - 16, W - Side, 1), BackColor = Theme.Line };
            content.Controls.Add(line); line.BringToFront();
            footerNext = new FlatButton(next, ButtonKind.Primary) { Bounds = new Rectangle(W - Side - 36 - 150, fy, 150, 38) };
            footerNext.Click += onNext;
            content.Controls.Add(footerNext); footerNext.BringToFront();
            if (back && step > 0)
            {
                footerBack = new FlatButton("Back", ButtonKind.Secondary) { Bounds = new Rectangle(W - Side - 36 - 150 - 100, fy, 90, 38) };
                footerBack.Click += delegate { ShowStep(step - 1); };
                content.Controls.Add(footerBack); footerBack.BringToFront();
            }
            // a status line that's always visible, left of the buttons
            footerStatus = Ui.Wrapped("", Theme.F(9.5f), Theme.Muted, W - Side - 36 - 150 - 110 - 36);
            footerStatus.Location = new Point(36, fy - 6);
            content.Controls.Add(footerStatus); footerStatus.BringToFront();
            page.Height = fy - 18;
        }

        Label footerStatus;

        void OnUi(Action a) { if (IsDisposed) return; if (InvokeRequired) BeginInvoke(a); else a(); }

        // ================================================================ wizard

        void StartWizard()
        {
            wizard = true;
            ShowStep(0);
        }

        void DrawSidebar()
        {
            sidebar.Controls.Clear();
            var logo = new Panel { Bounds = new Rectangle(24, 28, 30, 30), BackColor = Theme.Card };
            logo.Paint += (s, e) => Theme.DrawShield(e.Graphics, new RectangleF(0, 0, 30, 30), Theme.Accent);
            sidebar.Controls.Add(logo);
            var t = Ui.Text("DormVPN Admin", Theme.B(12), Theme.Ink); t.Location = new Point(60, 31); sidebar.Controls.Add(t);
            if (!wizard) return;
            var sub = Ui.Text("Setup", Theme.F(9), Theme.Muted); sub.Location = new Point(26, 84); sidebar.Controls.Add(sub);
            for (int i = 0; i < steps.Length; i++)
            {
                int idx = i;
                var row = new Panel { Bounds = new Rectangle(14, 108 + i * 40, Side - 28, 34), BackColor = i == step ? Theme.Soft : Theme.Card };
                row.Paint += (s, e) =>
                {
                    var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    var c = idx < step ? Theme.Accent : idx == step ? Theme.Ink : Theme.Grey;
                    using (var b = new SolidBrush(idx <= step ? c : Theme.Card)) g.FillEllipse(b, 10, 8, 18, 18);
                    using (var p = new Pen(c, 1.4f)) g.DrawEllipse(p, 10, 8, 18, 18);
                    var mark = idx < step ? "✓" : (idx + 1).ToString();
                    TextRenderer.DrawText(g, mark, Theme.B(8), new Rectangle(10, 8, 19, 18), idx <= step ? Color.White : Theme.Grey, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    TextRenderer.DrawText(g, steps[idx], idx == step ? Theme.B(10) : Theme.F(10), new Point(38, 7), idx > step ? Theme.Muted : Theme.Ink);
                };
                sidebar.Controls.Add(row);
            }
        }

        void ShowStep(int i)
        {
            step = i;
            DrawSidebar();
            switch (i)
            {
                case 0: PageWelcome(); break;
                case 1: PageCli(); break;
                case 2: PageAccount(); break;
                case 3: PageEngine(); break;
                case 4: PageCreate(); break;
                default: PageDone(); break;
            }
        }

        void PageWelcome()
        {
            NewPage("Set up your own VPN", "This sets up DormVPN in your Amazon Web Services (AWS) account, in about 10 minutes.");
            Para("What you'll get:", Theme.Ink, 10.5f);
            Step(1, "A private VPN server on AWS. Its traffic looks like normal HTTPS, so it works on strict networks.");
            Step(2, "A web admin panel (with its own admin login) to add users, set how much data each one may use per month, and pause the server.");
            Step(3, "An installer you send to your users. They sign in with the username and password you give them.");
            y += 6;
            var card = new Card { Bounds = new Rectangle(36, y, W - Side - 72, 112) };
            var ct = Ui.Text("What it costs (AWS prices)", Theme.B(10), Theme.Ink); ct.Location = new Point(16, 14); card.Controls.Add(ct);
            var cb = Ui.Wrapped("About $13/month if the server runs all the time, about $4.40/month while it's paused. " +
                "The first 100 GB of data per month are free, then about $0.09 per GB. The admin panel and user database cost cents. " +
                "Per-user limits and a monthly cap keep data costs in check.", Theme.F(9.5f), Theme.Muted, W - Side - 72 - 32);
            cb.Location = new Point(16, 40); card.Controls.Add(cb);
            page.Controls.Add(card);
            Footer("Get started", delegate { ShowStep(1); });
        }

        // ---------------------------------------------------------------- AWS CLI

        void PageCli()
        {
            NewPage("Install the AWS command line", "DormVPN Admin talks to AWS through Amazon's own command-line tool. You install it once.");
            var status = Para("Checking…", Theme.Muted, 10.5f);
            y += 4;
            Step(1, "Download the AWS CLI installer for Windows and run it (all the default options are fine).");
            Button("Download AWS CLI", ButtonKind.Secondary, 68, 170, delegate { Ui.Open("https://awscli.amazonaws.com/AWSCLIV2.msi"); });
            y += 46;
            Step(2, "Come back here and click Check again.");
            var again = Button("Check again", ButtonKind.Secondary, 68, 130, null);
            Footer("Next", delegate { ShowStep(2); });
            Action check = null;
            check = delegate
            {
                status.Text = "Checking…"; status.ForeColor = Theme.Muted; footerNext.Enabled = false;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    var r = Aws.Run("--version", 20000);
                    OnUi(delegate
                    {
                        bool ok = r.Code == 0;
                        status.Text = ok ? "✓  Found " + (r.Out + r.Err).Split(' ')[0] : "✗  The AWS CLI isn't installed yet.";
                        status.ForeColor = ok ? Theme.Accent : Theme.Bad;
                        footerNext.Enabled = ok;
                    });
                });
            };
            again.Click += delegate { check(); };
            check();
        }

        // ---------------------------------------------------------------- AWS account

        void PageAccount()
        {
            NewPage("Connect your AWS account", null);
            Para("Create an access key so DormVPN Admin can set things up for you:", Theme.Ink);
            Step(1, "Sign in to the AWS console and open IAM → Users → Create user. Name it e.g. dormvpn-admin.");
            Step(2, "Under permissions choose \"Attach policies directly\" and tick AdministratorAccess.");
            Step(3, "Open the new user → Security credentials → Create access key → Command Line Interface (CLI). Copy both keys below.");
            Button("Open AWS IAM", ButtonKind.Secondary, 68, 140, delegate { Ui.Open("https://console.aws.amazon.com/iam/home#/users"); });
            y += 50;

            var profiles = Aws.Run("configure list-profiles", 20000).Out.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
            var useExisting = new RadioButton { Text = "Use a profile already on this PC", Font = Theme.F(10), AutoSize = true, Enabled = profiles.Count > 0 };
            var addNew = new RadioButton { Text = "Add new keys", Font = Theme.F(10), AutoSize = true };
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.F(10), Width = 220 };
            combo.Items.AddRange(profiles.Cast<object>().ToArray());
            Add(useExisting, 36); combo.Location = new Point(300, useExisting.Top - 3); page.Controls.Add(combo);
            Add(addNew, 36);
            var lk = Ui.Text("Access key ID", Theme.F(9), Theme.Muted); Add(lk, 56);
            var keyId = Add(new InputBox(false) { Width = 230 }, 56);
            var ls = Ui.Text("Secret access key", Theme.F(9), Theme.Muted); ls.Location = new Point(300, lk.Top); page.Controls.Add(ls);
            var secret = new InputBox(true) { Width = 248, Location = new Point(300, keyId.Top) }; page.Controls.Add(secret);
            y += 4;

            var lr = Ui.Text("Region (where the server lives: pick one close to you)", Theme.F(9), Theme.Muted); Add(lr, 36);
            var region = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.F(10), Width = 300, Location = new Point(36, y) };
            foreach (var r in Regions.All) region.Items.Add(Regions.Label(r[0]));
            page.Controls.Add(region);
            var fastest = new FlatButton("Find the fastest", ButtonKind.Secondary) { Bounds = new Rectangle(346, y - 2, 140, 32) };
            page.Controls.Add(fastest);
            y += 40;
            Label result = null;  // the footer status line, created by Footer()

            // defaults
            string curProfile = Store.Profile, curRegion = Store.Region;
            if (curProfile != "" && profiles.Contains(curProfile)) { useExisting.Checked = true; combo.SelectedItem = curProfile; }
            else if (profiles.Count > 0) { useExisting.Checked = true; combo.SelectedIndex = 0; }
            else addNew.Checked = true;
            int ri = Array.FindIndex(Regions.All, r => r[0] == (curRegion != "" ? curRegion : "eu-west-3"));
            region.SelectedIndex = Math.Max(0, ri);
            bool deployed = Json.Str(Store.Read("state.json"), "iid") != "";
            if (deployed) { region.Enabled = false; fastest.Enabled = false; lr.Text = "Region (your server is already in this one)"; }

            Action sync = delegate { combo.Enabled = useExisting.Checked; keyId.Enabled = secret.Enabled = addNew.Checked; };
            useExisting.CheckedChanged += delegate { sync(); };
            addNew.CheckedChanged += delegate { sync(); };
            sync();

            fastest.Click += delegate
            {
                fastest.Enabled = false; fastest.Text = "Measuring…";
                ThreadPool.QueueUserWorkItem(delegate
                {
                    var times = new Dictionary<string, long>();
                    var threads = Regions.All.Select(r => new Thread(() => { var t = Ping("ec2." + r[0] + ".amazonaws.com"); lock (times) times[r[0]] = t; })).ToList();
                    threads.ForEach(t => t.Start()); threads.ForEach(t => t.Join());
                    var best = times.Where(kv => kv.Value > 0).OrderBy(kv => kv.Value).FirstOrDefault();
                    OnUi(delegate
                    {
                        fastest.Enabled = true; fastest.Text = "Find the fastest";
                        if (best.Key == null) { result.Text = "Couldn't measure (no internet?)."; return; }
                        region.SelectedIndex = Array.FindIndex(Regions.All, r => r[0] == best.Key);
                        result.ForeColor = Theme.Muted;
                        result.Text = "Fastest from here: " + Regions.Label(best.Key) + " (" + best.Value + " ms). Some regions must be turned on in your AWS account first; the check below tells you.";
                    });
                });
            };

            Footer("Check and continue", null);
            result = footerStatus;
            footerNext.Click += delegate
            {
                string profile = useExisting.Checked ? (combo.SelectedItem as string ?? "") : "dormvpn";
                string reg = Regions.All[Math.Max(0, region.SelectedIndex)][0];
                if (addNew.Checked && (keyId.Text.Trim().Length < 16 || secret.Text.Trim().Length < 30)) { result.ForeColor = Theme.Bad; result.Text = "Paste both keys (the access key ID starts with AKIA)."; return; }
                if (profile == "") { result.ForeColor = Theme.Bad; result.Text = "Pick a profile."; return; }
                footerNext.Enabled = false; result.ForeColor = Theme.Muted; result.Text = "Checking your AWS access…";
                string id = keyId.Text.Trim(), sec = secret.Text.Trim();
                bool add = addNew.Checked;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string msg = null;
                    if (add)
                    {
                        // stored by the AWS CLI in %USERPROFILE%\.aws\credentials, like `aws configure` does
                        var a = Aws.Run("configure set aws_access_key_id " + Aws.Q(id) + " --profile " + Aws.Q(profile));
                        var b = Aws.Run("configure set aws_secret_access_key " + Aws.Q(sec) + " --profile " + Aws.Q(profile));
                        if (a.Code != 0 || b.Code != 0) msg = "Couldn't save the keys: " + a.Err + b.Err;
                    }
                    string who = "";
                    if (msg == null)
                    {
                        var r = Aws.Run("sts get-caller-identity --output json --profile " + Aws.Q(profile), 30000);
                        if (r.Code != 0) msg = "AWS didn't accept these keys. " + FirstLine(r.Err);
                        else who = Json.Str(Json.Parse(r.Out), "Arn");
                    }
                    if (msg == null)
                    {
                        var r = Aws.Run("ec2 describe-availability-zones --output text --query AvailabilityZones[0].ZoneName --profile " + Aws.Q(profile) + " --region " + reg, 30000);
                        if (r.Code != 0) msg = "Your account can't use " + Regions.Label(reg) + " (it may need to be turned on under Account → AWS Regions). Pick another region.";
                    }
                    OnUi(delegate
                    {
                        footerNext.Enabled = true;
                        if (msg != null) { result.ForeColor = Theme.Bad; result.Text = msg; return; }
                        var s = Store.Read("secrets.json");
                        s["aws_profile"] = profile; s["aws_region"] = reg;
                        Store.Write("secrets.json", s);
                        result.ForeColor = Theme.Accent; result.Text = "✓  Connected as " + who;
                        ShowStep(3);
                    });
                });
            };
        }

        static long Ping(string host)
        {
            long best = -1;
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    using (var c = new TcpClient())
                    {
                        var ar = c.BeginConnect(host, 443, null, null);
                        if (!ar.AsyncWaitHandle.WaitOne(2500)) continue;
                        c.EndConnect(ar);
                    }
                    long ms = sw.ElapsedMilliseconds;
                    if (best < 0 || ms < best) best = ms;
                }
                catch { }
            }
            return best;
        }

        static string FirstLine(string s) { s = (s ?? "").Trim(); int i = s.IndexOf('\n'); return i > 0 ? s.Substring(0, i) : s; }

        // ---------------------------------------------------------------- engine

        void PageEngine()
        {
            Directory.CreateDirectory(Paths.Engine);
            NewPage("Add the VPN engine", "DormVPN runs on sing-box, an open-source VPN engine. Its files go into the installer you send your users.");
            Step(1, "Open the sing-box 1.14.4 download page and download sing-box-1.14.4-windows-amd64.zip.");
            Button("Open download page", ButtonKind.Secondary, 68, 180, delegate { Ui.Open("https://github.com/SagerNet/sing-box/releases/tag/v1.14.4"); });
            y += 50;
            Step(2, "Open the zip and copy these three files: sing-box.exe, libcronet.dll and LICENSE.");
            Step(3, "Paste them into this folder:");
            var box = Add(new TextBox { Text = Paths.Engine, ReadOnly = true, Font = Theme.Mono(9.5f), Width = W - Side - 68 - 36 - 120, BackColor = Theme.Card }, 68);
            var open = new FlatButton("Open folder", ButtonKind.Secondary) { Bounds = new Rectangle(box.Right + 10, box.Top - 6, 110, 34) };
            open.Click += delegate { Ui.Open(Paths.Engine); };
            page.Controls.Add(open);
            y += 10;
            Step(4, "Click Check.");
            var check = Button("Check", ButtonKind.Secondary, 68, 110, null);
            y += 46;
            var status = Para("", Theme.Muted, 10.5f);
            Footer("Next", delegate { ShowStep(4); });
            Action run = delegate
            {
                var exe = Path.Combine(Paths.Engine, "sing-box.exe");
                string msg = null;
                if (!File.Exists(exe)) msg = "sing-box.exe isn't in the folder yet.";
                else if (!File.Exists(Path.Combine(Paths.Engine, "libcronet.dll"))) msg = "libcronet.dll is missing (it's in the same zip).";
                else
                {
                    var v = Proc.Run(exe, "version", 15000);
                    if (v.Code != 0) msg = "sing-box.exe didn't run: " + FirstLine(v.Err);
                    else if (!v.Out.Contains("1.14.")) msg = "That's " + FirstLine(v.Out) + ". DormVPN needs version 1.14.x.";
                    else { status.ForeColor = Theme.Accent; status.Text = "✓  " + FirstLine(v.Out) + " is ready."; footerNext.Enabled = true; return; }
                }
                status.ForeColor = Theme.Bad; status.Text = "✗  " + msg; footerNext.Enabled = false;
            };
            check.Click += delegate { run(); };
            run();
        }

        // ---------------------------------------------------------------- create

        TextBox log;

        void PageCreate()
        {
            bool update = Json.Str(Store.Read("state.json"), "iid") != "";
            NewPage(update ? "Update your VPN" : "Create your VPN",
                "DormVPN Admin now creates the server, the admin panel and the user database in " + Regions.Label(Store.Region) +
                " (AWS profile " + Store.Profile + "). This takes 5–10 minutes. You can watch below.");
            // spending limit: a monthly AWS budget that emails you and stops the server at 100%
            var secrets = Store.Read("secrets.json");
            var lu = Ui.Text("Monthly spending limit (US$)", Theme.F(9), Theme.Muted); Add(lu, 36);
            var usd = Add(new InputBox(false) { Width = 170 }, 36);
            usd.Text = Json.Num(secrets, "budget_usd") > 0 ? Json.Num(secrets, "budget_usd").ToString() : "15";
            var le = Ui.Text("Email for cost alerts", Theme.F(9), Theme.Muted); le.Location = new Point(222, lu.Top); page.Controls.Add(le);
            var mail = new InputBox(false) { Width = W - Side - 222 - 36, Location = new Point(222, usd.Top) }; mail.Text = Json.Str(secrets, "budget_email"); page.Controls.Add(mail);
            Para("At the limit the server stops itself. Normal running costs about $13 a month, plus data over 100 GB. Credits don't count towards it, so it works while you're still on credits.", Theme.Muted, 9f);
            log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono(9), BackColor = Theme.Card, ForeColor = Theme.Ink,
                                Bounds = new Rectangle(36, y + 4, W - Side - 72, H - y - 104), BorderStyle = BorderStyle.FixedSingle };
            page.Controls.Add(log);
            Footer(update ? "Update" : "Create my VPN", null);
            footerNext.Click += delegate
            {
                double limit;
                if (!double.TryParse(usd.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out limit) || limit < 5)
                { footerStatus.ForeColor = Theme.Bad; footerStatus.Text = "Set a limit of at least $5."; return; }
                if (!mail.Text.Contains("@") || mail.Text.Trim().Length < 5)
                { footerStatus.ForeColor = Theme.Bad; footerStatus.Text = "Enter an email for cost alerts."; return; }
                footerStatus.Text = "";
                var saved = Store.Read("secrets.json");
                saved["budget_usd"] = limit; saved["budget_email"] = mail.Text.Trim();
                Store.Write("secrets.json", saved);
                footerNext.Enabled = false; if (footerBack != null) footerBack.Enabled = false;
                footerNext.Text = "Working…";
                RunScript("deploy.ps1", "", ok =>
                {
                    footerNext.Enabled = true; if (footerBack != null) footerBack.Enabled = true;
                    if (ok && Store.Deployed) ShowStep(5);
                    else { footerNext.Text = "Try again"; AppendLog("\r\nSomething went wrong (see above). Fix it and click Try again; it picks up where it stopped."); }
                });
            };
        }

        void AppendLog(string line)
        {
            OnUi(delegate { if (log != null && !log.IsDisposed) { log.AppendText(line + "\r\n"); } });
        }

        void RunScript(string name, string args, Action<bool> done)
        {
            bool sawDone = false;
            running = Proc.Script(name, args, line =>
            {
                if (line.StartsWith("DORMVPN_")) sawDone = true;
                AppendLog(line);
            }, code => OnUi(delegate { running = null; done(code == 0 && sawDone); }));
        }

        // ---------------------------------------------------------------- done

        void PageDone()
        {
            NewPage("Your VPN is ready", "Manage users in the admin panel: add people, set their monthly limits, pause the server.");
            PanelCard();
            AppCard();
            Footer("Finish", delegate { wizard = false; ShowDashboard(); }, false);
        }

        void PanelCard()
        {
            var card = new Card { Bounds = new Rectangle(36, y, W - Side - 72, 176) };
            page.Controls.Add(card);
            var t = Ui.Text("Admin panel", Theme.B(11), Theme.Ink); t.Location = new Point(18, 14); card.Controls.Add(t);
            string url = Store.PanelUrl, user = Store.AdminUser, pw = Store.AdminPassword;
            int ly = 46;
            Func<string, string, bool, int, Control> row = (label, value, secretValue, rowY) =>
            {
                var l = Ui.Text(label, Theme.F(9.5f), Theme.Muted); l.Location = new Point(18, rowY + 4); card.Controls.Add(l);
                var v = new TextBox { Text = value, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Card, Font = label == "Link" ? Theme.F(9.5f) : Theme.Mono(10.5f),
                                      Bounds = new Rectangle(100, rowY + 4, card.Width - 100 - 18 - 150, 22), UseSystemPasswordChar = secretValue };
                card.Controls.Add(v);
                var copy = new FlatButton("Copy", ButtonKind.Secondary) { Bounds = new Rectangle(card.Width - 18 - 70, rowY, 70, 30), Font = Theme.F(9) };
                copy.Click += delegate { try { Clipboard.SetText(value); copy.Text = "Copied"; } catch { } };
                card.Controls.Add(copy);
                if (secretValue)
                {
                    var show = new FlatButton("Show", ButtonKind.Secondary) { Bounds = new Rectangle(card.Width - 18 - 70 - 76, rowY, 70, 30), Font = Theme.F(9) };
                    show.Click += delegate { v.UseSystemPasswordChar = !v.UseSystemPasswordChar; show.Text = v.UseSystemPasswordChar ? "Show" : "Hide"; };
                    card.Controls.Add(show);
                }
                return v;
            };
            row("Link", url, false, ly);
            row("Username", user, false, ly + 38);
            row("Password", pw, true, ly + 76);
            var open = new FlatButton("Open admin panel", ButtonKind.Primary) { Bounds = new Rectangle(card.Width - 18 - 160, 10, 160, 32) };
            open.Click += delegate { Ui.Open(url); };
            card.Controls.Add(open);
            y = card.Bottom + 14;
        }

        void AppCard()
        {
            var card = new Card { Bounds = new Rectangle(36, y, W - Side - 72, 112) };
            page.Controls.Add(card);
            var t = Ui.Text("App for your users", Theme.B(11), Theme.Ink); t.Location = new Point(18, 14); card.Controls.Add(t);
            var d = Ui.Wrapped("One installer for everyone (no keys inside). Send it to your users; they unzip it, run install.cmd and sign in with the username and password you made for them in the panel.",
                Theme.F(9.5f), Theme.Muted, card.Width - 36 - 190);
            d.Location = new Point(18, 42); card.Controls.Add(d);
            var b = new FlatButton("Build installer…", ButtonKind.Secondary) { Bounds = new Rectangle(card.Width - 18 - 170, 14, 170, 34) };
            b.Click += delegate { BuildClient(); };
            card.Controls.Add(b);
            y = card.Bottom + 14;
        }

        void BuildClient()
        {
            var missing = new[] { Path.Combine(Paths.Client, "DormVPN.exe"), Path.Combine(Paths.Engine, "sing-box.exe"), Path.Combine(Paths.Engine, "libcronet.dll") }.Where(f => !File.Exists(f)).ToList();
            if (missing.Count > 0) { MessageBox.Show(this, "Missing: " + string.Join(", ", missing), "DormVPN Admin", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            Directory.CreateDirectory(Paths.Installers);
            using (var dlg = new SaveFileDialog { FileName = "DormVPN-Setup.zip", Filter = "Zip file|*.zip", InitialDirectory = Paths.Installers })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var stage = Path.Combine(Path.GetTempPath(), "dormvpn-client-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var root = Path.Combine(stage, "DormVPN"); var app = Path.Combine(root, "app");
                    Directory.CreateDirectory(app);
                    foreach (var f in new[] { "install.cmd", "install.ps1", "uninstall.cmd", "uninstall.ps1" }) File.Copy(Path.Combine(Paths.Client, f), Path.Combine(root, f));
                    File.Copy(Path.Combine(Paths.Client, "DormVPN.exe"), Path.Combine(app, "DormVPN.exe"));
                    foreach (var f in new[] { "sing-box.exe", "libcronet.dll", "LICENSE" })
                        if (File.Exists(Path.Combine(Paths.Engine, f))) File.Copy(Path.Combine(Paths.Engine, f), Path.Combine(app, f));
                    File.WriteAllText(Path.Combine(app, "api.json"), Json.Write(new Dictionary<string, object> { { "api", Store.PanelUrl } }), new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(root, "READ ME.txt"),
                        "DormVPN\r\n=======\r\n\r\n1. Double-click install.cmd (Windows asks for permission once).\r\n" +
                        "2. DormVPN opens. Sign in with the username and password you were given.\r\n" +
                        "3. Click the big button to connect. The shield in the system tray shows the status.\r\n\r\n" +
                        "To remove it: double-click uninstall.cmd.\r\n");
                    if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
                    ZipFile.CreateFromDirectory(stage, dlg.FileName);
                    Process.Start("explorer.exe", "/select,\"" + dlg.FileName + "\"");
                }
                catch (Exception ex) { MessageBox.Show(this, "Couldn't build the installer: " + ex.Message, "DormVPN Admin", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { try { Directory.Delete(stage, true); } catch { } }
            }
        }

        // ================================================================ dashboard

        Label srvState, srvAddr;
        FlatButton srvStart, srvPause;

        void ShowDashboard()
        {
            wizard = false;
            DrawSidebar();
            var nav = new[] {
                new KeyValuePair<string, Action>("Open admin panel", () => Ui.Open(Store.PanelUrl)),
                new KeyValuePair<string, Action>("AWS console", () => Ui.Open("https://" + Store.Region + ".console.aws.amazon.com/ec2/home?region=" + Store.Region + "#Instances:")),
                new KeyValuePair<string, Action>("Change AWS keys", () => { wizard = true; ShowStep(2); }),
            };
            int ny = 92;
            foreach (var n in nav)
            {
                var a = n.Value;
                var b = new FlatButton(n.Key, ButtonKind.Link) { Bounds = new Rectangle(24, ny, Side - 40, 30) };
                b.Click += delegate { a(); };
                sidebar.Controls.Add(b); ny += 32;
            }
            var acct = Ui.Wrapped("AWS profile " + Store.Profile + "\n" + Regions.Label(Store.Region), Theme.F(8.5f), Theme.Muted, Side - 40);
            acct.Location = new Point(24, H - 60); sidebar.Controls.Add(acct);

            NewPage("Dashboard", null);
            y -= 10;
            // server card
            var card = new Card { Bounds = new Rectangle(36, y, W - Side - 72, 96) };
            page.Controls.Add(card);
            var t = Ui.Text("VPN server", Theme.B(11), Theme.Ink); t.Location = new Point(18, 14); card.Controls.Add(t);
            srvState = Ui.Text("Checking…", Theme.B(10), Theme.Muted); srvState.Location = new Point(18, 44); card.Controls.Add(srvState);
            srvAddr = Ui.Text(Json.Str(Store.Read("state.json"), "ip") + ":443", Theme.F(9.5f), Theme.Muted); srvAddr.Location = new Point(18, 66); card.Controls.Add(srvAddr);
            srvStart = new FlatButton("Start", ButtonKind.Primary) { Bounds = new Rectangle(card.Width - 18 - 100, 30, 100, 34), Visible = false };
            srvPause = new FlatButton("Pause", ButtonKind.Secondary) { Bounds = new Rectangle(card.Width - 18 - 100, 30, 100, 34), Visible = false };
            var refresh = new FlatButton("Refresh", ButtonKind.Secondary) { Bounds = new Rectangle(card.Width - 18 - 100 - 96, 30, 88, 34) };
            srvStart.Click += delegate { Power("start"); };
            srvPause.Click += delegate
            {
                if (MessageBox.Show(this, "Pause the server? Everyone is disconnected until it's started again.", "DormVPN Admin", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK) Power("stop");
            };
            refresh.Click += delegate { LoadServer(); };
            card.Controls.AddRange(new Control[] { srvStart, srvPause, refresh });
            y = card.Bottom + 14;

            PanelCard();
            AppCard();

            // maintenance
            var m = new Card { Bounds = new Rectangle(36, y, W - Side - 72, 92) };
            page.Controls.Add(m);
            var mt = Ui.Text("Maintenance", Theme.B(11), Theme.Ink); mt.Location = new Point(18, 14); m.Controls.Add(mt);
            var upd = new FlatButton("Update server", ButtonKind.Secondary) { Bounds = new Rectangle(18, 44, 130, 34) };
            var pw = new FlatButton("New admin password", ButtonKind.Secondary) { Bounds = new Rectangle(156, 44, 168, 34) };
            var del = new FlatButton("Delete everything…", ButtonKind.Danger) { Bounds = new Rectangle(m.Width - 18 - 156, 44, 156, 34) };
            upd.Click += delegate { RunInDialog("Update server", "deploy.ps1", ""); };
            pw.Click += delegate
            {
                if (MessageBox.Show(this, "Make a new admin password? The old one stops working and open panels sign out.", "DormVPN Admin", MessageBoxButtons.OKCancel) == DialogResult.OK)
                    RunInDialog("New admin password", "admin-password.ps1", "");
            };
            del.Click += delegate { DeleteEverything(); };
            m.Controls.AddRange(new Control[] { upd, pw, del });
            y = m.Bottom + 14;
            LoadServer();
        }

        void LoadServer()
        {
            var iid = Json.Str(Store.Read("state.json"), "iid");
            ThreadPool.QueueUserWorkItem(delegate
            {
                var r = Aws.Run("ec2 describe-instances --instance-ids " + iid + " --query Reservations[0].Instances[0].State.Name --output text" + Store.AwsArgs, 30000);
                var st = r.Code == 0 ? r.Out.Trim() : "unknown";
                OnUi(delegate
                {
                    if (srvState == null || srvState.IsDisposed) return;
                    var map = new Dictionary<string, KeyValuePair<string, Color>> {
                        { "running", new KeyValuePair<string, Color>("● Running", Theme.Accent) }, { "stopped", new KeyValuePair<string, Color>("● Paused", Theme.Grey) },
                        { "pending", new KeyValuePair<string, Color>("● Starting…", Theme.Warn) }, { "stopping", new KeyValuePair<string, Color>("● Pausing…", Theme.Warn) } };
                    KeyValuePair<string, Color> v;
                    if (!map.TryGetValue(st, out v)) v = new KeyValuePair<string, Color>("● " + (st == "unknown" ? "Can't check (AWS unreachable?)" : st), Theme.Bad);
                    srvState.Text = v.Key; srvState.ForeColor = v.Value;
                    srvStart.Visible = st == "stopped"; srvPause.Visible = st == "running";
                    if (st == "pending" || st == "stopping") { var tm = new System.Windows.Forms.Timer { Interval = 5000 }; tm.Tick += delegate { tm.Dispose(); LoadServer(); }; tm.Start(); }
                });
            });
        }

        void Power(string action)
        {
            var iid = Json.Str(Store.Read("state.json"), "iid");
            srvStart.Visible = srvPause.Visible = false;
            srvState.Text = action == "start" ? "● Starting…" : "● Pausing…"; srvState.ForeColor = Theme.Warn;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var r = Aws.Run("ec2 " + (action == "start" ? "start" : "stop") + "-instances --instance-ids " + iid + Store.AwsArgs, 30000);
                OnUi(delegate
                {
                    if (r.Code != 0) MessageBox.Show(this, "AWS said: " + FirstLine(r.Err), "DormVPN Admin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    LoadServer();
                });
            });
        }

        void DeleteEverything()
        {
            var f = new Form { Text = "Delete DormVPN", ClientSize = new Size(440, 210), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                               StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Bg, Font = Theme.F(9.5f), AutoScaleMode = AutoScaleMode.Dpi };
            var l = Ui.Wrapped("This deletes the VPN server, its address, the admin panel and every user from AWS. Nobody can connect afterwards and it can't be undone.\n\nType DELETE to confirm.", Theme.F(10), Theme.Ink, 400);
            l.Location = new Point(20, 18); f.Controls.Add(l);
            var box = new InputBox(false) { Bounds = new Rectangle(20, 112, 400, 38) }; f.Controls.Add(box);
            var ok = new FlatButton("Delete everything", ButtonKind.Danger) { Bounds = new Rectangle(250, 162, 170, 36), Enabled = false };
            var cancel = new FlatButton("Cancel", ButtonKind.Secondary) { Bounds = new Rectangle(150, 162, 90, 36) };
            box.Box.TextChanged += delegate { ok.Enabled = box.Text == "DELETE"; };
            ok.Click += delegate { f.DialogResult = DialogResult.OK; };
            cancel.Click += delegate { f.DialogResult = DialogResult.Cancel; };
            f.Controls.Add(ok); f.Controls.Add(cancel);
            if (f.ShowDialog(this) != DialogResult.OK) return;
            RunInDialog("Deleting DormVPN", "destroy.ps1", "-Yes", ok2 => { if (ok2) StartWizard(); });
        }

        void RunInDialog(string title, string script, string args, Action<bool> after = null)
        {
            var f = new Form { Text = title, ClientSize = new Size(640, 420), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                               StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Bg, Font = Theme.F(9.5f), AutoScaleMode = AutoScaleMode.Dpi, ControlBox = false };
            var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono(9), BackColor = Theme.Card, Bounds = new Rectangle(16, 16, 608, 340), BorderStyle = BorderStyle.FixedSingle };
            var close = new FlatButton("Working…", ButtonKind.Primary) { Bounds = new Rectangle(504, 368, 120, 38), Enabled = false };
            close.Click += delegate { f.Close(); };
            f.Controls.Add(box); f.Controls.Add(close);
            bool sawDone = false;
            f.Shown += delegate
            {
                running = Proc.Script(script, args,
                    line => { if (line.StartsWith("DORMVPN_")) sawDone = true; f.BeginInvoke((Action)delegate { box.AppendText(line + "\r\n"); }); },
                    code => f.BeginInvoke((Action)delegate
                    {
                        running = null;
                        bool ok = code == 0 && sawDone;
                        box.AppendText(ok ? "\r\nDone." : "\r\nSomething went wrong (see above).");
                        close.Text = "Close"; close.Enabled = true; f.ControlBox = true;
                        if (after != null) after(ok);
                    }));
            };
            f.ShowDialog(this);
            if (!wizard) ShowDashboard();
        }
    }
}
