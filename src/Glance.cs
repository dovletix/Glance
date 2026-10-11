using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Glance
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, @"Local\DovleGlanceMetrics", out createdNew))
            {
                if (!createdNew) return;
                try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; }
                catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                AppFonts.Load();
                Application.Run(new MainForm());
                GC.KeepAlive(mutex);
            }
        }
    }

    sealed class Ease
    {
        public double Value;
        public bool On;
        public void Toward(double target, double dt)
        {
            if (!On)
            {
                Value = target;
                On = true;
                return;
            }
            if (dt <= 0) return;
            double k = 1.0 - Math.Exp(-dt / 0.22);
            Value = Value + (target - Value) * k;
            if (Math.Abs(target - Value) < 0.05) Value = target;
        }
    }

    sealed class Ring
    {
        public float[] V = new float[80];
        public int N;
        public int I;
        public void Push(float x)
        {
            V[I] = x;
            I = (I + 1) % V.Length;
            if (N < V.Length) N++;
        }
    }

    sealed class Snapshot
    {
        public DateTime TakenUtc;
        public bool HasCpu;
        public double Cpu;
        public bool HasRam;
        public double RamPct;
        public double RamUsed;
        public double RamTotal;
        public bool HasGpu;
        public double Gpu;
        public bool HasTemp;
        public int Temp;
        public bool HasVram;
        public double VramPct;
        public double VramUsed;
        public double VramTotal;
        public string GpuName = "";
        public bool HasDisk;
        public double DiskBusy;
        public double DiskMBs;
        public bool HasNet;
        public double Down;
        public double Up;
        public string NetName = "";
        public List<string> Grabbers = new List<string>();
        public List<FlowHit> Flows = new List<FlowHit>();
        public List<string> Hung = new List<string>();
        public List<string> TopName = new List<string>();
        public List<double> TopPct = new List<double>();
        public List<string> MemName = new List<string>();
        public List<double> MemMb = new List<double>();
    }

    sealed class FlowHit
    {
        public uint Pid;
        public string Name = "";
        public double Down;
        public double Up;
    }

    sealed class MainForm : Form
    {
        const int BaseW = 440;
        const int BaseH = 800;
        const int HotkeyId = 1;

        readonly object gate = new object();
        Snapshot current = new Snapshot();
        readonly Ring cpuRing = new Ring();
        readonly Ring ramRing = new Ring();
        readonly Ring gpuRing = new Ring();
        readonly Ring diskRing = new Ring();
        readonly Ease cpuEase = new Ease();
        readonly Ease ramEase = new Ease();
        readonly Ease ramUsedEase = new Ease();
        readonly Ease gpuEase = new Ease();
        readonly Ease tempEase = new Ease();
        readonly Ease vramEase = new Ease();
        readonly Ease vramUsedEase = new Ease();
        readonly Ease diskEase = new Ease();
        readonly Ease diskIoEase = new Ease();
        readonly Ease downEase = new Ease();
        readonly Ease upEase = new Ease();
        readonly Dictionary<string, Ease> flowDownEase = new Dictionary<string, Ease>();
        readonly Dictionary<string, Ease> flowUpEase = new Dictionary<string, Ease>();
        DateTime lastFrame = DateTime.MinValue;
        DateTime lastHist = DateTime.MinValue;
        DateTime hintUntil = DateTime.MinValue;
        string hint = "";

        Thread worker;
        volatile bool stop;
        float scale = 1f;
        bool clickThrough;
        bool hasPos;
        int savedX;
        int savedY;
        bool wantTop = true;
        bool dragging;
        Point dragOffset;
        Rectangle closeRect;

        Font titleFont;
        Font labelFont;
        Font valueFont;
        Font smallFont;
        Font tinyFont;
        StringFormat textFormat;

        readonly Color bg = Color.FromArgb(14, 16, 22);
        readonly Color text = Color.FromArgb(236, 238, 244);
        readonly Color muted = Color.FromArgb(148, 156, 174);
        readonly Color ok = Color.FromArgb(61, 214, 140);
        readonly Color warn = Color.FromArgb(245, 196, 81);
        readonly Color bad = Color.FromArgb(255, 92, 122);
        readonly Color barBg = Color.FromArgb(38, 42, 56);
        readonly Color line = Color.FromArgb(46, 50, 66);

        ContextMenuStrip menu;
        ToolStripMenuItem miTop;
        ToolStripMenuItem miClick;
        ToolStripMenuItem miAuto;

        static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        public MainForm()
        {
            LoadSettings();
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            TopMost = wantTop;
            BackColor = bg;
            ForeColor = text;
            Text = "Метрики";
            AutoScaleMode = AutoScaleMode.None;
            MinimumSize = new Size(200, 200);
            ClientSize = new Size(BaseW, BaseH);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            UpdateStyles();
            BuildMenu();

            System.Windows.Forms.Timer ui = new System.Windows.Forms.Timer();
            ui.Interval = 32;
            ui.Tick += delegate { Invalidate(); };
            ui.Start();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dpi = (int)GetDpiForWindow(Handle);
            if (dpi <= 0) dpi = 96;
            ApplyDpi(dpi);
            RegisterHotKey(Handle, HotkeyId, 0x0001 | 0x0002 | 0x4000, 0x47);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (hasPos)
            {
                Location = new Point(savedX, savedY);
                if (!VisibleEnough()) Place(LeftScreen());
            }
            else
            {
                Place(LeftScreen());
            }
            if (clickThrough) ApplyClickThrough(true);
            if (IsAdmin())
            {
                Thread boot = new Thread(MaintainAutostart);
                boot.IsBackground = true;
                boot.Start();
            }
            if (worker == null)
            {
                worker = new Thread(WorkerLoop);
                worker.IsBackground = true;
                worker.Priority = ThreadPriority.AboveNormal;
                worker.Start();
            }
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            ApplyDpi(e.DeviceDpiNew);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            stop = true;
            SaveSettings();
            try { UnregisterHotKey(Handle, HotkeyId); } catch { }
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotkeyId)
            {
                clickThrough = !clickThrough;
                ApplyClickThrough(clickThrough);
                hint = clickThrough ? "Клики проходят сквозь окно" : "Клики снова ловит окно";
                hintUntil = DateTime.UtcNow.AddSeconds(2.5);
                SaveSettings();
                Invalidate();
            }
            base.WndProc(ref m);
        }

        void ApplyDpi(int dpi)
        {
            float next = dpi / 96f;
            if (next < 0.75f) next = 1f;
            if (titleFont != null && Math.Abs(next - scale) < 0.02f) return;
            scale = next;
            if (menu != null) menu.Font = SystemFonts.MenuFont;
            DisposeFont(ref titleFont);
            DisposeFont(ref labelFont);
            DisposeFont(ref valueFont);
            DisposeFont(ref smallFont);
            DisposeFont(ref tinyFont);
            AppFonts.Load();
            titleFont = Mk(20f, FontStyle.Bold);
            labelFont = Mk(13f, FontStyle.Regular);
            valueFont = Mk(26f, FontStyle.Bold);
            smallFont = Mk(16f, FontStyle.Regular);
            tinyFont = Mk(13f, FontStyle.Regular);
            if (menu != null) menu.Font = labelFont;
            ClientSize = new Size(S(BaseW), S(BaseH));
        }

        Font Mk(float px, FontStyle style)
        {
            return AppFonts.Make(px * scale, style);
        }

        void DrawUi(Graphics g, string s, Font font, Rectangle r, Color color, TextFormatFlags flags)
        {
            if (string.IsNullOrEmpty(s) || font == null) return;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (SolidBrush brush = new SolidBrush(color))
                g.DrawString(s, font, brush, r, textFormat);
        }

        static void DisposeFont(ref Font f)
        {
            if (f != null) { f.Dispose(); f = null; }
        }

        int S(int px)
        {
            return (int)Math.Round(px * scale);
        }

        void BuildMenu()
        {
            menu = new ContextMenuStrip();
            menu.Items.Add("На левый монитор", null, delegate { Place(LeftScreen()); });
            menu.Items.Add("На основной монитор", null, delegate { Place(Screen.PrimaryScreen); });
            menu.Items.Add("На правый монитор", null, delegate { Place(RightScreen()); });
            menu.Items.Add(new ToolStripSeparator());
            miTop = new ToolStripMenuItem("Поверх всех окон");
            miTop.Click += delegate { TopMost = !TopMost; wantTop = TopMost; SaveSettings(); };
            menu.Items.Add(miTop);
            miClick = new ToolStripMenuItem("Пропускать клики");
            miClick.Click += delegate
            {
                clickThrough = !clickThrough;
                ApplyClickThrough(clickThrough);
                SaveSettings();
            };
            menu.Items.Add(miClick);
            miAuto = new ToolStripMenuItem("Запускать с Windows");
            miAuto.Click += delegate { SetAutostart(!IsAutostart()); };
            menu.Items.Add(miAuto);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Диспетчер задач", null, delegate { try { Process.Start("taskmgr.exe"); } catch { } });
            menu.Items.Add("Закрыть", null, delegate { Close(); });
            menu.Opening += delegate
            {
                miTop.Checked = TopMost;
                miClick.Checked = clickThrough;
                miAuto.Checked = IsAutostart();
            };
            ContextMenuStrip = menu;
            textFormat = new StringFormat();
            textFormat.Alignment = StringAlignment.Near;
            textFormat.LineAlignment = StringAlignment.Center;
            textFormat.Trimming = StringTrimming.EllipsisCharacter;
            textFormat.FormatFlags = StringFormatFlags.NoWrap;
        }

        void ApplyClickThrough(bool on)
        {
            if (!IsHandleCreated) return;
            if (on) Opacity = 0.96;
            IntPtr style = GetWindowLongPtrW(Handle, -20);
            long s = style.ToInt64();
            if (on) s |= 0x20L;
            else s &= ~0x20L;
            SetWindowLongPtrW(Handle, -20, new IntPtr(s));
            if (!on) Opacity = 1;
        }

        void Place(Screen screen)
        {
            if (screen == null) screen = Screen.PrimaryScreen;
            Rectangle wa = screen.WorkingArea;
            int x;
            if (screen.Bounds.X < Screen.PrimaryScreen.Bounds.X) x = wa.Right - Width - S(16);
            else if (screen.Bounds.Right > Screen.PrimaryScreen.Bounds.Right) x = wa.Left + S(16);
            else x = wa.Right - Width - S(16);
            int y = wa.Top + S(16);
            Location = new Point(x, y);
            SaveSettings();
        }

        static Screen LeftScreen()
        {
            Screen best = null;
            foreach (Screen s in Screen.AllScreens)
            {
                if (s.Bounds.X < 0 && (best == null || s.Bounds.X < best.Bounds.X)) best = s;
            }
            return best ?? Screen.PrimaryScreen;
        }

        static Screen RightScreen()
        {
            Screen best = null;
            foreach (Screen s in Screen.AllScreens)
            {
                if (s.Bounds.X > 0 && !s.Primary && (best == null || s.Bounds.X > best.Bounds.X)) best = s;
            }
            return best ?? Screen.PrimaryScreen;
        }

        bool VisibleEnough()
        {
            Rectangle me = new Rectangle(Location, Size);
            foreach (Screen s in Screen.AllScreens)
            {
                Rectangle inter = Rectangle.Intersect(me, s.WorkingArea);
                if (inter.Width > 80 && inter.Height > 80) return true;
            }
            return false;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && closeRect.Contains(e.Location))
            {
                Close();
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                dragging = true;
                dragOffset = e.Location;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                Point screen = PointToScreen(e.Location);
                Location = new Point(screen.X - dragOffset.X, screen.Y - dragOffset.Y);
            }
            Cursor = closeRect.Contains(e.Location) ? Cursors.Hand : Cursors.SizeAll;
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging)
            {
                dragging = false;
                SaveSettings();
            }
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(bg);
            if (titleFont == null) return;

            Snapshot snap;
            lock (gate) snap = current;
            Remember(snap);
            Follow(snap);

            Color stripe = ok;
            if (snap.Hung.Count > 0) stripe = bad;
            else
            {
                double worst = 0;
                if (snap.HasCpu && cpuEase.Value > worst) worst = cpuEase.Value;
                if (snap.HasRam && ramEase.Value > worst) worst = ramEase.Value;
                if (snap.HasGpu && gpuEase.Value > worst) worst = gpuEase.Value;
                if (snap.HasVram && vramEase.Value > worst) worst = vramEase.Value;
                if (snap.HasDisk && diskEase.Value > worst) worst = diskEase.Value;
                if (snap.HasTemp && tempEase.Value >= 88) worst = 100;
                stripe = Level(worst);
                bool netHotStripe = snap.HasNet && (snap.Down >= 5 || snap.Up >= 5 || downEase.Value >= 5 || upEase.Value >= 5);
                if (netHotStripe && worst < 75) stripe = warn;
            }
            using (SolidBrush b = new SolidBrush(stripe))
                g.FillRectangle(b, 0, 0, S(5), ClientSize.Height);

            int x = S(18);
            int w = ClientSize.Width - S(32);
            DrawUi(g, "Метрики", titleFont, new Rectangle(x, S(12), w - S(40), S(28)), text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            int cs = S(22);
            closeRect = new Rectangle(ClientSize.Width - S(18) - cs, S(14), cs, cs);
            using (Pen p = new Pen(muted, Math.Max(1.4f, scale)))
            {
                g.DrawEllipse(p, closeRect);
                int m = S(6);
                g.DrawLine(p, closeRect.Left + m, closeRect.Top + m, closeRect.Right - m, closeRect.Bottom - m);
                g.DrawLine(p, closeRect.Right - m, closeRect.Top + m, closeRect.Left + m, closeRect.Bottom - m);
            }

            string ageText = "";
            Color ageColor = muted;
            if (snap.TakenUtc == DateTime.MinValue)
            {
                ageText = "Сбор данных";
            }
            else
            {
                double age = (DateTime.UtcNow - snap.TakenUtc).TotalSeconds;
                if (age > 3)
                {
                    ageText = "Нет обновления " + ((int)age).ToString(CultureInfo.InvariantCulture) + " с";
                    ageColor = bad;
                }
                else ageText = "Обновляется";
            }
            if (DateTime.UtcNow < hintUntil) ageText = hint;
            DrawUi(g, ageText, tinyFont, new Rectangle(x, S(38), w, S(16)), ageColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            int y = S(76);
            y = DrawMetric(g, x, y, w, "Процессор", snap.HasCpu ? Pct(cpuEase.Value) : "н/д", snap.HasCpu ? (double?)cpuEase.Value : null, cpuRing, text);
            string ramVal = snap.HasRam ? Pct(ramEase.Value) + "   " + Gb(ramUsedEase.Value) + " / " + Gb(snap.RamTotal) + " ГБ" : "н/д";
            y = DrawMetric(g, x, y, w, "Память", ramVal, snap.HasRam ? (double?)ramEase.Value : null, ramRing, text);

            string gpuLabel = "Чип";
            if (!string.IsNullOrEmpty(snap.GpuName)) gpuLabel = "Чип  ·  " + snap.GpuName;
            string gpuVal = "н/д";
            if (snap.HasGpu)
            {
                gpuVal = Pct(gpuEase.Value);
                if (snap.HasTemp) gpuVal += "   " + ((int)Math.Round(tempEase.Value)).ToString(CultureInfo.InvariantCulture) + "°C";
            }
            y = DrawMetric(g, x, y, w, gpuLabel, gpuVal, snap.HasGpu ? (double?)gpuEase.Value : null, gpuRing, text);

            string vramVal = snap.HasVram ? Pct(vramEase.Value) + "   " + Gb(vramUsedEase.Value) + " / " + Gb(snap.VramTotal) + " ГБ" : "н/д";
            y = DrawMetric(g, x, y, w, "Видеопамять", vramVal, snap.HasVram ? (double?)vramEase.Value : null, null, text);

            string diskVal = "н/д";
            if (snap.HasDisk) diskVal = Pct(diskEase.Value) + "   " + Num(diskIoEase.Value) + " МБ/с";
            y = DrawMetric(g, x, y, w, "Диск", diskVal, snap.HasDisk ? (double?)diskEase.Value : null, diskRing, text);

            string netVal = "н/д";
            if (snap.HasNet) netVal = "↓ " + Num(downEase.Value) + "   ↑ " + Num(upEase.Value) + " Мбит/с";
            bool netHot = snap.HasNet && (snap.Down >= 5 || snap.Up >= 5 || downEase.Value >= 5 || upEase.Value >= 5);
            y = DrawMetric(g, x, y, w, "Сеть", netVal, null, null, netHot ? warn : text);
            if (snap.HasNet && !string.IsNullOrEmpty(snap.NetName))
            {
                DrawUi(g, snap.NetName, tinyFont, new Rectangle(x, y - S(18), w, S(16)), muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
            if (snap.Flows != null && snap.Flows.Count > 0)
            {
                y += S(10);
                int limit = snap.Flows.Count > 3 ? 3 : snap.Flows.Count;
                for (int i = 0; i < limit; i++)
                {
                    FlowHit f = snap.Flows[i];
                    double d = f.Down;
                    double u = f.Up;
                    string key = f.Pid.ToString(CultureInfo.InvariantCulture);
                    Ease ed;
                    Ease eu;
                    if (flowDownEase.TryGetValue(key, out ed) && ed.On) d = ed.Value;
                    if (flowUpEase.TryGetValue(key, out eu) && eu.On) u = eu.Value;
                    DrawUi(g, FlowText(f.Name, d, u), smallFont, new Rectangle(x, y, w, S(22)), warn,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    y += S(26);
                }
            }
            else
            {
                if (snap.HasNet && (snap.Down >= 5 || downEase.Value >= 5))
                {
                    y += S(10);
                    DrawUi(g, "Скачивание   " + Num(downEase.Value) + " Мбит/с", smallFont, new Rectangle(x, y, w, S(22)), warn,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    y += S(26);
                }
                if (snap.HasNet && (snap.Up >= 5 || upEase.Value >= 5))
                {
                    if (!(snap.Down >= 5 || downEase.Value >= 5)) y += S(10);
                    DrawUi(g, "Отдача   " + Num(upEase.Value) + " Мбит/с", smallFont, new Rectangle(x, y, w, S(22)), warn,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    y += S(26);
                }
            }

            y += S(18);
            using (Pen sep = new Pen(line, 1f))
                g.DrawLine(sep, x, y, x + w, y);
            y += S(20);

            Color hungColor = snap.Hung.Count > 0 ? bad : muted;
            string hungTitle = snap.Hung.Count > 0 ? "Не отвечают  " + snap.Hung.Count.ToString(CultureInfo.InvariantCulture) : "Не отвечают";
            DrawUi(g, hungTitle, labelFont, new Rectangle(x, y, w, S(18)), hungColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            y += S(20);
            if (snap.Hung.Count == 0)
            {
                DrawUi(g, "Нет", smallFont, new Rectangle(x, y, w, S(20)), ok,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                y += S(22);
            }
            else
            {
                int limit = snap.Hung.Count > 3 ? 3 : snap.Hung.Count;
                for (int i = 0; i < limit; i++)
                {
                    DrawUi(g, snap.Hung[i], smallFont, new Rectangle(x, y, w, S(20)), bad,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    y += S(22);
                }
            }

            y += S(22);
            DrawUi(g, "Кто грузит процессор", labelFont, new Rectangle(x, y, w, S(18)), muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            y += S(20);
            if (snap.TopName.Count == 0)
            {
                DrawUi(g, "Без явного лидера", smallFont, new Rectangle(x, y, w, S(20)), muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                y += S(22);
            }
            else
            {
                int limit = snap.TopName.Count > 4 ? 4 : snap.TopName.Count;
                for (int i = 0; i < limit; i++)
                {
                    Color c = Level(snap.TopPct[i]);
                    string row = Pct(snap.TopPct[i]) + "   " + snap.TopName[i];
                    DrawUi(g, row, smallFont, new Rectangle(x, y, w, S(20)), c,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    y += S(22);
                }
            }

            y += S(22);
            DrawUi(g, "Кто грузит память", labelFont, new Rectangle(x, y, w, S(18)), muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            y += S(20);
            if (snap.MemName.Count == 0)
            {
                DrawUi(g, "Без явного лидера", smallFont, new Rectangle(x, y, w, S(20)), muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                y += S(22);
            }
            else
            {
                int limit = snap.MemName.Count > 4 ? 4 : snap.MemName.Count;
                for (int i = 0; i < limit; i++)
                {
                    double share = 0;
                    if (snap.RamTotal > 0.1) share = snap.MemMb[i] / (snap.RamTotal * 1024.0) * 100.0;
                    string row = MemText(snap.MemMb[i]) + "   " + snap.MemName[i];
                    DrawUi(g, row, smallFont, new Rectangle(x, y, w, S(20)), Level(share),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    y += S(22);
                }
            }

            int need = y + S(18);
            if (Math.Abs(ClientSize.Height - need) > 6)
                ClientSize = new Size(ClientSize.Width, need);
        }

        int DrawMetric(Graphics g, int x, int y, int w, string label, string value, double? bar, Ring ring, Color valueColor)
        {
            DrawUi(g, label, labelFont, new Rectangle(x, y, w, S(16)), muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            int sparkW = S(86);
            int valueW = w;
            if (ring != null && ring.N > 1) valueW = w - sparkW - S(8);
            DrawUi(g, value, valueFont, new Rectangle(x, y + S(16), valueW, S(30)), valueColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            if (ring != null && ring.N > 1)
            {
                Color c = bar.HasValue ? Level(bar.Value) : muted;
                DrawSpark(g, new Rectangle(x + w - sparkW, y + S(18), sparkW, S(26)), ring, c);
            }
            int barY = y + S(48);
            Rectangle barRect = new Rectangle(x, barY, w, S(7));
            using (SolidBrush bb = new SolidBrush(barBg))
                FillRound(g, bb, barRect, S(3));
            if (bar.HasValue)
            {
                double pct = bar.Value;
                if (pct < 0) pct = 0;
                if (pct > 100) pct = 100;
                int fill = (int)Math.Round(barRect.Width * (pct / 100.0));
                if (fill > 0)
                {
                    Rectangle fr = new Rectangle(barRect.X, barRect.Y, Math.Max(fill, S(6)), barRect.Height);
                    using (SolidBrush fb = new SolidBrush(Level(pct)))
                        FillRound(g, fb, fr, S(3));
                }
            }
            return y + S(80);
        }

        void DrawSpark(Graphics g, Rectangle r, Ring ring, Color color)
        {
            if (ring.N < 2) return;
            float min = 0f;
            float max = 100f;
            using (Pen p = new Pen(color, Math.Max(1.4f, scale)))
            {
                PointF[] pts = new PointF[ring.N];
                for (int i = 0; i < ring.N; i++)
                {
                    int idx = (ring.I - ring.N + i + ring.V.Length) % ring.V.Length;
                    float v = ring.V[idx];
                    float t = (v - min) / (max - min);
                    if (t < 0) t = 0;
                    if (t > 1) t = 1;
                    float px = r.Left + (r.Width - 1) * (i / (float)(ring.N - 1));
                    float py = r.Bottom - 1 - t * (r.Height - 2);
                    pts[i] = new PointF(px, py);
                }
                if (pts.Length >= 2) g.DrawLines(p, pts);
            }
        }

        static void FillRound(Graphics g, Brush brush, Rectangle r, int radius)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d < 2)
            {
                g.FillRectangle(brush, r);
                return;
            }
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
        }

        void Follow(Snapshot snap)
        {
            DateTime now = DateTime.UtcNow;
            double dt = lastFrame == DateTime.MinValue ? 0 : (now - lastFrame).TotalSeconds;
            lastFrame = now;
            if (dt < 0) dt = 0;
            if (dt > 0.25) dt = 0.25;
            if (snap.HasCpu) cpuEase.Toward(snap.Cpu, dt);
            if (snap.HasRam)
            {
                ramEase.Toward(snap.RamPct, dt);
                ramUsedEase.Toward(snap.RamUsed, dt);
            }
            if (snap.HasGpu) gpuEase.Toward(snap.Gpu, dt);
            if (snap.HasTemp) tempEase.Toward(snap.Temp, dt);
            if (snap.HasVram)
            {
                vramEase.Toward(snap.VramPct, dt);
                vramUsedEase.Toward(snap.VramUsed, dt);
            }
            if (snap.HasDisk)
            {
                diskEase.Toward(snap.DiskBusy, dt);
                diskIoEase.Toward(snap.DiskMBs, dt);
            }
            if (snap.HasNet)
            {
                downEase.Toward(snap.Down, dt);
                upEase.Toward(snap.Up, dt);
            }
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            if (snap.Flows != null)
            {
                for (int i = 0; i < snap.Flows.Count; i++)
                {
                    FlowHit f = snap.Flows[i];
                    string key = f.Pid.ToString(CultureInfo.InvariantCulture);
                    seen[key] = true;
                    Ease ed;
                    Ease eu;
                    if (!flowDownEase.TryGetValue(key, out ed))
                    {
                        ed = new Ease();
                        eu = new Ease();
                        flowDownEase[key] = ed;
                        flowUpEase[key] = eu;
                    }
                    else if (!flowUpEase.TryGetValue(key, out eu))
                    {
                        eu = new Ease();
                        flowUpEase[key] = eu;
                    }
                    ed.Toward(f.Down, dt);
                    eu.Toward(f.Up, dt);
                }
            }
            if (seen.Count != flowDownEase.Count)
            {
                List<string> drop = new List<string>();
                foreach (KeyValuePair<string, Ease> kv in flowDownEase)
                {
                    if (!seen.ContainsKey(kv.Key)) drop.Add(kv.Key);
                }
                for (int i = 0; i < drop.Count; i++)
                {
                    flowDownEase.Remove(drop[i]);
                    flowUpEase.Remove(drop[i]);
                }
            }
        }

        void Remember(Snapshot snap)
        {
            if (snap.TakenUtc == DateTime.MinValue || snap.TakenUtc == lastHist) return;
            lastHist = snap.TakenUtc;
            if (snap.HasCpu) cpuRing.Push((float)snap.Cpu);
            if (snap.HasRam) ramRing.Push((float)snap.RamPct);
            if (snap.HasGpu) gpuRing.Push((float)snap.Gpu);
            if (snap.HasDisk) diskRing.Push((float)snap.DiskBusy);
        }

        Color Level(double v)
        {
            if (v >= 90) return bad;
            if (v >= 75) return warn;
            return ok;
        }

        static string Pct(double v)
        {
            return ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture) + "%";
        }

        static string Gb(double v)
        {
            return v.ToString("0.0", Ru);
        }

        static string Num(double v)
        {
            return v.ToString("0.0", Ru);
        }

        static string MemText(double mb)
        {
            if (mb >= 1024) return (mb / 1024.0).ToString("0.0", Ru) + " ГБ";
            return ((int)Math.Round(mb)).ToString(CultureInfo.InvariantCulture) + " МБ";
        }

        static string FlowText(string name, double down, double up)
        {
            if (string.IsNullOrEmpty(name)) name = "Процесс";
            if (down >= 5 && up >= 5) return name + "   ↓ " + Num(down) + "   ↑ " + Num(up) + " Мбит/с";
            if (down >= up) return name + "   ↓ " + Num(down) + " Мбит/с";
            return name + "   ↑ " + Num(up) + " Мбит/с";
        }

        void WorkerLoop()
        {
            Sampler sampler = new Sampler();
            int tick = 0;
            while (!stop)
            {
                try
                {
                    bool processes = (tick % 2) == 0;
                    tick++;
                    Snapshot snap = sampler.Collect(processes);
                    lock (gate) current = snap;
                }
                catch (Exception ex)
                {
                    Log("collect " + ex.GetType().Name + " " + ex.Message);
                }
                for (int i = 0; i < 5 && !stop; i++) Thread.Sleep(100);
            }
        }

        static string SettingsDir()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glance");
            Directory.CreateDirectory(dir);
            return dir;
        }

        static string SettingsPath()
        {
            return Path.Combine(SettingsDir(), "window.txt");
        }

        static void Log(string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(SettingsDir(), "log.txt"),
                    DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + " " + msg + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        void LoadSettings()
        {
            try
            {
                string path = SettingsPath();
                if (!File.Exists(path)) return;
                string[] lines = File.ReadAllLines(path);
                bool gotX = false, gotY = false;
                foreach (string raw in lines)
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = raw.Substring(0, eq);
                    string v = raw.Substring(eq + 1);
                    if (k == "X") { savedX = int.Parse(v, CultureInfo.InvariantCulture); gotX = true; }
                    else if (k == "Y") { savedY = int.Parse(v, CultureInfo.InvariantCulture); gotY = true; }
                    else if (k == "TopMost") wantTop = v == "1";
                    else if (k == "ClickThrough") clickThrough = v == "1";
                }
                hasPos = gotX && gotY;
            }
            catch { }
        }

        void SaveSettings()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("X=").Append(Location.X.ToString(CultureInfo.InvariantCulture)).Append('\n');
                sb.Append("Y=").Append(Location.Y.ToString(CultureInfo.InvariantCulture)).Append('\n');
                sb.Append("TopMost=").Append(TopMost ? "1" : "0").Append('\n');
                sb.Append("ClickThrough=").Append(clickThrough ? "1" : "0").Append('\n');
                File.WriteAllText(SettingsPath(), sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        static bool IsAdmin()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(id);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        static string AutostartOffMarker()
        {
            return Path.Combine(SettingsDir(), "autostart-off");
        }

        static void RemoveRunKey()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k != null) k.DeleteValue("Glance", false);
                }
            }
            catch { }
        }

        static bool TaskExists()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "schtasks.exe";
                psi.Arguments = "/Query /TN Glance";
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                if (p == null) return false;
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(); } catch { }
                    return false;
                }
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        static void MaintainAutostart()
        {
            try
            {
                if (File.Exists(AutostartOffMarker()))
                {
                    RemoveRunKey();
                    return;
                }
                if (!TaskExists()) RunTaskScript(false);
                if (TaskExists()) RemoveRunKey();
            }
            catch (Exception ex) { Log("task " + ex.GetType().Name + " " + ex.Message); }
        }

        static void RunTaskScript(bool off)
        {
            string script = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "register-task.ps1");
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "powershell.exe";
            psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -Exe \"" + Application.ExecutablePath + "\"" + (off ? " -Off" : "");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            Process p = Process.Start(psi);
            if (p == null) return;
            if (!p.WaitForExit(20000))
            {
                try { p.Kill(); } catch { }
                Log("task timeout");
                return;
            }
            if (p.ExitCode != 0) Log("task exit " + p.ExitCode.ToString(CultureInfo.InvariantCulture));
        }

        static bool IsAutostart()
        {
            if (IsAdmin()) return TaskExists();
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (k == null) return false;
                    object v = k.GetValue("Glance");
                    return v != null && v.ToString().IndexOf("Glance.exe", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        static void SetAutostart(bool on)
        {
            try
            {
                if (IsAdmin())
                {
                    string marker = AutostartOffMarker();
                    if (on)
                    {
                        if (File.Exists(marker)) File.Delete(marker);
                        RunTaskScript(false);
                        if (TaskExists()) RemoveRunKey();
                    }
                    else
                    {
                        File.WriteAllText(marker, "off", Encoding.UTF8);
                        RunTaskScript(true);
                        RemoveRunKey();
                    }
                    return;
                }
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("Glance", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("Glance", false);
                }
            }
            catch (Exception ex) { Log("autostart " + ex.Message); }
        }

        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }

    sealed class Sampler
    {
        ulong prevIdle, prevTotal;
        bool hasCpu;
        bool elevated;
        DiskReader disk = new DiskReader();
        GpuReader gpu = new GpuReader();
        NetReader net = new NetReader();
        ProcReader procs = new ProcReader();
        bool gpuTried;
        bool diskTried;
        List<string> holdHung = new List<string>();
        List<string> holdTop = new List<string>();
        List<double> holdPct = new List<double>();
        List<string> holdMem = new List<string>();
        List<double> holdMemMb = new List<double>();

        public Sampler()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(id);
                elevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { elevated = false; }
        }

        public Snapshot Collect(bool processes)
        {
            Snapshot s = new Snapshot();
            s.TakenUtc = DateTime.UtcNow;
            ReadCpu(s);
            ReadRam(s);
            if (!gpuTried)
            {
                gpuTried = true;
                try
                {
                    if (!gpu.Init()) MainFormLog("gpu init failed");
                }
                catch (Exception ex) { MainFormLog("gpu " + ex.GetType().Name + " " + ex.Message); }
            }
            int util, temp;
            double vu, vt;
            bool hasUtil, hasTemp, hasVram;
            if (gpu.TrySample(out util, out temp, out vu, out vt, out hasUtil, out hasTemp, out hasVram))
            {
                s.GpuName = gpu.Name;
                s.HasGpu = hasUtil;
                s.Gpu = util;
                s.HasTemp = hasTemp;
                s.Temp = temp;
                s.HasVram = hasVram;
                s.VramUsed = vu;
                s.VramTotal = vt;
                if (vt > 0.01) s.VramPct = vu / vt * 100.0;
            }
            if (!diskTried)
            {
                diskTried = true;
                try
                {
                    if (!disk.Init()) MainFormLog("disk init failed");
                }
                catch (Exception ex) { MainFormLog("disk " + ex.GetType().Name + " " + ex.Message); }
            }
            double busy, mbs;
            if (disk.TrySample(out busy, out mbs))
            {
                s.HasDisk = true;
                s.DiskBusy = busy;
                s.DiskMBs = mbs;
            }
            double down, up;
            string netName;
            if (net.TrySample(out down, out up, out netName))
            {
                s.HasNet = true;
                s.Down = down;
                s.Up = up;
                s.NetName = netName;
            }
            if (processes)
            {
                procs.Sample(s.Hung, s.TopName, s.TopPct, s.MemName, s.MemMb);
                holdHung = s.Hung;
                holdTop = s.TopName;
                holdPct = s.TopPct;
                holdMem = s.MemName;
                holdMemMb = s.MemMb;
            }
            else
            {
                s.Hung = holdHung;
                s.TopName = holdTop;
                s.TopPct = holdPct;
                s.MemName = holdMem;
                s.MemMb = holdMemMb;
            }
            if (elevated)
            {
                try { net.SampleFlows(); }
                catch (Exception ex) { MainFormLog("flows " + ex.GetType().Name + " " + ex.Message); }
                for (int i = 0; i < net.Hot.Count; i++)
                {
                    PidRate rate = net.Hot[i];
                    FlowHit hit = new FlowHit();
                    hit.Pid = rate.Pid;
                    hit.Name = procs.NameOf(rate.Pid);
                    hit.Down = rate.Down;
                    hit.Up = rate.Up;
                    s.Flows.Add(hit);
                }
            }
            return s;
        }

        void ReadCpu(Snapshot s)
        {
            FILETIME idle, kernel, user;
            if (!GetSystemTimes(out idle, out kernel, out user)) return;
            ulong idleNow = ToULong(idle);
            ulong totalNow = ToULong(kernel) + ToULong(user);
            if (hasCpu && totalNow > prevTotal)
            {
                double dTot = totalNow - prevTotal;
                double dIdle = idleNow - prevIdle;
                double pct = (1.0 - dIdle / dTot) * 100.0;
                if (pct < 0) pct = 0;
                if (pct > 100) pct = 100;
                s.HasCpu = true;
                s.Cpu = pct;
            }
            prevIdle = idleNow;
            prevTotal = totalNow;
            hasCpu = true;
        }

        static void ReadRam(Snapshot s)
        {
            MEMORYSTATUSEX m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (!GlobalMemoryStatusEx(ref m)) return;
            s.HasRam = true;
            s.RamPct = m.dwMemoryLoad;
            s.RamTotal = m.ullTotalPhys / 1073741824.0;
            s.RamUsed = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
        }

        static void MainFormLog(string msg)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glance");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "log.txt"),
                    DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + " " + msg + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        static ulong ToULong(FILETIME ft)
        {
            return ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
        }

        [DllImport("kernel32.dll")]
        static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    sealed class GpuReader
    {
        IntPtr device;
        bool ready;
        public string Name = "";

        public bool Init()
        {
            if (nvmlInit_v2() != 0) return false;
            uint count = 0;
            if (nvmlDeviceGetCount_v2(ref count) != 0 || count == 0) return false;
            IntPtr best = IntPtr.Zero;
            ulong bestMem = 0;
            string bestName = "";
            bool bestGeForce = false;
            for (uint i = 0; i < count; i++)
            {
                IntPtr h;
                if (nvmlDeviceGetHandleByIndex_v2(i, out h) != 0) continue;
                byte[] buf = new byte[128];
                string name = "";
                if (nvmlDeviceGetName(h, buf, (uint)buf.Length) == 0)
                {
                    int len = Array.IndexOf(buf, (byte)0);
                    if (len < 0) len = buf.Length;
                    name = Encoding.ASCII.GetString(buf, 0, len);
                }
                NvmlMemory mem;
                ulong total = 0;
                if (nvmlDeviceGetMemoryInfo(h, out mem) == 0) total = mem.Total;
                bool ge = name.IndexOf("GeForce", StringComparison.OrdinalIgnoreCase) >= 0;
                if (best == IntPtr.Zero || (ge && !bestGeForce) || (ge == bestGeForce && total > bestMem))
                {
                    best = h;
                    bestMem = total;
                    bestName = name;
                    bestGeForce = ge;
                }
            }
            if (best == IntPtr.Zero) return false;
            device = best;
            Name = Short(bestName);
            ready = true;
            return true;
        }

        static string Short(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Видеокарта";
            string n = name.Replace("NVIDIA GeForce ", "");
            if (n.Length > 22) n = n.Substring(0, 22);
            return n;
        }

        public bool TrySample(out int util, out int temp, out double usedGb, out double totalGb, out bool hasUtil, out bool hasTemp, out bool hasVram)
        {
            util = 0;
            temp = 0;
            usedGb = 0;
            totalGb = 0;
            hasUtil = false;
            hasTemp = false;
            hasVram = false;
            if (!ready) return false;
            NvmlUtilization u;
            if (nvmlDeviceGetUtilizationRates(device, out u) == 0)
            {
                util = (int)u.Gpu;
                hasUtil = true;
            }
            uint t;
            if (nvmlDeviceGetTemperature(device, 0, out t) == 0)
            {
                temp = (int)t;
                hasTemp = true;
            }
            NvmlMemory m;
            if (nvmlDeviceGetMemoryInfo(device, out m) == 0 && m.Total > 0)
            {
                usedGb = m.Used / 1073741824.0;
                totalGb = m.Total / 1073741824.0;
                hasVram = true;
            }
            return hasUtil || hasTemp || hasVram;
        }

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int nvmlInit_v2();

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int nvmlDeviceGetCount_v2(ref uint count);

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization util);

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int nvmlDeviceGetTemperature(IntPtr device, uint sensor, out uint temp);

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory mem);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    sealed class DiskReader
    {
        IntPtr query;
        IntPtr idle;
        IntPtr bytes;
        bool ready;
        int warmup;

        public bool Init()
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0) return false;
            int a = PdhAddEnglishCounter(query, @"\PhysicalDisk(_Total)\% Idle Time", IntPtr.Zero, out idle);
            int b = PdhAddEnglishCounter(query, @"\PhysicalDisk(_Total)\Disk Bytes/sec", IntPtr.Zero, out bytes);
            if (a != 0 || b != 0) return false;
            PdhCollectQueryData(query);
            ready = true;
            return true;
        }

        public bool TrySample(out double busy, out double mbps)
        {
            busy = 0;
            mbps = 0;
            if (!ready) return false;
            int rc = PdhCollectQueryData(query);
            if (rc != 0) return false;
            warmup++;
            if (warmup < 2) return false;
            PdhFmt v;
            bool ok = false;
            if (PdhGetFormattedCounterValue(idle, 0x200, IntPtr.Zero, out v) == 0)
            {
                busy = 100.0 - v.doubleValue;
                if (busy < 0) busy = 0;
                if (busy > 100) busy = 100;
                ok = true;
            }
            if (PdhGetFormattedCounterValue(bytes, 0x200, IntPtr.Zero, out v) == 0)
            {
                mbps = v.doubleValue / 1048576.0;
                if (mbps < 0) mbps = 0;
                ok = true;
            }
            return ok;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern int PdhOpenQuery(string src, IntPtr user, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);

        [DllImport("pdh.dll")]
        static extern int PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll")]
        static extern int PdhGetFormattedCounterValue(IntPtr counter, int format, IntPtr type, out PdhFmt value);
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    struct PdhFmt
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double doubleValue;
    }

    sealed class NetReader
    {
        Dictionary<string, long[]> prev = new Dictionary<string, long[]>();
        Dictionary<string, string> names = new Dictionary<string, string>();
        DateTime prevT;
        bool hasPrev;
        public readonly List<PidRate> Hot = new List<PidRate>();
        Dictionary<string, ulong[]> flowPrev = new Dictionary<string, ulong[]>();
        Dictionary<string, bool> flowEnabled = new Dictionary<string, bool>();
        DateTime flowT;
        bool hasFlow;
        bool flowLogged;
        int selfPid;

        public NetReader()
        {
            selfPid = Process.GetCurrentProcess().Id;
        }

        public bool TrySample(out double down, out double up, out string name)
        {
            down = 0;
            up = 0;
            name = "";
            DateTime now = DateTime.UtcNow;
            Dictionary<string, long[]> cur = new Dictionary<string, long[]>();
            NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
            for (int i = 0; i < nics.Length; i++)
            {
                NetworkInterface nic = nics[i];
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                string desc = (nic.Description ?? "").ToLowerInvariant();
                if (desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("hyper-v") ||
                    desc.Contains("bluetooth") || desc.Contains("loopback") || desc.Contains("pseudo") ||
                    desc.Contains("vpn") || desc.Contains("wintun") || desc.Contains("tap-") ||
                    desc.Contains("meta") || desc.Contains("radmin")) continue;
                long rx, tx;
                try
                {
                    IPv4InterfaceStatistics st = nic.GetIPv4Statistics();
                    rx = st.BytesReceived;
                    tx = st.BytesSent;
                }
                catch { continue; }
                cur[nic.Id] = new long[] { rx, tx };
                names[nic.Id] = nic.Name;
            }
            if (!hasPrev)
            {
                prev = cur;
                prevT = now;
                hasPrev = true;
                return false;
            }
            double sec = (now - prevT).TotalSeconds;
            if (sec < 0.2) sec = 0.2;
            double best = -1;
            string bestId = "";
            double bestDown = 0, bestUp = 0;
            foreach (KeyValuePair<string, long[]> kv in cur)
            {
                long[] old;
                if (!prev.TryGetValue(kv.Key, out old)) continue;
                long dr = kv.Value[0] - old[0];
                long ds = kv.Value[1] - old[1];
                if (dr < 0) dr = 0;
                if (ds < 0) ds = 0;
                double score = dr + ds;
                if (score > best)
                {
                    best = score;
                    bestId = kv.Key;
                    bestDown = dr * 8.0 / sec / 1000000.0;
                    bestUp = ds * 8.0 / sec / 1000000.0;
                }
            }
            prev = cur;
            prevT = now;
            if (best < 0) return false;
            down = bestDown;
            up = bestUp;
            string n;
            name = names.TryGetValue(bestId, out n) ? n : "";
            return true;
        }

        public void SampleFlows()
        {
            Hot.Clear();
            DateTime now = DateTime.UtcNow;
            Dictionary<string, ulong[]> cur = new Dictionary<string, ulong[]>();
            Dictionary<string, uint> pids = new Dictionary<string, uint>();
            try { ReadFamily(2, cur, pids); }
            catch (Exception ex) { FlowLogOnce("v4 " + ex.GetType().Name + " " + ex.Message); }
            try { ReadFamily(23, cur, pids); }
            catch (Exception ex) { FlowLogOnce("v6 " + ex.GetType().Name + " " + ex.Message); }

            Dictionary<uint, double[]> acc = new Dictionary<uint, double[]>();
            if (hasFlow)
            {
                double sec = (now - flowT).TotalSeconds;
                if (sec < 0.2) sec = 0.2;
                foreach (KeyValuePair<string, ulong[]> kv in cur)
                {
                    ulong[] old;
                    if (!flowPrev.TryGetValue(kv.Key, out old)) continue;
                    uint pid;
                    if (!pids.TryGetValue(kv.Key, out pid) || pid == 0 || pid == 4 || (int)pid == selfPid) continue;
                    ulong inn = kv.Value[0] < old[0] ? old[0] : kv.Value[0];
                    ulong outb = kv.Value[1] < old[1] ? old[1] : kv.Value[1];
                    double[] a;
                    if (!acc.TryGetValue(pid, out a))
                    {
                        a = new double[2];
                        acc[pid] = a;
                    }
                    a[0] += (inn - old[0]) * 8.0 / sec / 1000000.0;
                    a[1] += (outb - old[1]) * 8.0 / sec / 1000000.0;
                }
            }
            flowPrev = cur;
            flowT = now;
            hasFlow = true;
            List<string> stale = new List<string>();
            foreach (KeyValuePair<string, bool> en in flowEnabled)
            {
                if (!cur.ContainsKey(en.Key)) stale.Add(en.Key);
            }
            for (int i = 0; i < stale.Count; i++) flowEnabled.Remove(stale[i]);

            List<PidRate> list = new List<PidRate>();
            foreach (KeyValuePair<uint, double[]> kv in acc)
            {
                if (kv.Value[0] < 5 && kv.Value[1] < 5) continue;
                PidRate r = new PidRate();
                r.Pid = kv.Key;
                r.Down = kv.Value[0];
                r.Up = kv.Value[1];
                list.Add(r);
            }
            list.Sort(delegate(PidRate a, PidRate b)
            {
                double pa = a.Down > a.Up ? a.Down : a.Up;
                double pb = b.Down > b.Up ? b.Down : b.Up;
                return pb.CompareTo(pa);
            });
            int n = list.Count > 3 ? 3 : list.Count;
            for (int i = 0; i < n; i++) Hot.Add(list[i]);
        }

        void ReadFamily(int family, Dictionary<string, ulong[]> cur, Dictionary<string, uint> pids)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 5, 0);
            if (size <= 0) return;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                uint rc = GetExtendedTcpTable(buf, ref size, false, family, 5, 0);
                if (rc != 0) return;
                int count = Marshal.ReadInt32(buf);
                if (family == 2) ReadV4(buf, count, cur, pids);
                else ReadV6(buf, count, cur, pids);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        void ReadV4(IntPtr buf, int count, Dictionary<string, ulong[]> cur, Dictionary<string, uint> pids)
        {
            int rowSize = Marshal.SizeOf(typeof(MibTcpRowOwner));
            int limit = count > 600 ? 600 : count;
            for (int i = 0; i < limit; i++)
            {
                IntPtr p = new IntPtr(buf.ToInt64() + 4 + (long)i * rowSize);
                MibTcpRowOwner row = (MibTcpRowOwner)Marshal.PtrToStructure(p, typeof(MibTcpRowOwner));
                if (row.State != 5) continue;
                if ((row.LocalAddr & 0xFF) == 127 || (row.RemoteAddr & 0xFF) == 127) continue;
                if (row.RemoteAddr == 0) continue;
                MibTcpRow est = new MibTcpRow();
                est.State = row.State;
                est.LocalAddr = row.LocalAddr;
                est.LocalPort = row.LocalPort;
                est.RemoteAddr = row.RemoteAddr;
                est.RemotePort = row.RemotePort;
                string key = "4:" + row.LocalAddr.ToString(CultureInfo.InvariantCulture) + ":" + row.LocalPort.ToString(CultureInfo.InvariantCulture)
                    + "-" + row.RemoteAddr.ToString(CultureInfo.InvariantCulture) + ":" + row.RemotePort.ToString(CultureInfo.InvariantCulture);
                ulong inn, outb;
                if (!ReadV4Bytes(ref est, key, out inn, out outb)) continue;
                cur[key] = new ulong[] { inn, outb };
                pids[key] = row.OwningPid;
            }
        }

        void ReadV6(IntPtr buf, int count, Dictionary<string, ulong[]> cur, Dictionary<string, uint> pids)
        {
            int limit = count > 600 ? 600 : count;
            for (int i = 0; i < limit; i++)
            {
                IntPtr p = new IntPtr(buf.ToInt64() + 4 + (long)i * 56);
                byte[] local = new byte[16];
                byte[] remote = new byte[16];
                Marshal.Copy(p, local, 0, 16);
                Marshal.Copy(new IntPtr(p.ToInt64() + 24), remote, 0, 16);
                if (IsV6Loopback(local) || IsV6Loopback(remote)) continue;
                uint state = (uint)Marshal.ReadInt32(p, 48);
                if (state != 5) continue;
                uint localPort = (uint)Marshal.ReadInt32(p, 20);
                uint remotePort = (uint)Marshal.ReadInt32(p, 44);
                uint pid = (uint)Marshal.ReadInt32(p, 52);
                MibTcp6Row est = new MibTcp6Row();
                est.State = state;
                est.LocalAddr = local;
                est.LocalScopeId = (uint)Marshal.ReadInt32(p, 16);
                est.LocalPort = localPort;
                est.RemoteAddr = remote;
                est.RemoteScopeId = (uint)Marshal.ReadInt32(p, 40);
                est.RemotePort = remotePort;
                string key = "6:" + Bit(local) + ":" + localPort.ToString(CultureInfo.InvariantCulture)
                    + "-" + Bit(remote) + ":" + remotePort.ToString(CultureInfo.InvariantCulture);
                ulong inn, outb;
                if (!ReadV6Bytes(ref est, key, out inn, out outb)) continue;
                cur[key] = new ulong[] { inn, outb };
                pids[key] = pid;
            }
        }

        bool ReadV4Bytes(ref MibTcpRow row, string key, out ulong inn, out ulong outb)
        {
            inn = 0;
            outb = 0;
            if (!flowEnabled.ContainsKey(key))
            {
                TcpDataRw rw = new TcpDataRw();
                rw.Enable = true;
                uint src = SetPerTcpConnectionEStats(ref row, 1, ref rw, 0, 1, 0);
                if (src != 0)
                {
                    FlowLogOnce("set4 " + src.ToString(CultureInfo.InvariantCulture));
                    return false;
                }
                flowEnabled[key] = true;
            }
            TcpDataRod rod = new TcpDataRod();
            uint grc = GetPerTcpConnectionEStats(ref row, 1, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, ref rod, 0, 96);
            if (grc != 0)
            {
                FlowLogOnce("get4 " + grc.ToString(CultureInfo.InvariantCulture));
                return false;
            }
            inn = rod.DataBytesIn;
            outb = rod.DataBytesOut;
            return true;
        }

        bool ReadV6Bytes(ref MibTcp6Row row, string key, out ulong inn, out ulong outb)
        {
            inn = 0;
            outb = 0;
            if (!flowEnabled.ContainsKey(key))
            {
                TcpDataRw rw = new TcpDataRw();
                rw.Enable = true;
                uint src = SetPerTcp6ConnectionEStats(ref row, 1, ref rw, 0, 1, 0);
                if (src != 0)
                {
                    FlowLogOnce("set6 " + src.ToString(CultureInfo.InvariantCulture));
                    return false;
                }
                flowEnabled[key] = true;
            }
            TcpDataRod rod = new TcpDataRod();
            uint grc = GetPerTcp6ConnectionEStats(ref row, 1, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, ref rod, 0, 96);
            if (grc != 0)
            {
                FlowLogOnce("get6 " + grc.ToString(CultureInfo.InvariantCulture));
                return false;
            }
            inn = rod.DataBytesIn;
            outb = rod.DataBytesOut;
            return true;
        }

        static bool IsV6Loopback(byte[] a)
        {
            for (int i = 0; i < 15; i++) if (a[i] != 0) return false;
            return a[15] == 1;
        }

        static string Bit(byte[] a)
        {
            StringBuilder sb = new StringBuilder(a.Length * 2);
            for (int i = 0; i < a.Length; i++) sb.Append(a[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        void FlowLogOnce(string msg)
        {
            if (flowLogged) return;
            flowLogged = true;
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glance");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "log.txt"),
                    DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + " " + msg + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint SetPerTcpConnectionEStats(ref MibTcpRow row, int estatsType, ref TcpDataRw rw, uint rwVersion, uint rwSize, uint offset);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetPerTcpConnectionEStats(ref MibTcpRow row, int estatsType, IntPtr rw, uint rwVersion, uint rwSize, IntPtr ros, uint rosVersion, uint rosSize, ref TcpDataRod rod, uint rodVersion, uint rodSize);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint SetPerTcp6ConnectionEStats(ref MibTcp6Row row, int estatsType, ref TcpDataRw rw, uint rwVersion, uint rwSize, uint offset);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetPerTcp6ConnectionEStats(ref MibTcp6Row row, int estatsType, IntPtr rw, uint rwVersion, uint rwSize, IntPtr ros, uint rosVersion, uint rosSize, ref TcpDataRod rod, uint rodVersion, uint rodSize);
    }

    sealed class PidRate
    {
        public uint Pid;
        public double Down;
        public double Up;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MibTcpRowOwner
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MibTcpRow
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MibTcp6Row
    {
        public uint State;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TcpDataRw
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool Enable;
    }

    [StructLayout(LayoutKind.Explicit, Size = 96)]
    struct TcpDataRod
    {
        [FieldOffset(0)] public ulong DataBytesOut;
        [FieldOffset(16)] public ulong DataBytesIn;
    }

    sealed class ProcReader
    {
        Dictionary<uint, ulong> prevCpu = new Dictionary<uint, ulong>();
        Dictionary<uint, string> lastNames = new Dictionary<uint, string>();
        long prevWall;
        bool hasPrev;
        int selfPid;

        public ProcReader()
        {
            selfPid = Process.GetCurrentProcess().Id;
            prevWall = Stopwatch.GetTimestamp();
        }

        public string NameOf(uint pid)
        {
            string n;
            if (lastNames.TryGetValue(pid, out n) && n != null && n.Length > 0)
            {
                if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
                return n;
            }
            return "Процесс " + pid.ToString(CultureInfo.InvariantCulture);
        }

        public void Sample(List<string> hung, List<string> topName, List<double> topPct, List<string> memName, List<double> memMb)
        {
            Dictionary<uint, ulong> nowCpu = new Dictionary<uint, ulong>();
            Dictionary<uint, ulong> nowMem = new Dictionary<uint, ulong>();
            Dictionary<uint, string> names = new Dictionary<uint, string>();
            long wall = Stopwatch.GetTimestamp();
            IntPtr snap = CreateToolhelp32Snapshot(2, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return;
            try
            {
                PROCESSENTRY32 pe = new PROCESSENTRY32();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (!Process32First(snap, ref pe)) return;
                do
                {
                    uint pid = pe.th32ProcessID;
                    if (pid == 0 || pid == 4) continue;
                    names[pid] = pe.szExeFile ?? "";
                    IntPtr h = OpenProcess(0x1000, false, pid);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        FILETIME c, e, k, u;
                        if (GetProcessTimes(h, out c, out e, out k, out u))
                        {
                            ulong t = (((ulong)k.dwHighDateTime << 32) | k.dwLowDateTime) + (((ulong)u.dwHighDateTime << 32) | u.dwLowDateTime);
                            nowCpu[pid] = t;
                        }
                        MemCounters mem = new MemCounters();
                        mem.cb = (uint)Marshal.SizeOf(typeof(MemCounters));
                        if (GetProcessMemoryInfo(h, out mem, mem.cb))
                        {
                            ulong bytes = mem.PrivateWorkingSetSize;
                            if (bytes == 0) bytes = mem.WorkingSetSize;
                            nowMem[pid] = bytes;
                        }
                    }
                    finally { CloseHandle(h); }
                }
                while (Process32Next(snap, ref pe));
            }
            finally { CloseHandle(snap); }

            double sec = (wall - prevWall) / (double)Stopwatch.Frequency;
            if (sec < 0.2) sec = 0.2;
            List<Item> items = new List<Item>();
            if (hasPrev)
            {
                int cores = Environment.ProcessorCount;
                if (cores < 1) cores = 1;
                foreach (KeyValuePair<uint, ulong> kv in nowCpu)
                {
                    if ((int)kv.Key == selfPid) continue;
                    ulong old;
                    if (!prevCpu.TryGetValue(kv.Key, out old)) continue;
                    if (kv.Value < old) continue;
                    double procSec = (kv.Value - old) / 10000000.0;
                    double pct = procSec / sec / cores * 100.0;
                    if (pct < 0.8) continue;
                    if (pct > 100) pct = 100;
                    string nm;
                    if (!names.TryGetValue(kv.Key, out nm) || nm.Length == 0) nm = "pid " + kv.Key.ToString(CultureInfo.InvariantCulture);
                    if (nm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) nm = nm.Substring(0, nm.Length - 4);
                    Item it = new Item();
                    it.Pct = pct;
                    it.Name = nm;
                    items.Add(it);
                }
                items.Sort(delegate(Item a, Item b) { return b.Pct.CompareTo(a.Pct); });
                int n = items.Count > 4 ? 4 : items.Count;
                for (int i = 0; i < n; i++)
                {
                    topName.Add(items[i].Name);
                    topPct.Add(items[i].Pct);
                }
            }
            prevCpu = nowCpu;
            prevWall = wall;
            hasPrev = true;
            if (names.Count > 0) lastNames = names;
            FillMemory(nowMem, names, memName, memMb);
            CollectHung(hung, names);
        }

        void FillMemory(Dictionary<uint, ulong> nowMem, Dictionary<uint, string> names, List<string> memName, List<double> memMb)
        {
            Dictionary<string, double> byName = new Dictionary<string, double>();
            foreach (KeyValuePair<uint, ulong> kv in nowMem)
            {
                if ((int)kv.Key == selfPid) continue;
                string nm;
                if (!names.TryGetValue(kv.Key, out nm) || nm.Length == 0) continue;
                if (nm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) nm = nm.Substring(0, nm.Length - 4);
                double mb = kv.Value / 1048576.0;
                double have;
                if (!byName.TryGetValue(nm, out have)) have = 0;
                byName[nm] = have + mb;
            }
            List<Item> items = new List<Item>();
            foreach (KeyValuePair<string, double> kv in byName)
            {
                if (kv.Value < 200) continue;
                Item it = new Item();
                it.Name = kv.Key;
                it.Mb = kv.Value;
                items.Add(it);
            }
            items.Sort(delegate(Item a, Item b) { return b.Mb.CompareTo(a.Mb); });
            int n = items.Count > 4 ? 4 : items.Count;
            for (int i = 0; i < n; i++)
            {
                memName.Add(items[i].Name);
                memMb.Add(items[i].Mb);
            }
        }

        void CollectHung(List<string> hung, Dictionary<uint, string> names)
        {
            List<IntPtr> windows = new List<IntPtr>();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                windows.Add(h);
                return true;
            }, IntPtr.Zero);
            int added = 0;
            for (int i = 0; i < windows.Count && added < 3; i++)
            {
                IntPtr h = windows[i];
                if (!IsWindowVisible(h)) continue;
                int cloaked;
                if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) continue;
                if (!IsHungAppWindow(h)) continue;
                int len = GetWindowTextLength(h);
                if (len <= 0) continue;
                StringBuilder sb = new StringBuilder(len + 2);
                GetWindowText(h, sb, sb.Capacity);
                string title = sb.ToString().Trim();
                if (title.Length == 0) continue;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if ((int)pid == selfPid) continue;
                string exe;
                if (!names.TryGetValue(pid, out exe)) exe = "";
                if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = exe.Substring(0, exe.Length - 4);
                string line = exe.Length == 0 ? title : exe + " — " + title;
                if (line.Length > 64) line = line.Substring(0, 64);
                hung.Add(line);
                added++;
            }
        }

        class Item
        {
            public double Pct;
            public double Mb;
            public string Name;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MemCounters
        {
            public uint cb;
            public uint PageFaultCount;
            public ulong PeakWorkingSetSize;
            public ulong WorkingSetSize;
            public ulong QuotaPeakPagedPoolUsage;
            public ulong QuotaPagedPoolUsage;
            public ulong QuotaPeakNonPagedPoolUsage;
            public ulong QuotaNonPagedPoolUsage;
            public ulong PagefileUsage;
            public ulong PeakPagefileUsage;
            public ulong PrivateUsage;
            public ulong PrivateWorkingSetSize;
            public ulong SharedCommitUsage;
        }

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 pe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 pe);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetProcessTimes(IntPtr hProcess, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);

        [DllImport("psapi.dll", SetLastError = true)]
        static extern bool GetProcessMemoryInfo(IntPtr hProcess, out MemCounters counters, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr h);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool IsHungAppWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("dwmapi.dll")]
        static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}

static class AppFonts
{
    static System.Drawing.Text.PrivateFontCollection collection;
    static FontFamily family;

    public static void Load()
    {
        if (collection != null) return;
        collection = new System.Drawing.Text.PrivateFontCollection();
        string dir = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "fonts");
        string regular = Path.Combine(dir, "Roboto-Regular.ttf");
        string bold = Path.Combine(dir, "Roboto-Bold.ttf");
        if (File.Exists(regular)) collection.AddFontFile(regular);
        if (File.Exists(bold)) collection.AddFontFile(bold);
        if (collection.Families != null && collection.Families.Length > 0) family = collection.Families[0];
    }

    public static Font Make(float px, FontStyle style)
    {
        Load();
        if (family != null)
        {
            FontStyle use = style;
            if (!family.IsStyleAvailable(use)) use = FontStyle.Regular;
            return new Font(family, px, use, GraphicsUnit.Pixel);
        }
        return new Font("Segoe UI", px, style, GraphicsUnit.Pixel);
    }
}
