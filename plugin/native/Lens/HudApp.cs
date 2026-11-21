// lenshud.exe —— AI 操作电脑时的视觉反馈层（零第三方，纯 GDI+ / Win32）。
//
// 两个要求决定了实现方式：
//  1. 不能被拍到：所有窗口都设 WDA_EXCLUDEFROMCAPTURE，否则它会出现在 lens 自己的
//     截图里，污染候选表、把视觉定位带偏。
//  2. 不能抢焦点/挡点击：WS_EX_LAYERED|TRANSPARENT|TOOLWINDOW|NOACTIVATE + 逐像素 alpha。
//
// 状态通过一个文本文件与主进程通信（不用端口、不用 IPC 框架）：
//   %TEMP%\lens-hud\state.txt   →  v=1 / title=… / text=… / ts=<unix ms>
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Lens
{
    static class HudNative
    {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int CX, CY; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT pos, ref SIZE size, IntPtr srcDc, ref POINT src, int colorKey, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    }

    /// <summary>逐像素 alpha 的无边框覆盖窗：只能被 UpdateLayeredWindow 绘制，鼠标穿透。</summary>
    class LayerForm : Form
    {
        public LayerForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            SetStyle(ControlStyles.Opaque, true);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00080000;   // WS_EX_LAYERED
                cp.ExStyle |= 0x00000020;   // WS_EX_TRANSPARENT（鼠标穿透）
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW（不进 Alt+Tab）
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                return cp;
            }
        }

        public void ExcludeFromCapture()
        {
            if (!HudApp.Capturable) { try { HudNative.SetWindowDisplayAffinity(Handle, HudNative.WDA_EXCLUDEFROMCAPTURE); } catch { } }
        }

        public void Blit(Bitmap bmp)
        {
            IntPtr screenDc = HudNative.GetDC(IntPtr.Zero);
            IntPtr memDc = HudNative.CreateCompatibleDC(screenDc);
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = HudNative.SelectObject(memDc, hBmp);
            var size = new HudNative.SIZE { CX = bmp.Width, CY = bmp.Height };
            var src = new HudNative.POINT { X = 0, Y = 0 };
            var top = new HudNative.POINT { X = Left, Y = Top };
            var blend = new HudNative.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            HudNative.UpdateLayeredWindow(Handle, screenDc, ref top, ref size, memDc, ref src, 0, ref blend, 2);
            HudNative.SelectObject(memDc, old);
            HudNative.DeleteObject(hBmp);
            HudNative.DeleteDC(memDc);
            HudNative.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    static class HudApp
    {
        // 配色：冷蓝 → 青 → 靛，做一条会缓慢流动的渐变
        static readonly Color[] Ramp = {
            Color.FromArgb(37, 99, 235),    // #2563EB
            Color.FromArgb(6, 182, 212),    // #06B6D4
            Color.FromArgb(59, 130, 246),   // #3B82F6
            Color.FromArgb(99, 102, 241),   // #6366F1
            Color.FromArgb(56, 189, 248),   // #38BDF8
        };

        const int Thick = 46;        // 边框发光厚度
        const int CapsuleH = 46;
        const int CapsuleY = 20;

        static string StatePath
        {
            get { return Path.Combine(Path.GetTempPath(), "lens-hud", "state.txt"); }
        }

        // 调试用：设为 1 时不设 WDA_EXCLUDEFROMCAPTURE，截图才能拍到 HUD（正常使用时它必须对截屏不可见）
        public static readonly bool Capturable = Environment.GetEnvironmentVariable("LENS_HUD_CAPTURABLE") == "1";
        static LayerForm top, bottom, left, right, capsule;
        static int sw, sh;
        static double phase;
        static double alpha;          // 0..1 淡入淡出
        static double targetAlpha;
        static string title = "Lens AI 正在操作你的电脑";
        static string action = "";
        static long lastTs;
        static DateTime lastChange = DateTime.MinValue;

        [STAThread]
        static void Main()
        {
            try { HudNative.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.ThreadException += delegate { };
            AppDomain.CurrentDomain.UnhandledException += delegate { };

            sw = HudNative.GetSystemMetrics(0);
            sh = HudNative.GetSystemMetrics(1);

            top = NewForm(new Rectangle(0, 0, sw, Thick));
            bottom = NewForm(new Rectangle(0, sh - Thick, sw, Thick));
            left = NewForm(new Rectangle(0, 0, Thick, sh));
            right = NewForm(new Rectangle(sw - Thick, 0, Thick, sh));

            var timer = new Timer();
            timer.Interval = 55;      // ~18fps：渐变流动够顺，CPU 占用可忽略
            timer.Tick += delegate { Tick(); };
            timer.Start();

            Application.Run(new ApplicationContext());
        }

        static LayerForm NewForm(Rectangle r)
        {
            var f = new LayerForm();
            f.Bounds = r;
            f.Show();
            f.ExcludeFromCapture();
            return f;
        }

        static Color RampAt(double t)
        {
            t = t - Math.Floor(t);
            double x = t * Ramp.Length;
            int i = (int)Math.Floor(x);
            double f = x - i;
            Color a = Ramp[i % Ramp.Length], b = Ramp[(i + 1) % Ramp.Length];
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * f),
                (int)(a.G + (b.G - a.G) * f),
                (int)(a.B + (b.B - a.B) * f));
        }

        /// <summary>一条边缘：沿边方向的流动渐变 × 沿厚度方向的指数衰减（做出辉光感）。</summary>
        static Bitmap RenderEdge(int w, int h, bool horizontal, bool flip)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    byte* p0 = (byte*)data.Scan0;
                    int stride = data.Stride;
                    int along = horizontal ? w : h;
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            double d = horizontal ? y : x;                 // 距屏幕边缘的深度
                            if (flip) d = (horizontal ? h - 1 - y : w - 1 - x);
                            double fall = Math.Pow(Math.Max(0.0, 1.0 - d / Thick), 1.25);
                            double t = (double)(horizontal ? x : y) / Math.Max(1, along);
                            Color c = RampAt(t * 1.6 + phase);
                            byte a = (byte)Math.Max(0, Math.Min(255, fall * 255 * alpha));

                            // 边缘最外侧加一条更亮的细线，视觉上"边"更清晰
                            if (d < 2.0) a = (byte)Math.Max(a, (byte)Math.Min(255, 250 * alpha));
                            // 内侧一条细亮边：让"边"有实体感，而不只是一团光
                            else if (d > 9 && d < 11.2) a = (byte)Math.Max(a, (byte)Math.Min(255, 120 * alpha));

                            int off = y * stride + x * 4;
                            p0[off + 0] = c.B;
                            p0[off + 1] = c.G;
                            p0[off + 2] = c.R;
                            p0[off + 3] = a;
                        }
                    }
                }
            }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }

        static void Tick()
        {
            ReadState();

            bool want = lastTs > 0 && (NowMs() - lastTs) < 3500;
            targetAlpha = want ? 1.0 : 0.0;
            alpha += (targetAlpha - alpha) * 0.22;
            if (alpha < 0.01) alpha = 0;
            if (alpha > 0.99) alpha = 1;

            if (alpha <= 0)
            {
                HideAll();
                return;
            }
            if (!top.Visible) ShowAll();

            phase += 0.0038;   // 渐变缓慢流动

            using (var b = RenderEdge(sw, Thick, true, false)) top.Blit(b);
            using (var b = RenderEdge(sw, Thick, true, true)) bottom.Blit(b);
            using (var b = RenderEdge(Thick, sh, false, false)) left.Blit(b);
            using (var b = RenderEdge(Thick, sh, false, true)) right.Blit(b);

            using (var b = RenderCapsule()) BlitCapsule(b);
        }

        static void ShowAll()
        {
            top.Show(); bottom.Show(); left.Show(); right.Show();
            top.ExcludeFromCapture(); bottom.ExcludeFromCapture();
            left.ExcludeFromCapture(); right.ExcludeFromCapture();
        }

        static void HideAll()
        {
            top.Hide(); bottom.Hide(); left.Hide(); right.Hide();
            if (capsule != null) { capsule.Hide(); }
        }

        static Bitmap lastCapsule;

        static void BlitCapsule(Bitmap b)
        {
            if (capsule != null && (capsule.Width != b.Width || capsule.Height != b.Height))
            {
                capsule.Hide(); capsule.Dispose(); capsule = null;
            }
            if (capsule == null)
            {
                capsule = new LayerForm();
                capsule.Bounds = new Rectangle((sw - b.Width) / 2, CapsuleY, b.Width, b.Height);
                capsule.Show();
                capsule.ExcludeFromCapture();
            }
            capsule.Left = (sw - b.Width) / 2;
            capsule.Top = CapsuleY;
            capsule.Blit(b);
        }

        static Bitmap RenderCapsule()
        {
            using (var measure = Graphics.FromImage(new Bitmap(1, 1)))
            using (var fTitle = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold, GraphicsUnit.Point))
            using (var fAct = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular, GraphicsUnit.Point))
            {
                string t1 = title;
                string t2 = string.IsNullOrEmpty(action) ? "" : action;
                SizeF s1 = measure.MeasureString(t1, fTitle);
                SizeF s2 = t2.Length > 0 ? measure.MeasureString(t2, fAct) : SizeF.Empty;

                int padL = 46, padR = 22, gap = 26;
                int w = (int)(padL + s1.Width + (t2.Length > 0 ? gap + s2.Width : 0) + padR);
                w = Math.Max(300, Math.Min(sw - 120, w));
                int h = CapsuleH;

                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                    var rr = new Rectangle(0, 0, w - 1, h - 1);
                    int r = h / 2;
                    using (var path = Round(rr, r))
                    {
                        // 纯黑底：几乎不透，只有一点点让蓝边透出来
                        using (var bg = new SolidBrush(Color.FromArgb((int)(248 * alpha), 0, 0, 0)))
                            g.FillPath(bg, path);

                        // 描边：沿胶囊走向的蓝色渐变
                        using (var pen = new Pen(Color.FromArgb((int)(210 * alpha), 56, 140, 248), 1.4f))
                            g.DrawPath(pen, path);

                        // 纯黑底上不加高光，保持干净
                    }

                    // 左侧呼吸圆点
                    double pulse = 0.55 + 0.45 * Math.Sin(phase * 22);
                    int cx = 26, cy = h / 2;
                    using (var glow = new SolidBrush(Color.FromArgb((int)(90 * pulse * alpha), 56, 189, 248)))
                        g.FillEllipse(glow, cx - 11, cy - 11, 22, 22);
                    using (var dot = new SolidBrush(Color.FromArgb((int)(255 * alpha), 125, 211, 252)))
                        g.FillEllipse(dot, cx - 5, cy - 5, 10, 10);

                    // 文案
                    using (var fb = new SolidBrush(Color.FromArgb((int)(246 * alpha), 226, 240, 255)))
                        g.DrawString(t1, fTitle, fb, padL - 8, (h - s1.Height) / 2f);

                    if (t2.Length > 0)
                    {
                        float x2 = padL - 8 + s1.Width + gap;
                        using (var sep = new SolidBrush(Color.FromArgb((int)(120 * alpha), 125, 211, 252)))
                            g.FillRectangle(sep, x2 - gap / 2 - 0.5f, h / 2f - 9, 1.4f, 18);
                        using (var fa = new SolidBrush(Color.FromArgb((int)(250 * alpha), 125, 211, 252)))
                            g.DrawString(t2, fAct, fa, x2, (h - s2.Height) / 2f);
                    }
                }
                return bmp;
            }
        }

        static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        }

        static void ReadState()
        {
            try
            {
                if (!File.Exists(StatePath)) { lastTs = 0; return; }
                string[] lines;
                using (var fs = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    lines = sr.ReadToEnd().Split('\n');
                foreach (string raw in lines)
                {
                    string l = raw.Trim();
                    int eq = l.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = l.Substring(0, eq), v = l.Substring(eq + 1).Trim();
                    if (k == "title" && v.Length > 0) title = v;
                    else if (k == "text") action = v;
                    else if (k == "ts") { long t; if (long.TryParse(v, out t)) lastTs = t; }
                }
            }
            catch { }
        }
    }
}
