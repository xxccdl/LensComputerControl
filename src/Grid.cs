// 轴刻度网格定位：不往图上贴大量编号（编号会盖住内容、缩放后也糊），
// 只在边缘画列号/行号，模型报「第 c 列第 r 行」，坐标由引擎算。
// 需要更准时再 zoom 一级：把该格裁出来放大并画二级刻度。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace Lens
{
    public static class GridTools
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

        static string StatePath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "state", "grid.txt");
        }

        static void SaveState(ScanContext ctx, int cols, int rows)
        {
            try
            {
                string p = StatePath();
                string dir = Path.GetDirectoryName(p);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                sb.AppendLine("hwnd=" + ctx.Window.Hwnd);
                sb.AppendLine("offsetX=" + ctx.OffsetX);
                sb.AppendLine("offsetY=" + ctx.OffsetY);
                sb.AppendLine("scaleX=" + ctx.ScaleX.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture));
                sb.AppendLine("scaleY=" + ctx.ScaleY.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture));
                sb.AppendLine("imgW=" + ctx.Image.Width);
                sb.AppendLine("imgH=" + ctx.Image.Height);
                sb.AppendLine("cols=" + cols);
                sb.AppendLine("rows=" + rows);
                File.WriteAllText(p, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        public static Dictionary<string, string> LoadState()
        {
            var d = new Dictionary<string, string>();
            try
            {
                string p = StatePath();
                if (!File.Exists(p)) return d;
                foreach (string line in File.ReadAllLines(p))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch { }
            return d;
        }

        static double Num(Dictionary<string, string> d, string k, double def)
        {
            string v;
            double r;
            if (d.TryGetValue(k, out v) &&
                double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out r))
                return r;
            return def;
        }

        /// <summary>网格单元 -> 屏幕物理矩形（c/r 从 1 开始）。</summary>
        public static Rect CellToScreen(Dictionary<string, string> st, int c, int r)
        {
            int cols = (int)Num(st, "cols", 32), rows = (int)Num(st, "rows", 18);
            int imgW = (int)Num(st, "imgW", 0), imgH = (int)Num(st, "imgH", 0);
            double sx = Num(st, "scaleX", 1), sy = Num(st, "scaleY", 1);
            double ox = Num(st, "offsetX", 0), oy = Num(st, "offsetY", 0);
            double cw = (double)imgW / cols, ch = (double)imgH / rows;
            return new Rect((int)Math.Round(ox + (c - 1) * cw * sx), (int)Math.Round(oy + (r - 1) * ch * sy),
                (int)Math.Round(cw * sx), (int)Math.Round(ch * sy));
        }

        static Bitmap DrawGrid(Bitmap src, int cols, int rows, int scale)
        {
            int w = src.Width, h = src.Height;
            var bmp = new Bitmap(w * scale, h * scale, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(src, 0, 0, w * scale, h * scale);

                double cw = (double)w / cols * scale, ch = (double)h / rows * scale;
                using (var pen = new Pen(Color.FromArgb(60, 0, 210, 255), 1f))
                {
                    for (int c = 1; c < cols; c++) g.DrawLine(pen, (float)(c * cw), 0, (float)(c * cw), h * scale);
                    for (int r = 1; r < rows; r++) g.DrawLine(pen, 0, (float)(r * ch), w * scale, (float)(r * ch));
                }
                using (var pen = new Pen(Color.FromArgb(170, 0, 235, 255), 1.7f))
                {
                    for (int c = 4; c < cols; c += 4) g.DrawLine(pen, (float)(c * cw), 0, (float)(c * cw), h * scale);
                    for (int r = 4; r < rows; r += 4) g.DrawLine(pen, 0, (float)(r * ch), w * scale, (float)(r * ch));
                }

                // 字号跟随「输出图里格子的大小」，不跟缩放倍率走 —— 否则 10x 放大后标签会糊掉半个画面
                double cellPx = Math.Min((double)w * scale / cols, (double)h * scale / rows);
                float fs = (float)Math.Max(11.0, Math.Min(30.0, cellPx * 0.32));
                using (var font = new Font("Consolas", fs, FontStyle.Bold))
                using (var bg = new SolidBrush(Color.FromArgb(215, 0, 0, 0)))
                using (var fg = new SolidBrush(Color.FromArgb(140, 240, 255)))
                {
                    int step = cols > 24 ? 2 : 1;
                    for (int c = 1; c <= cols; c++)
                    {
                        if (step == 2 && c % 2 != 0) continue;
                        string t = c.ToString();
                        var sz = g.MeasureString(t, font);
                        float x = (float)((c - 0.5) * cw - sz.Width / 2);
                        g.FillRectangle(bg, x - 2, 0, sz.Width + 4, sz.Height);
                        g.DrawString(t, font, fg, x, 0);
                    }
                    int rstep = rows > 18 ? 2 : 1;
                    for (int r = 1; r <= rows; r++)
                    {
                        if (rstep == 2 && r % 2 != 0) continue;
                        string t = r.ToString();
                        var sz = g.MeasureString(t, font);
                        float y = (float)((r - 0.5) * ch - sz.Height / 2);
                        g.FillRectangle(bg, 0, y, sz.Width + 4, sz.Height);
                        g.DrawString(t, font, fg, 0, y);
                    }
                }
            }
            return bmp;
        }

        public static int CmdGrid(string[] a)
        {
            string key = Arg(a, "--window", null);
            if (key == null) { Console.Write("{\"ok\":false,\"error\":\"missing --window\"}"); return 2; }
            int cols = int.Parse(Arg(a, "--cols", "32"));
            int rows = int.Parse(Arg(a, "--rows", "18"));
            if (cols < 2 || cols > 200 || rows < 2 || rows > 200) { Console.Write("{\"ok\":false,\"error\":\"cols/rows out of range\"}"); return 2; }
            string outp = Arg(a, "--out", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "grid.png"));

            var ctx = Scanner.Scan(key, "none", false, null, !Flag(a, "--no-restore"));
            if (ctx.Image == null) { Console.Write("{\"ok\":false,\"error\":\"capture failed\"}"); return 3; }
            SaveState(ctx, cols, rows);

            int scale = cols > 60 ? 1 : 2;
            using (var bmp = DrawGrid(ctx.Image, cols, rows, scale))
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(outp));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                bmp.Save(outp, ImageFormat.Png);
            }

            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"cols\":").Append(cols).Append(",\"rows\":").Append(rows);
            sb.Append(",\"image\":").Append(Json.Str(Path.GetFullPath(outp)));
            sb.Append(",\"imgW\":").Append(ctx.Image.Width).Append(",\"imgH\":").Append(ctx.Image.Height);
            sb.Append(",\"cellW\":").Append(Math.Round((double)ctx.Image.Width / cols, 1));
            sb.Append(",\"cellH\":").Append(Math.Round((double)ctx.Image.Height / rows, 1));
            sb.Append(",\"screenCellW\":").Append(Math.Round((double)ctx.Image.Width / cols * ctx.ScaleX, 1));
            sb.Append(",\"screenCellH\":").Append(Math.Round((double)ctx.Image.Height / rows * ctx.ScaleY, 1));
            sb.Append(",\"origin\":{\"x\":").Append(ctx.OffsetX).Append(",\"y\":").Append(ctx.OffsetY).Append("}");
            sb.Append(",\"hint\":").Append(Json.Str("看边缘刻度报 (列,行)，用 click --cell 列,行 精确点击；更准就再 zoom 一级"));
            sb.Append("}");
            Console.Write(sb.ToString());
            return 0;
        }

        public static int CmdZoom(string[] a)
        {
            string cArg = Arg(a, "--c", null), rArg = Arg(a, "--r", null);
            if (cArg == null || rArg == null) { Console.Write("{\"ok\":false,\"error\":\"missing --c/--r\"}"); return 2; }
            int c = int.Parse(cArg), r = int.Parse(rArg);
            int sub = int.Parse(Arg(a, "--sub", "8"));
            if (sub < 2 || sub > 32) { Console.Write("{\"ok\":false,\"error\":\"sub out of range\"}"); return 2; }
            string key = Arg(a, "--window", null);
            string outp = Arg(a, "--out", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "zoom.png"));

            var st = LoadState();
            string stKey = key;
            if (stKey == null && st.ContainsKey("hwnd")) stKey = st["hwnd"];
            if (stKey == null) { Console.Write("{\"ok\":false,\"error\":\"run grid first\"}"); return 2; }

            var ctx = Scanner.Scan(stKey, "none", false, null, true);
            if (ctx.Image == null) { Console.Write("{\"ok\":false,\"error\":\"capture failed\"}"); return 3; }
            if (st.Count == 0 || key != null || Math.Abs(ctx.OffsetX - Num(st, "offsetX", -1)) > 1)
            {
                int cols2 = (int)Num(st, "cols", 32), rows2 = (int)Num(st, "rows", 18);
                SaveState(ctx, cols2, rows2);
                st = LoadState();
            }

            int cols = (int)Num(st, "cols", 32), rows = (int)Num(st, "rows", 18);
            int imgW = ctx.Image.Width, imgH = ctx.Image.Height;
            var cellLocal = new Rect((int)((double)imgW / cols * (c - 1)), (int)((double)imgH / rows * (r - 1)),
                (int)Math.Ceiling((double)imgW / cols), (int)Math.Ceiling((double)imgH / rows))
                .Intersect(new Rect(0, 0, imgW, imgH));
            if (cellLocal.IsEmpty) { Console.Write("{\"ok\":false,\"error\":\"cell out of range\"}"); return 4; }

            int mx = cellLocal.W / 2, my = cellLocal.H / 2;
            Rect crop = new Rect(cellLocal.X - mx, cellLocal.Y - my, cellLocal.W + 2 * mx, cellLocal.H + 2 * my)
                .Intersect(new Rect(0, 0, imgW, imgH));

            int scale = Math.Max(4, Math.Min(10, 2200 / Math.Max(1, Math.Max(crop.W, crop.H))));
            using (var sub0 = ctx.Image.Clone(new Rectangle(crop.X, crop.Y, crop.W, crop.H), PixelFormat.Format32bppArgb))
            using (var bmp = DrawGrid(sub0, sub, sub, scale))
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(outp));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                bmp.Save(outp, ImageFormat.Png);
            }

            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"c\":").Append(c).Append(",\"r\":").Append(r);
            sb.Append(",\"sub\":").Append(sub).Append(",\"image\":").Append(Json.Str(Path.GetFullPath(outp)));
            sb.Append(",\"scale\":").Append(scale).Append(",\"crop\":").Append(crop.ToJson());
            sb.Append(",\"cellScreen\":").Append(ctx.ToScreen(cellLocal).ToJson());
            sb.Append(",\"cells\":[");
            double scw = (double)crop.W / sub, sch = (double)crop.H / sub;
            int n = 0;
            for (int rr = 1; rr <= sub; rr++)
                for (int cc = 1; cc <= sub; cc++)
                {
                    var lr = new Rect((int)Math.Round(crop.X + (cc - 1) * scw), (int)Math.Round(crop.Y + (rr - 1) * sch),
                        (int)Math.Round(scw), (int)Math.Round(sch));
                    Rect scr = ctx.ToScreen(lr);
                    if (n++ > 0) sb.Append(",");
                    sb.Append("{\"c\":").Append(cc).Append(",\"r\":").Append(rr)
                      .Append(",\"cx\":").Append(scr.Cx).Append(",\"cy\":").Append(scr.Cy).Append("}");
                }
            sb.Append("],\"hint\":").Append(Json.Str("看放大图的二级刻度报 (列,行)，点击坐标取 cells 中对应项的 cx,cy"));
            sb.Append("}");
            Console.Write(sb.ToString());
            return 0;
        }
    }
}
