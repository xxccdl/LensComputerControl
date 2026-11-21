// 宣传片帧渲染：把真实运行截图合成为 1920x1080 的分镜画面。
// 每帧都是静态图，交给 ffmpeg 做缓慢推镜 + 交叉淡入，避免"假 UI 动画"。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace Lens
{
    static class PromoGen
    {
        [STAThread]
        public static void Main(string[] args) { Environment.Exit(Run(args)); }

        const int W = 1920, H = 1080;
        static readonly Color Bg = Color.FromArgb(5, 7, 13);
        static readonly Color Accent = Color.FromArgb(59, 130, 246);
        static readonly Color Cyan = Color.FromArgb(34, 211, 238);
        static readonly Color Text = Color.FromArgb(230, 240, 255);
        static readonly Color Dim = Color.FromArgb(124, 139, 161);

        static Graphics NewG(out Bitmap bmp)
        {
            bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Bg);
            return g;
        }

        /// <summary>底噪：极淡的网格 + 左上角的蓝光，避免纯黑显得廉价。</summary>
        static void Background(Graphics g, double glow = 1.0)
        {
            // 竖直渐变（不要用斜角渐变：矩形边界会在角落留下可见的斜切断层）
            using (var b = new LinearGradientBrush(new Rectangle(0, -2, W, H + 4),
                Color.FromArgb((int)(58 * glow), 24, 52, 140), Bg, 90f))
                g.FillRectangle(b, new Rectangle(0, 0, W, H));
            using (var p = new Pen(Color.FromArgb(10, 120, 160, 220), 1f))
            {
                for (int x = 0; x < W; x += 80) g.DrawLine(p, x, 0, x, H);
                for (int y = 0; y < H; y += 80) g.DrawLine(p, 0, y, W, y);
            }
        }

        static void GlowEdge(Graphics g, int thickness, double alpha)
        {
            for (int i = 0; i < thickness; i++)
            {
                double f = Math.Pow(1.0 - (double)i / thickness, 1.3);
                int a = (int)Math.Max(0, Math.Min(255, f * 210 * alpha));
                using (var p = new Pen(Color.FromArgb(a, 56, 140, 248), 1f))
                    g.DrawRectangle(p, i, i, W - 1 - i * 2, H - 1 - i * 2);
            }
        }

        static void Text_(Graphics g, string s, Font f, Color c, float x, float y)
        {
            using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, y);
        }

        static void TextC(Graphics g, string s, Font f, Color c, float y)
        {
            SizeF sz = g.MeasureString(s, f);
            Text_(g, s, f, c, (W - sz.Width) / 2, y);
        }

        /// <summary>把截图按 cover 方式铺满，返回实际绘制矩形（供后续计算遮罩）。</summary>
        static Rectangle Cover(Graphics g, string path, Rectangle? clip)
        {
            if (!File.Exists(path)) return Rectangle.Empty;
            using (var src = new Bitmap(path))
            {
                double s = Math.Max((double)W / src.Width, (double)H / src.Height);
                int dw = (int)(src.Width * s), dh = (int)(src.Height * s);
                var r = new Rectangle((W - dw) / 2, (H - dh) / 2, dw, dh);
                var target = clip ?? new Rectangle(0, 0, W, H);
                var st = g.Save();
                g.SetClip(target);
                g.DrawImage(src, r);
                g.Restore(st);
                return r;
            }
        }

        static void Scrim(Graphics g, int height, int alphaBottom)
        {
            var r = new Rectangle(0, H - height, W, height);
            using (var b = new LinearGradientBrush(r, Color.FromArgb(0, 5, 7, 13), Color.FromArgb(alphaBottom, 5, 7, 13), 90f))
                g.FillRectangle(b, r);
        }

        static void Caption(Graphics g, string kicker, string title, string sub, string data)
        {
            float x = 130, y = H - 400;
            using (var fk = new Font("Microsoft YaHei UI", 20f, FontStyle.Bold))
            using (var ft = new Font("Microsoft YaHei UI", 46f, FontStyle.Bold))
            using (var fs = new Font("Microsoft YaHei UI", 24f))
            using (var fd = new Font("Consolas", 22f))
            {
                using (var b = new SolidBrush(Cyan)) g.FillRectangle(b, x, y - 26, 6, 34);
                Text_(g, kicker, fk, Cyan, x + 18, y - 26);
                Text_(g, title, ft, Text, x, y + 20);
                if (!string.IsNullOrEmpty(sub)) Text_(g, sub, fs, Dim, x, y + 96);
                if (!string.IsNullOrEmpty(data))
                {
                    var r = new Rectangle((int)x, (int)(y + 150), (int)(g.MeasureString(data, fd).Width + 40), 52);
                    using (var b = new SolidBrush(Color.FromArgb(150, 10, 16, 30)))
                    using (var p = new Pen(Color.FromArgb(120, 56, 189, 248), 1.2f))
                    {
                        using (var path = Round(r, 10)) { g.FillPath(b, path); g.DrawPath(p, path); }
                    }
                    Text_(g, data, fd, Color.FromArgb(190, 220, 255), x + 20, y + 162);
                }
            }
        }

        /// <summary>把截图放进一个圆角面板里，带描边和标题条 —— 比直接铺满更适合竖长截图。</summary>
        static void Panel(Graphics g, string path, Rectangle box, string label)
        {
            using (var bg = new SolidBrush(Color.FromArgb(235, 10, 14, 24)))
            using (var pn = new Pen(Color.FromArgb(150, 56, 140, 248), 1.4f))
            using (var path0 = Round(box, 14))
            {
                g.FillPath(bg, path0);
                g.DrawPath(pn, path0);
            }
            if (file_exists(path))
            {
                using (var src = new Bitmap(path))
                {
                    var inner = new Rectangle(box.X + 3, box.Y + 3, box.Width - 6, box.Height - 6);
                    double s = Math.Min((double)inner.Width / src.Width, (double)inner.Height / src.Height);
                    int dw = (int)(src.Width * s), dh = (int)(src.Height * s);
                    var st = g.Save();
                    using (var cp = Round(inner, 12)) g.SetClip(cp);
                    g.DrawImage(src, new Rectangle(inner.X + (inner.Width - dw) / 2, inner.Y + (inner.Height - dh) / 2, dw, dh));
                    g.Restore(st);
                }
            }
            if (!string.IsNullOrEmpty(label))
            {
                using (var f = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold))
                using (var tb = new SolidBrush(Color.FromArgb(235, 8, 12, 20)))
                using (var tf = new SolidBrush(Color.FromArgb(200, 230, 250)))
                {
                    SizeF sz = g.MeasureString(label, f);
                    var chip = new Rectangle(box.X + 16, box.Y + 14, (int)sz.Width + 24, (int)sz.Height + 10);
                    using (var cp = Round(chip, 8)) g.FillPath(tb, cp);
                    g.DrawString(label, f, tf, chip.X + 12, chip.Y + 4);
                }
            }
        }

        /// <summary>cover 版面板：填满不留白，用于横向较宽的标注图。</summary>
        static void Panel2(Graphics g, string path, Rectangle box, string label)
        {
            using (var bg = new SolidBrush(Color.FromArgb(235, 10, 14, 24)))
            using (var pn = new Pen(Color.FromArgb(150, 56, 140, 248), 1.4f))
            using (var path0 = Round(box, 14)) { g.FillPath(bg, path0); g.DrawPath(pn, path0); }
            if (file_exists(path))
            {
                using (var src = new Bitmap(path))
                {
                    var inner = new Rectangle(box.X + 3, box.Y + 3, box.Width - 6, box.Height - 6);
                    double s = Math.Max((double)inner.Width / src.Width, (double)inner.Height / src.Height);
                    int dw = (int)(src.Width * s), dh = (int)(src.Height * s);
                    var st = g.Save();
                    using (var cp = Round(inner, 12)) g.SetClip(cp);
                    g.DrawImage(src, new Rectangle(inner.X + (inner.Width - dw) / 2, inner.Y + (inner.Height - dh) / 2, dw, dh));
                    g.Restore(st);
                }
            }
            if (!string.IsNullOrEmpty(label))
            {
                using (var f = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold))
                using (var tb = new SolidBrush(Color.FromArgb(235, 8, 12, 20)))
                using (var tf = new SolidBrush(Color.FromArgb(200, 230, 250)))
                {
                    SizeF sz = g.MeasureString(label, f);
                    var chip = new Rectangle(box.X + 16, box.Y + 14, (int)sz.Width + 24, (int)sz.Height + 10);
                    using (var cp = Round(chip, 8)) g.FillPath(tb, cp);
                    g.DrawString(label, f, tf, chip.X + 12, chip.Y + 4);
                }
            }
        }

        /// <summary>按真实 HUD 的样子画胶囊：纯黑底、蓝描边、呼吸圆点、右侧动作文案。</summary>
        static void DrawCapsule(Graphics g, int cy, string title, string action)
        {
            using (var fT = new Font("Microsoft YaHei UI", 21f, FontStyle.Bold))
            using (var fA = new Font("Microsoft YaHei UI", 20f))
            using (var fS = new Font("Microsoft YaHei UI", 20f))
            {
                SizeF s1 = g.MeasureString(title, fT);
                SizeF s2 = g.MeasureString(action, fA);
                int padL = 92, gap = 46, padR = 40;
                int w = (int)(padL + s1.Width + gap + s2.Width + padR);
                int h = 82;
                var box = new Rectangle((W - w) / 2, cy - h / 2, w, h);

                using (var shadow = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                using (var sp = Round(new Rectangle(box.X - 6, box.Y + 6, box.Width + 12, box.Height), h / 2))
                    g.FillPath(shadow, sp);
                using (var bg = new SolidBrush(Color.FromArgb(250, 0, 0, 0)))
                using (var pn = new Pen(Color.FromArgb(220, 56, 140, 248), 2.0f))
                using (var path = Round(box, h / 2))
                {
                    g.FillPath(bg, path);
                    g.DrawPath(pn, path);
                }
                int dotX = box.X + 44, dotY = cy;
                using (var glow = new SolidBrush(Color.FromArgb(70, 56, 189, 248)))
                    g.FillEllipse(glow, dotX - 18, dotY - 18, 36, 36);
                using (var dot = new SolidBrush(Color.FromArgb(255, 125, 211, 252)))
                    g.FillEllipse(dot, dotX - 8, dotY - 8, 16, 16);

                using (var tb = new SolidBrush(Color.FromArgb(246, 226, 240, 255)))
                    g.DrawString(title, fT, tb, box.X + padL, cy - s1.Height / 2);
                float ax = box.X + padL + s1.Width + gap;
                using (var sep = new SolidBrush(Color.FromArgb(120, 125, 211, 252)))
                    g.FillRectangle(sep, ax - gap / 2 - 1, cy - 20, 2, 40);
                using (var ab = new SolidBrush(Color.FromArgb(250, 125, 211, 252)))
                    g.DrawString(action, fA, ab, ax, cy - s2.Height / 2);
            }
        }

        static bool file_exists(string p) { return !string.IsNullOrEmpty(p) && File.Exists(p); }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static string Root
        {
            get { return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..")); }
        }

        static string Shot(string name)
        {
            string p = Path.Combine(Root, "build", name);
            return File.Exists(p) ? p : null;
        }

        public static int Run(string[] args)
        {
            string outDir = args.Length > 0 ? args[0] : Path.Combine(Root, "dist", "promo-frames");
            Directory.CreateDirectory(outDir);
            Bitmap b;

            // 1) 片头
            using (var g = NewG(out b))
            {
                Background(g, 1.3);
                GlowEdge(g, 70, 1.0);
                using (var f1 = new Font("Microsoft YaHei UI", 96f, FontStyle.Bold))
                using (var f2 = new Font("Microsoft YaHei UI", 30f))
                using (var f3 = new Font("Consolas", 20f))
                {
                    SizeF s1 = g.MeasureString("Lens", f1);
                    Text_(g, "Lens", f1, Text, (W - s1.Width) / 2, 400);
                    TextC(g, "让 AI 真正会操作电脑", f2, Cyan, 560);
                    string tag = "零第三方依赖  ·  仅 Windows  ·  MIT";
                    SizeF s3 = g.MeasureString(tag, f3);
                    Text_(g, tag, f3, Dim, (W - s3.Width) / 2, 660);
                }
                b.Save(Path.Combine(outDir, "01-title.png"), ImageFormat.Png);
            }

            // 2) 看得见（微信扫描）
            using (var g = NewG(out b))
            {
                string p = Shot("wechat-overlay.png");
                if (p != null)
                {
                    Background(g, 1.0);
                    // 左：完整的自绘窗口；右：扫描出来的标注细节 —— 两栏对照更有说服力
                    string full = Path.Combine(Root, "_dev", "wx-main2.png");
                    Panel(g, full, new Rectangle(130, 64, 410, 600), "微信 · Qt 自绘窗口");
                    Panel2(g, p, new Rectangle(588, 100, 1020, 560), "scan 输出的候选标注");
                    Scrim(g, 470, 240);
                }
                GlowEdge(g, 26, 0.35);
                Caption(g, "01 / 看得见", "微信、QQ 这类自绘窗口", "整个界面是一块画布，无障碍树里一个可点击节点都没有", "uia nodes=7 candidates=0   →   scan 仍给出 26 个候选");
                b.Save(Path.Combine(outDir, "02-scan.png"), ImageFormat.Png);
            }

            // 3) 找得到（视觉切格 L0）
            using (var g = NewG(out b))
            {
                string p = Shot("vision-L0.png");
                if (p != null) { Cover(g, p, null); Scrim(g, 560, 230); }
                Caption(g, "02 / 找得到", "识别不出来？那就切开来看", "整屏平均分成带坐标的格子，模型只报编号", "computer_vision {}  →  10 格，每格标注屏幕绝对坐标");
                b.Save(Path.Combine(outDir, "03-grid.png"), ImageFormat.Png);
            }

            // 4) 更准（视觉切格 L2）
            using (var g = NewG(out b))
            {
                string p = Shot("vision-L2.png");
                if (p != null)
                {
                    var clip = new Rectangle(0, 0, W, 760);
                    using (var bg = new SolidBrush(Color.FromArgb(255, 8, 10, 16))) g.FillRectangle(bg, clip);
                    Cover(g, p, clip);
                    Scrim(g, 520, 235);
                }
                Caption(g, "03 / 更准", "两级下钻，误差不到 30 像素", "选一格 → 再切那一格，坐标始终是屏幕绝对值", "误差 38px 的相邻图标，实测精准命中");
                b.Save(Path.Combine(outDir, "04-zoom.png"), ImageFormat.Png);
            }

            // 5) 点得准（HUD）
            using (var g = NewG(out b))
            {
                Background(g, 0.9);
                GlowEdge(g, 64, 0.95);
                DrawCapsule(g, 300, "Lens AI 正在操作你的电脑", "正在点击");
                using (var f = new Font("Microsoft YaHei UI", 42f, FontStyle.Bold))
                using (var f2 = new Font("Microsoft YaHei UI", 24f))
                {
                    TextC(g, "每一次动作，你都能看见", f, Text, 500);
                    TextC(g, "屏幕四周渐变蓝边 + 顶部胶囊，实时显示 AI 正在做什么", f2, Dim, 580);
                    TextC(g, "而对截屏不可见 —— 不会污染它自己的识别", f2, Cyan, 630);
                }
                b.Save(Path.Combine(outDir, "05-hud.png"), ImageFormat.Png);
            }

            // 6) 不点错（安全闸）
            using (var g = NewG(out b))
            {
                Background(g, 1.1);
                GlowEdge(g, 40, 0.5);
                using (var f = new Font("Microsoft YaHei UI", 40f, FontStyle.Bold))
                using (var fm = new Font("Consolas", 21f))
                {
                    TextC(g, "点错窗口？它会拒绝", f, Text, 240);
                    string[] lines = {
                        "{\"ok\":false,",
                        " \"code\":\"TARGET_COVERED\",",
                        " \"target\":     { \"title\":\"微信\" },",
                        " \"coveredBy\":  { \"title\":\"会议窗口\", \"process\":\"Teams\" },",
                        " \"hint\":\"把目标窗口置前再试\" }"
                    };
                    float y = 360;
                    var box = new Rectangle(360, (int)y - 30, W - 720, 330);
                    using (var bg = new SolidBrush(Color.FromArgb(190, 8, 12, 22)))
                    using (var pn = new Pen(Color.FromArgb(150, 56, 140, 248), 1.4f))
                    using (var path = Round(box, 16)) { g.FillPath(bg, path); g.DrawPath(pn, path); }
                    foreach (var ln in lines)
                    {
                        Text_(g, ln, fm, Color.FromArgb(200, 220, 245), 400, y);
                        y += 54;
                    }
                    using (var f2 = new Font("Microsoft YaHei UI", 22f))
                        TextC(g, "宿主自身永不作为目标  ·  标题歧义不猜  ·  遮挡拦截", f2, Dim, 760);
                }
                b.Save(Path.Combine(outDir, "06-safety.png"), ImageFormat.Png);
            }

            // 7) 一行装好
            using (var g = NewG(out b))
            {
                Background(g, 1.0);
                GlowEdge(g, 30, 0.45);
                using (var f = new Font("Microsoft YaHei UI", 38f, FontStyle.Bold))
                using (var fm = new Font("Consolas", 24f))
                using (var f2 = new Font("Microsoft YaHei UI", 22f))
                {
                    TextC(g, "解压，装进 DSH，重启一次", f, Text, 300);
                    var box = new Rectangle(430, 430, W - 860, 210);
                    using (var bg = new SolidBrush(Color.FromArgb(190, 8, 12, 22)))
                    using (var pn = new Pen(Color.FromArgb(150, 56, 140, 248), 1.4f))
                    using (var path = Round(box, 14)) { g.FillPath(bg, path); g.DrawPath(pn, path); }
                    Text_(g, "插件管理 → 安装本地插件 → 选中文件夹", fm, Color.FromArgb(210, 230, 255), 470, 470);
                    Text_(g, "→ 重启 DSH", fm, Cyan, 470, 540);
                    Text_(g, "→ 屏幕四周亮起蓝边，头顶出现胶囊", fm, Cyan, 470, 600);
                    TextC(g, "引擎 148 KB  ·  视觉层 24 KB  ·  不需要 Python / Node 包", f2, Dim, 720);
                }
                b.Save(Path.Combine(outDir, "07-install.png"), ImageFormat.Png);
            }

            // 8) 片尾
            using (var g = NewG(out b))
            {
                Background(g, 1.2);
                GlowEdge(g, 70, 1.0);
                using (var f1 = new Font("Microsoft YaHei UI", 72f, FontStyle.Bold))
                using (var f2 = new Font("Microsoft YaHei UI", 26f))
                using (var f3 = new Font("Consolas", 22f))
                {
                    TextC(g, "Lens 电脑控制", f1, Text, 380);
                    TextC(g, "模型只选编号，坐标由引擎算", f2, Cyan, 500);
                    TextC(g, "dsh-plugin-market.xxccdl.cn/plugin/lens-computer-control", f3, Dim, 600);
                    TextC(g, "MIT License  ·  零第三方依赖  ·  源码随包分发", f3, Dim, 660);
                }
                b.Save(Path.Combine(outDir, "08-end.png"), ImageFormat.Png);
            }

            Console.WriteLine("frames -> " + outDir);
            return 0;
        }
    }
}
