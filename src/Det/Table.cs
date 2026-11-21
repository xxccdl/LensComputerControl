// Lens.Det.TableDetector —— 表格线投影检测器。
// 思路：灰度 → 每行/每列的边缘强度投影 → 自适应阈值（中位数 + k*MAD）+ 贯穿比例过滤
//       → 相邻候选合并成线 → 线交叉出单元格 / 行 / 列。
// 约束：C# 5 语法（老 csc），只依赖 System.Drawing，像素全部走 LockBits + unsafe。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace Lens.Det
{
    public static class TableDetector
    {
        // ---------------- 可调参数 ----------------
        const int EdgeThr = 20;         // 单像素灰度差达到多少算「边缘」
        const double CoverMin = 0.60;   // 表格线的贯穿比例下限（该行 60% 宽度都是边缘）
        const double MadK = 3.0;        // 自适应阈值 = 中位数 + k*MAD
        const int MergeGap = 2;         // 相邻候选线间距 <= 2px 视为同一条
        const int MinLineGap = 6;       // 合并后仍太近的两条线（粗线两侧）再并一次
        const int CellMax = 400;        // cell 候选上限
        const int CellMinW = 20;        // 单元格最小宽
        const int CellMinH = 12;        // 单元格最小高
        const int GridMin = 2;          // 行数/列数下限
        const int GridMax = 200;        // 行数/列数上限

        // ---------------- 入口 ----------------

        /// <summary>扫描并产出候选。异常一律吞掉，只留 Note。</summary>
        public static void Run(ScanContext ctx)
        {
            if (ctx == null) return;
            if (ctx.Image == null) return;
            try
            {
                Analysis a = Analyze(ctx.Image);
                if (a == null || !a.IsTable) return;

                Rect table = a.TableBox;
                int rows = a.RowBands.Count;
                int cols = a.ColBands.Count;

                // 1) 行候选：每条水平分隔线之上的区域（row:3 便于模型做语义定位）
                int i;
                for (i = 0; i < rows; i++)
                {
                    Band b = a.RowBands[i];
                    Line top = a.HLines[b.Index];
                    Line bot = a.HLines[b.Index + 1];
                    double cov = (top.Cover(table.X, table.Right - 1) + bot.Cover(table.X, table.Right - 1)) * 0.5;
                    double score = 0.45 + 0.30 * cov;
                    ctx.Add("table", "row", new Rect(table.X, b.A, table.W, b.B - b.A), "row:" + (i + 1), null, Clamp(score, 0.4, 0.75));
                }

                // 2) 列候选：每条竖线之间的区域
                int j;
                for (j = 0; j < cols; j++)
                {
                    Band b = a.ColBands[j];
                    Line lf = a.VLines[b.Index];
                    Line rt = a.VLines[b.Index + 1];
                    double cov = (lf.Cover(table.Y, table.Bottom - 1) + rt.Cover(table.Y, table.Bottom - 1)) * 0.5;
                    double score = 0.45 + 0.30 * cov;
                    ctx.Add("table", "col", new Rect(b.A, table.Y, b.B - b.A, table.H), "col:" + (j + 1), null, Clamp(score, 0.4, 0.75));
                }

                // 3) 单元格候选：面积排序，最多 CellMax 个
                List<CellHit> cells = new List<CellHit>();
                for (i = 0; i < rows; i++)
                {
                    Band rb = a.RowBands[i];
                    for (j = 0; j < cols; j++)
                    {
                        Band cb = a.ColBands[j];
                        CellHit h = new CellHit();
                        h.Box = new Rect(cb.A, rb.A, cb.B - cb.A, rb.B - rb.A);
                        h.Score = a.CellScore(rb.Index, cb.Index, h.Box);
                        cells.Add(h);
                    }
                }

                int produced = cells.Count;
                if (produced > CellMax)
                {
                    cells.Sort(CellAreaDesc);
                    cells.RemoveRange(CellMax, produced - CellMax);
                    ctx.Notes.Add("table: cells truncated from " + produced + " to " + CellMax);
                }

                int k;
                for (k = 0; k < cells.Count; k++)
                {
                    ctx.Add("table", "cell", cells[k].Box, null, null, Clamp(cells[k].Score, 0.4, 0.75));
                }

                // 4) 能力护照 + 摘要
                ctx.Cap.TableDetected = true;
                ctx.Notes.Add("table: rows=" + rows + " cols=" + cols + " cells=" + cells.Count
                    + " lines_h=" + a.HLines.Count + " lines_v=" + a.VLines.Count);
            }
            catch (Exception ex)
            {
                try { ctx.Notes.Add("table: error " + Short(ex)); }
                catch (Exception) { }
            }
        }

        /// <summary>能力探测用：只判断「像不像表格」，不写 Candidates / Notes / Cap，无副作用。</summary>
        public static bool Detected(ScanContext ctx)
        {
            if (ctx == null) return false;
            if (ctx.Image == null) return false;
            try
            {
                Analysis a = Analyze(ctx.Image);
                return a != null && a.IsTable;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---------------- 分析核心 ----------------

        class Line
        {
            public int Pos;     // 线在图像上的坐标（行线用 y，列线用 x）
            public int[] Pre;   // 该线上「边缘像素」的累积计数，长度 = 扫描方向 + 1

            public double Cover(int lo, int hi)
            {
                if (Pre == null) return 0;
                if (lo < 0) lo = 0;
                if (hi > Pre.Length - 2) hi = Pre.Length - 2;
                if (hi < lo) return 0;
                return (double)(Pre[hi + 1] - Pre[lo]) / (double)(hi - lo + 1);
            }
        }

        class Band
        {
            public int A, B;    // 区间的起止坐标（含 A，不含 B）
            public int Index;   // 该区间左右/上下两条线在 Lines 里的下标
        }

        class CellHit
        {
            public Rect Box;
            public double Score;
        }

        class Analysis
        {
            public int W, H;
            public List<Line> HLines = new List<Line>();
            public List<Line> VLines = new List<Line>();
            public List<Band> RowBands = new List<Band>();
            public List<Band> ColBands = new List<Band>();

            public Rect TableBox
            {
                get
                {
                    if (RowBands.Count == 0 || ColBands.Count == 0) return new Rect(0, 0, 0, 0);
                    int x1 = ColBands[0].A;
                    int x2 = ColBands[ColBands.Count - 1].B;
                    int y1 = RowBands[0].A;
                    int y2 = RowBands[RowBands.Count - 1].B;
                    return new Rect(x1, y1, x2 - x1, y2 - y1);
                }
            }

            public bool IsTable
            {
                get
                {
                    int r = RowBands.Count;
                    int c = ColBands.Count;
                    return r >= GridMin && r <= GridMax && c >= GridMin && c <= GridMax;
                }
            }

            /// <summary>单元格分数 0.4~0.75：四条边实际有多少像素是真边缘。</summary>
            public double CellScore(int rowIdx, int colIdx, Rect box)
            {
                double cov = (HLines[rowIdx].Cover(box.X, box.Right - 1)
                            + HLines[rowIdx + 1].Cover(box.X, box.Right - 1)
                            + VLines[colIdx].Cover(box.Y, box.Bottom - 1)
                            + VLines[colIdx + 1].Cover(box.Y, box.Bottom - 1)) * 0.25;
                return 0.4 + 0.35 * cov;
            }
        }

        static Analysis Analyze(Bitmap bmp)
        {
            int w = 0, h = 0;
            byte[] g = ToGray(bmp, out w, out h);
            if (g == null || w < 16 || h < 16) return null;

            Analysis a = new Analysis();
            a.W = w;
            a.H = h;

            // ---- 水平投影：每行的边缘强度 & 边缘像素数 ----
            int[] projH = new int[h];
            int[] covH = new int[h];
            int y;
            for (y = 1; y < h; y++)
            {
                int o = y * w;
                int p = o - w;
                int s = 0, c = 0, x;
                for (x = 0; x < w; x++)
                {
                    int d = g[o + x] - g[p + x];
                    if (d < 0) d = -d;
                    s += d;
                    if (d >= EdgeThr) c++;
                }
                projH[y] = s;
                covH[y] = c;
            }

            double thrH = AdaptThr(projH, 1, h - 1);
            // 兜底：一条真正贯穿 60% 宽度的线，其强度和不会低于 EdgeThr*0.6*w；
            // 文字密集的行情表会让中位数/MAD 被抬到线上，必须把这个上限压下来，否则线全被误杀。
            if (thrH > EdgeThr * CoverMin * w) thrH = EdgeThr * CoverMin * w;
            List<int> rawH = Pick(projH, covH, 1, h - 1, thrH, w);
            List<int> posH = Coalesce(Coalesce(rawH, projH, MergeGap), projH, MinLineGap);
            posH = KeepMain(posH);
            if (posH.Count < GridMin + 1) return a;

            a.HLines = FilterBySpan(BuildLines(posH, g, w, h, true));
            if (a.HLines.Count < GridMin + 1) return a;

            // 表格的左右边界 = 横线跨度的中位数（后面补虚拟竖线要用）
            int tableX1, tableX2;
            MedianSpan(a.HLines, out tableX1, out tableX2);

            int bandTop = a.HLines[0].Pos;
            int bandBot = a.HLines[a.HLines.Count - 1].Pos;
            if (bandBot - bandTop < CellMinH) return a;

            BuildRowBands(a);

            // ---- 垂直投影：只统计表格的纵向范围，避免被标题栏/工具栏干扰 ----
            int[] projV = new int[w];
            int[] covV = new int[w];
            int span = bandBot - bandTop + 1;
            int x2;
            for (x2 = 1; x2 < w; x2++)
            {
                int s = 0, c = 0;
                for (y = bandTop; y <= bandBot; y++)
                {
                    int o = y * w + x2;
                    int d = g[o] - g[o - 1];
                    if (d < 0) d = -d;
                    s += d;
                    if (d >= EdgeThr) c++;
                }
                projV[x2] = s;
                covV[x2] = c;
            }

            double thrV = AdaptThr(projV, 1, w - 1);
            if (thrV > EdgeThr * CoverMin * span) thrV = EdgeThr * CoverMin * span;
            List<int> rawV = Pick(projV, covV, 1, w - 1, thrV, span);
            List<int> posV = Coalesce(Coalesce(rawV, projV, MergeGap), projV, MinLineGap);
            posV = KeepMain(posV);
            if (posV.Count < GridMin + 1) return a;

            // ---- 建线（带边缘累积数组，供打分用）----
            a.VLines = FilterBySpan(BuildLines(posV, g, w, h, false));
            if (a.VLines.Count < GridMin + 1) return a;

            // 对齐的文字笔画会在同一个 x 上堆出一条「假竖线」，按「每一行都要过」再刷一遍
            a.VLines = FilterByRowPresence(a.VLines, a.RowBands);
            if (a.VLines.Count < GridMin + 1) return a;

            // ---- 补虚拟外框 ----
            // 行情软件的自绘表常常只画内部线、不画最外框；不补的话最左/最右一列、最上/最下一行会被整段丢掉。
            int tableY1, tableY2;
            MedianSpan(a.VLines, out tableY1, out tableY2);

            if (tableY1 >= 0)
            {
                if (a.HLines[0].Pos - tableY1 >= CellMinH) InsertVirtual(a.HLines, tableY1, g, w, h, true);
                if (tableY2 - a.HLines[a.HLines.Count - 1].Pos >= CellMinH) InsertVirtual(a.HLines, tableY2, g, w, h, true);
                BuildRowBands(a);
            }
            if (tableX1 >= 0)
            {
                if (a.VLines[0].Pos - tableX1 >= CellMinW) InsertVirtual(a.VLines, tableX1, g, w, h, false);
                if (tableX2 - a.VLines[a.VLines.Count - 1].Pos >= CellMinW) InsertVirtual(a.VLines, tableX2, g, w, h, false);
            }

            // ---- 切列带 ----
            BuildColBands(a);
            return a;
        }

        static void BuildRowBands(Analysis a)
        {
            a.RowBands.Clear();
            int i;
            for (i = 0; i + 1 < a.HLines.Count; i++)
            {
                int y1 = a.HLines[i].Pos;
                int y2 = a.HLines[i + 1].Pos;
                if (y2 - y1 < CellMinH) continue;
                Band b = new Band();
                b.A = y1;
                b.B = y2;
                b.Index = i;
                a.RowBands.Add(b);
            }
        }

        static void BuildColBands(Analysis a)
        {
            a.ColBands.Clear();
            int i;
            for (i = 0; i + 1 < a.VLines.Count; i++)
            {
                int x1 = a.VLines[i].Pos;
                int x3 = a.VLines[i + 1].Pos;
                if (x3 - x1 < CellMinW) continue;
                Band b = new Band();
                b.A = x1;
                b.B = x3;
                b.Index = i;
                a.ColBands.Add(b);
            }
        }

        /// <summary>
        /// 真竖线必须「穿过每一行」。对齐的字符笔画会在同一 x 上堆出一条边缘很多的假竖线，
        /// 但它在每个行带里只断断续续出现，按行带存在率就能刷掉。
        /// </summary>
        static List<Line> FilterByRowPresence(List<Line> vlines, List<Band> rows)
        {
            if (rows.Count < 3) return vlines;
            List<Line> keep = new List<Line>();
            int i;
            for (i = 0; i < vlines.Count; i++)
            {
                Line ln = vlines[i];
                int ok = 0;
                int j;
                for (j = 0; j < rows.Count; j++)
                {
                    Band b = rows[j];
                    int hgt = b.B - b.A;
                    if (hgt < 1) continue;
                    int cnt = ln.Pre[b.B + 1] - ln.Pre[b.A];
                    if (cnt * 10 >= hgt * 8) ok++;
                }
                if (ok * 10 >= rows.Count * 8) keep.Add(ln);
            }
            if (keep.Count < GridMin + 1) return vlines;   // 刷得太狠说明前提不成立，保守回退
            return keep;
        }

        static Line MakeLine(int pos, byte[] g, int w, int h, bool horizontal)
        {
            Line ln = new Line();
            ln.Pos = pos;
            if (horizontal)
            {
                int[] pre = new int[w + 1];
                if (pos >= 1 && pos < h)
                {
                    int o = pos * w;
                    int q = o - w;
                    int x;
                    for (x = 0; x < w; x++)
                    {
                        int d = g[o + x] - g[q + x];
                        if (d < 0) d = -d;
                        pre[x + 1] = pre[x] + (d >= EdgeThr ? 1 : 0);
                    }
                }
                ln.Pre = pre;
            }
            else
            {
                int[] pre = new int[h + 1];
                if (pos >= 1 && pos < w)
                {
                    int y;
                    for (y = 0; y < h; y++)
                    {
                        int o = y * w + pos;
                        int d = g[o] - g[o - 1];
                        if (d < 0) d = -d;
                        pre[y + 1] = pre[y] + (d >= EdgeThr ? 1 : 0);
                    }
                }
                ln.Pre = pre;
            }
            return ln;
        }

        static List<Line> BuildLines(List<int> pos, byte[] g, int w, int h, bool horizontal)
        {
            List<Line> outl = new List<Line>(pos.Count);
            int i;
            for (i = 0; i < pos.Count; i++)
            {
                outl.Add(MakeLine(pos[i], g, w, h, horizontal));
            }
            return outl;
        }

        /// <summary>一组线跨度的中位数（横向线给出表格左右边界，竖向线给出表格上下边界）。</summary>
        static void MedianSpan(List<Line> lines, out int lo, out int hi)
        {
            lo = -1;
            hi = -1;
            int n = lines.Count;
            if (n == 0) return;
            int[] s = new int[n];
            int[] e = new int[n];
            int i;
            for (i = 0; i < n; i++)
            {
                int a, b;
                LineSpan(lines[i], out a, out b);
                s[i] = a;
                e[i] = b;
            }
            Array.Sort(s);
            Array.Sort(e);
            lo = s[n / 2];
            hi = e[n / 2];
        }

        static int ByPos(Line a, Line b)
        {
            if (a.Pos != b.Pos) return a.Pos - b.Pos;
            return 0;
        }

        /// <summary>
        /// 补一条「虚拟边界线」。行情软件的自绘表经常只画内部横线、不画最外框，
        /// 没有外框时最左/最右一列（或最上/最下一行）会被整段丢掉，这里用另一方向的真实跨度补齐。
        /// </summary>
        static void InsertVirtual(List<Line> lines, int pos, byte[] g, int w, int h, bool horizontal)
        {
            lines.Add(MakeLine(pos, g, w, h, horizontal));
            lines.Sort(ByPos);
        }

        // ---------------- 阈值 / 选线 / 合并 ----------------

        /// <summary>
        /// 真表格的所有线起止点应该一致；标题栏/工具栏那条「通栏横线」跨度对不上，用最大同跨度簇把它挤掉。
        /// 同时顺带压掉只覆盖一小段的分隔线。
        /// </summary>
        static List<Line> FilterBySpan(List<Line> lines)
        {
            int n = lines.Count;
            if (n < 3) return lines;

            const int Tol = 12;
            int[] st = new int[n];
            int[] en = new int[n];
            int i;
            for (i = 0; i < n; i++)
            {
                LineSpan(lines[i], out st[i], out en[i]);
            }

            int bestCount = 0, bestIdx = 0;
            for (i = 0; i < n; i++)
            {
                if (st[i] < 0) continue;
                int c = 0;
                int j;
                for (j = 0; j < n; j++)
                {
                    if (st[j] < 0) continue;
                    if (Math.Abs(st[i] - st[j]) <= Tol && Math.Abs(en[i] - en[j]) <= Tol) c++;
                }
                if (c > bestCount)
                {
                    bestCount = c;
                    bestIdx = i;
                }
            }
            if (bestCount <= 0 || bestCount >= n) return lines;

            List<Line> r = new List<Line>(bestCount);
            for (i = 0; i < n; i++)
            {
                if (st[i] < 0) continue;
                if (Math.Abs(st[i] - st[bestIdx]) <= Tol && Math.Abs(en[i] - en[bestIdx]) <= Tol) r.Add(lines[i]);
            }
            if (r.Count < 3) return lines;
            return r;
        }

        /// <summary>
        /// 一条线上「最长一段边缘」的起止位置。
        /// 细节：水平线每穿过一条竖线，交叉点上下同色，边缘会断 1~2 像素，所以要把 &lt;=4px 的小缺口连起来；
        /// 而文字笔画造成的远端孤立像素（离主线 &gt;4px）则不会被算进去。
        /// </summary>
        static void LineSpan(Line ln, out int s, out int e)
        {
            s = -1;
            e = -1;
            int[] pre = ln.Pre;
            if (pre == null) return;
            int last = pre.Length - 2;
            const int GapTol = 4;
            int bestLen = 0;
            int runS = -1;
            int runE = -1;
            int k;
            for (k = 0; k <= last; k++)
            {
                if (pre[k + 1] > pre[k])
                {
                    if (runS < 0)
                    {
                        runS = k;
                        runE = k;
                    }
                    else if (k - runE - 1 <= GapTol)
                    {
                        runE = k;
                    }
                    else
                    {
                        if (runE - runS + 1 > bestLen)
                        {
                            bestLen = runE - runS + 1;
                            s = runS;
                            e = runE;
                        }
                        runS = k;
                        runE = k;
                    }
                }
            }
            if (runS >= 0 && runE - runS + 1 > bestLen)
            {
                s = runS;
                e = runE;
            }
        }

        /// <summary>自适应阈值：中位数 + MadK*MAD（MAD 是中位绝对偏差）。</summary>
        static double AdaptThr(int[] proj, int lo, int hi)
        {
            int n = hi - lo + 1;
            if (n <= 0) return 1;

            int[] tmp = new int[n];
            Array.Copy(proj, lo, tmp, 0, n);
            Array.Sort(tmp);
            double med = tmp[n / 2];

            int[] dev = new int[n];
            int i;
            for (i = 0; i < n; i++)
            {
                double d = proj[lo + i] - med;
                if (d < 0) d = -d;
                dev[i] = (int)d;
            }
            Array.Sort(dev);
            double mad = dev[n / 2];

            double thr = med + MadK * mad;
            if (thr < med * 1.5) thr = med * 1.5;   // MAD 极小时别退化成中位数
            if (thr < 1) thr = 1;
            return thr;
        }

        /// <summary>候选线：贯穿比例够高 且 投影强度过阈值。</summary>
        static List<int> Pick(int[] proj, int[] cov, int lo, int hi, double thr, int denom)
        {
            List<int> r = new List<int>();
            double need = denom * CoverMin;
            int i;
            for (i = lo; i <= hi; i++)
            {
                if (cov[i] >= need && proj[i] >= thr) r.Add(i);
            }
            return r;
        }

        /// <summary>相邻候选线合并（间距 &lt;= gap），代表位置取组内投影最强的那个。</summary>
        static List<int> Coalesce(List<int> raw, int[] weight, int gap)
        {
            List<int> r = new List<int>();
            int i = 0;
            while (i < raw.Count)
            {
                int first = raw[i];
                int best = first;
                int bw = weight[best];
                int j = i;
                while (j + 1 < raw.Count && raw[j + 1] - first <= gap)
                {
                    j++;
                    if (weight[raw[j]] > bw)
                    {
                        bw = weight[raw[j]];
                        best = raw[j];
                    }
                }
                r.Add(best);
                i = j + 1;
            }
            return r;
        }

        /// <summary>线集里可能混进远处的孤立线（标题下划线等）：按间距切段，只留最大的那一段。</summary>
        static List<int> KeepMain(List<int> pos)
        {
            if (pos.Count < 4) return pos;

            int[] gaps = new int[pos.Count - 1];
            int i;
            for (i = 0; i < gaps.Length; i++) gaps[i] = pos[i + 1] - pos[i];
            int[] sorted = (int[])gaps.Clone();
            Array.Sort(sorted);
            int medGap = sorted[sorted.Length / 2];
            if (medGap < 1) medGap = 1;
            int maxGap = medGap * 6;
            if (maxGap < 60) maxGap = 60;

            int bestFrom = 0, bestTo = 0, curFrom = 0;
            for (i = 0; i < gaps.Length; i++)
            {
                if (gaps[i] > maxGap)
                {
                    if (i - curFrom > bestTo - bestFrom)
                    {
                        bestFrom = curFrom;
                        bestTo = i;
                    }
                    curFrom = i + 1;
                }
            }
            int last = gaps.Length;
            if (last - curFrom > bestTo - bestFrom)
            {
                bestFrom = curFrom;
                bestTo = last;
            }
            if (bestFrom == 0 && bestTo == gaps.Length) return pos;

            List<int> r = new List<int>(bestTo - bestFrom + 1);
            for (i = bestFrom; i <= bestTo; i++) r.Add(pos[i]);
            return r;
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

        // ---------------- 小工具 ----------------

        static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        static int CellAreaDesc(CellHit x, CellHit y)
        {
            int ax = x.Box.Area;
            int ay = y.Box.Area;
            if (ax != ay) return ay - ax;
            return x.Score > y.Score ? -1 : (x.Score < y.Score ? 1 : 0);
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
