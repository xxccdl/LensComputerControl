// Lens.Det.TemplateDetector —— 零依赖 NCC（归一化互相关）模板匹配。
// 「教一次，之后永远精确」：锚点 = anchors\<名字>\{meta.json, patch.png}。
// 粗搜（4x 降采样）→ 局部峰值 → 回原图精搜（零均值归一化互相关）→ 3x3 非极大值抑制。
// 约束：C# 5 语法（老 csc），只依赖 System.Drawing / System.IO，像素全部走 LockBits + unsafe。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;

namespace Lens.Det
{
    public static class TemplateDetector
    {
        /// <summary>锚点根目录。默认生产路径；自测时可临时改到别处。</summary>
        public static string AnchorsDir = DefaultAnchorsDir;

        // ---------------- 可调参数 ----------------
        const double FineThr = 0.85;        // 最终命中阈值
        const double CoarseThr = 0.55;      // 粗搜阈值（宁松勿紧，靠精搜把关）
        const int MaxPeaks = 16;            // 每个模板最多精搜几个粗峰值
        const int MaxHitsPerAnchor = 24;    // 每个模板最多产出几个命中
        const int MaxAnchors = 48;          // 模板库上限
        const double BudgetMs = 600.0;      // 单次 Run 的模板匹配时间预算
        const double CacheSeconds = 30.0;   // 模板库重扫间隔
        const string DefaultAnchorsDir = @"D:\code\computerconlex\anchors";

        // ---------------- 模板库 ----------------

        class Anchor
        {
            public string Name;
            public string Kind;
            public int W, H;
            public byte[] Pix;
            public long Sum;
            public long SumSq;
        }

        class Peak
        {
            public int X, Y;
            public double S;
        }

        static List<Anchor> _cache;
        static string _cacheDir;
        static DateTime _lastLoad = DateTime.MinValue;
        static bool _dirExists;

        // ---------------- 入口 ----------------

        public static void Run(ScanContext ctx)
        {
            if (ctx == null) return;
            if (ctx.Image == null) return;

            List<Anchor> lib;
            try
            {
                lib = Load();
            }
            catch (Exception)
            {
                lib = null;
            }

            if (lib == null || lib.Count == 0)
            {
                // 模板库为空是最常见情况：立刻返回。只有「目录在但没货」才留一条 Note。
                if (_dirExists) SafeNote(ctx, "template: loaded=0 hits=0 best=0.00");
                return;
            }

            int hits = 0;
            double best = 0;
            string err = null;
            try
            {
                int w = 0, h = 0;
                byte[] gray = ToGray(ctx.Image, out w, out h);
                if (gray == null)
                {
                    SafeNote(ctx, "template: loaded=" + lib.Count + " hits=0 best=0.00");
                    return;
                }

                DateTime t0 = DateTime.UtcNow;
                int i;
                for (i = 0; i < lib.Count; i++)
                {
                    if ((DateTime.UtcNow - t0).TotalMilliseconds > BudgetMs) break;
                    double b = MatchOne(lib[i], gray, w, h, ctx, t0, ref hits);
                    if (b > best) best = b;
                }
            }
            catch (Exception ex)
            {
                err = Short(ex);
            }

            // 有最坏情况兜底：hits 可能被上限截断，但 Note 永远是最后一次准数
            if (err != null)
            {
                SafeNote(ctx, "template: error " + err);
            }
            else
            {
                SafeNote(ctx, "template: loaded=" + lib.Count + " hits=" + hits + " best=" + Fmt(best));
            }
        }

        static string Fmt(double v)
        {
            return v.ToString("0.00", CultureInfo.InvariantCulture);
        }

        static void SafeNote(ScanContext ctx, string s)
        {
            try { ctx.Notes.Add(s); }
            catch (Exception) { }
        }

        // ---------------- 单片模板匹配 ----------------

        /// <summary>返回该模板的最高 NCC 值（同时把命中写进 ctx）。</summary>
        static double MatchOne(Anchor an, byte[] gray, int w, int h, ScanContext ctx, DateTime t0, ref int hits)
        {
            if (an.W > w || an.H > h) return 0;

            // 粗搜降采样倍数：常规 4x；小模板 2x；再小就只能在原图上搜（且限制图像规模）
            int f = 4;
            if (an.W < 20 || an.H < 20) f = 2;
            while (f > 1 && (an.W / f < 4 || an.H / f < 4)) f /= 2;
            if (f == 1 && (long)w * h > 400000)
            {
                // 极小模板 + 大图：原图滑窗太贵，退到 2x 粗搜，靠精搜窗口兜住定位误差
                f = 2;
            }
            int cw = w, ch = h;
            byte[] cimg = gray;
            if (f > 1)
            {
                cimg = Down(gray, w, h, f, out cw, out ch);
            }
            int ctw = an.W, cth = an.H;
            byte[] ctpl = an.Pix;
            long ctSum = an.Sum, ctSumSq = an.SumSq;
            if (f > 1)
            {
                ctpl = Down(an.Pix, an.W, an.H, f, out ctw, out cth);
                ctSum = 0;
                ctSumSq = 0;
                int k;
                for (k = 0; k < ctpl.Length; k++)
                {
                    ctSum += ctpl[k];
                    ctSumSq += (long)ctpl[k] * ctpl[k];
                }
            }
            if (ctw < 2 || cth < 2 || ctw > cw || cth > ch) return 0;

            int maxX = cw - ctw;
            int maxY = ch - cth;
            if (maxX < 0 || maxY < 0) return 0;

            // ---- 粗搜 ----
            int mapW = maxX + 1;
            int mapH = maxY + 1;
            float[] cmap = new float[mapW * mapH];
            int y;
            for (y = 0; y < mapH; y++)
            {
                int x;
                for (x = 0; x < mapW; x++)
                {
                    double s = NccAt(cimg, cw, ctpl, ctw, cth, x, y, ctSum, ctSumSq);
                    cmap[y * mapW + x] = (float)s;
                }
            }

            // ---- 3x3 局部最大值取峰 ----
            List<Peak> peaks = new List<Peak>();
            for (y = 0; y < mapH; y++)
            {
                int x;
                for (x = 0; x < mapW; x++)
                {
                    double s = cmap[y * mapW + x];
                    if (s < CoarseThr) continue;
                    if (!IsLocalMax(cmap, mapW, mapH, x, y, s)) continue;
                    Peak p = new Peak();
                    p.X = x;
                    p.Y = y;
                    p.S = s;
                    peaks.Add(p);
                }
            }
            if (peaks.Count == 0) return 0;

            peaks.Sort(PeakDesc);

            // 大模板每个位置都很贵 → 少精搜几个峰
            int maxPeaks = MaxPeaks;
            long area = (long)an.W * an.H;
            if (area > 2000)
            {
                maxPeaks = (int)(16000000L / (area * 400L));
                if (maxPeaks < 2) maxPeaks = 2;
                if (maxPeaks > MaxPeaks) maxPeaks = MaxPeaks;
            }

            // ---- 回原图精搜 ----
            List<Peak> found = new List<Peak>();
            int win = f * 2;
            int pi;
            for (pi = 0; pi < peaks.Count && pi < maxPeaks; pi++)
            {
                if ((DateTime.UtcNow - t0).TotalMilliseconds > BudgetMs) break;
                int cx = peaks[pi].X * f;
                int cy = peaks[pi].Y * f;
                int x0 = cx - win; if (x0 < 0) x0 = 0;
                int y0 = cy - win; if (y0 < 0) y0 = 0;
                int x1 = cx + win; if (x1 > w - an.W) x1 = w - an.W;
                int y1 = cy + win; if (y1 > h - an.H) y1 = h - an.H;
                double bs = -1;
                int bx = 0, by = 0;
                int yy;
                for (yy = y0; yy <= y1; yy++)
                {
                    int xx;
                    for (xx = x0; xx <= x1; xx++)
                    {
                        double s = NccAt(gray, w, an.Pix, an.W, an.H, xx, yy, an.Sum, an.SumSq);
                        if (s > bs)
                        {
                            bs = s;
                            bx = xx;
                            by = yy;
                        }
                    }
                }
                if (bs >= FineThr)
                {
                    Peak p = new Peak();
                    p.X = bx;
                    p.Y = by;
                    p.S = bs;
                    found.Add(p);
                }
            }
            if (found.Count == 0) return 0;

            // ---- 非极大值抑制（3x3，半径再放宽到粗搜步长，避免同一目标重复）----
            found.Sort(PeakDesc);
            List<Peak> keep = new List<Peak>();
            int rad = f > 1 ? f : 1;
            int fi;
            for (fi = 0; fi < found.Count; fi++)
            {
                Peak p = found[fi];
                bool ok = true;
                int ki;
                for (ki = 0; ki < keep.Count; ki++)
                {
                    int dx = p.X - keep[ki].X;
                    int dy = p.Y - keep[ki].Y;
                    if (dx < 0) dx = -dx;
                    if (dy < 0) dy = -dy;
                    if (dx <= rad && dy <= rad)
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok) keep.Add(p);
                if (keep.Count >= MaxHitsPerAnchor) break;
            }

            double bestOne = 0;
            int k2;
            for (k2 = 0; k2 < keep.Count; k2++)
            {
                Peak p = keep[k2];
                ctx.Add("template", an.Kind, new Rect(p.X, p.Y, an.W, an.H), an.Name, null, p.S);
                hits++;
                if (p.S > bestOne) bestOne = p.S;
            }
            return bestOne;
        }

        static bool IsLocalMax(float[] map, int mapW, int mapH, int x, int y, double s)
        {
            int dy;
            for (dy = -1; dy <= 1; dy++)
            {
                int yy = y + dy;
                if (yy < 0 || yy >= mapH) continue;
                int dx;
                for (dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int xx = x + dx;
                    if (xx < 0 || xx >= mapW) continue;
                    if (map[yy * mapW + xx] > s) return false;
                }
            }
            return true;
        }

        static int PeakDesc(Peak a, Peak b)
        {
            if (a.S > b.S) return -1;
            if (a.S < b.S) return 1;
            return 0;
        }

        // ---------------- NCC ----------------

        /// <summary>零均值归一化互相关：分子 = Σ(T-μt)(I-μi)，分母 = 两者标准差的乘积。</summary>
        static double NccAt(byte[] img, int iw, byte[] tpl, int tw, int th, int ox, int oy, long sT, long sT2)
        {
            int n = tw * th;
            long sI = 0;
            long sI2 = 0;
            long sTI = 0;
            int y;
            for (y = 0; y < th; y++)
            {
                int io = (oy + y) * iw + ox;
                int to = y * tw;
                int x;
                for (x = 0; x < tw; x++)
                {
                    int iv = img[io + x];
                    int tv = tpl[to + x];
                    sI += iv;
                    sI2 += (long)iv * iv;
                    sTI += (long)tv * iv;
                }
            }
            double num = sTI - (double)sT * sI / n;
            double varT = sT2 - (double)sT * sT / n;
            double varI = sI2 - (double)sI * sI / n;
            if (varT <= 1e-9 || varI <= 1e-9) return 0;
            return num / Math.Sqrt(varT * varI);
        }

        static byte[] Down(byte[] src, int w, int h, int f, out int ow, out int oh)
        {
            ow = w / f;
            oh = h / f;
            if (ow < 1) ow = 1;
            if (oh < 1) oh = 1;
            byte[] dst = new byte[ow * oh];
            int y;
            for (y = 0; y < oh; y++)
            {
                int y0 = y * f;
                int y1 = y0 + f;
                if (y1 > h) y1 = h;
                int x;
                for (x = 0; x < ow; x++)
                {
                    int x0 = x * f;
                    int x1 = x0 + f;
                    if (x1 > w) x1 = w;
                    int s = 0, n = 0;
                    int yy;
                    for (yy = y0; yy < y1; yy++)
                    {
                        int o = yy * w;
                        int xx;
                        for (xx = x0; xx < x1; xx++)
                        {
                            s += src[o + xx];
                            n++;
                        }
                    }
                    dst[y * ow + x] = (byte)(n > 0 ? (s / n) : 0);
                }
            }
            return dst;
        }

        // ---------------- 模板库加载 ----------------

        static List<Anchor> Load()
        {
            DateTime now = DateTime.UtcNow;
            if (_cache != null && _cacheDir != null && _cacheDir == AnchorsDir)
            {
                if ((now - _lastLoad).TotalSeconds < CacheSeconds) return _cache;
            }
            _lastLoad = now;

            List<Anchor> lib = new List<Anchor>();
            List<string> seen = new List<string>();      // 同名锚点只认先找到的那个
            bool exists = false;

            string[] roots = Roots();
            int ri;
            for (ri = 0; ri < roots.Length; ri++)
            {
                string root = roots[ri];
                bool ex = false;
                try { ex = Directory.Exists(root); }
                catch (Exception) { ex = false; }
                if (!ex) continue;
                exists = true;

                string[] dirs = null;
                try { dirs = Directory.GetDirectories(root); }
                catch (Exception) { dirs = null; }
                if (dirs == null) continue;
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);

                int i;
                for (i = 0; i < dirs.Length; i++)
                {
                    if (lib.Count >= MaxAnchors) break;
                    string nm = Path.GetFileName(dirs[i]);
                    if (nm == null || seen.Contains(nm)) continue;
                    Anchor a = LoadOne(dirs[i]);
                    if (a == null) continue;
                    lib.Add(a);
                    seen.Add(nm);
                }
            }

            _dirExists = exists;
            _cache = lib;
            _cacheDir = AnchorsDir;
            return lib;
        }

        /// <summary>
        /// 锚点根目录列表。
        /// 约定路径之外，再兼容引擎 `anchors add` 写到的 &lt;exe目录&gt;\anchors —— 否则「教一次」和「认出来」会读两个地方。
        /// AnchorsDir 被显式改成别的路径时只认它（自测/多库场景）。
        /// </summary>
        static string[] Roots()
        {
            if (AnchorsDir != null && AnchorsDir.Length > 0
                && !string.Equals(AnchorsDir, DefaultAnchorsDir, StringComparison.OrdinalIgnoreCase))
            {
                return new string[] { AnchorsDir };
            }

            List<string> r = new List<string>();
            if (AnchorsDir != null && AnchorsDir.Length > 0) r.Add(AnchorsDir);

            string exe = null;
            try
            {
                string bd = AppDomain.CurrentDomain.BaseDirectory;
                if (bd != null && bd.Length > 0) exe = Path.Combine(bd, "anchors");
            }
            catch (Exception) { exe = null; }

            if (exe != null && !string.Equals(exe, AnchorsDir, StringComparison.OrdinalIgnoreCase))
            {
                bool dup = false;
                int i;
                for (i = 0; i < r.Count; i++)
                {
                    if (string.Equals(r[i], exe, StringComparison.OrdinalIgnoreCase)) dup = true;
                }
                if (!dup) r.Add(exe);
            }
            return r.ToArray();
        }

        static Anchor LoadOne(string dir)
        {
            try
            {
                string mp = Path.Combine(dir, "meta.json");
                string pp = Path.Combine(dir, "patch.png");
                if (!File.Exists(mp) || !File.Exists(pp)) return null;

                string meta = File.ReadAllText(mp, Encoding.UTF8);
                string name = MetaStr(meta, "name");
                string kind = MetaStr(meta, "kind");

                int w = 0, h = 0;
                byte[] pix = null;
                using (Bitmap bmp = new Bitmap(pp))
                {
                    pix = ToGray(bmp, out w, out h);
                }
                if (pix == null || w < 4 || h < 4) return null;

                Anchor a = new Anchor();
                a.Name = (name != null && name.Length > 0) ? name : Path.GetFileName(dir);
                a.Kind = (kind != null && kind.Length > 0) ? kind : "icon";
                a.W = w;
                a.H = h;
                a.Pix = pix;
                long s = 0, s2 = 0;
                int i;
                for (i = 0; i < pix.Length; i++)
                {
                    s += pix[i];
                    s2 += (long)pix[i] * pix[i];
                }
                a.Sum = s;
                a.SumSq = s2;
                return a;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------------- 极简 meta.json 解析 ----------------
        // 只认 "key":"value" 和 "key":123 两种；不做嵌套 / 数组 / 注释。

        static bool IsWs(char c)
        {
            return c == ' ' || c == '\t' || c == '\r' || c == '\n';
        }

        static int IndexOfKey(string js, string key)
        {
            if (js == null || key == null) return -1;
            string pat = "\"" + key + "\"";
            int from = 0;
            while (true)
            {
                int i = js.IndexOf(pat, from, StringComparison.Ordinal);
                if (i < 0) return -1;
                int j = i + pat.Length;
                while (j < js.Length && IsWs(js[j])) j++;
                if (j < js.Length && js[j] == ':') return i;
                from = i + 1;
            }
        }

        static string MetaStr(string js, string key)
        {
            if (js == null) return null;
            int i = IndexOfKey(js, key);
            if (i < 0) return null;
            i += key.Length + 2;
            while (i < js.Length && IsWs(js[i])) i++;
            if (i >= js.Length || js[i] != ':') return null;
            i++;
            while (i < js.Length && IsWs(js[i])) i++;
            if (i >= js.Length) return null;

            if (js[i] == '"')
            {
                i++;
                StringBuilder sb = new StringBuilder();
                while (i < js.Length && js[i] != '"')
                {
                    char c = js[i];
                    if (c == '\\' && i + 1 < js.Length)
                    {
                        i++;
                        char e = js[i];
                        if (e == 'n') sb.Append('\n');
                        else if (e == 't') sb.Append('\t');
                        else if (e == 'r') sb.Append('\r');
                        else if (e == 'u' && i + 4 < js.Length)
                        {
                            int code;
                            if (int.TryParse(js.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                        }
                        else sb.Append(e);
                    }
                    else sb.Append(c);
                    i++;
                }
                return sb.ToString();
            }

            int st = i;
            while (i < js.Length)
            {
                char c = js[i];
                if (char.IsDigit(c) || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') i++;
                else break;
            }
            if (i <= st) return null;
            return js.Substring(st, i - st);
        }

        static int MetaInt(string js, string key)
        {
            string s = MetaStr(js, key);
            if (s == null) return -1;
            int v;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            double d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return (int)d;
            return -1;
        }

        // ---------------- 像素 ----------------

        static byte[] ToGray(Bitmap bmp, out int w, out int h)
        {
            w = bmp.Width;
            h = bmp.Height;
            if (w < 1 || h < 1) return null;

            byte[] gray = new byte[w * h];
            Bitmap work = null;
            try
            {
                PixelFormat pf = bmp.PixelFormat;
                if (pf != PixelFormat.Format24bppRgb
                    && pf != PixelFormat.Format32bppArgb
                    && pf != PixelFormat.Format32bppRgb
                    && pf != PixelFormat.Format32bppPArgb)
                {
                    work = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                    using (Graphics gr = Graphics.FromImage(work))
                    {
                        gr.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        gr.DrawImage(bmp, 0, 0, w, h);
                    }
                    bmp = work;
                    pf = PixelFormat.Format24bppRgb;
                }

                Rectangle r = new Rectangle(0, 0, w, h);
                BitmapData bd = bmp.LockBits(r, ImageLockMode.ReadOnly, pf);
                try
                {
                    unsafe
                    {
                        byte* base0 = (byte*)bd.Scan0.ToPointer();
                        int stride = bd.Stride;
                        bool bottomUp = stride < 0;
                        int sa = bottomUp ? -stride : stride;
                        int bpp = (pf == PixelFormat.Format24bppRgb) ? 3 : 4;
                        int y;
                        for (y = 0; y < h; y++)
                        {
                            byte* row = base0 + (long)(bottomUp ? (h - 1 - y) : y) * sa;
                            byte* px = row;
                            int o = y * w;
                            int x;
                            for (x = 0; x < w; x++)
                            {
                                int b = px[0];
                                int gg = px[1];
                                int rr = px[2];
                                gray[o + x] = (byte)((rr * 77 + gg * 151 + b * 28) >> 8);
                                px += bpp;
                            }
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }
            }
            finally
            {
                if (work != null) work.Dispose();
            }
            return gray;
        }

        static string Short(Exception ex)
        {
            string m = ex.GetType().Name;
            if (ex.Message != null && ex.Message.Length > 0) m = m + ": " + ex.Message;
            if (m.Length > 140) m = m.Substring(0, 140);
            return m;
        }
    }
}
