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
        public const int Version = 10;
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
        static readonly object gate = new object();

        // 掴んでいる板は全部つかんだまま持つ。呼ぶ側は画面番号で指す。
        // ハンドルを外に出さないのは、0 が正当なハンドルで「未設定」と
        // 区別できないため (IndexOf は見つからないとき -1 を返す)
        public static bool IsOpen { get { lock (gate) { return phys.Count > 0; } } }

        public static bool Has(int num)
        {
            lock (gate) { return IndexOf(num) >= 0; }
        }

        static int IndexOf(int num)
        {
            for (int i = 0; i < phys.Count; i++) if (phys[i].Num == num) return i;
            return -1;
        }

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
        public static int Peek(int num, byte code)
        {
            lock (gate)
            {
                int idx = IndexOf(num);
                return idx >= 0 ? Raw(phys[idx].Handle, code) : -1;
            }
        }

        // A single read costs ~60ms and fails outright maybe 1 time in 40.
        static int Raw(IntPtr h, byte code)
        {
            uint cur, max;
            if (!GetVCPFeatureAndVCPFeatureReply(h, code, IntPtr.Zero, out cur, out max)) return -1;
            return (int)cur;
        }

        // つながっている画面を全部開く。開き直し (寝起き・抜き差し・一時的な
        // 失敗からの復帰) も同じ入口。どれを操作するかは呼ぶ側が番号で決める
        public static bool Open()
        {
            lock (gate) { return OpenLocked(); }
        }

        static bool OpenLocked()
        {
            CloseLocked();

            List<IntPtr> hmons = new List<IntPtr>();
            MonitorEnumProc cb = delegate(IntPtr hm, IntPtr hdc, IntPtr rc, IntPtr d)
            { hmons.Add(hm); return true; };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);

            Dbg.W("Open: hmon=" + hmons.Count);
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
            return phys.Count > 0;
        }

        static void CloseLocked()
        {
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
        public static int Read(int num, byte code)
        {
            lock (gate)
            {
                int idx = IndexOf(num);
                for (int i = 0; idx >= 0 && i < 4; i++)
                {
                    int v = Raw(phys[idx].Handle, code);
                    if (v >= 0) return v;
                    Thread.Sleep(40);   // DDC needs a breather between transactions
                }

                Dbg.W("  Read(display" + num + " " + code.ToString("X2") + ") 開き直します");
                if (!OpenLocked()) return -1;
                idx = IndexOf(num);
                return idx >= 0 ? Raw(phys[idx].Handle, code) : -1;
            }
        }

        // NOTE: SetVCPFeature returns true even when the monitor silently ignores
        // an out-of-range value, so callers must clamp before calling.
        public static bool Write(int num, byte code, int val)
        {
            lock (gate)
            {
                int idx = IndexOf(num);
                if (idx >= 0 && SetVCPFeature(phys[idx].Handle, code, (uint)val)) return true;

                if (!OpenLocked()) return false;
                idx = IndexOf(num);
                return idx >= 0 && SetVCPFeature(phys[idx].Handle, code, (uint)val);
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
                     : (Flash > 0 ? T.Fill : (_hot ? T.BtnHot : T.BtnBg));
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
    // ================= 1 画面ぶんの操作列 =================
    // ディスプレイ 1 台ぶんの UI と状態。自分の画面番号だけを読み書きする。
    // 画面が 2 つあれば、これが横に 2 つ並ぶ (タブで切り替えるのではなく、
    // 両方を同じ窓の中で完結させる)。
    class Column
    {
        public readonly int Num;          // Windows の画面番号
        public readonly Panel Root;
        public readonly int Height;

        readonly MainForm form;
        readonly Cfg cfg;
        readonly float S;

        Slider[] sl = new Slider[MainForm.N];
        Label[] lblName = new Label[MainForm.N];
        Label[] lblVal = new Label[MainForm.N];
        Btn[] presets = new Btn[3];
        Btn bootBtn;
        Panel caption;                    // 見出し (画面が 2 つ以上のときだけ)
        Font capFont;
        int[,] presetVals = new int[3, MainForm.N];

        // ワーカーと UI で共有する値。gate の中でだけ触る
        readonly object gate = new object();
        int[] target = new int[MainForm.N];
        int[] applied = new int[MainForm.N];
        bool[] dirty = new bool[MainForm.N];

        volatile bool[] touched = new bool[MainForm.N];
        volatile bool[] avail = new bool[MainForm.N];
        volatile bool[] bootGot = new bool[MainForm.N];
        int[] boot = new int[MainForm.N];

        public volatile string Status = "init";
        public volatile bool Live = true;   // DDC に答える画面か

        int Sc(double v) { return (int)Math.Round(v * S); }

        public Column(MainForm form, Cfg cfg, int num, float s, int w, bool showCaption)
        {
            this.form = form;
            this.cfg = cfg;
            this.Num = num;
            this.S = s;

            for (int i = 0; i < MainForm.N; i++) avail[i] = true;
            for (int p = 0; p < 3; p++)
                for (int i = 0; i < MainForm.N; i++) presetVals[p, i] = cfg.Preset(num, p, i);

            Root = new Panel();
            Root.BackColor = T.Bg;
            Height = Build(w, showCaption);
            Root.Size = new Size(w, Height);
            Seed();
        }

        int Build(int w, bool showCaption)
        {
            int pad = Sc(15);
            int y = Sc(10);

            if (showCaption)
            {
                capFont = new Font(form.Font.FontFamily, 8.5f, FontStyle.Bold);
                caption = new Panel();
                caption.Bounds = new Rectangle(0, y, w, Sc(19));
                caption.BackColor = T.Bg;
                caption.Paint += CaptionPaint;
                // 見出しの余白でも窓を動かせる (ヘッダーが遠い列があるため)
                caption.MouseDown += form.HeaderDown;
                Root.Controls.Add(caption);
                y += Sc(19) + Sc(3);
            }

            for (int i = 0; i < MainForm.N; i++)
            {
                int idx = i;
                Channel c = MainForm.CH[i];

                lblName[i] = new Label();
                lblName[i].AutoSize = false;
                lblName[i].Bounds = new Rectangle(pad, y, w - pad * 2 - Sc(46), Sc(17));
                lblName[i].ForeColor = T.Label;
                lblName[i].Text = c.Label;
                lblName[i].TextAlign = ContentAlignment.MiddleLeft;
                lblName[i].Font = new Font(form.Font.FontFamily, 8.5f);
                Root.Controls.Add(lblName[i]);

                lblVal[i] = new Label();
                lblVal[i].AutoSize = false;
                lblVal[i].Bounds = new Rectangle(w - pad - Sc(46), y, Sc(46), Sc(17));
                lblVal[i].ForeColor = T.Text;
                lblVal[i].TextAlign = ContentAlignment.MiddleRight;
                lblVal[i].Font = new Font(form.Font.FontFamily, 10.5f, FontStyle.Bold);
                Root.Controls.Add(lblVal[i]);

                y += Sc(18);

                sl[i] = new Slider();
                sl[i].S = S;
                sl[i].Min = c.Min;
                sl[i].Max = c.Max;
                sl[i].BackColor = T.Bg;
                sl[i].Bounds = new Rectangle(pad - Sc(3), y, w - (pad - Sc(3)) * 2, Sc(28));
                sl[i].ValueChanged += delegate { OnSlide(idx); };
                sl[i].ValueCommitted += delegate { Commit(idx); };
                Root.Controls.Add(sl[i]);

                y += Sc(28) + Sc(10);
            }

            // 弱 / 中 / 強 と「起動時」で 4 つ。1 列に収める
            y += Sc(2);
            int gap = Sc(6);
            int bw = (w - pad * 2 - gap * 3) / 4;
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                presets[i] = new Btn();
                presets[i].Bounds = new Rectangle(pad + i * (bw + gap), y, bw, Sc(24));
                presets[i].Text = MainForm.PresetNames[i];
                presets[i].Font = new Font(form.Font.FontFamily, 8.5f);
                presets[i].Clicked += delegate(object s, MouseEventArgs e) { OnPreset(idx, e); };
                Root.Controls.Add(presets[i]);
            }

            // 起動時の値に戻すボタン。値は自動で入るので、右クリックの保存は無い
            bootBtn = new Btn();
            bootBtn.Bounds = new Rectangle(pad + 3 * (bw + gap), y, bw, Sc(24));
            bootBtn.Text = "起動時";
            bootBtn.Font = new Font(form.Font.FontFamily, 8f);
            bootBtn.Enabled = false;
            bootBtn.Clicked += OnBoot;
            Root.Controls.Add(bootBtn);

            return y + Sc(24) + Sc(13);
        }

        void CaptionPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Sc(7), x = Sc(15), yy = (caption.Height - d) / 2;
            using (SolidBrush b = new SolidBrush(MainForm.StatusColor(Status)))
                g.FillEllipse(b, x, yy, d, d);

            string s = "画面 " + Num.ToString(CultureInfo.InvariantCulture) + (Live ? "" : "  応答なし");
            TextRenderer.DrawText(g, s, capFont,
                new Rectangle(x + d + Sc(8), 0, caption.Width, caption.Height),
                Live ? T.Label : T.TextDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        // 種は ini の最終値。実値はワーカーが 200ms ほどで上書きする
        void Seed()
        {
            for (int i = 0; i < MainForm.N; i++)
            {
                int v = cfg.Last(Num, i, MainForm.CH[i].Min, MainForm.CH[i].Max);
                sl[i].SetValueSilent(v);
                lblVal[i].Text = v.ToString(CultureInfo.InvariantCulture);
                target[i] = v;
                applied[i] = v;
            }
        }

        public bool Owns(Slider s)
        {
            for (int i = 0; i < MainForm.N; i++) if (ReferenceEquals(sl[i], s)) return true;
            return false;
        }

        // ---------- 操作 ----------

        void OnSlide(int i)
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
            form.Wake();
        }

        void OnPreset(int p, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                // 沈んでいる項目はこの画面に無い。前の値をそのまま残す
                for (int i = 0; i < MainForm.N; i++) if (avail[i]) presetVals[p, i] = sl[i].Value;
                cfg.SavePreset(Num, p, presetVals);
                presets[p].Flash = 5;
                presets[p].Invalidate();
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            for (int i = 0; i < MainForm.N; i++)
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

        // 起動時の値に戻す。押せるのは 1 つでも値を掴めているときだけ
        void OnBoot(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;   // 自動保存なので右クリックは無し

            for (int i = 0; i < MainForm.N; i++)
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

        public void Tick()
        {
            for (int i = 0; i < 3; i++)
                if (presets[i].Flash > 0) { presets[i].Flash--; presets[i].Invalidate(); }
            if (bootBtn.Flash > 0) { bootBtn.Flash--; bootBtn.Invalidate(); }
        }

        public void SaveLast()
        {
            for (int i = 0; i < MainForm.N; i++)
                if (avail[i]) cfg.SetLast(Num, i, sl[i].Value);
        }

        // ---------- 見た目の状態 (UI スレッド) ----------

        // 対応していない項目は沈めて触れなくする。書けば必ず失敗するので、
        // 赤ランプを出し続けるよりこの方が正しい
        void SetAvail(int i, bool on)
        {
            avail[i] = on;
            lblName[i].ForeColor = on && Live ? T.Label : T.TextDim;
            lblVal[i].ForeColor = on && Live ? T.Text : T.TextDim;
            if (!on) lblVal[i].Text = "―";
            sl[i].Enabled = on && Live;
            sl[i].Invalidate();
        }

        // 画面ごと答えないときは列を丸ごと沈める
        public void SetLive(bool live)
        {
            if (Live == live) return;
            Live = live;
            for (int i = 0; i < MainForm.N; i++) SetAvail(i, avail[i]);
            for (int i = 0; i < 3; i++) { presets[i].Enabled = live; presets[i].Invalidate(); }
            bootBtn.Enabled = live && AnyBoot();
            bootBtn.Invalidate();
            if (caption != null) caption.Invalidate();
        }

        bool AnyBoot()
        {
            for (int i = 0; i < MainForm.N; i++) if (bootGot[i]) return true;
            return false;
        }

        // ワーカーが最初の実値を掴んだところで押せるようにする
        void SetBootReady()
        {
            StringBuilder sb = new StringBuilder("この画面を開いたときの値に戻す");
            bool any = false;
            for (int i = 0; i < MainForm.N; i++)
            {
                if (!bootGot[i]) continue;
                sb.Append(any ? " / " : "  ").Append(boot[i].ToString(CultureInfo.InvariantCulture));
                any = true;
            }
            bootBtn.Enabled = any && Live;
            bootBtn.Invalidate();
            form.Tip.SetToolTip(bootBtn, sb.ToString());
        }

        public void SetStatus(string st)
        {
            if (Status == st) return;
            Status = st;
            form.Post(delegate
            {
                if (caption != null) caption.Invalidate();
                else form.InvalidateHeader();
            });
        }

        // ---------- ワーカーから ----------

        public bool NextPending(out int ch, out int val)
        {
            lock (gate)
            {
                for (int i = 0; i < MainForm.N; i++)
                {
                    if (!dirty[i]) continue;
                    if (target[i] != applied[i]) { ch = i; val = target[i]; return true; }
                    dirty[i] = false;
                }
            }
            ch = -1;
            val = 0;
            return false;
        }

        public void AfterWrite(int ch, int val, bool ok)
        {
            lock (gate)
            {
                if (ok) applied[ch] = val;
                if (target[ch] == val) dirty[ch] = false;
            }
        }

        public bool HasPending()
        {
            lock (gate)
            {
                for (int i = 0; i < MainForm.N; i++) if (dirty[i]) return true;
            }
            return false;
        }

        public void ForgetTouched()
        {
            for (int i = 0; i < MainForm.N; i++) touched[i] = false;
        }

        // この画面の実値を読み直す。ワーカースレッドから呼ぶ
        public bool ReadAll()
        {
            bool healthy = false;
            bool gotBoot = false;

            for (int i = 0; i < MainForm.N; i++)
            {
                if (form.AnyPending()) return true;   // 操作中の値が優先
                if (!avail[i]) continue;              // この画面には無い項目

                Channel c = MainForm.CH[i];
                int raw = Ddc.Read(Num, c.Vcp);
                Dbg.W("read display" + Num + " " + c.Label + " vcp" + c.Vcp.ToString("X2") + " -> " + raw);
                if (raw < 0)
                {
                    // 他の項目が読めているなら線は生きている。この項目だけが
                    // 無い板ということなので、沈めて触れなくする
                    if (healthy) MarkMissing(i);
                    continue;
                }
                healthy = true;

                int v = (int)Math.Round((double)raw / c.GetDiv);
                if (v < c.Min) v = c.Min;
                if (v > c.Max) v = c.Max;

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

                form.Post(delegate
                {
                    if (touched[idx]) return;
                    sl[idx].SetValueSilent(vv);
                    lblVal[idx].Text = vv.ToString(CultureInfo.InvariantCulture);
                });
            }

            if (gotBoot) form.Post(delegate { SetBootReady(); });
            return healthy;
        }

        // 判定はその場で持つ (次の ReadAll がまた 700ms かけて読みに行かないように)
        void MarkMissing(int i)
        {
            if (!avail[i]) return;
            avail[i] = false;
            Dbg.W("  display" + Num + " has no vcp" + MainForm.CH[i].Vcp.ToString("X2"));
            form.Post(delegate { SetAvail(i, false); });
        }
    }

    // ================= main window =================
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
        // どれにも答えない板は、電源が落ちているか DDC で触れない板
        internal static readonly byte[] ProbeCodes = MakeProbeCodes();

        static byte[] MakeProbeCodes()
        {
            byte[] b = new byte[N];
            for (int i = 0; i < N; i++) b[i] = CH[i].Vcp;
            return b;
        }

        internal const int N = 3;
        internal static readonly string[] PresetNames = new string[] { "弱", "中", "強" };
        internal const int ColW = 250;   // 1 列の幅 (論理px)

        float S = 1f;
        Column[] cols = new Column[0];
        Panel header;
        Label title;
        Btn closeBtn;
        PinBtn pinBtn;
        System.Windows.Forms.Timer flashTimer;
        public ToolTip Tip;

        volatile bool alive = true;
        volatile bool refreshWanted;
        volatile bool closing, workerDone;
        AutoResetEvent signal = new AutoResetEvent(false);
        Thread worker;

        Cfg cfg;

        public MainForm()
        {
            cfg = Cfg.Load();
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) S = g.DpiX / 96f;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = T.Bg;
            Text = "OSCV";
            SetAppIcon();
            Font = new Font("Yu Gothic UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

            BuildUi();

            TopMost = cfg.Pin;
            UpdatePin();

            Point p = new Point(cfg.X, cfg.Y);
            if (cfg.X == int.MinValue || !OnAScreen(p, Size)) p = DefaultPos();
            Location = p;

            flashTimer = new System.Windows.Forms.Timer();
            flashTimer.Interval = 90;
            flashTimer.Tick += OnFlash;
            flashTimer.Start();

            Application.AddMessageFilter(this);

            // ワーカーからの BeginInvoke は、窓のハンドルが出来ていないと例外になる。
            // ハンドルが出来るのは Application.Run の中なので、起動直後の 1 回目
            // (実値の表示・「起動時」の有効化) が丸ごと捨てられていた。
            // ワーカーを起こす前にハンドルを作っておく
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

        // 左から右 (同じ位置なら上から下) に並べる。列の並び順が机の上の
        // 並びと一致していれば、番号を見なくてもどちらの画面か分かる
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

        internal static Color StatusColor(string st)
        {
            if (st == "ok") return T.Ok;
            if (st == "busy") return T.Busy;
            if (st == "err") return T.Err;
            return T.TextDim;
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

        // 画面の数だけ列を横に並べる。1 つなら v3 までと同じ窓の大きさ
        void BuildUi()
        {
            Screen[] scr = Displays();
            int colW = Sc(ColW);
            int hh = Sc(26);
            int totalW = colW * scr.Length;

            Tip = new ToolTip();

            header = new Panel();
            header.BackColor = T.Header;
            header.Bounds = new Rectangle(0, 0, totalW, hh);
            header.MouseDown += HeaderDown;
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
            header.Controls.Add(title);

            pinBtn = new PinBtn();
            pinBtn.Bounds = new Rectangle(totalW - Sc(56), Sc(4), Sc(24), hh - Sc(8));
            pinBtn.Clicked += OnPin;
            header.Controls.Add(pinBtn);

            closeBtn = new Btn();
            closeBtn.Bounds = new Rectangle(totalW - Sc(30), Sc(4), Sc(24), hh - Sc(8));
            closeBtn.Text = "✕";
            closeBtn.Font = new Font(Font.FontFamily, 8f);
            closeBtn.Clicked += delegate { Close(); };
            header.Controls.Add(closeBtn);

            cols = new Column[scr.Length];
            int colH = 0;
            for (int i = 0; i < scr.Length; i++)
            {
                cols[i] = new Column(this, cfg, Ddc.NumOf(scr[i].DeviceName), S, colW, scr.Length > 1);
                cols[i].Root.Location = new Point(i * colW, hh);
                Controls.Add(cols[i].Root);
                if (cols[i].Height > colH) colH = cols[i].Height;
            }
            foreach (Column c in cols) c.Root.Height = colH;

            // 列の境目に細い線を引く。どこまでが 1 台ぶんか分かるように
            for (int i = 1; i < cols.Length; i++)
            {
                Panel line = new Panel();
                line.BackColor = T.Header;
                line.Bounds = new Rectangle(i * colW, hh, 1, colH);
                Controls.Add(line);
                line.BringToFront();
            }

            // WM_NCCALCSIZE で非クライアント領域を 0 にしてあるので、
            // クライアント領域 = ウィンドウの大きさ。ClientSize から逆算させると
            // 付けたスタイルぶん (キャプションの高さ) だけ大きくなってしまう
            Size = new Size(totalW, hh + colH);
        }

        // 列が 1 つのときだけヘッダーに状態ランプを出す (2 つ以上なら列の見出しに出る)
        void HeaderPaint(object sender, PaintEventArgs e)
        {
            if (cols.Length != 1) return;

            int d = Sc(7), x = Sc(10), yy = (header.Height - d) / 2;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(StatusColor(cols[0].Status)))
                e.Graphics.FillEllipse(b, x, yy, d, d);
        }

        internal void InvalidateHeader() { header.Invalidate(); }

        public void Post(MethodInvoker m)
        {
            try { BeginInvoke(m); } catch { }
        }

        public void Wake() { signal.Set(); }

        public bool AnyPending()
        {
            foreach (Column c in cols) if (c.HasPending()) return true;
            return false;
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
        // 移動は OS の移動ループに任せる。自分で Location を書き換えると
        // WM_ENTERSIZEMOVE も EVENT_SYSTEM_MOVESIZESTART も飛ばないので、
        // 「ウィンドウが動き始めたこと」を見ている常駐ソフト (スナップ系など) が
        // ドラッグに気付けない。掴んだ場所をキャプション扱いで OS へ渡せば、
        // 見た目はそのままで標準の移動になる
        public void HeaderDown(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }

        // ---------- 枠なしのまま「普通のウィンドウ」にする ----------
        // 見た目のために枠を消すと、Windows から見て普通のウィンドウでなくなる。
        // タスクバーのボタンで最小化できない、スナップ系の常駐ソフトが吸着先として
        // 拾ってくれない、といった不都合が出る (どれも WS_CAPTION / WS_MINIMIZEBOX /
        // WS_SYSMENU の有無で判断されるため)。
        //
        // そこで**スタイルは普通のウィンドウと同じにして、WM_NCCALCSIZE で
        // 非クライアント領域を 0 にする**。タイトルバーも枠も描かれないので
        // 見た目は枠なしのまま、扱いだけ普通のウィンドウになる。
        // 最大化だけは外してある (250px 幅の道具を全画面にしても困るだけで、
        // WS_MAXIMIZEBOX を残すと上端ドラッグの Aero Snap で最大化してしまう)。
        const int WS_CAPTION     = 0x00C00000;
        const int WS_MAXIMIZEBOX = 0x00010000;
        const int WS_SYSMENU     = 0x00080000;
        const int WS_MINIMIZEBOX = 0x00020000;
        const int WM_NCCALCSIZE  = 0x0083;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
                // MaximizeBox = false だけでは枠なしのとき消えなかった (実測) ので、
                // ここで確実に落とす。残っていると上端ドラッグの Aero Snap で
                // 250px 幅の窓が全画面になってしまう
                cp.Style &= ~WS_MAXIMIZEBOX;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            // 0 を返す = クライアント領域がウィンドウ全体。非クライアント領域が
            // 残らないので、キャプションも枠も描かれない
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero) { m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);
        }

        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        const int WM_NCLBUTTONDOWN = 0x00A1;
        const int HTCAPTION = 2;

        // ---------- wheel routing: hover is enough, no click required ----------
        const int WM_MOUSEWHEEL = 0x020A;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;
            Slider s = Control.FromHandle(WindowFromPoint(Cursor.Position)) as Slider;
            if (s == null) return false;
            foreach (Column c in cols)
            {
                if (!c.Owns(s)) continue;
                int delta = unchecked((short)(((long)m.WParam >> 16) & 0xFFFF));
                s.Wheel(delta);
                return true;
            }
            return false;
        }

        [DllImport("user32.dll")]
        static extern IntPtr WindowFromPoint(Point p);

        // Re-read when the window is brought forward, so changes made with the
        // monitor's own buttons show up.
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            refreshWanted = true;
            signal.Set();
        }

        void OnFlash(object s, EventArgs e)
        {
            foreach (Column c in cols) c.Tick();
        }

        // ---------- background worker ----------
        // DDC は 1 本のシリアル線なので、列が増えてもワーカーは 1 本のまま。
        // 書きたい値があればそれを最優先で流し、無いときに読み直す
        void WorkerLoop()
        {
            foreach (Column c in cols) c.SetStatus("busy");

            bool opened = Ddc.Open();
            Dbg.W("worker: open=" + opened + " columns=" + cols.Length);
            foreach (Column c in cols) c.SetStatus(opened && c.ReadAll() ? "ok" : "err");
            ProbeLive(false);   // 実値を出した後で、答えない画面を沈める

            int lastHeal = Environment.TickCount;

            while (alive)
            {
                Column w = null;
                int ch = -1, val = 0;
                foreach (Column c in cols)
                    if (c.NextPending(out ch, out val)) { w = c; break; }

                if (w == null)
                {
                    if (closing) break;

                    if (refreshWanted)
                    {
                        refreshWanted = false;
                        foreach (Column c in cols)
                        {
                            c.ForgetTouched();
                            if (c.ReadAll()) c.SetStatus("ok");
                        }
                        // 消えていた画面の電源が入ったかもしれない。前面に
                        // 出したこの機会に見に行く (生きている画面は叩かない)
                        ProbeLive(true);
                    }
                    else if (AnyErr() && Environment.TickCount - lastHeal > 5000)
                    {
                        // cable replugged / monitor woke up / DDC re-enabled
                        lastHeal = Environment.TickCount;
                        Ddc.Open();
                        foreach (Column c in cols)
                            if (c.Status == "err") c.SetStatus(c.ReadAll() ? "ok" : "err");
                        ProbeLive(true);
                    }

                    signal.WaitOne(500);
                    continue;
                }

                // Clamp: an out-of-range VCP write is accepted and then ignored.
                if (val < CH[ch].Min) val = CH[ch].Min;
                if (val > CH[ch].Max) val = CH[ch].Max;

                bool ok = Ddc.Write(w.Num, CH[ch].Vcp, val);
                w.AfterWrite(ch, val, ok);
                w.SetStatus(ok ? "ok" : "err");
            }

            Ddc.Close();
            workerDone = true;
            try { BeginInvoke((MethodInvoker)delegate { Close(); }); } catch { }
        }

        bool AnyErr()
        {
            foreach (Column c in cols) if (c.Status == "err") return true;
            return false;
        }

        // 答えない画面の列を沈める。onlyDead なら、いま死んでいる列だけ見に行く
        // (生きている列を毎回叩くと、前面に出すたびに 60ms x 台数を捨てることになる)
        void ProbeLive(bool onlyDead)
        {
            foreach (Column c in cols)
            {
                if (onlyDead && c.Live) continue;

                // 読めている列は、その時点で生きている
                bool live = c.Status == "ok" || Ddc.Alive(c.Num, ProbeCodes);
                if (live == c.Live) continue;

                Column cc = c;
                bool lv = live;
                Post(delegate { cc.SetLive(lv); });
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            cfg.X = Location.X;
            cfg.Y = Location.Y;
            cfg.Pin = TopMost;
            foreach (Column c in cols) c.SaveLast();
            cfg.Save();

            if (!workerDone && AnyPending())
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
            Ddc.Open();

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
            if (!Ready(num))
            {
                Thread.Sleep(120);
                Ddc.Open();
                if (!Ready(num))
                {
                    Dbg.W("cli:   display" + num + " が答えません");
                    return false;
                }
            }

            bool any = false;
            for (int i = 0; i < MainForm.N; i++)
            {
                Channel c = MainForm.CH[i];
                int v = cfg.Preset(num, p, i);
                if (v < c.Min) v = c.Min;   // 範囲外は黙って無視されるので必ず丸める
                if (v > c.Max) v = c.Max;

                bool ok = WriteVerified(num, c, v);
                Dbg.W("cli:   vcp" + c.Vcp.ToString("X2") + " <- " + v + (ok ? "" : "  失敗"));
                any |= ok;
            }
            return any;
        }

        // その画面を掴めていて、DDC にも答えるか
        static bool Ready(int num)
        {
            return Ddc.Has(num) && Ddc.Alive(num, MainForm.ProbeCodes);
        }

        // 「書けた」と言われても板が黙って無視することがある (SetVCPFeature は
        // 範囲外でも true を返すし、DDC 自体たまに取りこぼす)。窓を出さない分だけ
        // 結果が目に見えないので、読み返して確かめ、違っていたら一度だけ書き直す。
        // その板に無い項目 (LG 以外の 0xF9 など) はここで false になって次へ進む
        static bool WriteVerified(int num, Channel c, int v)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Ddc.Write(num, c.Vcp, v);
                Thread.Sleep(40);   // DDC は続けざまの往復に弱い。少し置いてから読む
                int raw = Ddc.Peek(num, c.Vcp);
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


        int GetI(string k, int def)
        {
            string s;
            if (!d.TryGetValue(k, out s)) return def;
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        // 画面ごとの値。キーは画面番号を頭に付ける (m2.last0 など)。
        // **番号なしのキーへは落とさない。** 落とすと 1 台目で保存した値が
        // 2 台目の出発点になり、板ごとに分けている意味がなくなる。
        // まだ値を持っていない画面は、素の初期値から始める
        static string Pre(int num)
        {
            return "m" + num.ToString(CultureInfo.InvariantCulture) + ".";
        }

        int GetPer(int num, string k, int def)
        {
            return GetI(Pre(num) + k, def);
        }

        void SetPer(int num, string k, int v)
        {
            d[Pre(num) + k] = v.ToString(CultureInfo.InvariantCulture);
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

        public int Last(int num, int i, int min, int max)
        {
            int v = GetPer(num, "last" + i, (min + max) / 2);
            return v < min ? min : (v > max ? max : v);
        }

        public void SetLast(int num, int i, int v)
        {
            SetPer(num, "last" + i, v);
        }

        public int Preset(int num, int p, int i)
        {
            return GetPer(num, "preset" + p + "_" + i, DefPreset[p, i]);
        }

        public void SavePreset(int num, int p, int[,] vals)
        {
            for (int i = 0; i < 3; i++) SetPer(num, "preset" + p + "_" + i, vals[p, i]);
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
