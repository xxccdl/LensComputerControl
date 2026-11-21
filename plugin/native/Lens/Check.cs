// Lens/Check.cs — 坐标检验（pre-flight check）。
//
// 为什么要它：模型自己推算的坐标没人复核，点错了代价是误操作。这个命令让模型在真点之前
// 「看见」自己选的位置 —— 整窗图上画一个绿色框标出实际命中范围，右下角再贴一张该处的
// 放大镜（最近邻放大 + 像素格），压住的是哪个字、是不是按钮边缘，一眼就能判断。
//
// 只读：不移动鼠标、不发按键、不改任何窗口状态。
// 单独编成 lenscheck.exe（只依赖 GDI+/Win32，不拉 OCR/UIA 那套 WinRT），所以起得快。
//
// C# 5 语法（老 csc 不支持表达式体成员/字符串插值/?. /nameof/out var/模式匹配）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Text;

namespace Lens
{
    public static class CheckTools
    {
        /// <summary>默认点击命中范围（屏幕像素）。鼠标热区一般 20~24px，取 24 是个保守值。</summary>
        const int DefaultBox = 24;
        /// <summary>输出图长边上限。太大白吃模型上下文，太小看不清位置。</summary>
        const int MaxLongSide = 1500;
        /// <summary>放大镜面板宽度上限。</summary>
        const int PanelWidth = 380;

        static readonly Color Green = Color.FromArgb(255, 34, 197, 94);      // #22C55E
        static readonly Color Ink = Color.FromArgb(235, 5, 8, 12);
        static readonly Color Warn = Color.FromArgb(255, 250, 216, 21);      // #FAD815

        static string Arg(string[] a, string name, string def)
        {
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
            return def;
        }

        static bool Flag(string[] a, string name)
        {
            foreach (string s in a) if (s == name) return true;
            return false;
        }

        static int IntArg(string[] a, string name, int def)
        {
            string v = Arg(a, name, null);
            int r;
            if (v != null && int.TryParse(v, out r)) return r;
            return def;
        }

        static string StatePath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "state", "last-scan.tsv");
        }

        /// <summary>窗口一句话描述，遮挡提示里用。</summary>
        public static string DescribeWindow(long hwnd)
        {
            try
            {
                IntPtr h = new IntPtr(hwnd);
                var sbTitle = new StringBuilder(512);
                Win32.GetWindowTextW(h, sbTitle, sbTitle.Capacity);
                var sbClass = new StringBuilder(256);
                Win32.GetClassNameW(h, sbClass, sbClass.Capacity);
                uint pid = 0;
                Win32.GetWindowThreadProcessId(h, out pid);
                string proc = "";
                try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
                Win32.RECT r;
                Win32.GetWindowRect(h, out r);
                return sbTitle + " [hwnd=" + hwnd + " " + proc + " " + sbClass + " "
                    + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + " @" + r.Left + "," + r.Top + "]";
            }
            catch { return "hwnd=" + hwnd; }
        }

        /// <summary>虚拟屏幕位图（覆盖所有显示器；副屏在主屏左边时原点为负）。</summary>
        static Bitmap CaptureDesktop(out Rect screen)
        {
            int vx = Win32.GetSystemMetrics(76), vy = Win32.GetSystemMetrics(77);
            int w = Win32.GetSystemMetrics(78), h = Win32.GetSystemMetrics(79);
            if (w <= 0 || h <= 0) { vx = 0; vy = 0; w = Win32.GetSystemMetrics(0); h = Win32.GetSystemMetrics(1); }
            screen = new Rect(vx, vy, w, h);
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr dst = g.GetHdc();
                IntPtr src = Win32.GetDC(IntPtr.Zero);
                Win32.BitBlt(dst, 0, 0, w, h, src, vx, vy, 0x00CC0020);
                Win32.ReleaseDC(IntPtr.Zero, src);
                g.ReleaseHdc(dst);
            }
            return bmp;
        }

        /// <summary>上一次 scan 留下的候选表里，哪个候选吃住了这个点（取面积最小的那个）。
        /// 只读 state/last-scan.tsv，不顺带重新扫描 —— check 必须快。</summary>
        static string MatchCandidate(long hwnd, int x, int y, out Rect hit)
        {
            hit = new Rect(0, 0, 0, 0);
            try
            {
                string p = StatePath();
                if (!File.Exists(p)) return null;
                string[] lines = File.ReadAllLines(p);
                long stateHwnd = 0;
                foreach (string h in lines)
                {
                    if (!h.StartsWith("#window")) continue;
                    foreach (string kv in h.Split('\t'))
                        if (kv.StartsWith("hwnd=")) long.TryParse(kv.Substring(5), out stateHwnd);
                    break;
                }
                // 窗口对不上就别拿旧候选糊弄：跨窗口套用会给出错误的"你点的是 XX"
                if (hwnd != 0 && stateHwnd != 0 && hwnd != stateHwnd) return null;

                string best = null; long bestArea = long.MaxValue;
                foreach (string line in lines)
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] f = line.Split('\t');
                    if (f.Length < 5) continue;
                    int cx, cy, cw, ch;
                    if (!int.TryParse(f[1], out cx)) continue;
                    if (!int.TryParse(f[2], out cy)) continue;
                    if (!int.TryParse(f[3], out cw)) continue;
                    if (!int.TryParse(f[4], out ch)) continue;
                    if (x < cx || y < cy || x >= cx + cw || y >= cy + ch) continue;
                    long area = (long)cw * ch;
                    if (area >= bestArea) continue;
                    bestArea = area;
                    best = f[0] + "\t" + (f.Length > 7 ? f[7] : "");
                    hit = new Rect(cx, cy, cw, ch);
                }
                return best;
            }
            catch { return null; }
        }

        public static int CmdCheck(string[] a)
        {
            string outPath = Arg(a, "--out", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "check.png"));
            string key = Arg(a, "--window", null);
            bool desktop = Flag(a, "--desktop") || key == null;

            string sx = Arg(a, "--x", null), sy = Arg(a, "--y", null);
            int px, py;
            if (sx == null || sy == null || !int.TryParse(sx, out px) || !int.TryParse(sy, out py))
            {
                Console.Write("{\"ok\":false,\"code\":\"BAD_ARGS\",\"message\":\"需要 --x 和 --y（屏幕物理坐标）\"}");
                return 2;
            }

            int bw = IntArg(a, "--w", DefaultBox);
            int bh = IntArg(a, "--h", bw);
            if (bw < 1) bw = 1; if (bw > 4000) bw = 4000;
            if (bh < 1) bh = 1; if (bh > 4000) bh = 4000;
            int zoomWant = IntArg(a, "--zoom", 8);
            if (zoomWant < 2) zoomWant = 2; if (zoomWant > 16) zoomWant = 16;

            var notes = new List<string>();
            Bitmap image;
            Rect screenRect;
            double scaleX = 1.0, scaleY = 1.0;
            WindowInfo win = null;

            if (desktop)
            {
                image = CaptureDesktop(out screenRect);
                notes.Add("source=desktop");
            }
            else
            {
                WindowInfo wi = Win32.FindWindow(key);
                if (wi == null)
                {
                    Console.Write("{\"ok\":false,\"code\":\"WINDOW_NOT_FOUND\",\"message\":\"找不到窗口: " + key + "\"}");
                    return 3;
                }
                if (wi.Minimized && !Flag(a, "--no-restore"))
                {
                    notes.Add("window minimized -> restoring before capture");
                    WindowInfo r0 = Win32.RestoreAndFocus(wi.Hwnd.ToString(), 320);
                    if (r0 != null) wi = r0;
                }
                win = wi;
                var cap = new Capability();
                string method;
                image = Capture.Acquire(wi, cap, out method, Win32.ListOwnedPopups(new IntPtr(wi.Hwnd)));
                foreach (string n in cap.Notes) notes.Add(n);
                if (image == null)
                {
                    Console.Write("{\"ok\":false,\"code\":\"CAPTURE_FAILED\",\"message\":\"截不到该窗口（可能被反截屏保护）\",\"notes\":" + Json.StrArray(notes) + "}");
                    return 4;
                }
                notes.Add("capture=" + method);

                // 与 Scanner 同样的处理：裁掉 DPI-unaware 窗口右侧/下方的空白哨兵，算出图像→屏幕缩放
                Rect content = Win32.ContentBox(image);
                if (content.IsEmpty) content = new Rect(0, 0, image.Width, image.Height);
                double scx = content.W > 0 ? (double)wi.Frame.W / content.W : 1.0;
                double scy = content.H > 0 ? (double)wi.Frame.H / content.H : 1.0;
                if (Math.Abs(scx - 1.0) < 0.02 && Math.Abs(scy - 1.0) < 0.02) { scx = 1.0; scy = 1.0; }
                if (content.X > 0 || content.Y > 0 || content.W < image.Width || content.H < image.Height)
                {
                    var cropped = image.Clone(new Rectangle(content.X, content.Y, content.W, content.H), PixelFormat.Format32bppArgb);
                    image.Dispose();
                    image = cropped;
                }
                scaleX = scx; scaleY = scy;
                screenRect = new Rect(wi.Frame.X + (int)Math.Round(content.X * scx),
                    wi.Frame.Y + (int)Math.Round(content.Y * scy),
                    (int)Math.Round(image.Width * scx), (int)Math.Round(image.Height * scy));
            }

            // 屏幕坐标 -> 图像像素坐标
            double lx = scaleX > 0 ? (px - screenRect.X) / scaleX : 0;
            double ly = scaleY > 0 ? (py - screenRect.Y) / scaleY : 0;
            bool inside = lx >= 0 && ly >= 0 && lx < image.Width && ly < image.Height;

            double bwL = Math.Max(1.0, bw / (scaleX > 0 ? scaleX : 1.0));
            double bhL = Math.Max(1.0, bh / (scaleY > 0 ? scaleY : 1.0));
            var boxLocal = new Rect(
                (int)Math.Round(lx - bwL / 2.0), (int)Math.Round(ly - bhL / 2.0),
                (int)Math.Round(bwL), (int)Math.Round(bhL));

            string pixel = null;
            try
            {
                int gx = (int)Math.Round(lx), gy = (int)Math.Round(ly);
                if (gx < 0) gx = 0; if (gx >= image.Width) gx = image.Width - 1;
                if (gy < 0) gy = 0; if (gy >= image.Height) gy = image.Height - 1;
                Color c = image.GetPixel(gx, gy);
                pixel = "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
            }
            catch { }

            long top = Win32.TopWindowAt(px, py);
            bool covered = win != null && top != 0 && top != win.Hwnd;
            long hwndForCandidate = win != null ? win.Hwnd : 0;
            Rect hitBox;
            string candRaw = MatchCandidate(hwndForCandidate, px, py, out hitBox);
            string candId = null, candText = null;
            if (candRaw != null)
            {
                int t = candRaw.IndexOf('\t');
                if (t > 0) { candId = candRaw.Substring(0, t); candText = candRaw.Substring(t + 1); }
            }

            // ---- 画图
            double f = 1.0;
            int longSide = Math.Max(image.Width, image.Height);
            if (longSide > MaxLongSide) f = (double)MaxLongSide / longSide;

            int vw = Math.Max(1, (int)Math.Round(image.Width * f));
            int vh = Math.Max(1, (int)Math.Round(image.Height * f));

            string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            int zoomUsed = 1;
            using (var view = new Bitmap(vw, vh, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(view))
                {
                    g.InterpolationMode = f < 1.0 ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    g.DrawImage(image, 0, 0, vw, vh);

                    var vb = new Rect((int)Math.Round(boxLocal.X * f), (int)Math.Round(boxLocal.Y * f),
                        Math.Max(2, (int)Math.Round(boxLocal.W * f)), Math.Max(2, (int)Math.Round(boxLocal.H * f)));
                    int ccx = (int)Math.Round(lx * f), ccy = (int)Math.Round(ly * f);
                    if (!inside)
                    {
                        if (ccx < 0) ccx = 0; if (ccx >= vw) ccx = vw - 1;
                        if (ccy < 0) ccy = 0; if (ccy >= vh) ccy = vh - 1;
                    }

                    float lw = Math.Max(1.6f, 2.2f * (float)f);
                    using (var halo = new Pen(Color.FromArgb(190, 0, 0, 0), lw + 2.4f))
                        g.DrawRectangle(halo, vb.X, vb.Y, vb.W, vb.H);
                    using (var pen = new Pen(Green, lw))
                        g.DrawRectangle(pen, vb.X, vb.Y, vb.W, vb.H);
                    using (var corner = new Pen(Green, lw + 1.6f))
                    {
                        int arm = Math.Max(6, vb.W / 3);
                        g.DrawLine(corner, vb.X, vb.Y, vb.X + arm, vb.Y);
                        g.DrawLine(corner, vb.X, vb.Y, vb.X, vb.Y + Math.Max(6, vb.H / 3));
                        g.DrawLine(corner, vb.Right, vb.Bottom, vb.Right - arm, vb.Bottom);
                        g.DrawLine(corner, vb.Right, vb.Bottom, vb.Right, vb.Bottom - Math.Max(6, vb.H / 3));
                    }

                    if (inside)
                    {
                        using (var wh = new Pen(Color.FromArgb(210, 255, 255, 255), 3.2f))
                        using (var gh = new Pen(Green, 1.4f))
                        {
                            int arm = Math.Max(7, (int)(vb.W * 0.9));
                            foreach (var p in new Pen[] { wh, gh })
                            {
                                g.DrawLine(p, ccx - arm, ccy, ccx + arm, ccy);
                                g.DrawLine(p, ccx, ccy - arm, ccx, ccy + arm);
                            }
                            using (var dot = new SolidBrush(Green)) g.FillEllipse(dot, ccx - 2, ccy - 2, 4, 4);
                        }
                    }

                    string label = px + "," + py + "  " + bw + "x" + bh;
                    if (candId != null) label += "  -> #" + candId + " " + (candText == null ? "" : candText);
                    using (var fLbl = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold))
                    using (var bgLbl = new SolidBrush(Ink))
                    using (var fgLbl = new SolidBrush(Green))
                    {
                        SizeF szL = g.MeasureString(label, fLbl);
                        float tx = vb.X, ty = vb.Y - szL.Height - 4;
                        if (ty < 0) ty = vb.Bottom + 4;
                        if (tx + szL.Width + 10 > vw) tx = Math.Max(0, vw - szL.Width - 10);
                        g.FillRectangle(bgLbl, tx, ty, szL.Width + 8, szL.Height + 2);
                        g.DrawString(label, fLbl, fgLbl, tx + 4, ty + 1);
                    }

                    // 放大镜：框外留一圈上下文就够（crop ≈ 框 + 24px），这样放大倍率才上得去
                    int radius = (int)Math.Max(24, Math.Max(boxLocal.W, boxLocal.H) / 2 + 12);
                    var cropL = new Rect(boxLocal.Cx - radius, boxLocal.Cy - radius, radius * 2, radius * 2)
                        .Intersect(new Rect(0, 0, image.Width, image.Height));
                    if (!cropL.IsEmpty)
                    {
                        int zx = zoomWant;
                        if (cropL.W * zx > PanelWidth) zx = Math.Max(2, PanelWidth / Math.Max(1, cropL.W));
                        if (zx > zoomWant) zx = zoomWant;
                        zoomUsed = zx;
                        int pw = cropL.W * zx, ph = cropL.H * zx;
                        int header = 26, margin = 14;
                        int panelX = vw - pw - margin - 4;
                        int panelY = vh - ph - header - margin - 4;
                        if (panelX < 4) panelX = 4;
                        if (panelY < 4) panelY = 4;
                        var panelRect = new Rect(panelX, panelY, pw + 4, ph + header + 4);
                        if (panelRect.Intersect(new Rect(vb.X - 6, vb.Y - 30, vb.W + 12, vb.H + 44)).Area > 0)
                        {
                            panelY = 6;
                            panelRect = new Rect(panelX, panelY, pw + 4, ph + header + 4);
                        }

                        using (var crop = image.Clone(new Rectangle(cropL.X, cropL.Y, cropL.W, cropL.H), PixelFormat.Format32bppArgb))
                        {
                            using (var pg = Graphics.FromImage(view))
                            {
                                pg.InterpolationMode = InterpolationMode.NearestNeighbor;
                                pg.PixelOffsetMode = PixelOffsetMode.Half;
                                pg.SmoothingMode = SmoothingMode.AntiAlias;
                                pg.DrawImage(crop, panelX + 2, panelY + header + 2, pw, ph);

                                using (var grid = new Pen(Color.FromArgb(46, 255, 255, 255), 1f))
                                {
                                    for (int gx = 8; gx < cropL.W; gx += 8)
                                        pg.DrawLine(grid, panelX + 2 + gx * zx, panelY + header + 2,
                                            panelX + 2 + gx * zx, panelY + header + 2 + ph);
                                    for (int gy = 8; gy < cropL.H; gy += 8)
                                        pg.DrawLine(grid, panelX + 2, panelY + header + 2 + gy * zx,
                                            panelX + 2 + pw, panelY + header + 2 + gy * zx);
                                }

                                var zb = new Rect(panelX + 2 + (boxLocal.X - cropL.X) * zx, panelY + header + 2 + (boxLocal.Y - cropL.Y) * zx,
                                    Math.Max(2, boxLocal.W * zx), Math.Max(2, boxLocal.H * zx));
                                using (var pen = new Pen(Green, 2f))
                                    pg.DrawRectangle(pen, zb.X, zb.Y, zb.W, zb.H);
                                if (lx >= cropL.X && lx < cropL.Right && ly >= cropL.Y && ly < cropL.Bottom)
                                {
                                    int zcx = panelX + 2 + (int)Math.Round((lx - cropL.X) * zx);
                                    int zcy = panelY + header + 2 + (int)Math.Round((ly - cropL.Y) * zx);
                                    using (var wh = new Pen(Color.FromArgb(220, 255, 255, 255), 3f))
                                    using (var gh = new Pen(Green, 1.2f))
                                    {
                                        int arm = Math.Max(9, zb.W);
                                        foreach (var p in new Pen[] { wh, gh })
                                        {
                                            pg.DrawLine(p, zcx - arm, zcy, zcx + arm, zcy);
                                            pg.DrawLine(p, zcx, zcy - arm, zcx, zcy + arm);
                                        }
                                    }
                                }

                                using (var border = new Pen(Color.FromArgb(230, 255, 255, 255), 2f))
                                    pg.DrawRectangle(border, panelX, panelY, panelRect.W - 1, panelRect.H - 1);
                                using (var bgH = new SolidBrush(Ink))
                                    pg.FillRectangle(bgH, panelX + 1, panelY + 1, panelRect.W - 2, header);
                                using (var fH = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
                                using (var fgH = new SolidBrush(Green))
                                    pg.DrawString("放大 " + zx + "x  绿框=点击范围  格=8px", fH, fgH, panelX + 7, panelY + 5);
                            }
                        }
                    }

                    string banner = null;
                    if (!inside) banner = "坐标不在截图范围内：这个点落到窗口外面去了";
                    else if (covered) banner = "注意：这一点此刻被其他窗口盖住，直接点会打到别的窗口";
                    if (banner != null)
                    {
                        using (var fB = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold))
                        using (var bgB = new SolidBrush(Color.FromArgb(235, 30, 26, 8)))
                        using (var fgB = new SolidBrush(Warn))
                        {
                            SizeF szB = g.MeasureString(banner, fB);
                            g.FillRectangle(bgB, 0, 0, Math.Min(vw, szB.Width + 16), szB.Height + 8);
                            g.DrawString(banner, fB, fgB, 8, 4);
                        }
                    }
                }

                view.Save(outPath, ImageFormat.Png);
            }

            image.Dispose();

            var sb = new StringBuilder();
            sb.Append("{\"ok\":true");
            sb.Append(",\"image\":").Append(Json.Str(Path.GetFullPath(outPath)));
            sb.Append(",\"point\":{\"x\":").Append(px).Append(",\"y\":").Append(py).Append("}");
            sb.Append(",\"box\":{\"x\":").Append(px - bw / 2).Append(",\"y\":").Append(py - bh / 2)
              .Append(",\"w\":").Append(bw).Append(",\"h\":").Append(bh)
              .Append(",\"x1\":").Append(px - bw / 2).Append(",\"y1\":").Append(py - bh / 2)
              .Append(",\"x2\":").Append(px - bw / 2 + bw).Append(",\"y2\":").Append(py - bh / 2 + bh).Append("}");
            sb.Append(",\"inside\":").Append(Json.Bool(inside));
            sb.Append(",\"covered\":").Append(Json.Bool(covered));
            sb.Append(",\"zoom\":").Append(zoomUsed);
            if (pixel != null) sb.Append(",\"pixel\":").Append(Json.Str(pixel));
            if (win != null) sb.Append(",\"window\":").Append(win.ToJson());
            if (covered && top != 0) sb.Append(",\"coveredBy\":").Append(Json.Str(DescribeWindow(top)));
            if (candId != null)
            {
                sb.Append(",\"candidate\":{\"id\":").Append(candId)
                  .Append(",\"text\":").Append(Json.Str(candText == null ? "" : candText))
                  .Append(",\"screen\":{\"x\":").Append(hitBox.X).Append(",\"y\":").Append(hitBox.Y)
                  .Append(",\"w\":").Append(hitBox.W).Append(",\"h\":").Append(hitBox.H)
                  .Append(",\"cx\":").Append(hitBox.Cx).Append(",\"cy\":").Append(hitBox.Cy).Append("}}");
            }
            string hint;
            if (!inside) hint = "这个坐标不在目标窗口内，先 computer_scan 或 computer_vision 重新取点";
            else if (covered) hint = "该点被其他窗口遮挡，点之前先 focus 目标窗口";
            else if (candId != null) hint = "绿框落在候选 #" + candId + " 上，确认是你要的那个就 computer_act 点它";
            else hint = "看图确认绿框压住的是你要的那块；偏了就把坐标按图上的偏差修正后再 check 一次";
            sb.Append(",\"hint\":").Append(Json.Str(hint));
            sb.Append(",\"notes\":").Append(Json.StrArray(notes));
            sb.Append("}");
            Console.Write(sb.ToString());
            return 0;
        }
    }
}
