// Shared UI kit and helpers for the DormVPN client and admin apps (C# 5, .NET Framework 4.x, WinForms).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DormVPN.Shared
{
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(25, 22, 46);
        public static readonly Color Card = Color.FromArgb(35, 31, 63);
        public static readonly Color Field = Color.FromArgb(29, 26, 54);
        public static readonly Color Ink = Color.FromArgb(244, 240, 255);
        public static readonly Color Muted = Color.FromArgb(163, 155, 203);
        public static readonly Color Line = Color.FromArgb(53, 47, 92);
        public static readonly Color Soft = Color.FromArgb(44, 39, 80);
        public static readonly Color Accent = Color.FromArgb(255, 182, 72);
        public static readonly Color AccentHover = Color.FromArgb(255, 202, 128);
        public static readonly Color AccentDark = Color.FromArgb(232, 156, 44);
        public static readonly Color OnAccent = Color.FromArgb(42, 26, 0);
        public static readonly Color Warn = Color.FromArgb(255, 143, 107);
        public static readonly Color Bad = Color.FromArgb(255, 92, 122);
        public static readonly Color Grey = Color.FromArgb(112, 104, 150);
        public static readonly Color Busy = Color.FromArgb(156, 140, 255);
        public static readonly Color GridLine = Color.FromArgb(25, 22, 46);
        public const bool Grid = false, Gauge = false;
        const string DisplayFamily = "Bahnschrift SemiBold";

        public static Font F(float size) { return new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Point); }
        public static Font B(float size) { return new Font("Segoe UI Semibold", size, FontStyle.Regular, GraphicsUnit.Point); }
        public static Font Mono(float size) { return new Font("Consolas", size, FontStyle.Regular, GraphicsUnit.Point); }
        public static Font D(float size) { return new Font(DisplayFamily, size, FontStyle.Regular, GraphicsUnit.Point); }

        /// The window background (and the blueprint grid in CloudLabs), lined up across controls.
        public static void PaintBackdrop(Graphics g, Control c)
        {
            g.Clear(Bg);
            if (!Grid) return;
            var f = c.FindForm();
            var o = f != null && c.IsHandleCreated ? f.PointToClient(c.PointToScreen(Point.Empty)) : Point.Empty;
            const int step = 24;
            using (var pen = new Pen(GridLine))
            {
                for (int x = -(((o.X % step) + step) % step); x < c.Width; x += step) g.DrawLine(pen, x, 0, x, c.Height);
                for (int y = -(((o.Y % step) + step) % step); y < c.Height; y += step) g.DrawLine(pen, 0, y, c.Width, y);
            }
        }

        /// What shows around a rounded control: the backdrop if its parent draws one, else the parent's colour.
        public static void PaintUnder(Graphics g, Control c)
        {
            var p = c.Parent;
            if (p != null && "grid".Equals(p.Tag)) PaintBackdrop(g, c); else g.Clear(p != null ? p.BackColor : Bg);
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static GraphicsPath Shield(RectangleF r)
        {
            // the DormVPN shield, drawn in a 32x32 box and scaled into r
            var p = new GraphicsPath();
            p.AddLine(16, 2, 29, 7);
            p.AddBezier(29, 7, 29, 19, 24, 26, 16, 30);
            p.AddBezier(16, 30, 8, 26, 3, 19, 3, 7);
            p.CloseFigure();
            var m = new Matrix();
            m.Translate(r.X, r.Y);
            m.Scale(r.Width / 32f, r.Height / 32f);
            p.Transform(m);
            return p;
        }

        public static void DrawShield(Graphics g, RectangleF r, Color fill)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Shield(r))
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            float s = r.Width / 32f;
            using (var w = new SolidBrush(Color.White))
            {
                g.FillEllipse(w, r.X + 12 * s, r.Y + 9 * s, 8 * s, 8 * s);
                g.FillPolygon(w, new[] { new PointF(r.X + 14 * s, r.Y + 15 * s), new PointF(r.X + 18 * s, r.Y + 15 * s),
                                         new PointF(r.X + 19 * s, r.Y + 23 * s), new PointF(r.X + 13 * s, r.Y + 23 * s) });
            }
        }

        public static Icon MakeIcon(Color fill)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    DrawShield(g, new RectangleF(0, 0, 32, 32), fill);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }

    enum ButtonKind { Primary, Secondary, Danger, Link }

    /// Flat rounded button with hover/pressed states.
    class FlatButton : Control
    {
        public ButtonKind Kind = ButtonKind.Secondary;
        bool hover, down;

        public FlatButton(string text, ButtonKind kind)
        {
            Text = text; Kind = kind;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            BackColor = Color.Transparent;
            Font = kind == ButtonKind.Primary ? Theme.B(10) : Theme.F(10);
            Cursor = Cursors.Hand;
            Size = new Size(120, 36);
            TabStop = true;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fill, border, ink;
            switch (Kind)
            {
                case ButtonKind.Primary:
                    fill = down ? Theme.AccentDark : hover ? Theme.AccentHover : Theme.Accent; border = fill; ink = Theme.OnAccent; break;
                case ButtonKind.Danger:
                    fill = hover ? Color.FromArgb(58, 30, 30) : Theme.Card; border = Color.FromArgb(110, 50, 46); ink = Theme.Bad; break;
                case ButtonKind.Link:
                    fill = Color.Transparent; border = Color.Transparent; ink = hover ? Theme.AccentDark : Theme.Accent; break;
                default:
                    fill = down ? Theme.Line : hover ? Theme.Soft : Theme.Card; border = Theme.Line; ink = Theme.Ink; break;
            }
            if (!Enabled) { ink = Color.FromArgb(150, ink); if (Kind == ButtonKind.Primary) { fill = Theme.Soft; border = Theme.Line; ink = Theme.Muted; } }
            if (Kind != ButtonKind.Link)
            {
                using (var path = Theme.Round(r, 8))
                {
                    using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                    using (var p = new Pen(Focused && ShowFocusCues ? Theme.AccentDark : border, Focused && ShowFocusCues ? 2 : 1)) g.DrawPath(p, path);
                }
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, ink,
                (Kind == ButtonKind.Link ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter) | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// White rounded card with a hairline border.
    class Card : Panel
    {
        Color fill = Theme.Card;
        public Color Fill { get { return fill; } set { fill = value; BackColor = value; } }
        public Color Border = Theme.Line;
        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Padding = new Padding(16);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.PaintUnder(g, this);
            using (var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 12))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var p = new Pen(Border)) g.DrawPath(p, path);
            }
        }
    }

    /// Thin rounded progress bar; turns orange at 80% and red at 100%.
    class UsageBar : Control
    {
        double value;
        public double Value { get { return value; } set { this.value = Math.Max(0, Math.Min(1, value)); Invalidate(); } }
        public UsageBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 8;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.PaintUnder(g, this);
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Round(r, Height / 2f)) using (var b = new SolidBrush(Theme.Line)) g.FillPath(b, path);
            if (value > 0)
            {
                var c = value >= 1 ? Theme.Bad : value >= 0.8 ? Theme.Warn : Theme.Accent;
                var f = new RectangleF(0, 0, Math.Max(Height, (float)(r.Width * value)), r.Height);
                using (var path = Theme.Round(f, Height / 2f)) using (var b = new SolidBrush(c)) g.FillPath(b, path);
            }
        }
    }

    /// Text box with padding and a rounded border (a borderless TextBox inside a painted panel).
    class InputBox : Panel
    {
        public readonly TextBox Box = new TextBox();
        public InputBox(bool password)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 38; Padding = new Padding(10, 9, 10, 6); BackColor = Theme.Field;
            Box.BorderStyle = BorderStyle.None; Box.Font = Theme.F(10.5f); Box.Dock = DockStyle.Fill; Box.BackColor = Theme.Field; Box.ForeColor = Theme.Ink;
            Box.UseSystemPasswordChar = password;
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Controls.Add(Box);
            Click += delegate { Box.Focus(); };
        }
        public override string Text { get { return Box.Text; } set { Box.Text = value; } }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.PaintUnder(g, this);
            using (var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8))
            {
                using (var b = new SolidBrush(Theme.Field)) g.FillPath(b, path);
                using (var p = new Pen(Box.Focused ? Theme.Accent : Theme.Line, Box.Focused ? 1.6f : 1f)) g.DrawPath(p, path);
            }
        }
    }

    static class Ui
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// Windows ignores colours on dropdown lists, so these are drawn by hand.
        public static ComboBox DarkCombo(ComboBox c)
        {
            c.DrawMode = DrawMode.OwnerDrawFixed;
            c.ItemHeight = 22;
            c.BackColor = Theme.Field;
            c.ForeColor = Theme.Ink;
            c.DrawItem += (s, e) =>
            {
                var sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                using (var b = new SolidBrush(sel ? Theme.Soft : Theme.Field)) e.Graphics.FillRectangle(b, e.Bounds);
                if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, Convert.ToString(c.Items[e.Index]), c.Font, e.Bounds, c.Enabled ? Theme.Ink : Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            return c;
        }

        /// Dark title bar on Windows 10 2004+ and 11; older versions just ignore it.
        public static void Dark(Form f)
        {
            f.ForeColor = Theme.Ink;
            f.HandleCreated += delegate { int on = 1; try { DwmSetWindowAttribute(f.Handle, 20, ref on, 4); } catch { } };
        }

        public static Label Text(string text, Font font, Color color)
        {
            return new Label { Text = text, Font = font, ForeColor = color, AutoSize = true, BackColor = Color.Transparent };
        }

        public static Label Wrapped(string text, Font font, Color color, int width)
        {
            var l = new Label { Text = text, Font = font, ForeColor = color, AutoSize = true, BackColor = Color.Transparent, MaximumSize = new Size(width, 0) };
            return l;
        }

        public static string Gb(double bytes) { return (bytes / 1e9).ToString(bytes < 1e10 ? "0.00" : "0.0"); }

        public static void Open(string target)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
        }
    }

    /// Minimal JSON access on top of JavaScriptSerializer.
    static class Json
    {
        static readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        public static Dictionary<string, object> Parse(string s)
        {
            try { return js.Deserialize<Dictionary<string, object>>(s) ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }
        public static string Write(object o) { return js.Serialize(o); }
        public static object Get(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
        public static string Str(Dictionary<string, object> d, string k) { var v = Get(d, k); return v == null ? "" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture); }
        public static double Num(Dictionary<string, object> d, string k)
        {
            var v = Get(d, k);
            try { return v == null ? 0 : Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return 0; }
        }
        public static bool Bool(Dictionary<string, object> d, string k) { var v = Get(d, k); return v is bool && (bool)v; }
        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string k) { return Get(d, k) as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        public static ArrayList Arr(Dictionary<string, object> d, string k) { return Get(d, k) as ArrayList ?? new ArrayList(); }
    }

    class ApiResult
    {
        public int Status;
        public Dictionary<string, object> Data;
        public string Error { get { var e = Json.Str(Data, "error"); return e != "" ? e : "Couldn't reach the DormVPN server (" + Status + ")."; } }
        public bool Ok { get { return Status >= 200 && Status < 300; } }
    }

    static class Http
    {
        public static ApiResult Call(string method, string url, string token, object body)
        {
            var res = new ApiResult { Data = new Dictionary<string, object>() };
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                req.Timeout = 20000; req.ReadWriteTimeout = 20000;
                req.UserAgent = "DormVPN-app";
                req.Accept = "application/json";
                if (!string.IsNullOrEmpty(token)) req.Headers["Authorization"] = "Bearer " + token;
                if (body != null)
                {
                    var bytes = Encoding.UTF8.GetBytes(Json.Write(body));
                    req.ContentType = "application/json";
                    req.ContentLength = bytes.Length;
                    using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                }
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    res.Status = (int)resp.StatusCode;
                    res.Data = Json.Parse(sr.ReadToEnd());
                }
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp != null)
                {
                    res.Status = (int)resp.StatusCode;
                    try { using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) res.Data = Json.Parse(sr.ReadToEnd()); } catch { }
                }
                else
                {
                    res.Status = 0;
                    res.Data["error"] = "No internet connection, or the DormVPN service can't be reached.";
                }
            }
            catch (Exception ex)
            {
                res.Status = 0;
                res.Data["error"] = ex.Message;
            }
            return res;
        }
    }

    /// Strings encrypted for the current Windows user (DPAPI).
    static class Secret
    {
        public static void Save(string path, string text)
        {
            var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, enc);
        }
        public static string Load(string path)
        {
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)); }
            catch { return null; }
        }
        /// Decrypts the hex string PowerShell's ConvertFrom-SecureString writes (DPAPI over UTF-16).
        public static string FromPowerShell(string hex)
        {
            try
            {
                var bytes = new byte[hex.Length / 2];
                for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                return Encoding.Unicode.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
            }
            catch { return null; }
        }
    }
}
