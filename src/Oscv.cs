using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Oscv
{
    static class App
    {
        // 版番号はここが唯一の出どころ (build.ps1 が読む)。v1 から 1 ずつ上げる。
        public const int Version = 1;
    }

    // ================= theme =================
    static class T
    {
        public static Color Bg      = Color.FromArgb(28, 30, 34);
        public static Color Header  = Color.FromArgb(21, 23, 26);
        public static Color Track   = Color.FromArgb(56, 60, 66);
        public static Color Fill    = Color.FromArgb(96, 170, 255);
        public static Color FillHot = Color.FromArgb(134, 194, 255);
        public static Color Knob    = Color.FromArgb(238, 242, 247);
        public static Color Text    = Color.FromArgb(216, 222, 230);
        public static Color TextDim = Color.FromArgb(124, 132, 144);
        public static Color BtnBg   = Color.FromArgb(44, 48, 54);
        public static Color BtnHot  = Color.FromArgb(64, 70, 79);
        public static Color Ok      = Color.FromArgb(96, 200, 120);
        public static Color Busy    = Color.FromArgb(240, 180, 70);
        public static Color Err     = Color.FromArgb(230, 90, 90);
    }

    static class Gfx
    {
        public static void Round(Graphics g, Brush b, int x, int y, int w, int h, int d)
        {
            if (w <= 0 || h <= 0) return;
            if (d > w) d = w;
            if (d > h) d = h;
            if (d <= 1) { g.FillRectangle(b, x, y, w, h); return; }
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddArc(x, y, d, d, 180, 90);
                p.AddArc(x + w - d, y, d, d, 270, 90);
                p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
                p.AddArc(x, y + h - d, d, d, 90, 90);
                p.CloseFigure();
                g.FillPath(b, p);
            }
        }
    }

    // ================= diagnostics (enabled by a "debug.on" file) =================
    static class Dbg
    {
        static readonly object l = new object();
        static string dir;
        public static bool On;

        static Dbg()
        {
            try
            {
                dir = Path.GetDirectoryName(Application.ExecutablePath);
                On = File.Exists(Path.Combine(dir, "debug.on"));
            }
            catch { On = false; }
        }

        public static void W(string s)
        {
            if (!On) return;
            try
            {
                lock (l)
                    File.AppendAllText(Path.Combine(dir, "oscv-debug.log"),
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n",
                        Encoding.UTF8);
            }
            catch { }
        }
    }

    // ================= DDC/CI (dxva2.dll) =================
    // Talks to the panel directly over the display link. No LG software of any
    // kind is involved - not OnScreen Control, not osccli, not the LG service.
    static class Ddc
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szDescription;
        }

        delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr data);

        [DllImport("user32.dll")]
        static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);

        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
        // Marshalled by hand: PHYSICAL_MONITOR is non-blittable (it holds a string),
        // and letting the marshaller copy the array back proved unreliable - it
        // handed back a zero handle that then looked like "not open".
        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, IntPtr buf);
        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool DestroyPhysicalMonitors(uint n, IntPtr buf);
        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type,
                                                           out uint cur, out uint max);
        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool SetVCPFeature(IntPtr h, byte code, uint val);

        static IntPtr buf = IntPtr.Zero;   // native PHYSICAL_MONITOR[] we must destroy
        static uint bufCount;
        static IntPtr target = IntPtr.Zero;
        // 0 is a legitimate physical-monitor handle, so openness needs its own flag.
        static bool opened;
        static readonly object gate = new object();

        public static bool IsOpen { get { return opened; } }

        // A single read costs ~60ms and fails outright maybe 1 time in 40.
        static int Raw(IntPtr h, byte code)
        {
            uint cur, max;
            if (!GetVCPFeatureAndVCPFeatureReply(h, code, IntPtr.Zero, out cur, out max)) return -1;
            return (int)cur;
        }

        public static bool Open(byte probeVcp)
        {
            lock (gate)
            {
                CloseLocked();

                List<IntPtr> hmons = new List<IntPtr>();
                MonitorEnumProc cb = delegate(IntPtr hm, IntPtr hdc, IntPtr rc, IntPtr d)
                { hmons.Add(hm); return true; };
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);

                Dbg.W("Open: hmon=" + hmons.Count);
                if (hmons.Count == 0) return false;

                // Take the physical monitors of the first HMONITOR that has any.
                int stride = Marshal.SizeOf(typeof(PHYSICAL_MONITOR));
                for (int i = 0; i < hmons.Count && bufCount == 0; i++)
                {
                    uint n;
                    if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hmons[i], out n) || n == 0)
                    { Dbg.W("  GetNumber failed err=" + Marshal.GetLastWin32Error()); continue; }

                    IntPtr b = Marshal.AllocHGlobal(stride * (int)n);
                    if (!GetPhysicalMonitorsFromHMONITOR(hmons[i], n, b))
                    {
                        Dbg.W("  GetPhysical failed err=" + Marshal.GetLastWin32Error());
                        Marshal.FreeHGlobal(b);
                        continue;
                    }
                    buf = b;
                    bufCount = n;
                }
                Dbg.W("Open: physical=" + bufCount);
                if (bufCount == 0) return false;

                // Prefer the panel that answers the vendor-specific code - on a
                // multi-monitor desk that is the LG one.
                for (int pass = 0; pass < 2; pass++)
                {
                    byte code = pass == 0 ? probeVcp : (byte)0x10;
                    for (int i = 0; i < bufCount; i++)
                    {
                        PHYSICAL_MONITOR pm = (PHYSICAL_MONITOR)Marshal.PtrToStructure(
                            (IntPtr)(buf.ToInt64() + (long)i * stride), typeof(PHYSICAL_MONITOR));
                        int v = Raw(pm.hPhysicalMonitor, code);
                        Dbg.W("  probe[" + i + "] '" + pm.szDescription + "' h=" +
                              pm.hPhysicalMonitor.ToInt64() + " vcp" + code.ToString("X2") +
                              " -> " + v + (v < 0 ? " err=" + Marshal.GetLastWin32Error() : ""));
                        if (v >= 0)
                        {
                            target = pm.hPhysicalMonitor;
                            opened = true;
                            return true;
                        }
                    }
                }

                Dbg.W("Open: FAILED");
                return false;
            }
        }

        static void CloseLocked()
        {
            target = IntPtr.Zero;
            opened = false;
            if (buf == IntPtr.Zero) return;
            try { DestroyPhysicalMonitors(bufCount, buf); } catch { }
            try { Marshal.FreeHGlobal(buf); } catch { }
            buf = IntPtr.Zero;
            bufCount = 0;
        }

        public static void Close() { lock (gate) { CloseLocked(); } }

        // Retries the transient failures, then re-acquires handles once in case
        // the display went to sleep / was replugged / changed resolution.
        public static int Read(byte code, byte probeVcp)
        {
            lock (gate)
            {
                for (int i = 0; opened && i < 4; i++)
                {
                    int v = Raw(target, code);
                    if (v >= 0) return v;
                    Thread.Sleep(40);   // DDC needs a breather between transactions
                }
            }
            Dbg.W("  Read(" + code.ToString("X2") + ") retries exhausted, reopening");
            if (!Open(probeVcp)) return -1;
            lock (gate)
            {
                return opened ? Raw(target, code) : -1;
            }
        }

        // NOTE: SetVCPFeature returns true even when the monitor silently ignores
        // an out-of-range value, so callers must clamp before calling.
        public static bool Write(byte code, int val, byte probeVcp)
        {
            lock (gate)
            {
                if (opened && SetVCPFeature(target, code, (uint)val)) return true;
            }
            if (!Open(probeVcp)) return false;
            lock (gate)
            {
                return opened && SetVCPFeature(target, code, (uint)val);
            }
        }
    }

    // ================= channel model =================
    class Channel
    {
        public string Label;
        public byte Vcp;
        public int Min;
        public int Max;
        public int GetDiv;   // read reports the written value multiplied by this

        public Channel(string label, byte vcp, int min, int max, int div)
        { Label = label; Vcp = vcp; Min = min; Max = max; GetDiv = div; }
    }

    // ================= custom slider =================
    class Slider : Control
    {
        public int Min = 0, Max = 100;
        int _value = 50;
        bool _drag, _hot;
        public float S = 1f;

        public event EventHandler ValueChanged;
        public event EventHandler ValueCommitted;

        public Slider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int v = value < Min ? Min : (value > Max ? Max : value);
                if (v == _value) return;
                _value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        public void SetValueSilent(int v)
        {
            _value = v < Min ? Min : (v > Max ? Max : v);
            Invalidate();
        }

        int KnobR { get { return (int)Math.Round(8 * S); } }
        int TrackL { get { return KnobR + 1; } }
        int TrackR { get { return Width - KnobR - 1; } }

        int PosOf(int val)
        {
            if (Max <= Min) return TrackL;
            double t = (double)(val - Min) / (Max - Min);
            return TrackL + (int)Math.Round(t * (TrackR - TrackL));
        }

        int ValueAt(int x)
        {
            if (TrackR <= TrackL) return Min;
            double t = (double)(x - TrackL) / (TrackR - TrackL);
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return Min + (int)Math.Round(t * (Max - Min));
        }

        // Only a band around the track reacts, so a stray click in the row margin does nothing.
        bool InBand(Point p)
        {
            int cy = Height / 2;
            int band = (int)Math.Round(13 * S);
            return Math.Abs(p.Y - cy) <= band && p.X >= 0 && p.X <= Width;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !InBand(e.Location)) return;
            _drag = true;
            Capture = true;
            Value = ValueAt(e.X);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag) { Value = ValueAt(e.X); return; }
            bool h = InBand(e.Location);
            if (h != _hot) { _hot = h; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!_drag) return;
            _drag = false;
            Capture = false;
            Invalidate();
            if (ValueCommitted != null) ValueCommitted(this, EventArgs.Empty);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hot) { _hot = false; Invalidate(); }
        }

        public void Wheel(int delta)
        {
            int steps = delta / 120;
            if (steps == 0) steps = delta > 0 ? 1 : -1;
            Value = _value + steps;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            int cy = Height / 2;
            int th = (int)Math.Round(6 * S);
            int px = PosOf(_value);

            using (SolidBrush b = new SolidBrush(T.Track))
                Gfx.Round(g, b, TrackL, cy - th / 2, TrackR - TrackL, th, th);
            using (SolidBrush b = new SolidBrush((_hot || _drag) ? T.FillHot : T.Fill))
                Gfx.Round(g, b, TrackL, cy - th / 2, Math.Max(th, px - TrackL), th, th);

            int r = KnobR + ((_hot || _drag) ? (int)Math.Round(1.5 * S) : 0);
            using (SolidBrush sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                g.FillEllipse(sh, px - r, cy - r + 1, r * 2, r * 2);
            using (SolidBrush b = new SolidBrush(T.Knob))
                g.FillEllipse(b, px - r, cy - r, r * 2, r * 2);
        }
    }

    // ================= small flat button =================
    class Btn : Control
    {
        bool _hot;
        public int Flash;
        public event MouseEventHandler Clicked;

        public Btn()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.X < 0 || e.Y < 0 || e.X > Width || e.Y > Height) return;
            if (Clicked != null) Clicked(this, e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent == null ? T.Bg : Parent.BackColor);
            Color bg = Flash > 0 ? T.Fill : (_hot ? T.BtnHot : T.BtnBg);
            using (SolidBrush b = new SolidBrush(bg))
                Gfx.Round(g, b, 0, 0, Width, Height, (int)Math.Round(Height * 0.45));
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height),
                Flash > 0 ? Color.White : T.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // ================= main form =================
    class MainForm : Form, IMessageFilter
    {
        // Verified on this panel (LG UN700):
        //   0x10 brightness   write 0-100, read 1:1
        //   0x12 contrast     write 0-100, read 1:1
        //   0xF9 black stab.  write 0-20,  read x5   <- vendor specific
        static readonly Channel[] CH = new Channel[] {
            new Channel("明るさ",                 0x10, 0, 100, 1),
            new Channel("コントラスト",           0x12, 0, 100, 1),
            new Channel("ブラックスタビライザー", 0xF9, 0,  20, 5)
        };
        const byte ProbeVcp = 0xF9;

        const int N = 3;
        float S = 1f;
        Slider[] sl = new Slider[N];
        Label[] lblName = new Label[N];
        Label[] lblVal = new Label[N];
        Btn[] presets = new Btn[3];
        Panel header;
        Label title;
        Btn closeBtn, pinBtn;
        System.Windows.Forms.Timer flashTimer;

        volatile string status = "init";
        volatile bool alive = true;
        volatile bool refreshWanted;

        readonly object gate = new object();
        int[] target = new int[N];
        int[] applied = new int[N];
        bool[] dirty = new bool[N];
        volatile bool[] touched = new bool[N];
        AutoResetEvent signal = new AutoResetEvent(false);
        Thread worker;
        volatile bool closing, workerDone;

        Cfg cfg;
        int[,] presetVals = new int[3, N];
        static readonly string[] PresetNames = new string[] { "弱", "中", "強" };

        bool moving;
        Point moveOrigin;

        public MainForm()
        {
            cfg = Cfg.Load();
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) S = g.DpiX / 96f;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = T.Bg;
            Text = "OSCV";
            Font = new Font("Yu Gothic UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

            for (int i = 0; i < 3; i++)
                for (int j = 0; j < N; j++)
                    presetVals[i, j] = cfg.Preset(i, j);

            BuildUi();

            TopMost = cfg.Pin;
            UpdatePin();

            Point p = new Point(cfg.X, cfg.Y);
            if (cfg.X == int.MinValue || !OnAScreen(p, Size)) p = DefaultPos();
            Location = p;

            // Seeded from last session; the worker replaces these ~200ms later.
            for (int i = 0; i < N; i++)
            {
                int v = cfg.Last(i, CH[i].Min, CH[i].Max);
                sl[i].SetValueSilent(v);
                target[i] = v;
                applied[i] = v;
                lblVal[i].Text = v.ToString(CultureInfo.InvariantCulture);
            }

            flashTimer = new System.Windows.Forms.Timer();
            flashTimer.Interval = 90;
            flashTimer.Tick += OnFlash;
            flashTimer.Start();

            Application.AddMessageFilter(this);

            worker = new Thread(WorkerLoop);
            worker.IsBackground = false;
            worker.Start();
        }

        Point DefaultPos()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            return new Point(wa.Right - Width - Sc(24), wa.Bottom - Height - Sc(24));
        }

        static bool OnAScreen(Point p, Size sz)
        {
            Rectangle r = new Rectangle(p, sz);
            foreach (Screen s in Screen.AllScreens)
                if (s.WorkingArea.IntersectsWith(r)) return true;
            return false;
        }

        int Sc(double v) { return (int)Math.Round(v * S); }

        void BuildUi()
        {
            int W = Sc(280);
            int pad = Sc(15);
            int hh = Sc(26);

            header = new Panel();
            header.BackColor = T.Header;
            header.Bounds = new Rectangle(0, 0, W, hh);
            header.MouseDown += HeaderDown;
            header.MouseMove += HeaderMove;
            header.MouseUp += HeaderUp;
            header.Paint += HeaderPaint;
            Controls.Add(header);

            title = new Label();
            title.AutoSize = false;
            title.Bounds = new Rectangle(Sc(24), 0, Sc(130), hh);
            title.TextAlign = ContentAlignment.MiddleLeft;
            title.ForeColor = T.TextDim;
            title.BackColor = Color.Transparent;
            title.Text = "OSCV v" + App.Version.ToString(CultureInfo.InvariantCulture);
            title.Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold);
            title.MouseDown += HeaderDown;
            title.MouseMove += HeaderMove;
            title.MouseUp += HeaderUp;
            header.Controls.Add(title);

            pinBtn = new Btn();
            pinBtn.Bounds = new Rectangle(W - Sc(56), Sc(4), Sc(24), hh - Sc(8));
            pinBtn.Font = new Font(Font.FontFamily, 8f);
            pinBtn.Clicked += OnPin;
            header.Controls.Add(pinBtn);

            closeBtn = new Btn();
            closeBtn.Bounds = new Rectangle(W - Sc(30), Sc(4), Sc(24), hh - Sc(8));
            closeBtn.Text = "✕";
            closeBtn.Font = new Font(Font.FontFamily, 8f);
            closeBtn.Clicked += delegate { Close(); };
            header.Controls.Add(closeBtn);

            int y = hh + Sc(10);
            for (int i = 0; i < N; i++)
            {
                int idx = i;

                lblName[i] = new Label();
                lblName[i].AutoSize = false;
                lblName[i].Bounds = new Rectangle(pad, y, W - pad * 2 - Sc(46), Sc(17));
                lblName[i].ForeColor = T.TextDim;
                lblName[i].Text = CH[i].Label;
                lblName[i].TextAlign = ContentAlignment.MiddleLeft;
                lblName[i].Font = new Font(Font.FontFamily, 8.5f);
                Controls.Add(lblName[i]);

                lblVal[i] = new Label();
                lblVal[i].AutoSize = false;
                lblVal[i].Bounds = new Rectangle(W - pad - Sc(46), y, Sc(46), Sc(17));
                lblVal[i].ForeColor = T.Text;
                lblVal[i].TextAlign = ContentAlignment.MiddleRight;
                lblVal[i].Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold);
                Controls.Add(lblVal[i]);

                y += Sc(18);

                sl[i] = new Slider();
                sl[i].S = S;
                sl[i].Min = CH[i].Min;
                sl[i].Max = CH[i].Max;
                sl[i].BackColor = T.Bg;
                sl[i].Bounds = new Rectangle(pad - Sc(3), y, W - (pad - Sc(3)) * 2, Sc(28));
                sl[i].ValueChanged += delegate { OnLive(idx); };
                sl[i].ValueCommitted += delegate { Commit(idx); };
                Controls.Add(sl[i]);

                y += Sc(28) + Sc(10);
            }

            y += Sc(2);
            int bw = (W - pad * 2 - Sc(12)) / 3;
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                presets[i] = new Btn();
                presets[i].Bounds = new Rectangle(pad + i * (bw + Sc(6)), y, bw, Sc(24));
                presets[i].Text = PresetNames[i];
                presets[i].Font = new Font(Font.FontFamily, 8.5f);
                presets[i].Clicked += delegate(object s, MouseEventArgs e) { OnPreset(idx, e); };
                Controls.Add(presets[i]);
            }
            y += Sc(24) + Sc(13);

            ClientSize = new Size(W, y);
        }

        void HeaderPaint(object sender, PaintEventArgs e)
        {
            Color c = T.TextDim;
            string st = status;
            if (st == "ok") c = T.Ok;
            else if (st == "busy") c = T.Busy;
            else if (st == "err") c = T.Err;

            int d = Sc(7), x = Sc(10), yy = (header.Height - d) / 2;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(c)) e.Graphics.FillEllipse(b, x, yy, d, d);
        }

        void UpdatePin()
        {
            pinBtn.Text = TopMost ? "◆" : "◇";
            pinBtn.Invalidate();
        }

        void OnPin(object s, MouseEventArgs e)
        {
            TopMost = !TopMost;
            cfg.Pin = TopMost;
            UpdatePin();
        }

        // ---------- window dragging ----------
        void HeaderDown(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            moving = true;
            moveOrigin = Cursor.Position;
            moveOrigin.Offset(-Location.X, -Location.Y);
        }

        void HeaderMove(object s, MouseEventArgs e)
        {
            if (!moving) return;
            Point p = Cursor.Position;
            Location = new Point(p.X - moveOrigin.X, p.Y - moveOrigin.Y);
        }

        void HeaderUp(object s, MouseEventArgs e) { moving = false; }

        // ---------- wheel routing: hover is enough, no click required ----------
        const int WM_MOUSEWHEEL = 0x020A;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;
            Slider s = Control.FromHandle(WindowFromPoint(Cursor.Position)) as Slider;
            if (s == null) return false;
            for (int i = 0; i < N; i++)
            {
                if (!ReferenceEquals(sl[i], s)) continue;
                int delta = unchecked((short)(((long)m.WParam >> 16) & 0xFFFF));
                s.Wheel(delta);
                return true;
            }
            return false;
        }

        [DllImport("user32.dll")]
        static extern IntPtr WindowFromPoint(Point p);

        // ---------- value flow ----------
        // A DDC write is ~60ms, so every change is pushed immediately and the
        // worker coalesces to the newest value. The panel tracks the drag live.
        void OnLive(int i)
        {
            touched[i] = true;
            lblVal[i].Text = sl[i].Value.ToString(CultureInfo.InvariantCulture);
            Commit(i);
        }

        void Commit(int i)
        {
            int v = sl[i].Value;
            lock (gate)
            {
                if (target[i] == v && !dirty[i]) return;
                target[i] = v;
                dirty[i] = true;
            }
            signal.Set();
        }

        // Re-read when the window is brought forward, so changes made with the
        // monitor's own buttons show up.
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            refreshWanted = true;
            signal.Set();
        }

        void OnPreset(int p, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                for (int i = 0; i < N; i++) presetVals[p, i] = sl[i].Value;
                cfg.SavePreset(p, presetVals);
                presets[p].Flash = 5;
                presets[p].Invalidate();
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            for (int i = 0; i < N; i++)
            {
                sl[i].Value = presetVals[p, i];
                touched[i] = true;
                lblVal[i].Text = sl[i].Value.ToString(CultureInfo.InvariantCulture);
                Commit(i);
            }
            presets[p].Flash = 3;
            presets[p].Invalidate();
        }

        void OnFlash(object s, EventArgs e)
        {
            for (int i = 0; i < 3; i++)
                if (presets[i].Flash > 0) { presets[i].Flash--; presets[i].Invalidate(); }
        }

        void SetStatus(string st)
        {
            if (status == st) return;
            status = st;
            try { BeginInvoke((MethodInvoker)delegate { header.Invalidate(); }); } catch { }
        }

        // ---------- background worker ----------
        bool ReadAll()
        {
            bool healthy = false;
            for (int i = 0; i < N; i++)
            {
                if (HasPending()) return true;   // user is driving; their value wins

                int raw = Ddc.Read(CH[i].Vcp, ProbeVcp);
                Dbg.W("ReadAll " + CH[i].Label + " vcp" + CH[i].Vcp.ToString("X2") + " -> " + raw);
                if (raw < 0) continue;
                healthy = true;

                int v = (int)Math.Round((double)raw / CH[i].GetDiv);
                if (v < CH[i].Min) v = CH[i].Min;
                if (v > CH[i].Max) v = CH[i].Max;

                int idx = i, vv = v;
                lock (gate)
                {
                    applied[idx] = vv;
                    if (!touched[idx]) target[idx] = vv;
                }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (touched[idx]) return;
                        sl[idx].SetValueSilent(vv);
                        lblVal[idx].Text = vv.ToString(CultureInfo.InvariantCulture);
                    });
                }
                catch { }
            }
            return healthy;
        }

        void WorkerLoop()
        {
            SetStatus("busy");
            SetStatus(Ddc.Open(ProbeVcp) && ReadAll() ? "ok" : "err");

            int lastHeal = Environment.TickCount;

            while (alive)
            {
                int ch = -1, val = 0;
                lock (gate)
                {
                    for (int i = 0; i < N; i++)
                    {
                        if (!dirty[i]) continue;
                        if (target[i] != applied[i]) { ch = i; val = target[i]; break; }
                        dirty[i] = false;
                    }
                }

                if (ch < 0)
                {
                    if (closing) break;

                    if (refreshWanted)
                    {
                        refreshWanted = false;
                        for (int i = 0; i < N; i++) touched[i] = false;
                        if (ReadAll()) SetStatus("ok");
                    }
                    else if (status == "err" && Environment.TickCount - lastHeal > 5000)
                    {
                        // cable replugged / monitor woke up / DDC re-enabled
                        lastHeal = Environment.TickCount;
                        SetStatus(Ddc.Open(ProbeVcp) && ReadAll() ? "ok" : "err");
                    }

                    signal.WaitOne(500);
                    continue;
                }

                // Clamp: an out-of-range VCP write is accepted and then ignored.
                if (val < CH[ch].Min) val = CH[ch].Min;
                if (val > CH[ch].Max) val = CH[ch].Max;

                bool ok = Ddc.Write(CH[ch].Vcp, val, ProbeVcp);

                lock (gate)
                {
                    if (ok) applied[ch] = val;
                    if (target[ch] == val) dirty[ch] = false;
                }
                SetStatus(ok ? "ok" : "err");
            }

            Ddc.Close();
            workerDone = true;
            try { BeginInvoke((MethodInvoker)delegate { Close(); }); } catch { }
        }

        bool HasPending()
        {
            lock (gate)
            {
                for (int i = 0; i < N; i++) if (dirty[i]) return true;
            }
            return false;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            cfg.X = Location.X;
            cfg.Y = Location.Y;
            cfg.Pin = TopMost;
            for (int i = 0; i < N; i++) cfg.SetLast(i, sl[i].Value);
            cfg.Save();

            if (!workerDone && HasPending())
            {
                e.Cancel = true;
                closing = true;
                Hide();
                signal.Set();
                return;
            }

            alive = false;
            closing = true;
            signal.Set();
            base.OnFormClosing(e);
        }

        [STAThread]
        static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
    }

    // ================= tiny ini =================
    class Cfg
    {
        Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string path;

        static readonly int[,] DefPreset = new int[3, 3] {
            { 30, 10, 20 },   // 弱
            { 40, 40, 16 },   // 中
            { 65, 75, 13 }    // 強
        };

        public static Cfg Load()
        {
            Cfg c = new Cfg();
            c.path = PickPath();
            try
            {
                if (File.Exists(c.path))
                {
                    foreach (string line in File.ReadAllLines(c.path, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        c.d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch { }
            return c;
        }

        static string PickPath()
        {
            try
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                string p = Path.Combine(dir, "oscv.ini");
                File.AppendAllText(p, "");
                return p;
            }
            catch
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "oscv");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "oscv.ini");
            }
        }

        int GetI(string k, int def)
        {
            string s;
            if (!d.TryGetValue(k, out s)) return def;
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        public int X
        {
            get { return GetI("x", int.MinValue); }
            set { d["x"] = value.ToString(CultureInfo.InvariantCulture); }
        }

        public int Y
        {
            get { return GetI("y", int.MinValue); }
            set { d["y"] = value.ToString(CultureInfo.InvariantCulture); }
        }

        public bool Pin
        {
            get { return GetI("pin", 1) != 0; }
            set { d["pin"] = value ? "1" : "0"; }
        }

        public int Last(int i, int min, int max)
        {
            int v = GetI("last" + i, (min + max) / 2);
            return v < min ? min : (v > max ? max : v);
        }

        public void SetLast(int i, int v)
        {
            d["last" + i] = v.ToString(CultureInfo.InvariantCulture);
        }

        public int Preset(int p, int i) { return GetI("preset" + p + "_" + i, DefPreset[p, i]); }

        public void SavePreset(int p, int[,] vals)
        {
            for (int i = 0; i < 3; i++)
                d["preset" + p + "_" + i] = vals[p, i].ToString(CultureInfo.InvariantCulture);
            Save();
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in d)
                    sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }
}
