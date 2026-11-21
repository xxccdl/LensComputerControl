// 纯视觉递归定位：把屏幕切成 N 个带坐标的格子给 AI 看，AI 选一格，再切那一格。
// 设计动机：当所有识别通道（UIA/OCR/色块/模板）都失效时——游戏、画布、远程桌面、
// 自绘且无文字的面板——唯一的通用办法就是"把图放大，让人（AI）用眼睛找"。
// 关键点：每一格都把屏幕绝对坐标写在格子里，AI 只要报编号，坐标由引擎算。
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
    public static class Vision
    {
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

        /// <summary>选 rows/cols。两条规则：
        /// ① 如果存在"格子长宽比 ≈ 父区域长宽比"的整数网格（k×k 一定满足），就用它——
        ///    这样每级下钻形状完全不变，"第几格"的经验可逐级复用；
        /// ② 否则退化成最接近正方形的划分，并**重罚细条**（实测出现过 96×22 这种没法看的条带）。
        /// 不能只按父区域长宽比硬套：10 格在 16:9 上凑不出等比网格，会来回翻转并产生细条。</summary>
        static void BestGrid(int n, int w, int h, out int cols, out int rows)
        {
            double parentAspect = (double)Math.Max(1, w) / Math.Max(1, h);
            cols = 1; rows = n;
            double best = double.MaxValue;
            bool found = false;
            for (int c = 1; c <= n; c++)
            {
                if (n % c != 0) continue;
                int r = n / c;
                double cellAspect = ((double)w / c) / Math.Max(1.0, (double)h / r);
                double shapeErr = Math.Abs(Math.Log(cellAspect / parentAspect));   // 与父区域形状的偏差
                double squareErr = Math.Abs(Math.Log(cellAspect));                 // 接近正方形的程度
                double score = shapeErr < 0.12 ? shapeErr : 1.0 + squareErr;       // 能等比就优先等比
                if (cellAspect > 3.0 || cellAspect < 1.0 / 3.0) score += 3.0;      // 细条重罚
                if (score < best) { best = score; cols = c; rows = r; found = true; }
            }
            if (found) return;

            // 素数 N 无法整除：按长宽比分配，最后一行可能不满
            cols = Math.Max(1, (int)Math.Round(Math.Sqrt(n * parentAspect)));
            rows = (int)Math.Ceiling((double)n / cols);
        }

        static List<Rect> Split(Rect region, int cols, int rows, out int produced, int limit)
        {
            var list = new List<Rect>();
            produced = 0;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    if (produced >= limit) return list;
                    int x = region.X + (int)Math.Round((double)region.W * c / cols);
                    int y = region.Y + (int)Math.Round((double)region.H * r / rows);
                    int x2 = region.X + (int)Math.Round((double)region.W * (c + 1) / cols);
                    int y2 = region.Y + (int)Math.Round((double)region.H * (r + 1) / rows);
                    list.Add(new Rect(x, y, Math.Max(1, x2 - x), Math.Max(1, y2 - y)));
                    produced++;
                }
            }
            return list;
        }

        /// <summary>位图像素 (0,0) 对应的屏幕坐标（多显示器时可能是负的）。</summary>
        static int OriginX, OriginY;

        static Bitmap CaptureDesktop(out Rect screen)
        {
            // 虚拟屏幕：覆盖所有显示器（SM_XVIRTUALSCREEN..），副屏在主屏左边时 x 会是负数
            int vx = Win32.GetSystemMetrics(76), vy = Win32.GetSystemMetrics(77);
            int w = Win32.GetSystemMetrics(78), h = Win32.GetSystemMetrics(79);
            if (w <= 0 || h <= 0) { vx = 0; vy = 0; w = Win32.GetSystemMetrics(0); h = Win32.GetSystemMetrics(1); }
            screen = new Rect(vx, vy, w, h);
            OriginX = vx; OriginY = vy;
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

        /// <summary>把 region 裁出来、按需放大，并在每格里写上「编号 + 屏幕绝对坐标」。
        /// 两个可读性要点：① 格子越小标签越小、只留编号，避免盖住要看的内容；
        /// ② 右下角常驻一张整屏缩略图，标出当前在哪一块，深层下钻时不至于迷失方向。</summary>
        static Bitmap Render(Bitmap full, Rect region, List<Rect> cells, string breadcrumb, int scaleHint, int markX, int markY)
        {
            // region 是屏幕坐标，位图坐标要减掉虚拟屏幕原点，否则副屏（负坐标）会裁错
            var bmpRect = new Rect(0, 0, full.Width, full.Height);
            var regionLocal = new Rect(region.X - OriginX, region.Y - OriginY, region.W, region.H);
            Rect crop = regionLocal.Intersect(bmpRect);
            if (crop.IsEmpty) crop = regionLocal;
            int scale = scaleHint;
            if (scale <= 0)
            {
                double target = 1500.0;   // 长边目标像素：太小看不清，太大浪费上下文
                scale = (int)Math.Max(1, Math.Min(6, Math.Round(target / Math.Max(1, Math.Max(crop.W, crop.H)))));
            }

            var outBmp = new Bitmap(crop.W * scale, crop.H * scale, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(outBmp))
            {
                // 低倍用最近邻（保住像素边缘），高倍用双三次（否则文字全成马赛克，反而更难认）
                g.InterpolationMode = scale <= 2 ? InterpolationMode.NearestNeighbor
                    : (scale >= 4 ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor);
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                using (var src = full.Clone(new Rectangle(crop.X, crop.Y, crop.W, crop.H), PixelFormat.Format32bppArgb))
                    g.DrawImage(src, 0, 0, outBmp.Width, outBmp.Height);

                using (var pen = new Pen(Color.FromArgb(190, 0, 220, 255), Math.Max(1.2f, scale * 0.6f)))
                using (var thick = new Pen(Color.FromArgb(150, 255, 210, 0), Math.Max(2f, scale * 1.1f)))
                using (var chip = new SolidBrush(Color.FromArgb(215, 0, 0, 0)))
                using (var chipLite = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                using (var fgBig = new SolidBrush(Color.FromArgb(120, 245, 255)))
                using (var fgSmall = new SolidBrush(Color.FromArgb(200, 240, 255)))
                {
                    for (int i = 0; i < cells.Count; i++)
                    {
                        Rect rc = cells[i];
                        var local = new Rectangle((rc.X - OriginX - crop.X) * scale, (rc.Y - OriginY - crop.Y) * scale, rc.W * scale, rc.H * scale);
                        g.DrawRectangle(i % 5 == 0 ? thick : pen, local);

                        // 标签尺寸随格子大小自适应：小格子只写编号，大格子才写坐标
                        int cw = local.Width, ch = local.Height;
                        float fs = Math.Max(9f, Math.Min(26f, Math.Min(cw, ch) / 9f));
                        bool roomy = cw >= 120 && ch >= 96;
                        using (var fIdx = new Font("Consolas", fs, FontStyle.Bold))
                        using (var fCrd = new Font("Consolas", Math.Max(8f, fs * 0.5f)))
                        {
                            string idx = "#" + (i + 1);
                            SizeF s1 = g.MeasureString(idx, fIdx);
                            if (roomy)
                            {
                                string coord = rc.X + "," + rc.Y + " " + rc.W + "x" + rc.H + "  @" + rc.Cx + "," + rc.Cy;
                                SizeF s2 = g.MeasureString(coord, fCrd);
                                g.FillRectangle(chip, local.X + 1, local.Y + 1, Math.Max(s1.Width, s2.Width) + 8, s1.Height + s2.Height + 4);
                                g.DrawString(idx, fIdx, fgBig, local.X + 4, local.Y + 2);
                                g.DrawString(coord, fCrd, fgSmall, local.X + 4, local.Y + 2 + s1.Height);
                            }
                            else
                            {
                                g.FillRectangle(chipLite, local.X + 1, local.Y + 1, s1.Width + 6, s1.Height + 3);
                                g.DrawString(idx, fIdx, fgBig, local.X + 3, local.Y + 1);
                            }
                        }
                    }

                    // 可选：十字准星（--mark x,y），用来确认某个点落在哪一格
                    if (markX != int.MinValue)
                    {
                        int mx = (markX - OriginX - crop.X) * scale, my = (markY - OriginY - crop.Y) * scale;
                        if (mx >= 0 && my >= 0 && mx < outBmp.Width && my < outBmp.Height)
                        {
                            using (var mp = new Pen(Color.FromArgb(255, 255, 70, 90), Math.Max(2f, scale * 0.9f)))
                            {
                                int arm = (int)Math.Max(18, 26 * Math.Min(scale, 3));
                                g.DrawLine(mp, mx - arm, my, mx + arm, my);
                                g.DrawLine(mp, mx, my - arm, mx, my + arm);
                                g.DrawEllipse(mp, mx - arm / 3, my - arm / 3, arm * 2 / 3, arm * 2 / 3);
                            }
                        }
                    }

                    // 面包屑：当前处在哪一级
                    if (!string.IsNullOrEmpty(breadcrumb))
                    {
                        using (var f = new Font("Consolas", Math.Max(11f, 14f * Math.Min(scale, 3) / 2f), FontStyle.Bold))
                        {
                            SizeF sb = g.MeasureString(breadcrumb, f);
                            g.FillRectangle(chip, 0, 0, sb.Width + 12, sb.Height + 6);
                            g.DrawString(breadcrumb, f, fgBig, 6, 3);
                        }
                    }

                    // 整屏缩略图：告诉你"现在这一块在整块屏幕的哪里"
                    DrawMinimap(g, full, region, outBmp.Width, outBmp.Height);
                }
            }
            return outBmp;
        }

        /// <summary>右下角缩略图 + 当前区域高亮框。</summary>
        static void DrawMinimap(Graphics g, Bitmap full, Rect region, int outW, int outH)
        {
            const int MW = 268;                      // 缩略图宽
            int mh = Math.Max(60, (int)(MW * (double)full.Height / Math.Max(1, full.Width)));
            int pad = 16;
            var box = new Rectangle(outW - MW - pad, outH - mh - pad, MW, mh);
            if (box.X < 8 || box.Y < 8) return;      // 图太小就不画，别挡内容

            using (var bg = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                g.FillRectangle(bg, new Rectangle(box.X - 4, box.Y - 4, box.Width + 8, box.Height + 8));
            using (var thumb = new Bitmap(full, MW, mh))
            using (var dim = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
            {
                g.DrawImage(thumb, box);
                g.FillRectangle(dim, box);
            }
            double sx = (double)MW / full.Width, sy = (double)mh / full.Height;
            var hi = new Rectangle(
                box.X + (int)((region.X - OriginX) * sx),
                box.Y + (int)((region.Y - OriginY) * sy),
                Math.Max(3, (int)(region.W * sx)),
                Math.Max(3, (int)(region.H * sy)));
            using (var fill = new SolidBrush(Color.FromArgb(70, 56, 189, 248)))
            using (var pen = new Pen(Color.FromArgb(255, 255, 210, 0), 2f))
            {
                g.FillRectangle(fill, hi);
                g.DrawRectangle(pen, hi);
            }
            using (var bp = new Pen(Color.FromArgb(150, 56, 140, 248), 1f))
                g.DrawRectangle(bp, box);
        }

        public static int CmdVision(string[] args)
        {
            string wkey = Arg(args, "--window", null);
            bool desktop = Flag(args, "--desktop") || wkey == null;
            int cells = int.Parse(Arg(args, "--cells", "10"));
            if (cells < 2 || cells > 64) { Console.Write("{\"ok\":false,\"code\":\"BAD_ARGS\",\"message\":\"--cells 需在 2..64\"}"); return 2; }
            string pathArg = Arg(args, "--path", "");
            string outp = Arg(args, "--out", null);
            int scaleHint = int.Parse(Arg(args, "--scale", "0"));
            string rectArg = Arg(args, "--rect", null);   // 显式区域 x,y,w,h（屏幕坐标），跳过 path 下钻
            string markArg = Arg(args, "--mark", null);   // 十字准星 x,y，用于确认某个点落在哪格
            int colsOverride = int.Parse(Arg(args, "--cols", "0"));
            int rowsOverride = int.Parse(Arg(args, "--rows", "0"));
            string click = Arg(args, "--click", null);   // 点第几个子格（1-based）；用于最后一步直接点

            Bitmap full;
            Rect region;
            if (desktop)
            {
                full = CaptureDesktop(out region);
            }
            else
            {
                var wi = Win32.FindWindow(wkey);
                if (wi == null) { Console.Write("{\"ok\":false,\"code\":\"WINDOW_NOT_FOUND\",\"message\":" + Json.Str("找不到窗口: " + wkey) + "}"); return 3; }
                var cap = new Capability();
                string method;
                full = Capture.Acquire(wi, cap, out method, Win32.ListOwnedPopups(new IntPtr(wi.Hwnd)));
                if (full == null) { Console.Write("{\"ok\":false,\"code\":\"CAPTURE_FAILED\",\"message\":" + Json.Str("抓不到该窗口") + "}"); return 4; }
                region = wi.Frame;
                // Capture 返回的是窗口局部图：把它贴到一张全屏画布上，保持屏幕坐标一致
                OriginX = 0; OriginY = 0;
                var canvas = new Bitmap(Win32.GetSystemMetrics(0), Win32.GetSystemMetrics(1), PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(canvas))
                {
                    Rect content = Win32.ContentBox(full);
                    if (content.IsEmpty) content = new Rect(0, 0, full.Width, full.Height);
                    double sx = (double)wi.Frame.W / Math.Max(1, content.W);
                    double sy = (double)wi.Frame.H / Math.Max(1, content.H);
                    g.DrawImage(full, new Rectangle(wi.Frame.X, wi.Frame.Y, wi.Frame.W, wi.Frame.H));
                    full.Dispose();
                    full = canvas;
                    region = new Rect(wi.Frame.X, wi.Frame.Y, wi.Frame.W, wi.Frame.H);
                    if (Math.Abs(sx - 1) > 0.02 || Math.Abs(sy - 1) > 0.02) { /* 已按 frame 拉伸，坐标一致 */ }
                }
            }

            // 按路径逐级下钻
            var crumbs = new List<string>();
            string curPath = "";
            // 显式区域优先于 path：调用方已经知道大概在哪一块时，直接切那块，省掉逐级下钻
            if (!string.IsNullOrEmpty(rectArg))
            {
                string[] rp = rectArg.Split(',');
                int rx, ry, rw, rh;
                if (rp.Length != 4 || !int.TryParse(rp[0], out rx) || !int.TryParse(rp[1], out ry)
                    || !int.TryParse(rp[2], out rw) || !int.TryParse(rp[3], out rh) || rw <= 0 || rh <= 0)
                {
                    Console.Write("{\"ok\":false,\"code\":\"BAD_ARGS\",\"message\":\"--rect 需要 x,y,w,h\"}");
                    return 2;
                }
                region = new Rect(rx, ry, rw, rh);
                curPath = "rect(" + rx + "," + ry + "," + rw + "," + rh + ")";
            }
            else if (!string.IsNullOrEmpty(pathArg))
            {
                foreach (string seg in pathArg.Split('.'))
                {
                    if (seg.Length == 0) continue;
                    int pick;
                    if (!int.TryParse(seg, out pick)) { Console.Write("{\"ok\":false,\"code\":\"BAD_ARGS\",\"message\":\"path 段必须是数字\"}"); return 2; }
                    int cols, rows;
                    if (colsOverride > 0 && rowsOverride > 0) { cols = colsOverride; rows = rowsOverride; }
                    else BestGrid(cells, region.W, region.H, out cols, out rows);
                    int produced;
                    var list = Split(region, cols, rows, out produced, cells);
                    if (pick < 1 || pick > list.Count) { Console.Write("{\"ok\":false,\"code\":\"PATH_OUT_OF_RANGE\",\"message\":" + Json.Str("该级没有第 " + pick + " 格") + "}"); return 5; }
                    region = list[pick - 1];
                    curPath = curPath.Length == 0 ? pick.ToString() : curPath + "." + pick;
                    crumbs.Add("#" + pick + "(" + region.W + "x" + region.H + ")");
                }
            }

            // 当前这一级：把这个 region 再切成 N 格
            int cols2, rows2;
            if (colsOverride > 0 && rowsOverride > 0) { cols2 = colsOverride; rows2 = rowsOverride; }
            else BestGrid(cells, region.W, region.H, out cols2, out rows2);
            int produced2;
            var cellsList = Split(region, cols2, rows2, out produced2, cells);

            string breadcrumb = "L" + (string.IsNullOrEmpty(curPath) ? 0 : curPath.Split('.').Length) + "/" + cells + "格"
                + (curPath.Length > 0 ? "  路径 " + curPath : "")
                + "  区域 " + region.X + "," + region.Y + " " + region.W + "x" + region.H;

            if (string.IsNullOrEmpty(outp))
                outp = Path.Combine(Path.GetTempPath(), "lens-vision", "v-" + DateTime.Now.ToString("HHmmss") + "-" + (curPath.Length == 0 ? "0" : curPath.Replace('.', '-')) + ".png");
            string dir = Path.GetDirectoryName(Path.GetFullPath(outp));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            int scaleUsed = scaleHint;
            int markX = int.MinValue, markY = int.MinValue;
            if (markArg != null)
            {
                string[] mp = markArg.Split(',');
                if (mp.Length == 2) { int.TryParse(mp[0], out markX); int.TryParse(mp[1], out markY); }
            }
            using (var bmp = Render(full, region, cellsList, breadcrumb, scaleHint, markX, markY))
            {
                if (scaleHint <= 0) scaleUsed = (int)Math.Max(1, Math.Min(6, Math.Round(1500.0 / Math.Max(1, Math.Max(region.W, region.H)))));
                bmp.Save(outp, ImageFormat.Png);
            }
            full.Dispose();

            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"path\":").Append(Json.Str(curPath));
            sb.Append(",\"level\":").Append(string.IsNullOrEmpty(curPath) ? 0 : curPath.Split('.').Length);
            sb.Append(",\"region\":").Append(region.ToJson());
            sb.Append(",\"cellW\":").Append((int)Math.Round((double)region.W / cols2));
            sb.Append(",\"cellH\":").Append((int)Math.Round((double)region.H / rows2));
            sb.Append(",\"cols\":").Append(cols2).Append(",\"rows\":").Append(rows2);
            sb.Append(",\"image\":").Append(Json.Str(Path.GetFullPath(outp)));
            sb.Append(",\"imgScale\":").Append(scaleUsed);
            sb.Append(",\"cells\":[");
            for (int i = 0; i < cellsList.Count; i++)
            {
                Rect rc = cellsList[i];
                if (i > 0) sb.Append(",");
                sb.Append("{\"i\":").Append(i + 1)
                  .Append(",\"x\":").Append(rc.X).Append(",\"y\":").Append(rc.Y)
                  .Append(",\"w\":").Append(rc.W).Append(",\"h\":").Append(rc.H)
                  .Append(",\"cx\":").Append(rc.Cx).Append(",\"cy\":").Append(rc.Cy)
                  .Append(",\"nextPath\":").Append(Json.Str(curPath.Length == 0 ? (i + 1).ToString() : curPath + "." + (i + 1)))
                  .Append("}");
            }
            sb.Append("]");
            sb.Append(",\"hint\":").Append(Json.Str(
                "用 read_image 看这张图，报你要的格子编号 i；" +
                "要更准就用 computer_vision {path: nextPath} 继续切那一格；" +
                "定位到够准后再 computer_act {x,y} 或 {cell:\"i\"} 点击。所有坐标都是屏幕物理像素。"));
            sb.Append("}");

            // 可选：直接点击指定子格中心
            if (!string.IsNullOrEmpty(click))
            {
                int pick;
                if (int.TryParse(click, out pick) && pick >= 1 && pick <= cellsList.Count)
                {
                    Rect rc = cellsList[pick - 1];
                    Human.ClickAt(rc.Cx, rc.Cy, "left", 1, !Flag(args, "--instant"));
                }
            }

            Console.Write(sb.ToString());
            return 0;
        }
    }
}
