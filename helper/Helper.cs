// Desktop Copilot native helper.
// Talks newline-delimited JSON over TCP to the DSH host plugin.
// Compile: csc /target:winexe /out:DesktopCopilot.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll Helper.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Automation;
using System.Web.Script.Serialization;

namespace DesktopCopilot
{
    internal static class Native
    {
        [DllImport("user32.dll")]
        internal static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")]
        internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")]
        internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        internal static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")]
        internal static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        internal static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);
        internal const uint LWA_COLORKEY = 0x1;
        [DllImport("user32.dll")]
        internal static extern uint GetLastInputInfo(ref LASTINPUTINFO info);

        internal const uint MOD_ALT = 0x0001;
        internal const uint MOD_CONTROL = 0x0002;
        internal const uint VK_ESCAPE = 0x1B;
        internal const int WM_HOTKEY = 0x0312;
        internal const int HOTKEY_STOP = 0xA17C;
        internal const int HOTKEY_SELF = 0xA17D;
        internal const int HOTKEY_NEXT = 0xA17E;
        internal const int HOTKEY_REJECT = 0xA17F;
        internal const int HOTKEY_INTERJECT = 0xA180;

        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")]
        internal static extern IntPtr WindowFromPoint(POINT pt);

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT { internal int X; internal int Y; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { internal int Left; internal int Top; internal int Right; internal int Bottom; }


        [StructLayout(LayoutKind.Sequential)]
        internal struct LASTINPUTINFO { internal uint cbSize; internal uint dwTime; }
    }

    internal class NoActivateForm : Form
    {
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                return cp;
            }
        }
    }

    internal sealed class Box
    {
        internal int X;
        internal int Y;
        internal int W;
        internal int H;
        internal int N;
    }

    internal sealed class Highlights
    {
        private readonly List<Form> windows = new List<Form>();

        internal int Count
        {
            get { return windows.Count; }
        }

        internal void Show(List<Box> boxes)
        {
            Hide();
            foreach (Box b in boxes)
            {
                FrameWindow frame = new FrameWindow(b);
                frame.Show();
                windows.Add(frame);
                BadgeWindow badge = new BadgeWindow(b);
                badge.Show();
                windows.Add(badge);
            }
        }

        internal void Hide()
        {
            foreach (Form f in windows)
            {
                try { f.Hide(); f.Close(); f.Dispose(); }
                catch { }
            }
            windows.Clear();
        }
    }

    internal sealed class FrameWindow : NoActivateForm
    {
        private readonly Box box;

        internal FrameWindow(Box b)
        {
            box = b;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(b.X - 4, b.Y - 4, b.W + 8, b.H + 8);
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(255, 45, 60);
            AutoScaleMode = AutoScaleMode.None;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            const int t = 4;
            int w = Width, h = Height;
            Region r = new Region(new Rectangle(0, 0, w, t));
            r.Union(new Rectangle(0, h - t, w, t));
            r.Union(new Rectangle(0, 0, t, h));
            r.Union(new Rectangle(w - t, 0, t, h));
            Region = r;
        }
    }

    internal sealed class BadgeWindow : NoActivateForm
    {
        private readonly string label;

        internal BadgeWindow(Box b)
        {
            label = b.N.ToString(CultureInfo.InvariantCulture);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(b.X - 4, b.Y - 30, 26, 24);
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(255, 45, 60);
            AutoScaleMode = AutoScaleMode.None;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Brush text = new SolidBrush(Color.White))
            using (Font font = new Font("Segoe UI", 10f, FontStyle.Bold))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                e.Graphics.DrawString(label, font, text, new RectangleF(0, 0, Width, Height), sf);
            }
        }
    }

    /// <summary>Small top-most field for typing a note to the agent without leaving the task.</summary>
    internal sealed class InterjectForm : Form
    {
        internal event Action<string> Submitted;
        private readonly TextBox box;
        private System.Windows.Forms.Timer focusTicker;

        internal InterjectForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Width = 660;
            Height = 100;
            Text = "DSH Copilot Interject";
            KeyPreview = true;
            BackColor = Color.FromArgb(26, 27, 32);
            Padding = new Padding(18, 12, 18, 14);
            Rectangle screen = Screen.PrimaryScreen.WorkingArea;
            Left = screen.Left + (screen.Width - Width) / 2;
            Top = screen.Top + 56;

            Button send = new Button();
            send.Text = "\u53d1\u9001";
            send.Dock = DockStyle.Right;
            send.Width = 70;
            send.FlatStyle = FlatStyle.Flat;
            send.BackColor = Color.FromArgb(52, 96, 178);
            send.ForeColor = Color.White;
            send.Font = new Font("Microsoft YaHei UI", 9.5f);
            send.Click += delegate { Accept(); };

            Label hint = new Label();
            hint.Text = "\u63d2\u8bdd\u7ed9\u9cb8\u9c7c\u5a18\uff08Enter \u53d1\u9001 \u00b7 Esc \u53d6\u6d88\uff09";
            hint.ForeColor = Color.FromArgb(150, 155, 165);
            hint.Font = new Font("Microsoft YaHei UI", 8.5f);
            hint.Dock = DockStyle.Top;
            hint.Height = 20;

            box = new TextBox();
            box.BorderStyle = BorderStyle.FixedSingle;
            box.BackColor = Color.FromArgb(38, 40, 48);
            box.ForeColor = Color.White;
            box.Font = new Font("Microsoft YaHei UI", 11f);
            box.Dock = DockStyle.Bottom;
            box.Height = 38;
            box.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Accept(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Hide(); }
            };

            Controls.Add(box);
            Controls.Add(hint);
            Controls.Add(send);
            // The keyboard may land on the form rather than the text box, so handle the keys
            // here too: Enter and Esc must always work once the box is open.
            KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Accept(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Hide(); }
            };
            Shown += delegate
            {
                ActiveControl = box;
                box.Focus();
                box.SelectAll();
            };
        }

        internal void Open()
        {
            box.Text = "";
            Show();
            TopMost = true;
            ForceFocus();
            // Windows hands the foreground back to whoever asked last, so keep re-asserting
            // focus for a moment: the user is likely already typing.
            if (focusTicker != null) { focusTicker.Stop(); focusTicker.Dispose(); }
            int attempts = 0;
            focusTicker = new System.Windows.Forms.Timer();
            focusTicker.Interval = 150;
            focusTicker.Tick += delegate
            {
                attempts++;
                if (!Visible || attempts > 12) { focusTicker.Stop(); return; }
                if (!ContainsFocus) ForceFocus();
            };
            focusTicker.Start();
        }

        private void ForceFocus()
        {
            try
            {
                Input.PressKeys("alt");
                System.Threading.Thread.Sleep(40);
                IntPtr fg = Native.GetForegroundWindow();
                uint pidUnused;
                uint fgThread = Native.GetWindowThreadProcessId(fg, out pidUnused);
                uint myThread = Native.GetCurrentThreadId();
                bool attached = Native.AttachThreadInput(myThread, fgThread, true);
                try
                {
                    Native.BringWindowToTop(Handle);
                    Native.SetForegroundWindow(Handle);
                }
                finally
                {
                    if (attached) Native.AttachThreadInput(myThread, fgThread, false);
                }
            }
            catch { }
            Activate();
            ActiveControl = box;
            box.Select();
            try { BeginInvoke(new Action(delegate { ActiveControl = box; box.Focus(); })); } catch { }
            // A synthetic click on our own field is a real user-level input event, so Windows
            // lets it take the keyboard: that is what makes typing work without a manual click.
            try
            {
                Point p = box.PointToScreen(new Point(Math.Max(8, box.Width / 2), Math.Max(8, box.Height / 2)));
                Input.Click(p.X, p.Y, false, 1);
            }
            catch { }
        }

        private void Accept()
        {
            string text = (box.Text ?? "").Trim();
            Hide();
            if (text.Length > 0 && Submitted != null) Submitted(text);
        }
    }

    internal sealed class CardForm : NoActivateForm
    {
        internal Action<string> OnChoice;
        internal Action<bool> OnAutoChanged;
        internal bool Auto;
        private TextBox reasonBox;
        private string lastChoice;

        internal CardForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(24, 24, 27);
            Padding = new Padding(14);
            AutoScaleMode = AutoScaleMode.None;
            Width = 560;
        }

        internal void Build(string title, List<string> lines, bool auto, bool canType)
        {
            Auto = auto;
            SuspendLayout();
            Controls.Clear();
            Width = 620;
            int y = 14;

            Label tl = new Label();
            tl.Text = title;
            tl.ForeColor = Color.FromArgb(150, 156, 168);
            tl.Font = new Font("Segoe UI", 9f);
            tl.AutoSize = false;
            tl.Left = 18; tl.Top = y; tl.Width = Width - 36; tl.Height = 20;
            tl.AutoEllipsis = true;
            Controls.Add(tl);
            y += 26;

            foreach (string line in lines)
            {
                Label al = new Label();
                al.Text = line;
                al.ForeColor = Color.FromArgb(236, 238, 243);
                al.Font = new Font("Segoe UI", 10.5f);
                al.AutoSize = false;
                al.Left = 18; al.Top = y; al.Width = Width - 36; al.Height = 26;
                al.AutoEllipsis = true;
                Controls.Add(al);
                y += 28;
            }
            y += 14;

            string[] captions = new string[] { "1  自己执行", "2  下一步", "3  驳回" };
            string[] values = new string[] { "self", "next", "reject" };
            Color[] backs = new Color[] { Color.FromArgb(35, 39, 47), Color.FromArgb(42, 88, 184), Color.FromArgb(43, 34, 38) };
            Color[] borders = new Color[] { Color.FromArgb(58, 65, 80), Color.FromArgb(59, 111, 212), Color.FromArgb(74, 51, 58) };
            Color[] fores = new Color[] { Color.FromArgb(223, 227, 234), Color.White, Color.FromArgb(255, 143, 143) };
            for (int i = 0; i < 3; i++)
            {
                Button b = new Button();
                b.Text = captions[i];
                b.Tag = values[i];
                b.Left = 18 + i * 168;
                b.Top = y;
                b.Width = 152;
                b.Height = 46;
                StyleButton(b, backs[i], fores[i], borders[i]);
                b.Click += delegate(object s, EventArgs e) { Choose((string)((Button)s).Tag); };
                Controls.Add(b);
            }
            y += 58;

            Button ab = new Button();
            ab.Text = Auto ? "自动执行：开" : "自动执行：关";
            ab.Left = 18;
            ab.Top = y;
            ab.Width = 132;
            ab.Height = 34;
            ab.Tag = "auto";
            StyleButton(ab, Auto ? Color.FromArgb(150, 96, 18) : Color.FromArgb(35, 39, 47), Auto ? Color.White : Color.FromArgb(180, 186, 198), Color.FromArgb(58, 65, 80));
            ab.Click += delegate(object s, EventArgs e)
            {
                Auto = !Auto;
                StyleButton((Button)s, Auto ? Color.FromArgb(150, 96, 18) : Color.FromArgb(35, 39, 47), Auto ? Color.White : Color.FromArgb(180, 186, 198), Color.FromArgb(58, 65, 80));
                ((Button)s).Text = Auto ? "自动执行：开" : "自动执行：关";
                if (OnAutoChanged != null) OnAutoChanged(Auto);
            };
            Controls.Add(ab);

            Label hint = new Label();
            hint.Text = "Ctrl+Alt+1 自己执行　Ctrl+Alt+2 下一步　Ctrl+Alt+3 驳回　Ctrl+Alt+Esc 停止";
            hint.ForeColor = Color.FromArgb(116, 122, 134);
            hint.Font = new Font("Segoe UI", 8.5f);
            hint.AutoSize = false;
            hint.Left = 164; hint.Top = y + 8; hint.Width = Width - 182; hint.Height = 20;
            Controls.Add(hint);

            Height = y + 62;
            ResumeLayout(true);
            ApplyRounded(16);
        }

        private static Color Lighten(Color color, int amount)
        {
            return Color.FromArgb(
                Math.Min(255, color.R + amount),
                Math.Min(255, color.G + amount),
                Math.Min(255, color.B + amount));
        }

        private static Region RoundRegion(int width, int height, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(width - d, 0, d, d, 270, 90);
            path.AddArc(width - d, height - d, d, d, 0, 90);
            path.AddArc(0, height - d, d, d, 90, 90);
            path.CloseFigure();
            return new Region(path);
        }

        private static void StyleButton(Button button, Color back, Color fore, Color border)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = border;
            button.FlatAppearance.MouseOverBackColor = Lighten(back, 22);
            button.FlatAppearance.MouseDownBackColor = Lighten(back, 34);
            button.BackColor = back;
            button.ForeColor = fore;
            button.Font = new Font("Segoe UI", 10f);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
            button.Region = RoundRegion(button.Width, button.Height, 10);
        }

        internal void ApplyRounded(int radius)
        {
            using (GraphicsPath path = new GraphicsPath())
            {
                Rectangle r = new Rectangle(0, 0, Width, Height);
                int d = radius * 2;
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
                path.CloseFigure();
                Region = new Region(path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Color.FromArgb(72, 82, 98), 1.4f))
            using (GraphicsPath path = new GraphicsPath())
            {
                Rectangle r = new Rectangle(1, 1, Width - 3, Height - 3);
                int d = 26;
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                e.Graphics.DrawPath(pen, path);
            }
        }

        /// <summary>Quick fade-in so a card appears instead of blinking into place.</summary>
        internal void FadeIn()
        {
            Opacity = 0;
            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            timer.Interval = 16;
            timer.Tick += delegate
            {
                Opacity = Math.Min(1.0, Opacity + 0.14);
                if (Opacity >= 1.0) { timer.Stop(); timer.Dispose(); }
            };
            timer.Start();
        }

        internal void TriggerChoice(string choice) { Choose(choice); }

        private void Choose(string choice)
        {
            if (choice == "reject")
            {
                string reason = Prompt("驳回原因（可留空）");
                if (reason == null) return; // cancelled, stay
                lastChoice = reason;
                Choice(choice + "|" + reason);
                return;
            }
            Choice(choice);
        }

        private void Choice(string value)
        {
            if (OnChoice != null) OnChoice(value);
        }

        internal string Prompt(string caption)
        {
            using (Form dlg = new Form())
            {
                dlg.Text = caption;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterScreen;
                dlg.Width = 460;
                dlg.Height = 150;
                dlg.TopMost = true;
                dlg.ShowInTaskbar = false;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;

                TextBox tb = new TextBox();
                tb.Left = 16; tb.Top = 16; tb.Width = 412;
                dlg.Controls.Add(tb);

                Button ok = new Button();
                ok.Text = "确定";
                ok.Left = 260; ok.Top = 56; ok.Width = 80;
                ok.DialogResult = DialogResult.OK;
                dlg.Controls.Add(ok);

                Button cancel = new Button();
                cancel.Text = "取消";
                cancel.Left = 348; cancel.Top = 56; cancel.Width = 80;
                cancel.DialogResult = DialogResult.Cancel;
                dlg.Controls.Add(cancel);

                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                dlg.ActiveControl = tb;
                DialogResult r = dlg.ShowDialog();
                if (r != DialogResult.OK) return null;
                return tb.Text;
            }
        }
    }

    internal sealed class Bridge
    {
        private readonly TcpClient tcp = new TcpClient();
        private StreamReader reader;
        private StreamWriter writer;
        private readonly object writeLock = new object();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private Form host;
        private CardForm card;
        private readonly Highlights highlights = new Highlights();
        private bool auto;
        private bool busy;
        private string pendingShowId;
        private string pendingKind = "show";
        private bool pendingGuide;
        private object[] pendingActions;
        private Form indicator;
        private static readonly string[] DangerWords = new string[]
        {
            "\u5220\u9664", "\u79fb\u81f3\u56de\u6536\u7ad9", "\u6e05\u7a7a", "\u683c\u5f0f\u5316", "\u5378\u8f7d", "\u5b89\u88c5",
            "\u6ce8\u9500", "\u767b\u51fa", "\u767b\u5f55", "\u6ce8\u518c", "\u652f\u4ed8", "\u8f6c\u8d26", "\u91cd\u7f6e",
            "\u5173\u673a", "\u91cd\u542f", "\u8986\u76d6",
            "delete", "remove", "uninstall", "install", "format", "erase", "clear all", "sign out", "log out",
            "pay", "checkout", "transfer", "reset", "shutdown", "restart",
        };
        private readonly List<Box> lastBoxes = new List<Box>();
        private Dictionary<string, object> lastUia;
        private List<Dictionary<string, object>> lastElements = new List<Dictionary<string, object>>();
        private IntPtr lastWindowHandle;
        private string logPath;

        internal void Log(string message)
        {
            try
            {
                if (logPath == null) return;
                File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        internal void Start(int port, string token, string logDir)
        {
            try
            {
                Directory.CreateDirectory(logDir);
                logPath = Path.Combine(logDir, "helper.log");
            }
            catch
            {
                try { logPath = Path.Combine(Path.GetTempPath(), "desktop-copilot-helper.log"); } catch { }
            }
            tcp.Connect("127.0.0.1", port);
            tcp.NoDelay = true;
            NetworkStream stream = tcp.GetStream();
            reader = new StreamReader(stream, new UTF8Encoding(false));
            writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.NewLine = "\n";
            writer.AutoFlush = true;
            Log("connected to host port " + port);
            Send("{\"type\":\"hello\",\"token\":" + Json(token) + ",\"pid\":" + System.Diagnostics.Process.GetCurrentProcess().Id + "}");
            Thread t = new Thread(ReadLoop);
            t.IsBackground = true;
            t.Start();
        }

        internal void Attach(Form form)
        {
            host = form;
        }

        private static string Json(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 32) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        internal void Send(string line)
        {
            lock (writeLock)
            {
                try { writer.Write(line + "\n"); }
                catch (Exception ex) { Log("send failed: " + ex.Message); }
            }
        }

        private void ReadLoop()
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string copy = line;
                    try { host.BeginInvoke(new Action(delegate { Dispatch(copy); })); }
                    catch (Exception ex) { Log("dispatch failed: " + ex.Message); }
                }
            }
            catch (Exception ex) { Log("read loop ended: " + ex.Message); }
            try { host.BeginInvoke(new Action(delegate { Application.Exit(); })); } catch { }
        }

        private void Reply(string id, Dictionary<string, object> result)
        {
            Dictionary<string, object> msg = new Dictionary<string, object>();
            msg["id"] = id;
            msg["ok"] = true;
            msg["result"] = result;
            Send(json.Serialize(msg));
        }

        private void Fail(string id, string error)
        {
            Dictionary<string, object> msg = new Dictionary<string, object>();
            msg["id"] = id;
            msg["ok"] = false;
            msg["error"] = error;
            Send(json.Serialize(msg));
        }

        private static Dictionary<string, object> Dict(object value)
        {
            Dictionary<string, object> d = value as Dictionary<string, object>;
            return d;
        }

        private static int IntOf(Dictionary<string, object> d, string key, int fallback)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return Convert.ToInt32(v, CultureInfo.InvariantCulture);
            return fallback;
        }

        private void Dispatch(string line)
        {
            Dictionary<string, object> msg;
            try { msg = Dict(json.DeserializeObject(line)); }
            catch (Exception ex) { Log("bad json: " + ex.Message); return; }
            if (msg == null) return;
            string id = msg.ContainsKey("id") ? Convert.ToString(msg["id"], CultureInfo.InvariantCulture) : null;
            string op = msg.ContainsKey("op") ? Convert.ToString(msg["op"], CultureInfo.InvariantCulture) : "";
            try
            {
                if (op == "ping") { Dictionary<string, object> r = new Dictionary<string, object>(); r["pong"] = true; r["auto"] = auto; Reply(id, r); return; }
                if (op == "status") { Dictionary<string, object> r = new Dictionary<string, object>(); r["auto"] = auto; r["busy"] = busy; r["highlightWindows"] = highlights.Count; if (card != null && card.Visible) { r["cardX"] = card.Left; r["cardY"] = card.Top; r["cardW"] = card.Width; r["cardH"] = card.Height; } Reply(id, r); return; }
                if (op == "hide") { CancelPending("cancelled"); HideAll(); Dictionary<string, object> r = new Dictionary<string, object>(); Reply(id, r); return; }
                if (op == "setauto") { auto = Convert.ToBoolean(msg["value"], CultureInfo.InvariantCulture); if (auto) ShowIndicator(); else HideIndicator(); Dictionary<string, object> r = new Dictionary<string, object>(); r["auto"] = auto; Reply(id, r); return; }
                if (op == "probe") { Probe(id, msg); return; }
                if (op == "capture") { Capture(id, msg); return; }
                if (op == "uia") { UiaOp(id, msg); return; }
                if (op == "focus") { FocusOp(id, msg); return; }
                if (op == "input") { Dictionary<string, object> rr = new Dictionary<string, object>(); rr["results"] = ExecuteActions(msg.ContainsKey("actions") ? msg["actions"] as object[] : null); Reply(id, rr); return; }
                if (op == "move") { int mx = IntOf(msg, "x", 0); int my = IntOf(msg, "y", 0); Native.POINT before; Native.GetCursorPos(out before); bool setOk = Input.SetCursorPos(mx, my); int setErr = Marshal.GetLastWin32Error(); Native.POINT after; Native.GetCursorPos(out after); Dictionary<string, object> r = new Dictionary<string, object>(); r["beforeX"] = before.X; r["beforeY"] = before.Y; r["afterX"] = after.X; r["afterY"] = after.Y; r["is64"] = IntPtr.Size == 8; r["inputSize"] = Marshal.SizeOf(typeof(Input.INPUT)); r["sent"] = Input.LastSendCount; r["err"] = Input.LastSendError; r["setOk"] = setOk; r["setErr"] = setErr; Reply(id, r); return; }
                if (op == "cursor") { Dictionary<string, object> r = new Dictionary<string, object>(); Native.POINT pt; Native.GetCursorPos(out pt); r["x"] = pt.X; r["y"] = pt.Y; IntPtr under = Native.WindowFromPoint(pt); System.Text.StringBuilder cls = new System.Text.StringBuilder(256); Native.GetClassName(under, cls, cls.Capacity); r["hwnd"] = under.ToInt64(); r["class"] = cls.ToString(); Reply(id, r); return; }
                if (op == "token") { TokenOp(id); return; }
                if (op == "uiaraw") { UiaRawOp(id, msg); return; }
                if (op == "step") { StepOp(id, msg); return; }
                if (op == "show") { ShowCard(id, msg, "show", null); return; }
                Fail(id, "unknown op: " + op);
            }
            catch (Exception ex)
            {
                Log("op " + op + " failed: " + ex);
                Fail(id, ex.Message);
            }
        }

        private void Probe(string id, Dictionary<string, object> msg)
        {
            int x = IntOf(msg, "x", 0);
            int y = IntOf(msg, "y", 0);
            using (Bitmap bmp = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(new Point(x, y), Point.Empty, new Size(1, 1));
                Color c = bmp.GetPixel(0, 0);
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["r"] = c.R; r["g"] = c.G; r["b"] = c.B;
                Reply(id, r);
            }
        }

        private void Capture(string id, Dictionary<string, object> msg)
        {
            string path = msg.ContainsKey("path") ? Convert.ToString(msg["path"], CultureInfo.InvariantCulture) : null;
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            Dictionary<string, object> crop = msg.ContainsKey("crop") ? Dict(msg["crop"]) : null;
            if (crop != null) bounds = new Rectangle(IntOf(crop, "x", bounds.Left), IntOf(crop, "y", bounds.Top), IntOf(crop, "w", bounds.Width), IntOf(crop, "h", bounds.Height));
            int maxWidth = IntOf(msg, "maxWidth", 0);
            bool jpeg = msg.ContainsKey("jpeg") && Convert.ToBoolean(msg["jpeg"], CultureInfo.InvariantCulture);
            bool zoom = msg.ContainsKey("zoom") && Convert.ToBoolean(msg["zoom"], CultureInfo.InvariantCulture);
            using (Bitmap full = new Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height)))
            {
                using (Graphics g = Graphics.FromImage(full)) g.CopyFromScreen(bounds.Location, Point.Empty, full.Size);
                Bitmap output = full;
                int width = bounds.Width, height = bounds.Height;
                // zoom allows upscaling a small region so a dense control becomes readable.
                int targetWidth = maxWidth;
                if (zoom && maxWidth > bounds.Width) targetWidth = Math.Min(maxWidth, bounds.Width * 3);
                if (maxWidth > 0 && bounds.Width != targetWidth)
                {
                    width = targetWidth;
                    height = (int)Math.Round(bounds.Height * (double)targetWidth / bounds.Width);
                    output = new Bitmap(width, height);
                    using (Graphics g2 = Graphics.FromImage(output))
                    {
                        g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g2.DrawImage(full, 0, 0, width, height);
                    }
                }
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["width"] = width; r["height"] = height;
                r["originX"] = bounds.X; r["originY"] = bounds.Y; r["sourceWidth"] = bounds.Width;
                if (path != null)
                {
                    SaveImage(output, path, jpeg);
                    r["path"] = path;
                }
                else
                {
                    // No filesystem round trip: the host may be confined to a sandbox.
                    using (MemoryStream stream = new MemoryStream())
                    {
                        SaveImageTo(output, stream, jpeg);
                        r["base64"] = Convert.ToBase64String(stream.ToArray());
                    }
                }
                if (!ReferenceEquals(output, full)) output.Dispose();
                Reply(id, r);
            }
        }

        private static void SaveImage(Bitmap bmp, string path, bool jpeg)
        {
            ImageCodecInfo codec = JpegCodec(jpeg);
            using (EncoderParameters ps = new EncoderParameters(1))
            {
                ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
                if (codec == null) bmp.Save(path, jpeg ? ImageFormat.Jpeg : ImageFormat.Png);
                else bmp.Save(path, codec, ps);
            }
        }

        private static void SaveImageTo(Bitmap bmp, Stream stream, bool jpeg)
        {
            ImageCodecInfo codec = JpegCodec(jpeg);
            using (EncoderParameters ps = new EncoderParameters(1))
            {
                ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
                if (codec == null) bmp.Save(stream, jpeg ? ImageFormat.Jpeg : ImageFormat.Png);
                else bmp.Save(stream, codec, ps);
            }
        }

        private static ImageCodecInfo JpegCodec(bool jpeg)
        {
            if (!jpeg) return null;
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
            return null;
        }

        private void FocusOp(string id, Dictionary<string, object> msg)
        {
            int pid = IntOf(msg, "pid", 0);
            IntPtr hwnd = IntPtr.Zero;
            try { hwnd = System.Diagnostics.Process.GetProcessById(pid).MainWindowHandle; }
            catch { }
            if (hwnd == IntPtr.Zero) { Fail(id, "no window for pid " + pid); return; }
            Input.PressKeys("alt"); // unlock the foreground lock
            System.Threading.Thread.Sleep(80);
            IntPtr fg = Native.GetForegroundWindow();
            uint pidUnused;
            uint fgThread = Native.GetWindowThreadProcessId(fg, out pidUnused);
            uint myThread = Native.GetCurrentThreadId();
            bool attached = Native.AttachThreadInput(myThread, fgThread, true);
            try
            {
                Native.ShowWindow(hwnd, 9);
                Native.BringWindowToTop(hwnd);
                Native.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) Native.AttachThreadInput(myThread, fgThread, false);
            }
            System.Threading.Thread.Sleep(150);
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["foreground"] = Native.GetForegroundWindow() == hwnd;
            Reply(id, r);
        }

        private void TokenOp(string id)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["integrity"] = Input.IntegrityRid();
            try
            {
                string probe = Path.Combine(Path.GetTempPath(), "desktop-copilot-write-test.txt");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                r["tempWrite"] = "ok";
            }
            catch (Exception ex) { r["tempWrite"] = ex.Message; }
            r["user"] = Environment.UserName;
            r["session"] = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            Reply(id, r);
        }

        private void UiaRawOp(string id, Dictionary<string, object> msg)
        {
            int pid = IntOf(msg, "pid", 0);
            IntPtr hwnd = pid > 0 ? System.Diagnostics.Process.GetProcessById(pid).MainWindowHandle : Native.GetForegroundWindow();
            IntPtr target = hwnd;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> rows = new List<string>();
                try
                {
                    AutomationElement root = AutomationElement.FromHandle(target);
                    rows.Add("root=" + SafeName(root) + " children=" + SafeChildCount(root));
                    for (int round = 0; round < 4; round++)
                    {
                        List<AutomationElement> all = new List<AutomationElement>();
                        Uia.WalkForDebug(root, 0, all, 400);
                        rows.Add("round " + round + " total=" + all.Count);
                        for (int i = 0; i < Math.Min(8, all.Count); i++) rows.Add("  " + i + " " + SafeName(all[i]) + " [" + SafeControlType(all[i]) + "]");
                        if (all.Count > 20) break;
                        System.Threading.Thread.Sleep(400);
                    }
                }
                catch (Exception ex) { rows.Add("failed: " + ex.Message); }
                try { host.BeginInvoke(new Action(delegate { Dictionary<string, object> r = new Dictionary<string, object>(); r["rows"] = rows; Reply(id, r); })); } catch { }
            });
        }

        private static string SafeName(AutomationElement el) { try { return el.Current.Name ?? ""; } catch { return "<err>"; } }
        private static string SafeControlType(AutomationElement el) { try { return el.Current.ControlType.ProgrammaticName; } catch { return "<err>"; } }
        private static int SafeChildCount(AutomationElement el) { try { return TreeWalker.RawViewWalker.GetFirstChild(el) == null ? 0 : 1; } catch { return -1; } }

        private void UiaOp(string id, Dictionary<string, object> msg)
        {
            int pid = IntOf(msg, "pid", 0);
            IntPtr hwnd = IntPtr.Zero;
            if (pid > 0)
            {
                try { hwnd = System.Diagnostics.Process.GetProcessById(pid).MainWindowHandle; }
                catch { }
            }
            if (hwnd == IntPtr.Zero) hwnd = Native.GetForegroundWindow();
            IntPtr target = hwnd;
            string scope = msg.ContainsKey("scope") ? Convert.ToString(msg["scope"], CultureInfo.InvariantCulture) : "window";
            if (scope == "full") target = Native.GetForegroundWindow();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Dictionary<string, object> payload = DumpUia(target, scope);
                    host.BeginInvoke(new Action(delegate
                    {
                        lastWindowHandle = target;
                        lastUia = payload;
                        lastElements = new List<Dictionary<string, object>>();
                        object[] elements = payload.ContainsKey("elements") ? payload["elements"] as object[] : null;
                        if (elements != null)
                        {
                            foreach (object entry in elements)
                            {
                                Dictionary<string, object> element = Dict(entry);
                                if (element != null) lastElements.Add(element);
                            }
                        }
                        Dictionary<string, object> r = new Dictionary<string, object>();
                        // Windows with no UI Automation text (WeChat, Electron shells) still
                        // have pixels: fall back to the Windows recognizer so the model can read.
                        if (lastElements.Count < 8)
                        {
                            try
                            {
                                Dictionary<string, object> ocr = RunOcr(new Rectangle(
                                    IntOf(payload, "x", 0), IntOf(payload, "y", 0),
                                    Math.Max(1, IntOf(payload, "w", 0)), Math.Max(1, IntOf(payload, "h", 0))));
                                if (ocr != null) r["ocr"] = ocr.ContainsKey("lines") ? ocr["lines"] : null;
                            }
                            catch (Exception ocrError) { Log("ocr failed: " + ocrError); }
                        }
                        r["title"] = payload.ContainsKey("title") ? payload["title"] : "";
                        r["process"] = payload.ContainsKey("process") ? payload["process"] : "";
                        r["pid"] = payload.ContainsKey("pid") ? payload["pid"] : 0;
                        r["x"] = payload.ContainsKey("x") ? payload["x"] : 0;
                        r["y"] = payload.ContainsKey("y") ? payload["y"] : 0;
                        r["w"] = payload.ContainsKey("w") ? payload["w"] : 0;
                        r["h"] = payload.ContainsKey("h") ? payload["h"] : 0;
                        r["total"] = payload.ContainsKey("total") ? payload["total"] : 0;
                        r["elements"] = lastElements;
                        Reply(id, r);
                    }));
                }
                catch (Exception ex)
                {
                    Log("uia failed: " + ex);
                    try { host.BeginInvoke(new Action(delegate { Fail(id, "uia failed: " + ex.Message); })); } catch { }
                }
            });
        }

        /// <summary>Runs the PowerShell UIA dump: this process cannot reach Chromium's MSAA bridge.</summary>
        private Dictionary<string, object> DumpUia(IntPtr hwnd, string scope)
        {
            string script = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "uia-dump.ps1");
            if (!File.Exists(script)) throw new Exception("uia-dump.ps1 is missing next to the helper");
            string arguments = scope == "full"
                ? "-NoProfile -ExecutionPolicy Bypass -NonInteractive -File \"" + script + "\" -Scope full"
                : "-NoProfile -ExecutionPolicy Bypass -NonInteractive -File \"" + script + "\" -Scope window -Hwnd " + hwnd.ToInt64().ToString(CultureInfo.InvariantCulture);
            System.Diagnostics.ProcessStartInfo info = new System.Diagnostics.ProcessStartInfo("powershell.exe", arguments);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(info))
            {
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(120000);
                string jsonLine = null;
                foreach (string line in stdout.Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("{")) jsonLine = trimmed;
                }
                if (jsonLine == null) throw new Exception("uia dump produced no JSON" + (stderr.Length > 0 ? ": " + stderr.Trim() : ""));
                Dictionary<string, object> parsed = Dict(json.DeserializeObject(jsonLine));
                if (parsed == null) throw new Exception("uia dump returned invalid JSON");
                return parsed;
            }
        }

        /// <summary>OCR one screen rectangle with the Windows recognizer. Returns null when unavailable.</summary>
        private Dictionary<string, object> RunOcr(Rectangle bounds)
        {
            string directory = Path.GetDirectoryName(Application.ExecutablePath);
            string script = Path.Combine(directory, "ocr-capture.ps1");
            if (!File.Exists(script)) return null;
            string png = Path.Combine(Path.GetTempPath(), "dsh-dc-ocr.png");
            using (Bitmap shot = new Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height)))
            {
                using (Graphics g = Graphics.FromImage(shot)) g.CopyFromScreen(bounds.Location, Point.Empty, shot.Size);
                shot.Save(png, ImageFormat.Png);
            }
            try
            {
                System.Diagnostics.ProcessStartInfo info = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -NonInteractive -File \"" + script + "\" -Path \"" + png + "\"");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.StandardOutputEncoding = Encoding.UTF8;
                using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(info))
                {
                    string stdout = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(60000);
                    string jsonLine = null;
                    foreach (string line in stdout.Split('\n'))
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("{")) jsonLine = trimmed;
                    }
                    if (jsonLine == null) return null;
                    return Dict(json.DeserializeObject(jsonLine));
                }
            }
            finally
            {
                try { File.Delete(png); } catch { }
            }
        }

        private Dictionary<string, object> FindElement(int id)
        {
            if (id <= 0) return null;
            foreach (Dictionary<string, object> element in lastElements)
            {
                if (IntOf(element, "id", 0) == id) return element;
            }
            return null;
        }

        private void FocusWindow(IntPtr hwnd)
        {
            Input.PressKeys("alt");
            System.Threading.Thread.Sleep(60);
            IntPtr fg = Native.GetForegroundWindow();
            uint pidUnused;
            uint fgThread = Native.GetWindowThreadProcessId(fg, out pidUnused);
            uint myThread = Native.GetCurrentThreadId();
            bool attached = Native.AttachThreadInput(myThread, fgThread, true);
            try
            {
                Native.ShowWindow(hwnd, 9);
                Native.BringWindowToTop(hwnd);
                Native.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) Native.AttachThreadInput(myThread, fgThread, false);
            }
        }

        private List<object> ExecuteActions(object[] actions)
        {
            List<object> results = new List<object>();
            int startTick = Environment.TickCount;
            if (lastWindowHandle != IntPtr.Zero && Native.GetForegroundWindow() != lastWindowHandle)
            {
                FocusWindow(lastWindowHandle);
                System.Threading.Thread.Sleep(120);
            }
            if (actions != null)
            {
                string previousKind = null;
                foreach (object raw in actions)
                {
                    Dictionary<string, object> a = Dict(raw);
                    string kind = Convert.ToString(a["kind"], CultureInfo.InvariantCulture);
                    Dictionary<string, object> step = new Dictionary<string, object>();
                    step["kind"] = kind;
                    // After typing, browsers may leave an autofill dropdown covering the
                    // next field; dismiss it before the next hit.
                    if (previousKind == "type" && (kind == "click" || kind == "type"))
                    {
                        Input.PressKeys("esc");
                        System.Threading.Thread.Sleep(120);
                    }
                    previousKind = kind;
                    if (kind != "wait" && Input.UserActiveSince(startTick, 600))
                    {
                        step["ok"] = false;
                        step["note"] = "paused: you used the keyboard or mouse";
                        results.Add(step);
                        break;
                    }
                    Dictionary<string, object> element = FindElement(IntOf(a, "id", 0));
                    // Explicit coordinates win: the host re-resolved them from a fresh dump,
                    // so they survive a page scroll between the capture and the card.
                    bool hasCoords = a.ContainsKey("x") && a.ContainsKey("y");
                    int x = hasCoords ? IntOf(a, "x", 0) : (element != null ? IntOf(element, "x", 0) + IntOf(element, "w", 0) / 2 : 0);
                    int y = hasCoords ? IntOf(a, "y", 0) : (element != null ? IntOf(element, "y", 0) + IntOf(element, "h", 0) / 2 : 0);
                    try
                    {
                        if (kind == "click")
                        {
                            Input.Click(x, y, IntOf(a, "button", 0) == 1, IntOf(a, "count", 1));
                            step["ok"] = true;
                            step["note"] = element != null ? "element " + IntOf(a, "id", 0) : "coords";
                        }
                        else if (kind == "type")
                        {
                            string text = Convert.ToString(a["text"], CultureInfo.InvariantCulture);
                            if (element != null)
                            {
                                Input.Click(x, y, false, 1);
                                System.Threading.Thread.Sleep(140);
                            }
                            bool focused = lastWindowHandle == IntPtr.Zero || Native.GetForegroundWindow() == lastWindowHandle;
                            if (!focused)
                            {
                                step["ok"] = false;
                                step["note"] = "refused: target window is not focused";
                            }
                            else
                            {
                                Input.TypeText(text);
                                step["ok"] = true;
                                step["note"] = "keyboard";
                            }
                        }
                        else if (kind == "key")
                        {
                            bool focused = lastWindowHandle == IntPtr.Zero || Native.GetForegroundWindow() == lastWindowHandle;
                            if (!focused)
                            {
                                step["ok"] = false;
                                step["note"] = "refused: target window is not focused";
                            }
                            else
                            {
                                bool ok = Input.PressKeys(Convert.ToString(a["keys"], CultureInfo.InvariantCulture));
                                step["ok"] = ok;
                                step["note"] = ok ? "sent" : "unknown key";
                            }
                        }
                        else if (kind == "scroll")
                        {
                            int notches = IntOf(a, "delta", -3);
                            // pages is independent of the Windows "lines per notch" setting.
                            if (a.ContainsKey("pages"))
                            {
                                double pages = Convert.ToDouble(a["pages"], CultureInfo.InvariantCulture);
                                notches = (int)Math.Round(pages * 24);
                            }
                            Input.Wheel(x, y, notches);
                            step["ok"] = true;
                            step["note"] = notches + " notches";
                        }
                        else if (kind == "drag")
                        {
                            Dictionary<string, object> target = FindElement(IntOf(a, "toId", 0));
                            int toX = target != null ? IntOf(target, "x", 0) + IntOf(target, "w", 0) / 2 : IntOf(a, "toX", 0);
                            int toY = target != null ? IntOf(target, "y", 0) + IntOf(target, "h", 0) / 2 : IntOf(a, "toY", 0);
                            if (toX == 0 && toY == 0) { step["ok"] = false; step["note"] = "drag needs toId or toX/toY"; }
                            else
                            {
                                Input.Drag(x, y, toX, toY);
                                step["ok"] = true;
                                step["note"] = "drag " + x + "," + y + " -> " + toX + "," + toY;
                            }
                        }
                        else if (kind == "open")
                        {
                            // Always a fresh tab: typing into the current tab would replace
                            // whatever page the user is working in.
                            string url = Convert.ToString(a["url"], CultureInfo.InvariantCulture);
                            if (string.IsNullOrEmpty(url)) { step["ok"] = false; step["note"] = "open needs url"; }
                            else
                            {
                                Input.PressKeys("ctrl+t");
                                System.Threading.Thread.Sleep(450);
                                Input.TypeText(url);
                                System.Threading.Thread.Sleep(250);
                                Input.PressKeys("enter");
                                step["ok"] = true;
                                step["note"] = "new tab";
                            }
                        }
                        else if (kind == "wait")
                        {
                            int ms = IntOf(a, "ms", 500);
                            System.Threading.Thread.Sleep(Math.Max(0, Math.Min(10000, ms)));
                            step["ok"] = true;
                        }
                        else { step["ok"] = false; step["note"] = "unknown kind"; }
                    }
                    catch (Exception ex)
                    {
                        step["ok"] = false; step["note"] = ex.Message;
                    }
                    results.Add(step);
                    System.Threading.Thread.Sleep(160);
                }
            }
            return results;
        }

        private void CancelPending(string choice)
        {
            if (pendingShowId == null) return;
            string id = pendingShowId;
            pendingShowId = null;
            pendingActions = null;
            busy = false;
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["choice"] = choice;
            r["reason"] = "";
            Reply(id, r);
        }

        private void HideAll()
        {
            highlights.Hide();
            lastBoxes.Clear();
            HideIndicatorForced();
            if (card != null)
            {
                card.Hide();
                card = null;
            }
        }

        private void ShowCard(string id, Dictionary<string, object> msg, string kind, object[] actions)
        {
            if (pendingShowId != null) { Fail(id, "another card is pending"); return; }
            HideAll();
            pendingShowId = id;
            pendingKind = kind;
            pendingGuide = msg.ContainsKey("guide") && Convert.ToBoolean(msg["guide"], CultureInfo.InvariantCulture);
            pendingActions = actions;
            string title = msg.ContainsKey("title") ? Convert.ToString(msg["title"], CultureInfo.InvariantCulture) : "";
            List<string> lines = new List<string>();
            object[] rawLines = msg.ContainsKey("lines") ? msg["lines"] as object[] : null;
            if (rawLines != null) foreach (object o in rawLines) lines.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
            if (msg.ContainsKey("auto")) auto = Convert.ToBoolean(msg["auto"], CultureInfo.InvariantCulture);

            object[] rawBoxes = msg.ContainsKey("boxes") ? msg["boxes"] as object[] : null;
            lastBoxes.Clear();
            if (rawBoxes != null)
            {
                int n = 1;
                foreach (object o in rawBoxes)
                {
                    Dictionary<string, object> b = Dict(o);
                    Box box = new Box();
                    box.X = IntOf(b, "x", 0); box.Y = IntOf(b, "y", 0);
                    box.W = IntOf(b, "w", 0); box.H = IntOf(b, "h", 0);
                    box.N = IntOf(b, "n", n);
                    lastBoxes.Add(box);
                    n++;
                }
            }
            if (lastBoxes.Count > 0) highlights.Show(lastBoxes);

            if (card == null) card = new CardForm();
            card.OnChoice = delegate(string value) { CompleteShow(value); };
            card.OnAutoChanged = delegate(bool value) { auto = value; Send("{\"type\":\"event\",\"event\":\"auto\",\"value\":" + (value ? "true" : "false") + "}"); };
            card.Build(title, lines, auto, false);
            Rectangle screen = Screen.PrimaryScreen.Bounds;
            card.Left = screen.Left + (screen.Width - card.Width) / 2;
            card.Top = screen.Bottom - card.Height - 96;
            card.Show();
            card.TopMost = true;
            card.FadeIn();
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
            busy = true;
        }

        private void CompleteShow(string value)
        {
            if (pendingShowId == null) return;
            string id = pendingShowId;
            string kind = pendingKind;
            object[] actions = pendingActions;
            pendingShowId = null;
            pendingActions = null;
            busy = false;
            if (card != null) card.Hide();
            highlights.Hide();
            Dictionary<string, object> r = new Dictionary<string, object>();
            int bar = value.IndexOf('|');
            string choice = bar >= 0 ? value.Substring(0, bar) : value;
            r["choice"] = choice;
            r["reason"] = bar >= 0 ? value.Substring(bar + 1) : "";
            if (kind == "step" && !pendingGuide && (choice == "next" || choice == "auto"))
            {
                pendingGuide = false;
                ShowIndicator();
                try { r["results"] = ExecuteActions(actions); }
                finally { HideIndicator(); }
            }
            Reply(id, r);
        }

        private void ShowIndicator()
        {
            if (indicator == null)
            {
                Form f = new Form();
                f.FormBorderStyle = FormBorderStyle.None;
                f.StartPosition = FormStartPosition.Manual;
                f.ShowInTaskbar = false;
                f.TopMost = true;
                f.BackColor = Color.FromArgb(30, 30, 34);
                f.Width = 430; f.Height = 30;
                f.Text = "DSH Copilot HUD";
                Label label = new Label();
                label.Text = "\u9cb8\u9c7c\u5a18\u5728\u64cd\u63a7\u5c4f\u5e55 \u00b7 Ctrl+Alt+Esc \u505c\u6b62 \u00b7 Ctrl+Alt+I \u63d2\u8bdd";
                label.ForeColor = Color.FromArgb(235, 237, 242);
                label.Font = new Font("Microsoft YaHei UI", 9f);
                label.Dock = DockStyle.Fill;
                label.TextAlign = ContentAlignment.MiddleCenter;
                f.Controls.Add(label);
                f.Opacity = 0.92;
                Rectangle screen = Screen.PrimaryScreen.Bounds;
                f.Left = screen.Left + (screen.Width - f.Width) / 2;
                f.Top = screen.Top + 8;
                indicator = f;
            }
            indicator.Show();
            indicator.TopMost = true;
        }

        private void HideIndicator()
        {
            // In auto mode the bar stays up for the whole session: it is also the reminder of
            // which keys stop or interrupt the agent.
            if (indicator != null && !auto) indicator.Hide();
        }

        private void HideIndicatorForced()
        {
            if (indicator != null) indicator.Hide();
        }

        private bool IsDangerous(object[] actions)
        {
            if (actions == null) return false;
            foreach (object raw in actions)
            {
                Dictionary<string, object> a = Dict(raw);
                int elementId = IntOf(a, "id", 0);
                Dictionary<string, object> element = FindElement(elementId);
                if (element == null) continue;
                string name = element.ContainsKey("name") ? (Convert.ToString(element["name"], CultureInfo.InvariantCulture) ?? "") : "";
                string lower = name.ToLowerInvariant();
                foreach (string word in DangerWords)
                {
                    if (lower.Contains(word)) return true;
                }
            }
            return false;
        }

        private void StepOp(string id, Dictionary<string, object> msg)
        {
            object[] actions = msg.ContainsKey("actions") ? msg["actions"] as object[] : null;
            string mode = msg.ContainsKey("mode") ? Convert.ToString(msg["mode"], CultureInfo.InvariantCulture) : "manual";
            bool dangerous = IsDangerous(actions);
            if (mode == "auto" && !dangerous)
            {
                ShowIndicator();
                try { Dictionary<string, object> r = new Dictionary<string, object>(); r["choice"] = "auto"; r["reason"] = ""; r["results"] = ExecuteActions(actions); Reply(id, r); }
                finally { HideIndicator(); }
                return;
            }
            if (mode == "auto" && dangerous)
            {
                Dictionary<string, object> copy = new Dictionary<string, object>(msg);
                object[] lines = msg.ContainsKey("lines") ? msg["lines"] as object[] : null;
                List<object> merged = new List<object>();
                if (lines != null) foreach (object line in lines) merged.Add(line);
                merged.Add("\u5371\u9669\u52a8\u4f5c\uff0c\u5df2\u5207\u56de\u4eba\u5de5\u786e\u8ba4");
                copy["lines"] = merged.ToArray();
                ShowCard(id, copy, "step", actions);
                return;
            }
            ShowCard(id, msg, "step", actions);
        }

        internal void OnChoiceKey(string choice)
        {
            if (pendingShowId == null) return;
            if (card != null) card.TriggerChoice(choice);
        }

        private InterjectForm interject;

        internal void OnInterjectKey()
        {
            if (interject == null || interject.IsDisposed)
            {
                interject = new InterjectForm();
                interject.Submitted += OnInterjectText;
            }
            interject.Open();
        }

        private void OnInterjectText(string text)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["text"] = text;
            Dictionary<string, object> msg = new Dictionary<string, object>();
            msg["id"] = "__interject";
            msg["ok"] = true;
            msg["result"] = payload;
            Send(json.Serialize(msg));
        }

        internal void OnHotkey()
        {
            Log("hotkey stop");
            auto = false;
            string stopId = pendingShowId;
            pendingShowId = null;
            busy = false;
            HideAll();
            Send("{\"type\":\"event\",\"event\":\"stop\"}");
            if (stopId != null)
            {
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["choice"] = "stop"; r["reason"] = "";
                Reply(stopId, r);
            }
        }
    }

    internal sealed class HostForm : Form
    {
        internal Bridge Bridge;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == Native.HOTKEY_STOP)
                {
                    if (Bridge != null) Bridge.OnHotkey();
                    return;
                }
                if (id == Native.HOTKEY_SELF || id == Native.HOTKEY_NEXT || id == Native.HOTKEY_REJECT)
                {
                    string choice = id == Native.HOTKEY_SELF ? "self" : (id == Native.HOTKEY_NEXT ? "next" : "reject");
                    if (Bridge != null) Bridge.OnChoiceKey(choice);
                    return;
                }
                if (id == Native.HOTKEY_INTERJECT)
                {
                    if (Bridge != null) Bridge.OnInterjectKey();
                    return;
                }
            }
            base.WndProc(ref m);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] argv)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "desktop-copilot-start.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " main enter\r\n"); } catch { }
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { try { Native.SetProcessDPIAware(); } catch { } }

            int port = 0;
            string token = "";
            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dsh-desktop-copilot");
            for (int i = 0; i < argv.Length; i++)
            {
                if (argv[i] == "--port" && i + 1 < argv.Length) port = int.Parse(argv[i + 1], CultureInfo.InvariantCulture);
                if (argv[i] == "--token" && i + 1 < argv.Length) token = argv[i + 1];
                if (argv[i] == "--logdir" && i + 1 < argv.Length) logDir = argv[i + 1];
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Bridge bridge = new Bridge();
            HostForm host = new HostForm();
            host.Bridge = bridge;
            host.FormBorderStyle = FormBorderStyle.None;
            host.ShowInTaskbar = false;
            host.StartPosition = FormStartPosition.Manual;
            host.Bounds = new Rectangle(-4000, -4000, 1, 1);
            host.Opacity = 0;

            bridge.Attach(host);
            IntPtr forced = host.Handle; // create the window handle before commands can arrive
            try { bridge.Start(port, token, logDir); }
            catch (Exception ex)
            {
                bridge.Log("start failed: " + ex);
                MessageBox.Show("Desktop Copilot helper could not connect: " + ex.Message);
                return;
            }

            bool hotkey = Native.RegisterHotKey(host.Handle, Native.HOTKEY_STOP, Native.MOD_CONTROL | Native.MOD_ALT, Native.VK_ESCAPE);
            Native.RegisterHotKey(host.Handle, Native.HOTKEY_SELF, Native.MOD_CONTROL | Native.MOD_ALT, 0x31);
            Native.RegisterHotKey(host.Handle, Native.HOTKEY_NEXT, Native.MOD_CONTROL | Native.MOD_ALT, 0x32);
            Native.RegisterHotKey(host.Handle, Native.HOTKEY_REJECT, Native.MOD_CONTROL | Native.MOD_ALT, 0x33);
            Native.RegisterHotKey(host.Handle, Native.HOTKEY_INTERJECT, Native.MOD_CONTROL | Native.MOD_ALT, 0x49);
            bridge.Log("hotkey registered: " + hotkey);

            Application.Run(host);
        }
    }
}
