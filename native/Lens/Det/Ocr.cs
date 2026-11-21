// Lens/Det/Ocr.cs — Windows 自带 OCR（Windows.Media.Ocr）多通道文字检测，零第三方依赖。
//
// 设计要点（依据 _probe 实测）：
//   1) 原图通道：直接识别。Windows.Media.Ocr 对中文按"字"切词（「切换账号」= 切/换/账/号 四个 Word），
//      所以词级结果必须按基线合并成行框，否则点「登」还是点「登录」就无法区分。
//   2) 反色通道：整图 255-x 后再识别。深色皮肤 IM/行情软件里浅色文字靠它对冲极性偏好。
//      注：Win10/11 26100 这颗引擎实测对极性不敏感（反色结果与原图逐词一致），该通道保留作兜底，
//      代价约 40ms，重复结果会被去重吸收，不会污染候选。
//   3) 上采样通道：默认双线性/双三次放大对小字是灾难（实测「账号」被读成「呗长」），
//      必须用最近邻整数倍复制像素，2x 后「账号」正确。因此这里自己做 NN 像素复制，不走 GDI+ 插值。
//
// C# 5 语法（老 csc 不支持表达式体成员/字符串插值/?. /nameof/out var/模式匹配）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Lens.Det
{
    /// <summary>OCR 文字检测器。多通道识别 → 按文本+位置去重 → 按基线合并成可点击的行框。</summary>
    public static class OcrDetector
    {
        const string Chn = "ocr";

        /// <summary>总时间预算（ms）。超预算就跳过还没跑的可选通道。</summary>
        const int BudgetMs = 1200;

        /// <summary>原图通道词数低于此值才补跑反色通道。</summary>
        const int InvertBelowWords = 15;

        /// <summary>放大后长边上限，超过则不做上采样通道。</summary>
        const int MaxUpLongSide = 2600;

        /// <summary>去重空间网格边长（像素）。</summary>
        const int CellSize = 24;

        // 通道优先级：数值越大，其文本越优先被保留。
        // 实测 9~11px 中文字形上，2x 最近邻 > 原图 > 反色，所以 up2 排最前（只影响"留哪份文本"，不影响打分）。
        const int RankInvert = 1;
        const int RankOrig = 2;
        const int RankUp2 = 3;

        /// <summary>一个 OCR 词级命中（坐标已还原到原图像素）。</summary>
        sealed class Hit
        {
            public string Text;
            public Rect Box;
            public int Rank;
        }

        /// <summary>同一条基线上的一堆词。</summary>
        sealed class Row
        {
            public List<Hit> Hits = new List<Hit>();
            public int Cy;
            public int H;
        }

        static OcrEngine _engine;
        static int _engineState; // 0=未尝试 1=可用 2=不可用
        static readonly object _engineLock = new object();

        /// <summary>唯一入口。绝不抛异常：失败只写 Notes / Cap。</summary>
        public static void Run(ScanContext ctx)
        {
            if (ctx == null) return;
            Stopwatch sw = Stopwatch.StartNew();
            Bitmap img = ctx.Image;
            if (img == null || img.Width < 8 || img.Height < 8)
            {
                ctx.Cap.OcrUsable = false;
                ctx.Notes.Add("ocr: skipped (no usable image)");
                return;
            }

            int imgW = img.Width;
            int imgH = img.Height;
            bool usable = false;
            StringBuilder via = new StringBuilder("orig");
            List<Hit> hits = new List<Hit>();
            byte[] pixels = null;

            try
            {
                int maxDim = 10000;
                try
                {
                    uint md = OcrEngine.MaxImageDimension;
                    if (md > 0) maxDim = (int)md;
                }
                catch (Exception) { }

                if (Math.Max(imgW, imgH) > maxDim)
                {
                    ctx.Cap.OcrUsable = false;
                    ctx.Notes.Add("ocr: skipped (image " + imgW + "x" + imgH + " > MaxImageDimension " + maxDim + ")");
                    return;
                }

                OcrEngine engine = GetEngine();
                if (engine == null)
                {
                    ctx.Cap.OcrUsable = false;
                    ctx.Notes.Add("ocr: unavailable (no zh-Hans / profile-language OCR recognizer)");
                    return;
                }

                pixels = LoadPixels(img);

                // ---- 通道 1：原图 ----
                int origWords = Recognize(engine, pixels, imgW, imgH, 1.0, false, RankOrig, hits);
                usable = true;

                // ---- 通道 2：反色（原图几乎没读到字时才补，深色背景浅色文字） ----
                if (origWords < InvertBelowWords && sw.ElapsedMilliseconds < BudgetMs)
                {
                    try
                    {
                        Recognize(engine, pixels, imgW, imgH, 1.0, true, RankInvert, hits);
                        via.Append("+invert");
                    }
                    catch (Exception e)
                    {
                        ctx.Notes.Add("ocr: invert channel failed (" + Brief(e) + ")");
                    }
                }

                // ---- 通道 3：上采样 2x（金融软件 9~11px 小字） ----
                if (Math.Max(imgW, imgH) * 2 <= MaxUpLongSide && sw.ElapsedMilliseconds < BudgetMs)
                {
                    try
                    {
                        Recognize(engine, pixels, imgW, imgH, 2.0, false, RankUp2, hits);
                        via.Append("+up2");
                    }
                    catch (Exception e)
                    {
                        ctx.Notes.Add("ocr: up2 channel failed (" + Brief(e) + ")");
                    }
                }

                // ---- 去重 → 分行 → 合并成可点行框 ----
                List<Hit> kept = Dedupe(hits, imgW, imgH);
                int lines = Emit(ctx, kept, pixels, imgW, imgH);

                ctx.Cap.OcrUsable = true;
                ctx.Cap.OcrWords = kept.Count;
                ctx.Notes.Add("ocr: words=" + kept.Count + " lines=" + lines + " via=" + via.ToString()
                    + " elapsed=" + sw.ElapsedMilliseconds + "ms");
            }
            catch (Exception e)
            {
                ctx.Cap.OcrUsable = usable;
                ctx.Notes.Add("ocr: failed (" + Brief(e) + ") elapsed=" + sw.ElapsedMilliseconds + "ms");
            }
        }

        // ---------------------------------------------------------------- 引擎

        static OcrEngine GetEngine()
        {
            lock (_engineLock)
            {
                if (_engineState != 0) return _engine;
                try
                {
                    _engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans"));
                }
                catch (Exception)
                {
                    // 语言标签不被识别（缺语言包）等：咽掉，走 profile 兜底
                    _engine = null;
                }
                if (_engine == null)
                {
                    try { _engine = OcrEngine.TryCreateFromUserProfileLanguages(); }
                    catch (Exception) { _engine = null; }
                }
                _engineState = (_engine != null) ? 1 : 2;
                return _engine;
            }
        }

        static T Await<T>(Windows.Foundation.IAsyncOperation<T> op)
        {
            return System.Threading.Tasks.Task.Run(async () => await op.AsTask()).GetAwaiter().GetResult();
        }

        // ---------------------------------------------------------------- 通道

        /// <summary>跑一个通道，把词级命中追加进 hits，返回该通道词数。</summary>
        static int Recognize(OcrEngine engine, byte[] src, int w, int h, double scale, bool invert, int rank, List<Hit> hits)
        {
            int vw = w;
            int vh = h;
            byte[] buf = src;
            if (scale == 2.0)
            {
                vw = w * 2;
                vh = h * 2;
                buf = Upscale2(src, w, h);
            }
            if (invert) buf = Invert(buf);

            SoftwareBitmap sb = ToSoftwareBitmap(buf, vw, vh);
            try
            {
                OcrResult res;
                lock (_engineLock) { res = Await(engine.RecognizeAsync(sb)); }
                if (res == null) return 0;

                int n = 0;
                foreach (OcrLine line in res.Lines)
                {
                    foreach (OcrWord word in line.Words)
                    {
                        string t = word.Text;
                        if (t == null) continue;
                        t = t.Trim();
                        if (t.Length == 0) continue;

                        Windows.Foundation.Rect r = word.BoundingRect;
                        Hit hit = new Hit();
                        hit.Text = t;
                        hit.Rank = rank;
                        hit.Box = new Rect((int)Math.Round(r.X / scale), (int)Math.Round(r.Y / scale),
                            (int)Math.Round(r.Width / scale), (int)Math.Round(r.Height / scale));
                        hits.Add(hit);
                        n++;
                    }
                }
                return n;
            }
            finally
            {
                sb.Dispose();
            }
        }

        /// <summary>Bitmap → BGRA 像素（丢弃 alpha：PrintWindow 截图常给 alpha=0 但 RGB 有效）。</summary>
        static byte[] LoadPixels(Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            byte[] px = new byte[w * h * 4];
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                long baseAddr = bd.Scan0.ToInt64();
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(new IntPtr(baseAddr + (long)y * bd.Stride), px, y * w * 4, w * 4);
                }
            }
            finally
            {
                bmp.UnlockBits(bd);
            }
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            return px;
        }

        /// <summary>整数 2x 最近邻复制（不能交给 GDI+ 插值：小字会被插糊，实测错字率大幅上升）。</summary>
        static byte[] Upscale2(byte[] src, int w, int h)
        {
            int dw = w * 2;
            int dstride = dw * 4;
            byte[] dst = new byte[dstride * h * 2];
            for (int y = 0; y < h; y++)
            {
                int srow = y * w * 4;
                int d0 = (y * 2) * dstride;
                int d1 = d0 + dstride;
                for (int x = 0; x < w; x++)
                {
                    int s = srow + x * 4;
                    byte b = src[s], g = src[s + 1], r = src[s + 2];
                    int d = d0 + x * 8;
                    dst[d] = b; dst[d + 1] = g; dst[d + 2] = r; dst[d + 3] = 255;
                    dst[d + 4] = b; dst[d + 5] = g; dst[d + 6] = r; dst[d + 7] = 255;
                    int e = d1 + x * 8;
                    dst[e] = b; dst[e + 1] = g; dst[e + 2] = r; dst[e + 3] = 255;
                    dst[e + 4] = b; dst[e + 5] = g; dst[e + 6] = r; dst[e + 7] = 255;
                }
            }
            return dst;
        }

        /// <summary>
        /// 反色：255-x。
        /// 另外：低对比图（深色皮肤的行情/IM，浅色文字本身就压得很暗）纯反色后依旧糊，
        /// 实测 #4A4A4A 文字压在 #1E1E1E 底上，三个通道全是 0 词；按 1%/99% 分位线性拉伸再反色就能出字。
        /// 所以这里顺带做一次拉伸，但只在"本来就灰"的图上做（分位跨度 &lt; 160），
        /// 对比度正常的图仍然保持纯反色语义，行为与规格一致。
        /// </summary>
        static byte[] Invert(byte[] src)
        {
            int n = src.Length / 4;
            int[] hist = new int[256];
            for (int i = 0; i < src.Length; i += 4)
            {
                int lum = (src[i + 2] * 299 + src[i + 1] * 587 + src[i] * 114) / 1000;
                hist[lum]++;
            }

            int lo = Percentile(hist, n, 1);
            int hi = Percentile(hist, n, 99);
            int span = hi - lo;
            bool boost = (span > 8 && span < 160);
            double k = boost ? 255.0 / span : 1.0;

            byte[] lut = new byte[256];
            for (int v = 0; v < 256; v++)
            {
                int t = v;
                if (boost)
                {
                    t = (int)Math.Round((v - lo) * k);
                    if (t < 0) t = 0;
                    if (t > 255) t = 255;
                }
                lut[v] = (byte)(255 - t);
            }

            byte[] dst = new byte[src.Length];
            for (int i = 0; i < src.Length; i += 4)
            {
                dst[i] = lut[src[i]];
                dst[i + 1] = lut[src[i + 1]];
                dst[i + 2] = lut[src[i + 2]];
                dst[i + 3] = 255;
            }
            return dst;
        }

        static int Percentile(int[] hist, int n, int pct)
        {
            if (n <= 0) return 0;
            long target = (long)n * pct / 100;
            long acc = 0;
            for (int v = 0; v < 256; v++)
            {
                acc += hist[v];
                if (acc >= target) return v;
            }
            return 255;
        }

        static SoftwareBitmap ToSoftwareBitmap(byte[] bgra, int w, int h)
        {
            IBuffer buf = bgra.AsBuffer();
            return SoftwareBitmap.CreateCopyFromBuffer(buf, BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
        }

        // ---------------------------------------------------------------- 去重

        /// <summary>按"文本+位置"去重：位置重叠的词只留优先级最高的那份（同一字在多通道下只出一次）。</summary>
        static List<Hit> Dedupe(List<Hit> all, int imgW, int imgH)
        {
            List<Hit> sorted = new List<Hit>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                Hit h = all[i];
                Rect b = h.Box;
                if (b.X < 0) { b.W += b.X; b.X = 0; }
                if (b.Y < 0) { b.H += b.Y; b.Y = 0; }
                if (b.Right > imgW) b.W = imgW - b.X;
                if (b.Bottom > imgH) b.H = imgH - b.Y;
                if (b.W < 2 || b.H < 4) continue;
                h.Box = b;
                sorted.Add(h);
            }

            sorted.Sort(delegate(Hit a, Hit b)
            {
                if (a.Rank != b.Rank) return b.Rank - a.Rank;
                if (a.Box.Y != b.Box.Y) return a.Box.Y - b.Box.Y;
                return a.Box.X - b.Box.X;
            });

            Dictionary<long, List<Hit>> grid = new Dictionary<long, List<Hit>>();
            List<Hit> kept = new List<Hit>();
            for (int i = 0; i < sorted.Count; i++)
            {
                Hit h = sorted[i];
                if (GridOverlaps(grid, h)) continue;
                kept.Add(h);
                GridInsert(grid, h);
            }
            return kept;
        }

        static long CellKey(int cx, int cy)
        {
            return ((long)cy << 32) | (uint)cx;
        }

        static void GridInsert(Dictionary<long, List<Hit>> grid, Hit h)
        {
            long k = CellKey(h.Box.Cx / CellSize, h.Box.Cy / CellSize);
            List<Hit> list;
            if (!grid.TryGetValue(k, out list))
            {
                list = new List<Hit>();
                grid[k] = list;
            }
            list.Add(h);
        }

        static bool GridOverlaps(Dictionary<long, List<Hit>> grid, Hit h)
        {
            int cx = h.Box.Cx / CellSize;
            int cy = h.Box.Cy / CellSize;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    List<Hit> list;
                    if (!grid.TryGetValue(CellKey(cx + dx, cy + dy), out list)) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (SameGlyph(list[i].Box, h.Box)) return true;
                    }
                }
            }
            return false;
        }

        /// <summary>两个词框是否指向同一处文字。按较小框算重叠率，容忍通道间 ±几像素漂移。</summary>
        static bool SameGlyph(Rect a, Rect b)
        {
            Rect it = a.Intersect(b);
            if (!it.IsEmpty)
            {
                int minArea = Math.Min(a.Area, b.Area);
                if (minArea > 0 && (double)it.Area >= 0.45 * minArea) return true;
            }
            int dx = Math.Abs(a.Cx - b.Cx);
            int dy = Math.Abs(a.Cy - b.Cy);
            int tolX = Math.Max(3, Math.Min(a.W, b.W) / 2);
            int tolY = Math.Max(3, Math.Min(a.H, b.H) / 2);
            return dx <= tolX && dy <= tolY;
        }

        // ---------------------------------------------------------------- 合并成行

        /// <summary>按基线聚类成行（垂直重叠 ≥ 50% 视为同一行）。</summary>
        static List<Row> BuildRows(List<Hit> hits)
        {
            List<Hit> sorted = new List<Hit>(hits);
            sorted.Sort(delegate(Hit a, Hit b)
            {
                if (a.Box.Cy != b.Box.Cy) return a.Box.Cy - b.Box.Cy;
                return a.Box.X - b.Box.X;
            });

            List<Row> rows = new List<Row>();
            for (int i = 0; i < sorted.Count; i++)
            {
                Hit h = sorted[i];
                int best = -1;
                int bestDy = int.MaxValue;
                for (int r = rows.Count - 1; r >= 0 && r >= rows.Count - 8; r--)
                {
                    Row row = rows[r];
                    int top = row.Cy - row.H / 2;
                    int bot = row.Cy + row.H / 2;
                    int ov = Math.Min(bot, h.Box.Bottom) - Math.Max(top, h.Box.Y);
                    int minH = Math.Min(row.H, h.Box.H);
                    if (ov * 2 < minH) continue;
                    int dy = Math.Abs(row.Cy - h.Box.Cy);
                    if (dy < bestDy) { bestDy = dy; best = r; }
                }

                if (best < 0)
                {
                    Row row = new Row();
                    row.Hits.Add(h);
                    row.Cy = h.Box.Cy;
                    row.H = h.Box.H;
                    rows.Add(row);
                }
                else
                {
                    Row row = rows[best];
                    int top = Math.Min(row.Cy - row.H / 2, h.Box.Y);
                    int bot = Math.Max(row.Cy + row.H / 2, h.Box.Bottom);
                    row.Hits.Add(h);
                    row.Cy = (top + bot) / 2;
                    row.H = bot - top;
                }
            }
            return rows;
        }

        /// <summary>行内按"字间距"再切一刀：间距 ≤ 1 个字高算同一段，超过就是两个可点目标（如「切换账号」|「仅传输文件」）。</summary>
        static int Emit(ScanContext ctx, List<Hit> kept, byte[] pixels, int imgW, int imgH)
        {
            List<Row> rows = BuildRows(kept);
            int emitted = 0;

            for (int r = 0; r < rows.Count; r++)
            {
                List<Hit> line = rows[r].Hits;
                line.Sort(delegate(Hit a, Hit b) { return a.Box.X - b.Box.X; });

                int rowH = 0;
                for (int k = 0; k < line.Count; k++) if (line[k].Box.H > rowH) rowH = line[k].Box.H;
                int gapMax = Math.Max(6, (int)(rowH * 1.05));

                int i = 0;
                while (i < line.Count)
                {
                    Rect box = line[i].Box;
                    int j = i + 1;
                    while (j < line.Count && line[j].Box.X - box.Right <= gapMax)
                    {
                        box = Union(box, line[j].Box);
                        j++;
                    }

                    StringBuilder sb = new StringBuilder();
                    bool nonOrig = false;
                    for (int k = i; k < j; k++)
                    {
                        if (k > i)
                        {
                            int gap = line[k].Box.X - line[k - 1].Box.Right;
                            if (gap * 2 >= rowH) sb.Append(' ');
                        }
                        sb.Append(line[k].Text);
                        if (line[k].Rank != RankOrig) nonOrig = true;
                    }
                    string text = sb.ToString().Trim();

                    if (text.Length > 0 && !box.IsEmpty)
                    {
                        // 单独成行的词（往往是被识别碎了的孤立字符）给低分，多词合并的行框给高分；
                        // 非原图通道来的整体 -0.05。
                        bool merged = (j - i) > 1;
                        double score = merged ? 0.80 : 0.62;
                        if (text.Length >= 6) score += 0.04;
                        if (nonOrig) score -= 0.05;
                        if (score < 0.5) score = 0.5;
                        if (score > 0.9) score = 0.9;

                        string color = SampleTextColor(pixels, imgW, imgH, box);
                        ctx.Add(Chn, "text", box, text, color, score);
                        emitted++;
                    }
                    i = j;
                }
            }
            return emitted;
        }

        static Rect Union(Rect a, Rect b)
        {
            int x1 = Math.Min(a.X, b.X);
            int y1 = Math.Min(a.Y, b.Y);
            int x2 = Math.Max(a.Right, b.Right);
            int y2 = Math.Max(a.Bottom, b.Bottom);
            return new Rect(x1, y1, x2 - x1, y2 - y1);
        }

        /// <summary>
        /// 取框内"离平均亮度最远的显眼色调"当文字色（深底取最亮、浅底取最暗）。
        /// 按亮度直方图分箱再对箱内 RGB 求均值，避免一两个 ClearType 彩边像素把颜色带偏。
        /// </summary>
        static string SampleTextColor(byte[] px, int w, int h, Rect box)
        {
            try
            {
                if (px == null) return null;
                int x1 = Math.Max(0, box.X);
                int y1 = Math.Max(0, box.Y);
                int x2 = Math.Min(w, box.Right);
                int y2 = Math.Min(h, box.Bottom);
                if (x2 <= x1 || y2 <= y1) return null;

                int[] cnt = new int[256];
                int[] sr = new int[256];
                int[] sg = new int[256];
                int[] sb = new int[256];
                long sumLum = 0;
                int n = 0;
                for (int y = y1; y < y2; y++)
                {
                    int o = (y * w + x1) * 4;
                    for (int x = x1; x < x2; x++, o += 4)
                    {
                        int lum = (px[o + 2] * 299 + px[o + 1] * 587 + px[o] * 114) / 1000;
                        cnt[lum]++;
                        sr[lum] += px[o + 2];
                        sg[lum] += px[o + 1];
                        sb[lum] += px[o];
                        sumLum += lum;
                        n++;
                    }
                }
                if (n == 0) return null;

                int mean = (int)(sumLum / n);
                int need = n / 50;
                if (need < 2) need = 2;

                int bestBin = -1;
                int bestD = -1;
                for (int v = 0; v < 256; v++)
                {
                    if (cnt[v] < need) continue;
                    int d = Math.Abs(v - mean);
                    if (d > bestD) { bestD = d; bestBin = v; }
                }
                if (bestBin < 0) return null;

                int c = cnt[bestBin];
                int r = sr[bestBin] / c;
                int g = sg[bestBin] / c;
                int b = sb[bestBin] / c;
                return "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string Brief(Exception e)
        {
            return e.GetType().Name + ": " + e.Message;
        }
    }
}
