using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
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
        public const int Version = 8;
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
        // 項目名。暗い部屋で読めるよう背景 Bg に対して約 9.7:1 を確保する
        // (以前の TextDim は 4.4:1 で AA にも届いていなかった)
        public static Color Label   = Color.FromArgb(190, 198, 210);
        // ヘッダーのタイトルなど、内容ではない飾りだけに使う
        public static Color TextDim = Color.FromArgb(124, 132, 144);
        public static Color BtnBg   = Color.FromArgb(44, 48, 54);
        public static Color BtnHot  = Color.FromArgb(64, 70, 79);
        // 選ばれている画面のボタン。押し込まれた状態が一目で分かる程度の青
        public static Color BtnSel  = Color.FromArgb(46, 90, 145);
        // その画面が対応していない項目 (スライダーを沈める色)
        public static Color Off     = Color.FromArgb(40, 43, 48);
        public static Color OffKnob = Color.FromArgb(78, 84, 93);
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

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;   // \\.\DISPLAY2
        }

        delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr data);

        [DllImport("user32.dll")]
        static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetMonitorInfo(IntPtr hmon, ref MONITORINFOEX mi);

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

        // 開いた 1 台ぶん。ハンドルは buf の中身なので、解放は CloseLocked がまとめて行う
        class Phys
        {
            public IntPtr Handle;
            public string Desc;
            public int Num;      // \\.\DISPLAY2 の 2 = Windows の画面番号
        }

        static List<IntPtr> bufs = new List<IntPtr>();      // native PHYSICAL_MONITOR[] we must destroy
        static List<uint> bufCounts = new List<uint>();
        static List<Phys> phys = new List<Phys>();
        static IntPtr target = IntPtr.Zero;
        // 0 is a legitimate physical-monitor handle, so openness needs its own flag.
        static bool opened;
        static int curNum = -1;    // いま掴んでいる画面番号
        static int wantNum = -1;   // 選ばれている画面番号 (-1 = おまかせ)
        static readonly object gate = new object();

        public static bool IsOpen { get { return opened; } }
        public static int CurrentNum { get { return curNum; } }

        // "\\.\DISPLAY2" -> 2。取れなければ -1
        public static int NumOf(string device)
        {
            if (device == null) return -1;
            int i = device.Length;
            while (i > 0 && device[i - 1] >= '0' && device[i - 1] <= '9') i--;
            if (i >= device.Length) return -1;
            int n;
            return int.TryParse(device.Substring(i), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out n) ? n : -1;
        }

        // その画面が DDC に答えるか。開いてあるハンドルで読むだけなので、
        // いま掴んでいる板は動かさない。
        // codes のどれか 1 つでも答えれば生きているとみなす。読み取りは 2.5% で
        // 空振りするが、3 つ続けて空振りする確率は 1/64000 なので、
        // 複数のコードを試すこと自体がリトライになっている
        public static bool Alive(int num, byte[] codes)
        {
            lock (gate)
            {
                for (int i = 0; i < phys.Count; i++)
                {
                    if (phys[i].Num != num) continue;
                    for (int c = 0; c < codes.Length; c++)
                    {
                        if (Raw(phys[i].Handle, codes[c]) < 0) continue;
                        Dbg.W("  display" + num + " alive (vcp" + codes[c].ToString("X2") + ")");
                        return true;
                    }
                }
                Dbg.W("  display" + num + " does not answer");
                return false;
            }
        }

        // 1 回だけ読む。リトライも開き直しもしない (書いた値が入ったかの確認用)
        public static int Peek(byte code)
        {
            lock (gate) { return opened ? Raw(target, code) : -1; }
        }

        // A single read costs ~60ms and fails outright maybe 1 time in 40.
        static int Raw(IntPtr h, byte code)
        {
            uint cur, max;
            if (!GetVCPFeatureAndVCPFeatureReply(h, code, IntPtr.Zero, out cur, out max)) return -1;
            return (int)cur;
        }

        // want = 操作したい画面番号。-1 なら、ベンダー固有コードに答える板 (= LG) を選ぶ。
        // 番号を指定したのにその画面が答えないときは、黙って別の画面を掴まない。
        // 掴んだら、指定と違う板をいじってしまうより赤ランプの方がましなので失敗させる。
        public static bool Open(byte probeVcp, int want)
        {
            lock (gate)
            {
                wantNum = want;
                return OpenLocked(probeVcp);
            }
        }

        // いま選ばれている画面のまま開き直す (寝起き・抜き差し・一時的な失敗から復帰する)
        public static bool Reopen(byte probeVcp)
        {
            lock (gate) { return OpenLocked(probeVcp); }
        }

        static bool OpenLocked(byte probeVcp)
        {
            CloseLocked();

            List<IntPtr> hmons = new List<IntPtr>();
            MonitorEnumProc cb = delegate(IntPtr hm, IntPtr hdc, IntPtr rc, IntPtr d)
            { hmons.Add(hm); return true; };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);

            Dbg.W("Open: hmon=" + hmons.Count + " want=" + wantNum);
            if (hmons.Count == 0) return false;

            // 画面ごとに物理モニターを開く。1 台目で止めない (v3 まではここで
            // 止めていたので、2 台目以降が存在しないのと同じだった)。
            // 同時に開いても、ハンドルは 1 台ずつ別の値が返る (実測: 0 と 2)
            int stride = Marshal.SizeOf(typeof(PHYSICAL_MONITOR));
            for (int i = 0; i < hmons.Count; i++)
            {
                MONITORINFOEX mi = new MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                mi.szDevice = "";
                int num = GetMonitorInfo(hmons[i], ref mi) ? NumOf(mi.szDevice) : -1;

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
                bufs.Add(b);
                bufCounts.Add(n);

                for (int j = 0; j < n; j++)
                {
                    PHYSICAL_MONITOR pm = (PHYSICAL_MONITOR)Marshal.PtrToStructure(
                        (IntPtr)(b.ToInt64() + (long)j * stride), typeof(PHYSICAL_MONITOR));
                    Phys p = new Phys();
                    p.Handle = pm.hPhysicalMonitor;
                    p.Desc = pm.szDescription;
                    p.Num = num;
                    phys.Add(p);
                    Dbg.W("  found display" + num + " '" + p.Desc + "' h=" + p.Handle.ToInt64());
                }
            }
            Dbg.W("Open: physical=" + phys.Count);
            if (phys.Count == 0) return false;

            // 指定が無ければ、ベンダー固有コードに答える板を優先する。
            // 2 台つないだ机では、それが LG の方になる
            for (int pass = 0; pass < 2; pass++)
            {
                byte code = pass == 0 ? probeVcp : (byte)0x10;
                for (int i = 0; i < phys.Count; i++)
                {
                    if (wantNum >= 0 && phys[i].Num != wantNum) continue;
                    int v = Raw(phys[i].Handle, code);
                    Dbg.W("  probe display" + phys[i].Num + " h=" + phys[i].Handle.ToInt64() +
                          " vcp" + code.ToString("X2") + " -> " + v +
                          (v < 0 ? " err=" + Marshal.GetLastWin32Error() : ""));
                    if (v >= 0)
                    {
                        target = phys[i].Handle;
                        curNum = phys[i].Num;
                        opened = true;
                        return true;
                    }
                }
            }

            Dbg.W("Open: FAILED");
            return false;
        }

        static void CloseLocked()
        {
            target = IntPtr.Zero;
            opened = false;
            curNum = -1;
            phys.Clear();
            for (int i = 0; i < bufs.Count; i++)
            {
                try { DestroyPhysicalMonitors(bufCounts[i], bufs[i]); } catch { }
                try { Marshal.FreeHGlobal(bufs[i]); } catch { }
            }
            bufs.Clear();
            bufCounts.Clear();
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
            if (!Reopen(probeVcp)) return -1;
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
            if (!Reopen(probeVcp)) return false;
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

            // その画面が持っていない項目は沈めて描く (触っても何も起きないことを見せる)
            using (SolidBrush b = new SolidBrush(Enabled ? T.Track : T.Off))
                Gfx.Round(g, b, TrackL, cy - th / 2, TrackR - TrackL, th, th);
            if (Enabled)
                using (SolidBrush b = new SolidBrush((_hot || _drag) ? T.FillHot : T.Fill))
                    Gfx.Round(g, b, TrackL, cy - th / 2, Math.Max(th, px - TrackL), th, th);

            int r = KnobR + ((_hot || _drag) ? (int)Math.Round(1.5 * S) : 0);
            if (Enabled)
                using (SolidBrush sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                    g.FillEllipse(sh, px - r, cy - r + 1, r * 2, r * 2);
            using (SolidBrush b = new SolidBrush(Enabled ? T.Knob : T.OffKnob))
                g.FillEllipse(b, px - r, cy - r, r * 2, r * 2);
        }
    }

    // ================= small flat button =================
    class Btn : Control
    {
        bool _hot;
        public int Flash;
        public bool Selected;   // 画面切り替えボタンで、今いじっている画面を示す
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
            Color bg = !Enabled ? T.Off
                     : (Flash > 0 ? T.Fill : (_hot ? T.BtnHot : (Selected ? T.BtnSel : T.BtnBg)));
            using (SolidBrush b = new SolidBrush(bg))
                Gfx.Round(g, b, 0, 0, Width, Height, (int)Math.Round(Height * 0.45));
            DrawGlyph(g, !Enabled ? T.OffKnob : (Flash > 0 ? Color.White : T.Text));
        }

        protected virtual void DrawGlyph(Graphics g, Color fg)
        {
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // ================= pin toggle =================
    // 押しピンの絵で最前面固定の状態を示す。
    //   留めている : 白く塗って斜めに倒す
    //   外している : 輪郭だけにして真横に倒す
    class PinBtn : Btn
    {
        public bool Pinned;

        // 24 単位四方の中に、針を下に向けた押しピンの輪郭を 1 本の閉じた線で組む。
        // 頭・胴・つば・針を別々の図形で重ねると、輪郭表示のときに内側の線が
        // 出てしまうので、最初から 1 つの多角形にしてある。
        static readonly PointF[] Shape = new PointF[] {
            new PointF( 8.5f,  2.0f), new PointF(15.5f,  2.0f),   // 頭
            new PointF(15.5f,  5.5f), new PointF(16.5f, 14.0f),   // 胴 (下ほど広がる)
            new PointF(18.0f, 14.0f), new PointF(18.0f, 16.5f),   // つば 右
            new PointF(12.9f, 16.5f), new PointF(12.0f, 22.0f),   // 針
            new PointF(11.1f, 16.5f),
            new PointF( 6.0f, 16.5f), new PointF( 6.0f, 14.0f),   // つば 左
            new PointF( 7.5f, 14.0f), new PointF( 8.5f,  5.5f)
        };

        protected override void DrawGlyph(Graphics g, Color fg)
        {
            float scale = Math.Min(Width, Height) / 24f;
            GraphicsState st = g.Save();
            g.TranslateTransform(Width / 2f, Height / 2f);
            g.RotateTransform(Pinned ? -45f : -90f);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-12f, -12f);

            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddPolygon(Shape);
                if (Pinned)
                {
                    using (SolidBrush b = new SolidBrush(Color.White)) g.FillPath(b, p);
                }
                else
                {
                    // 線幅も一緒に拡縮されるので、実寸で約 1.2px になるよう割り戻す
                    using (Pen pen = new Pen(fg, 1.2f / scale)) g.DrawPath(pen, p);
                }
            }
            g.Restore(st);
        }
    }

    // ================= main form =================
    class MainForm : Form, IMessageFilter
    {
        // Verified on this panel (LG UN700):
        //   0x10 brightness   write 0-100, read 1:1
        //   0x12 contrast     write 0-100, read 1:1
        //   0xF9 black stab.  write 0-20,  read x5   <- vendor specific
        internal static readonly Channel[] CH = new Channel[] {
            new Channel("明るさ",                 0x10, 0, 100, 1),
            new Channel("コントラスト",           0x12, 0, 100, 1),
            new Channel("ブラックスタビライザー", 0xF9, 0,  20, 5)
        };
        internal const byte ProbeVcp = 0xF9;

        // 画面の生死判定に使う VCP。扱っている項目をそのまま使う。
        // どれにも答えない板は、電源が落ちているか DDC で触れない板なので選ばせない
        static readonly byte[] ProbeCodes = MakeProbeCodes();

        static byte[] MakeProbeCodes()
        {
            byte[] b = new byte[N];
            for (int i = 0; i < N; i++) b[i] = CH[i].Vcp;
            return b;
        }

        internal const int N = 3;
        float S = 1f;
        Slider[] sl = new Slider[N];
        Label[] lblName = new Label[N];
        Label[] lblVal = new Label[N];
        Btn[] presets = new Btn[3];
        Btn[] monBtns = new Btn[0];   // 画面の切り替え (画面が 2 つ以上あるときだけ出す)
        int[] monNums = new int[0];   // 上のボタンに対応する Windows の画面番号
        bool[] monLive = new bool[0]; // DDC に答えるか。答えない画面は非活性にする
        string[] monTipText = new string[0];
        ToolTip tip;                  // 画面ボタンと「起動時」で共用

        // その画面を最初に読んだときの値。触ったあとに戻すためのもの。
        // ini には残さない (起動し直せば、その時点の実値がまた入るため)
        Btn bootBtn;
        int[] boot = new int[N];
        volatile bool[] bootGot = new bool[N];
        Panel header;
        Label title;
        Btn closeBtn;
        PinBtn pinBtn;
        System.Windows.Forms.Timer flashTimer;

        volatile string status = "init";
        volatile bool alive = true;
        volatile bool refreshWanted;

        // 画面の切り替え。UI は番号を置いてワーカーを起こすだけで、
        // 開き直しと読み直しはワーカー側でやる (DDC は 1 回 60ms かかる)
        const int NoSwitch = -2;
        volatile int wantMon = NoSwitch;
        int curMon = -1;                 // いま操作している画面番号 (-1 = おまかせ)
        readonly int startMon;           // 起動時に開く画面。ワーカーが読む
        // その画面が持っている項目か。持っていない例: LG 以外の板の 0xF9
        volatile bool[] avail = new bool[N] { true, true, true };

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
        internal static readonly string[] PresetNames = new string[] { "弱", "中", "強" };

        bool moving;
        Point moveOrigin;

        public MainForm()
        {
            cfg = Cfg.Load();

            // 前回いじっていた画面を引き継ぐ。抜かれていたら、おまかせに戻す
            // (指定を残したままだと、その画面が見つからず赤ランプのままになる)
            curMon = cfg.Display;
            if (curMon >= 0 && !DisplayExists(curMon)) curMon = -1;
            cfg.Prefix = PrefixOf(curMon);
            startMon = curMon;

            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) S = g.DpiX / 96f;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = T.Bg;
            Text = "OSCV";
            SetAppIcon();
            Font = new Font("Yu Gothic UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

            for (int i = 0; i < 3; i++)
                for (int j = 0; j < N; j++)
                    presetVals[i, j] = cfg.Preset(i, j);

            BuildUi();
            MarkMonButtons();

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

            // ワーカーからの BeginInvoke は、窓のハンドルが出来ていないと例外になる。
            // ハンドルが出来るのは Application.Run の中なので、起動直後の 1 回目
            // (実値の表示・掴んだ画面番号の確定・「起動時」の有効化) が丸ごと
            // 捨てられていた。ワーカーを起こす前にハンドルを作っておく
            IntPtr hwnd = Handle;
            Dbg.W("form handle=" + hwnd.ToInt64());

            worker = new Thread(WorkerLoop);
            worker.IsBackground = false;
            worker.Start();
        }

        // アイコンは exe に埋め込んだ ico をそのまま Icon に渡す。ico のまま渡せば
        // タスクバー用の 16px を WinForms がその大きさの絵から選ぶ (Bitmap 経由や
        // ExtractAssociatedIcon だと 32px を縮めるだけになって滲む)。
        // 埋め込みは build.ps1 の /resource:assets\oscv.ico,oscv.ico で行う。
        void SetAppIcon()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("oscv.ico"))
                    if (s != null) Icon = new Icon(s);
            }
            catch { }  // アイコンが無くても起動はできる
        }

        // ---------- 画面の選択 ----------

        // 左から右 (同じ位置なら上から下) に並べる。ボタンの並び順が机の上の
        // 並びと一致していれば、番号を覚えていなくても見当がつく
        static Screen[] Displays()
        {
            List<Screen> l = new List<Screen>(Screen.AllScreens);
            l.Sort(delegate(Screen a, Screen b)
            {
                if (a.Bounds.X != b.Bounds.X) return a.Bounds.X - b.Bounds.X;
                return a.Bounds.Y - b.Bounds.Y;
            });
            return l.ToArray();
        }

        static bool DisplayExists(int num)
        {
            foreach (Screen s in Screen.AllScreens)
                if (Ddc.NumOf(s.DeviceName) == num) return true;
            return false;
        }

        // ini のキーを画面ごとに分ける。番号が決まらないうち (おまかせ) は
        // v3 までのキーをそのまま使う
        static string PrefixOf(int num)
        {
            return num < 0 ? "" : "m" + num.ToString(CultureInfo.InvariantCulture) + ".";
        }

        // 最終値もプリセットも画面ごとに持つ。板が違えば同じ「強」でも
        // ちょうどいい値は違うため
        void UseMonitor(int num)
        {
            curMon = num;
            cfg.Prefix = PrefixOf(num);
            for (int p = 0; p < 3; p++)
                for (int i = 0; i < N; i++) presetVals[p, i] = cfg.Preset(p, i);
            MarkMonButtons();
        }

        void MarkMonButtons()
        {
            for (int i = 0; i < monBtns.Length; i++)
            {
                bool on = monNums[i] == curMon;
                if (monBtns[i].Selected == on) continue;
                monBtns[i].Selected = on;
                monBtns[i].Invalidate();
            }
        }

        // 答えない画面のボタンを非活性にする。電源が落ちている板や、DDC を
        // 通さないアダプタの先の板を選ばせても、赤ランプになるだけなので
        void SetMonLive(int i, bool live)
        {
            monLive[i] = live;
            monBtns[i].Enabled = live;
            monBtns[i].Invalidate();
            tip.SetToolTip(monBtns[i], monTipText[i] + (live ? "" : "  応答なし"));
        }

        // ワーカーから。onlyDead なら、いま死んでいる画面だけ見に行く
        // (生きている画面を毎回叩くと、前面に出すたびに 60ms x 台数を捨てることになる)
        void ProbeDisplays(bool onlyDead)
        {
            for (int i = 0; i < monNums.Length; i++)
            {
                if (onlyDead && monLive[i]) continue;

                // いま掴めている板は、読めている時点で生きている
                bool live = (Ddc.IsOpen && monNums[i] == Ddc.CurrentNum)
                            || Ddc.Alive(monNums[i], ProbeCodes);
                if (live == monLive[i]) continue;

                int idx = i;
                try { BeginInvoke((MethodInvoker)delegate { SetMonLive(idx, live); }); }
                catch { }
            }
        }

        void OnMonitor(int idx, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (!monLive[idx]) return;   // 答えない画面は選ばせない
            int num = monNums[idx];
            if (num == curMon) return;

            // いま表示している値は前の画面のもの。書きかけを捨ててから切り替える
            lock (gate)
            {
                for (int i = 0; i < N; i++)
                {
                    dirty[i] = false;
                    touched[i] = false;
                    applied[i] = -1;   // 実値が読めるまで「不明」。次の書き込みを抑えない
                }
            }

            cfg.Display = num;
            UseMonitor(num);

            // 種は ini の最終値。実値は 200ms 後にワーカーが上書きする
            for (int i = 0; i < N; i++)
            {
                SetAvail(i, true);
                int v = cfg.Last(i, CH[i].Min, CH[i].Max);
                sl[i].SetValueSilent(v);
                lblVal[i].Text = v.ToString(CultureInfo.InvariantCulture);
                lock (gate) target[i] = v;
            }

            bootBtn.Enabled = false;   // 切り替え先の値を読むまでは戻り先が無い
            bootBtn.Invalidate();

            SetStatus("busy");
            wantMon = num;
            signal.Set();
        }

        // 対応していない項目は沈めて触れなくする。書けば必ず失敗するので、
        // 赤ランプを出し続けるよりこの方が正しい
        void SetAvail(int i, bool on)
        {
            avail[i] = on;
            sl[i].Enabled = on;
            sl[i].Invalidate();
            lblName[i].ForeColor = on ? T.Label : T.TextDim;
            lblVal[i].ForeColor = on ? T.Text : T.TextDim;
            if (!on) lblVal[i].Text = "―";
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
            int W = Sc(250);
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
            title.Text = "oscv v" + App.Version.ToString(CultureInfo.InvariantCulture);
            title.Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold);
            title.MouseDown += HeaderDown;
            title.MouseMove += HeaderMove;
            title.MouseUp += HeaderUp;
            header.Controls.Add(title);

            pinBtn = new PinBtn();
            pinBtn.Bounds = new Rectangle(W - Sc(56), Sc(4), Sc(24), hh - Sc(8));
            pinBtn.Clicked += OnPin;
            header.Controls.Add(pinBtn);

            closeBtn = new Btn();
            closeBtn.Bounds = new Rectangle(W - Sc(30), Sc(4), Sc(24), hh - Sc(8));
            closeBtn.Text = "✕";
            closeBtn.Font = new Font(Font.FontFamily, 8f);
            closeBtn.Clicked += delegate { Close(); };
            header.Controls.Add(closeBtn);

            tip = new ToolTip();
            int y = hh + Sc(10);

            // 画面が 2 つ以上あるときだけ切り替えの列を出す。1 つなら v3 までと同じ窓
            Screen[] scr = Displays();
            if (scr.Length > 1)
            {
                Label pick = new Label();
                pick.AutoSize = false;
                pick.Bounds = new Rectangle(pad, y, Sc(34), Sc(24));
                pick.ForeColor = T.Label;
                pick.Text = "画面";
                pick.TextAlign = ContentAlignment.MiddleLeft;
                pick.Font = new Font(Font.FontFamily, 8.5f);
                Controls.Add(pick);

                int bx = pad + Sc(38);
                int gap = Sc(6);
                int bw2 = (W - pad - bx - gap * (scr.Length - 1)) / scr.Length;
                monBtns = new Btn[scr.Length];
                monNums = new int[scr.Length];
                monLive = new bool[scr.Length];
                monTipText = new string[scr.Length];
                for (int i = 0; i < scr.Length; i++)
                {
                    int idx = i;
                    monNums[i] = Ddc.NumOf(scr[i].DeviceName);
                    monLive[i] = true;   // 触れないと分かるまでは押せるままにする
                    monBtns[i] = new Btn();
                    monBtns[i].Bounds = new Rectangle(bx + i * (bw2 + gap), y, bw2, Sc(24));
                    monBtns[i].Text = monNums[i].ToString(CultureInfo.InvariantCulture);
                    monBtns[i].Font = new Font(Font.FontFamily, 8.5f);
                    monBtns[i].Clicked += delegate(object s, MouseEventArgs e) { OnMonitor(idx, e); };
                    // 同じ型の板が 2 枚並ぶと番号だけでは分からないので、大きさも出す
                    monTipText[i] = "ディスプレイ " + monNums[i] + "  " +
                        scr[i].Bounds.Width + " x " + scr[i].Bounds.Height +
                        (scr[i].Primary ? " (メイン)" : "");
                    tip.SetToolTip(monBtns[i], monTipText[i]);
                    Controls.Add(monBtns[i]);
                }
                y += Sc(24) + Sc(12);
            }

            for (int i = 0; i < N; i++)
            {
                int idx = i;

                lblName[i] = new Label();
                lblName[i].AutoSize = false;
                lblName[i].Bounds = new Rectangle(pad, y, W - pad * 2 - Sc(46), Sc(17));
                lblName[i].ForeColor = T.Label;
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
            // 弱 / 中 / 強 と「起動時」で 4 つ。1 行に収める
            int gapp = Sc(6);
            int bw = (W - pad * 2 - gapp * 3) / 4;
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                presets[i] = new Btn();
                presets[i].Bounds = new Rectangle(pad + i * (bw + gapp), y, bw, Sc(24));
                presets[i].Text = PresetNames[i];
                presets[i].Font = new Font(Font.FontFamily, 8.5f);
                presets[i].Clicked += delegate(object s, MouseEventArgs e) { OnPreset(idx, e); };
                Controls.Add(presets[i]);
            }

            // 起動時の値に戻すボタン。値は自動で入るので、右クリックの保存は無い。
            // 読めるまでは押せない
            bootBtn = new Btn();
            bootBtn.Bounds = new Rectangle(pad + 3 * (bw + gapp), y, bw, Sc(24));
            bootBtn.Text = "起動時";
            bootBtn.Font = new Font(Font.FontFamily, 8f);
            bootBtn.Enabled = false;
            bootBtn.Clicked += OnBoot;
            Controls.Add(bootBtn);
            tip.SetToolTip(bootBtn, "この画面を開いたときの値に戻す");
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
            pinBtn.Pinned = TopMost;
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

        // 起動時の値に戻す。押せるのは 1 つでも値を掴めているときだけ
        void OnBoot(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;   // 自動保存なので右クリックは無し

            for (int i = 0; i < N; i++)
            {
                if (!avail[i] || !bootGot[i]) continue;
                sl[i].Value = boot[i];
                touched[i] = true;
                lblVal[i].Text = sl[i].Value.ToString(CultureInfo.InvariantCulture);
                Commit(i);
            }
            bootBtn.Flash = 3;
            bootBtn.Invalidate();
        }

        // ワーカーが最初の実値を掴んだところで押せるようにする
        void SetBootReady()
        {
            StringBuilder sb = new StringBuilder("この画面を開いたときの値に戻す");
            bool any = false;
            for (int i = 0; i < N; i++)
            {
                if (!bootGot[i]) continue;
                sb.Append(any ? " / " : "  ").Append(boot[i].ToString(CultureInfo.InvariantCulture));
                any = true;
            }
            bootBtn.Enabled = any;
            bootBtn.Invalidate();
            tip.SetToolTip(bootBtn, sb.ToString());
        }

        void OnPreset(int p, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                // 沈んでいる項目はこの画面に無い。前の値をそのまま残す
                for (int i = 0; i < N; i++) if (avail[i]) presetVals[p, i] = sl[i].Value;
                cfg.SavePreset(p, presetVals);
                presets[p].Flash = 5;
                presets[p].Invalidate();
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            for (int i = 0; i < N; i++)
            {
                if (!avail[i]) continue;
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
            if (bootBtn.Flash > 0) { bootBtn.Flash--; bootBtn.Invalidate(); }
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
            bool gotBoot = false;
            for (int i = 0; i < N; i++)
            {
                if (HasPending()) return true;   // user is driving; their value wins
                if (!avail[i]) continue;         // この画面には無い項目

                int raw = Ddc.Read(CH[i].Vcp, ProbeVcp);
                Dbg.W("ReadAll " + CH[i].Label + " vcp" + CH[i].Vcp.ToString("X2") + " -> " + raw);
                if (raw < 0)
                {
                    // 他の項目が読めているなら線は生きている。この項目だけが
                    // 無い板ということなので、沈めて触れなくする
                    if (healthy) MarkMissing(i);
                    continue;
                }
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

                // 「起動時」= その画面で最初に読めた値。読めた順に 1 項目ずつ
                // 押さえるので、途中で操作されても掴んだぶんは正しい
                if (!bootGot[idx])
                {
                    boot[idx] = vv;
                    bootGot[idx] = true;
                    gotBoot = true;
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
            if (gotBoot)
                try { BeginInvoke((MethodInvoker)delegate { SetBootReady(); }); } catch { }
            return healthy;
        }

        // ワーカー側から。UI の更新だけ投げ、判定はその場で持つ
        // (次の ReadAll がまた 700ms かけて読みに行かないように)
        void MarkMissing(int i)
        {
            if (!avail[i]) return;
            avail[i] = false;
            Dbg.W("  display" + Ddc.CurrentNum + " has no vcp" + CH[i].Vcp.ToString("X2"));
            try { BeginInvoke((MethodInvoker)delegate { SetAvail(i, false); }); } catch { }
        }

        // おまかせで開いたときは、どの画面になったかを UI に伝える
        // (ini のキーとボタンの点灯がそれで決まる)
        void SyncSelection()
        {
            int n = Ddc.CurrentNum;
            if (n < 0 || n == curMon) return;
            try { BeginInvoke((MethodInvoker)delegate { UseMonitor(n); }); } catch { }
        }

        void WorkerLoop()
        {
            SetStatus("busy");
            SetStatus(Ddc.Open(ProbeVcp, startMon) && ReadAll() ? "ok" : "err");
            SyncSelection();
            ProbeDisplays(false);   // 実値を出した後で、残りの画面の生死を調べる

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

                    int mw = wantMon;
                    if (mw != NoSwitch)
                    {
                        // 画面の切り替え。掴み直して、その板の実値を読み直す
                        wantMon = NoSwitch;
                        SetStatus("busy");
                        // 「起動時」は画面ごとに持ち直す。切り替えた先の板を
                        // 最初に読んだ値が、その板の戻り先になる
                        for (int i = 0; i < N; i++)
                        { touched[i] = false; avail[i] = true; bootGot[i] = false; }
                        SetStatus(Ddc.Open(ProbeVcp, mw) && ReadAll() ? "ok" : "err");
                    }
                    else if (refreshWanted)
                    {
                        refreshWanted = false;
                        for (int i = 0; i < N; i++) touched[i] = false;
                        if (ReadAll()) SetStatus("ok");
                        SyncSelection();   // 起動直後に取りこぼしていたら、ここで揃える
                        // 消えていた画面の電源が入ったかもしれない。前面に
                        // 出したこの機会に見に行く (生きている画面は叩かない)
                        ProbeDisplays(true);
                    }
                    else if (status == "err" && Environment.TickCount - lastHeal > 5000)
                    {
                        // cable replugged / monitor woke up / DDC re-enabled
                        lastHeal = Environment.TickCount;
                        SetStatus(Ddc.Reopen(ProbeVcp) && ReadAll() ? "ok" : "err");
                        SyncSelection();
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
            for (int i = 0; i < N; i++) if (avail[i]) cfg.SetLast(i, sl[i].Value);
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
        static int Main(string[] args)
        {
            // 引数があれば窓は出さない。反映して終わる (Cli.Run 参照)
            if (args.Length > 0) return Cli.Run(args);

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
    }

    // ================= コマンドライン =================
    // oscv.exe 1=弱 2=強
    //
    // 「画面番号=プリセット」の組を並べる。1 つでも指定があれば窓は出さず、
    // その場でモニターに反映して終わる。ショートカットやホットキーに割り当てて、
    // 2 台まとめて切り替えるための入口。
    //
    // プリセットの中身は GUI と同じ ini から読む (画面ごとの値があればそれを、
    // 無ければ番号なしの値を使う)。ini には何も書き戻さない。
    static class Cli
    {
        // 終了コード: 0 = 全部の組を反映できた / 1 = 反映できなかった組がある
        public static int Run(string[] args)
        {
            List<int> nums = new List<int>();
            List<int> presets = new List<int>();

            foreach (string a in args)
            {
                if (a == "/?" || a == "-h" || a == "--help") { Usage(null); return 0; }
                int num, p;
                if (!Parse(a, out num, out p)) { Usage(a); return 1; }
                nums.Add(num);
                presets.Add(p);
            }

            Cfg cfg = Cfg.Load();
            bool allOk = true;
            for (int k = 0; k < nums.Count; k++)
                if (!Apply(cfg, nums[k], presets[k])) allOk = false;

            Ddc.Close();
            return allOk ? 0 : 1;
        }

        // "2=強" -> num=2, p=2。プリセットは名前でも番号 (1..3) でもよい
        static bool Parse(string arg, out int num, out int p)
        {
            num = -1;
            p = -1;
            if (arg == null) return false;
            int eq = arg.IndexOf('=');
            if (eq <= 0 || eq >= arg.Length - 1) return false;

            string left = arg.Substring(0, eq).Trim();
            string right = arg.Substring(eq + 1).Trim();

            if (!int.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out num) ||
                num < 1) return false;

            for (int i = 0; i < MainForm.PresetNames.Length; i++)
                if (right == MainForm.PresetNames[i]) { p = i; return true; }

            int n;
            if (int.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) &&
                n >= 1 && n <= MainForm.PresetNames.Length) { p = n - 1; return true; }

            return false;
        }

        static bool Apply(Cfg cfg, int num, int p)
        {
            Dbg.W("cli: display" + num + " <- " + MainForm.PresetNames[p]);

            // 指定された画面が答えなければ、そこで終わり。
            // 黙って別の板に書くことだけは避ける。
            // ただし直前に別プロセスが DDC を叩いていると最初の一撃が空振りする
            // ことがあるので、一度だけ置いて出し直す (GUI と違って一発勝負なので)
            if (!Ddc.Open(MainForm.ProbeVcp, num))
            {
                Thread.Sleep(120);
                if (!Ddc.Open(MainForm.ProbeVcp, num))
                {
                    Dbg.W("cli:   display" + num + " が開けません");
                    return false;
                }
            }

            cfg.Prefix = "m" + num.ToString(CultureInfo.InvariantCulture) + ".";

            bool any = false;
            for (int i = 0; i < MainForm.N; i++)
            {
                Channel c = MainForm.CH[i];
                int v = cfg.Preset(p, i);
                if (v < c.Min) v = c.Min;   // 範囲外は黙って無視されるので必ず丸める
                if (v > c.Max) v = c.Max;

                bool ok = WriteVerified(c, v);
                Dbg.W("cli:   vcp" + c.Vcp.ToString("X2") + " <- " + v + (ok ? "" : "  失敗"));
                any |= ok;
            }
            return any;
        }

        // 「書けた」と言われても板が黙って無視することがある (SetVCPFeature は
        // 範囲外でも true を返すし、DDC 自体たまに取りこぼす)。窓を出さない分だけ
        // 結果が目に見えないので、読み返して確かめ、違っていたら一度だけ書き直す。
        // その板に無い項目 (LG 以外の 0xF9 など) はここで false になって次へ進む
        static bool WriteVerified(Channel c, int v)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Ddc.Write(c.Vcp, v, MainForm.ProbeVcp);
                Thread.Sleep(40);   // DDC は続けざまの往復に弱い。少し置いてから読む
                int raw = Ddc.Peek(c.Vcp);
                if (raw >= 0 && (int)Math.Round((double)raw / c.GetDiv) == v) return true;
            }
            return false;
        }

        // 窓なし exe なので、コンソールには何も出せない。
        // 指定を間違えたときだけダイアログで知らせる
        static void Usage(string bad)
        {
            string s =
                "使い方:  oscv.exe <画面番号>=<プリセット> ...\r\n\r\n" +
                "  例:  oscv.exe 1=弱 2=強\r\n\r\n" +
                "  画面番号は Windows のディスプレイ番号。\r\n" +
                "  プリセットは 弱 / 中 / 強 (1 / 2 / 3 でも可)。\r\n" +
                "  組はいくつでも並べられ、書いた順に反映します。\r\n\r\n" +
                "  指定を付けずに実行すると、いつもの窓が開きます。\r\n" +
                "  終了コード 0 = 全部反映、1 = 反映できなかった画面がある。";
            if (bad != null) s = "解釈できない指定: " + bad + "\r\n\r\n" + s;
            MessageBox.Show(s, "oscv v" + App.Version.ToString(CultureInfo.InvariantCulture),
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
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

        // 画面ごとに分けるキーの接頭辞 ("m2." など)。空なら v3 までのキー
        public string Prefix = "";

        int GetI(string k, int def)
        {
            string s;
            if (!d.TryGetValue(k, out s)) return def;
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        // 画面ごとの値。**番号なしのキーへは落とさない。**
        // 落とすと 1 台目で保存した値が 2 台目の出発点になり、板ごとに分けている
        // 意味がなくなる (2 台目の適正値は 1 台目とは違う)。
        // まだ持っていない画面は、素の初期値から始める
        int GetPer(string k, int def)
        {
            return GetI(Prefix + k, def);
        }

        void SetPer(string k, int v)
        {
            d[Prefix + k] = v.ToString(CultureInfo.InvariantCulture);
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

        // 操作する画面。-1 = おまかせ (ベンダー固有コードに答える板を選ぶ)
        public int Display
        {
            get { return GetI("display", -1); }
            set { d["display"] = value.ToString(CultureInfo.InvariantCulture); }
        }

        public int Last(int i, int min, int max)
        {
            int v = GetPer("last" + i, (min + max) / 2);
            return v < min ? min : (v > max ? max : v);
        }

        public void SetLast(int i, int v)
        {
            SetPer("last" + i, v);
        }

        public int Preset(int p, int i) { return GetPer("preset" + p + "_" + i, DefPreset[p, i]); }

        public void SavePreset(int p, int[,] vals)
        {
            for (int i = 0; i < 3; i++) SetPer("preset" + p + "_" + i, vals[p, i]);
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
