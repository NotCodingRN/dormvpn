// DormVPN - tray app for a personal sing-box VLESS+REALITY VPN.
// Build: see build.ps1 (uses the .NET Framework 4 C# compiler that ships with Windows).
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("DormVPN")]
[assembly: System.Reflection.AssemblyProduct("DormVPN")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace DormVPN
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var m = new Mutex(true, "Global\\DormVPN-single-instance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("DormVPN is already running. Look for its shield icon in the system tray (bottom-right, maybe under the ^ arrow).",
                        "DormVPN", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }

    enum VpnState { Disconnected, Connecting, Connected, Reconnecting }

    class TrayApp : ApplicationContext
    {
        readonly string dir = AppDomain.CurrentDomain.BaseDirectory;
        string SingBox { get { return Path.Combine(dir, "sing-box.exe"); } }
        string ConfigPath { get { return Path.Combine(dir, "config.json"); } }
        string LogPath { get { return Path.Combine(dir, "dormvpn.log"); } }
        string SettingsPath { get { return Path.Combine(dir, "settings.ini"); } }

        NotifyIcon tray;
        ToolStripMenuItem miStatus, miExit, miToggle, miAutoConnect, miStartup;
        System.Windows.Forms.Timer timer;
        Icon icoOff, icoWait, icoOn;

        Process proc;
        bool wantConnected;
        VpnState state = VpnState.Disconnected;
        string serverIp = "";
        string exitInfo = "";
        int failCount;
        int restartCount;
        DateTime lastStart = DateTime.MinValue;
        DateTime nextHealth = DateTime.MinValue;
        DateTime connectingSince = DateTime.MinValue;

        // health check results, written by a worker thread, read by the UI timer
        volatile bool healthBusy;
        volatile int healthResult; // 0 = none pending, 1 = ok, 2 = fail
        volatile string healthDetail = "";

        readonly object logLock = new object();
        static readonly Regex Ansi = new Regex("\x1B\\[[0-9;]*m", RegexOptions.Compiled);

        public TrayApp()
        {
            icoOff = MakeIcon(Color.FromArgb(138, 143, 152));
            icoWait = MakeIcon(Color.FromArgb(240, 160, 32));
            icoOn = MakeIcon(Color.FromArgb(34, 165, 89));

            serverIp = ReadServerIp();

            var menu = new ContextMenuStrip();
            miStatus = new ToolStripMenuItem("Disconnected") { Enabled = false };
            miStatus.Font = new Font(miStatus.Font, FontStyle.Bold);
            miExit = new ToolStripMenuItem("Server: " + serverIp + " (Paris)") { Enabled = false };
            miToggle = new ToolStripMenuItem("Connect", null, delegate { Toggle(); });
            miAutoConnect = new ToolStripMenuItem("Connect when DormVPN opens", null, delegate { ToggleAutoConnect(); });
            miStartup = new ToolStripMenuItem("Start DormVPN when I sign in", null, delegate { ToggleStartup(); });
            menu.Items.Add(miStatus);
            menu.Items.Add(miExit);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miToggle);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miAutoConnect);
            menu.Items.Add(miStartup);
            menu.Items.Add(new ToolStripMenuItem("Open log", null, delegate { OpenFile(LogPath); }));
            menu.Items.Add(new ToolStripMenuItem("Open DormVPN folder", null, delegate { OpenFile(dir); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Quit DormVPN", null, delegate { Quit(); }));
            menu.Opening += delegate { miStartup.Checked = StartupTaskExists(); };

            tray = new NotifyIcon { Icon = icoOff, Text = "DormVPN: disconnected", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { Toggle(); };

            miAutoConnect.Checked = ReadSetting("autoconnect", "1") == "1";

            timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += delegate { Tick(); };
            timer.Start();

            Log("[app] DormVPN started. Server " + serverIp);
            if (!File.Exists(SingBox) || !File.Exists(ConfigPath))
            {
                Balloon("DormVPN is missing files", "sing-box.exe or config.json not found in " + dir, ToolTipIcon.Error);
                return;
            }
            if (miAutoConnect.Checked) Connect();
            UpdateUi();
        }

        // ---------------- connection control ----------------

        void Toggle()
        {
            if (wantConnected) Disconnect(); else Connect();
        }

        void Connect()
        {
            wantConnected = true;
            restartCount = 0;
            SetState(VpnState.Connecting);
            StartCore();
        }

        void Disconnect()
        {
            wantConnected = false;
            StopCore();
            SetState(VpnState.Disconnected);
            Balloon("DormVPN disconnected", "Your traffic is no longer protected.", ToolTipIcon.Info);
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
                    WorkingDirectory = dir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) Log(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Log(e.Data); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                lastStart = DateTime.Now;
                connectingSince = DateTime.Now;
                nextHealth = DateTime.Now.AddSeconds(3);
                failCount = 0;
                Log("[app] engine started (pid " + proc.Id + ")");
            }
            catch (Exception ex)
            {
                Log("[app] failed to start engine: " + ex.Message);
                Balloon("DormVPN could not start", ex.Message, ToolTipIcon.Error);
            }
        }

        void StopCore()
        {
            try
            {
                if (proc != null && !proc.HasExited)
                {
                    proc.Kill();
                    proc.WaitForExit(4000);
                    Log("[app] engine stopped");
                }
            }
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

        // ---------------- watchdog ----------------

        void Tick()
        {
            if (wantConnected)
            {
                bool dead = proc == null || proc.HasExited;
                if (dead)
                {
                    // engine crashed or exited: restart with backoff (2,4,8..30s)
                    int backoff = Math.Min(30, 1 << Math.Min(5, restartCount + 1));
                    if (state != VpnState.Reconnecting) { SetState(VpnState.Reconnecting); Log("[app] engine exited unexpectedly"); }
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
                        failCount = 0;
                        restartCount = 0;
                        exitInfo = healthDetail;
                        if (state != VpnState.Connected)
                        {
                            SetState(VpnState.Connected);
                            Balloon("DormVPN connected", "Exit: " + exitInfo, ToolTipIcon.Info);
                        }
                        nextHealth = DateTime.Now.AddSeconds(30);
                    }
                    else
                    {
                        failCount++;
                        Log("[app] health check failed (" + failCount + "): " + healthDetail);
                        if (state == VpnState.Connected)
                        {
                            if (failCount >= 2) SetState(VpnState.Reconnecting);
                            nextHealth = DateTime.Now.AddSeconds(5);
                            if (failCount >= 4) RestartCore("server not reachable");
                        }
                        else
                        {
                            nextHealth = DateTime.Now.AddSeconds(3);
                            if ((DateTime.Now - connectingSince).TotalSeconds > 45) RestartCore("no connection after 45s");
                        }
                    }
                }
            }
            UpdateUi();
        }

        void HealthCheck()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://www.cloudflare.com/cdn-cgi/trace");
                req.Proxy = null;
                req.Timeout = 8000;
                req.ReadWriteTimeout = 8000;
                req.UserAgent = "DormVPN-health";
                string body;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                    body = sr.ReadToEnd();
                var ip = Regex.Match(body, "(?m)^ip=(.+)$").Groups[1].Value.Trim();
                var loc = Regex.Match(body, "(?m)^loc=(.+)$").Groups[1].Value.Trim();
                if (ip == serverIp)
                {
                    healthDetail = ip + " (" + loc + ")";
                    healthResult = 1;
                }
                else
                {
                    healthDetail = "traffic not going through VPN (seen IP " + ip + ")";
                    healthResult = 2;
                }
            }
            catch (Exception ex)
            {
                healthDetail = ex.Message;
                healthResult = 2;
            }
            finally { healthBusy = false; }
        }

        // ---------------- UI ----------------

        void SetState(VpnState s)
        {
            if (state == s) return;
            state = s;
            Log("[app] state -> " + s);
            UpdateUi();
        }

        void UpdateUi()
        {
            string text;
            switch (state)
            {
                case VpnState.Connected:
                    tray.Icon = icoOn; text = "Connected"; break;
                case VpnState.Connecting:
                    tray.Icon = icoWait; text = "Connecting..."; break;
                case VpnState.Reconnecting:
                    tray.Icon = icoWait; text = "Reconnecting... (traffic held)"; break;
                default:
                    tray.Icon = icoOff; text = "Disconnected"; break;
            }
            miStatus.Text = text;
            miExit.Text = state == VpnState.Connected && exitInfo != "" ? "Exit IP: " + exitInfo : "Server: " + serverIp + " (Paris)";
            miToggle.Text = wantConnected ? "Disconnect" : "Connect";
            string tip = "DormVPN: " + text;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        void Balloon(string title, string text, ToolTipIcon icon)
        {
            try { tray.ShowBalloonTip(3000, title, text, icon); } catch { }
        }

        static Icon MakeIcon(Color fill)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var path = new GraphicsPath())
                    {
                        // shield outline
                        path.AddLine(16, 2, 29, 7);
                        path.AddBezier(29, 7, 29, 19, 24, 26, 16, 30);
                        path.AddBezier(16, 30, 8, 26, 3, 19, 3, 7);
                        path.CloseFigure();
                        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                        using (var pen = new Pen(Color.FromArgb(200, 255, 255, 255), 1.5f)) g.DrawPath(pen, path);
                    }
                    // keyhole
                    using (var w = new SolidBrush(Color.White))
                    {
                        g.FillEllipse(w, 12, 9, 8, 8);
                        g.FillPolygon(w, new[] { new Point(14, 15), new Point(18, 15), new Point(19, 23), new Point(13, 23) });
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        // ---------------- settings, startup, files ----------------

        string ReadServerIp()
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                // the VPN outbound is the "server" entry followed by "server_port" (DNS servers have no port)
                var m = Regex.Match(json, "\"server\"\\s*:\\s*\"([0-9.]+)\"\\s*,\\s*\"server_port\"");
                return m.Success ? m.Groups[1].Value : "?";
            }
            catch { return "?"; }
        }

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

        void WriteSetting(string key, string val)
        {
            try { File.WriteAllText(SettingsPath, key + "=" + val + Environment.NewLine); } catch { }
        }

        void ToggleAutoConnect()
        {
            miAutoConnect.Checked = !miAutoConnect.Checked;
            WriteSetting("autoconnect", miAutoConnect.Checked ? "1" : "0");
        }

        bool StartupTaskExists()
        {
            return RunHidden("schtasks.exe", "/Query /TN DormVPN") == 0;
        }

        void ToggleStartup()
        {
            if (StartupTaskExists())
            {
                RunHidden("schtasks.exe", "/Delete /TN DormVPN /F");
                Balloon("DormVPN", "Will no longer start when you sign in.", ToolTipIcon.Info);
            }
            else
            {
                var exe = Application.ExecutablePath;
                // highest privileges so the VPN can start at sign-in without a UAC prompt
                int rc = RunHidden("schtasks.exe", "/Create /TN DormVPN /TR \"\\\"" + exe + "\\\"\" /SC ONLOGON /RL HIGHEST /F");
                Balloon("DormVPN", rc == 0 ? "Will start automatically when you sign in." : "Could not create the startup task.", rc == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning);
            }
            miStartup.Checked = StartupTaskExists();
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

        static void OpenFile(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
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

        void Log(string line)
        {
            lock (logLock)
            {
                try { File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + Ansi.Replace(line, "") + Environment.NewLine); } catch { }
            }
        }

        void Quit()
        {
            wantConnected = false;
            timer.Stop();
            StopCore();
            Log("[app] quit");
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }
    }
}
