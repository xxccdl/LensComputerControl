// 实心色块 / 圆角按钮检测（按量化颜色做连通域，不是全局颜色包围盒）。
// 血泪教训：全局颜色包围盒会把"按钮的同色文字"一起圈进来，包围盒被拉成整屏、填充率不合格 → 按钮全漏。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace Lens.Det
{
    public static class SolidDetector
    {
        public static void Run(ScanContext ctx)
        {
            Bitmap b = ctx.Image;
            if (b == null) return;
            int w = b.Width, h = b.Height;
            if (w < 8 || h < 8) return;

            var q = new int[w * h];
            var bd = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            unsafe
            {
                byte* p0 = (byte*)bd.Scan0;
                for (int y = 0; y < h; y++)
                {
                    byte* row = p0 + y * bd.Stride;
                    int off = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int bb = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                        q[off + x] = ((r >> 3) << 10) | ((g >> 3) << 5) | (bb >> 3); // 5-5-5
                    }
                }
            }
            b.UnlockBits(bd);

            int minArea = Math.Max(300, (int)((double)w * h * 0.0006));
            int maxBBoxArea = (int)((double)w * h * 0.45);
            var visited = new bool[w * h];
            var stack = new Stack<int>();
            int produced = 0, comps = 0;

            for (int start = 0; start < q.Length; start++)
            {
                if (visited[start]) continue;
                int key = q[start];
                visited[start] = true;
                stack.Clear();
                stack.Push(start);

                int area = 0, minX = w, minY = h, maxX = -1, maxY = -1;
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    int x = i % w, y = i / w;
                    area++;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;

                    // 4 邻接同色扩散
                    if (x > 0) { int n = i - 1; if (!visited[n] && q[n] == key) { visited[n] = true; stack.Push(n); } }
                    if (x < w - 1) { int n = i + 1; if (!visited[n] && q[n] == key) { visited[n] = true; stack.Push(n); } }
                    if (y > 0) { int n = i - w; if (!visited[n] && q[n] == key) { visited[n] = true; stack.Push(n); } }
                    if (y < h - 1) { int n = i + w; if (!visited[n] && q[n] == key) { visited[n] = true; stack.Push(n); } }

                    if (area > maxBBoxArea) break;   // 背景太大，提前放弃
                }
                comps++;
                if (area < minArea) continue;

                int bw = maxX - minX + 1, bh = maxY - minY + 1;
                if (bw < 20 || bh < 12) continue;
                if (bh > h * 0.55 || bw > w * 0.95) continue;
                if (bw * bh > maxBBoxArea) continue;
                double fill = (double)area / ((double)bw * bh);
                if (fill < 0.85) continue;   // 实测真实应用里 fill 低的都是头像/图标噪声
                double aspect = (double)bw / bh;
                if (aspect < 1.6 || aspect > 6) continue;   // 按钮的典型长宽比，砍掉方形图标

                int qq = key;
                int r = ((qq >> 10) & 31) << 3, g = ((qq >> 5) & 31) << 3, bl = (qq & 31) << 3;
                int maxc = Math.Max(r, Math.Max(g, bl)), minc = Math.Min(r, Math.Min(g, bl));
                int sat = maxc - minc;
                int luma = (r * 299 + g * 587 + bl * 114) / 1000;

                double score = 0.5;
                score += Math.Min(0.25, fill * 0.25);
                if (sat > 40) score += 0.12;
                if (aspect >= 1.6 && aspect <= 8) score += 0.10;
                if (bh >= 24 && bh <= 90) score += 0.06;
                if (luma < 12 && sat < 20) score -= 0.15;

                // 低饱和度的规整块多半是表格行底纹/面板底色，不是按钮：降级成 block，避免淹没真正的按钮
                string kind = "button";
                if (sat < 30)
                {
                    kind = "block";
                    score -= 0.22;
                }
                if (score > 0.95) score = 0.95;
                if (score < 0.45) continue;

                var c = ctx.Add("solid", kind, new Rect(minX, minY, bw, bh), null,
                    Json.Hex(Color.FromArgb(r, g, bl)), score);
                if (c != null) produced++;
            }

            // 色块通道最容易刷屏：按分数裁到最多 45 个，保留真正像按钮的那批
            int solidCount = 0;
            foreach (Candidate c in ctx.Candidates) if (c.Channel == "solid") solidCount++;
            if (solidCount > 45)
            {
                var mine = new System.Collections.Generic.List<Candidate>();
                foreach (Candidate c in ctx.Candidates) if (c.Channel == "solid") mine.Add(c);
                mine.Sort(new Comparison<Candidate>(delegate(Candidate x, Candidate y) { return y.Score.CompareTo(x.Score); }));
                for (int i = 45; i < mine.Count; i++) ctx.Candidates.Remove(mine[i]);
                ctx.Notes.Add("solid: trimmed " + (solidCount - 45) + " low-score blocks");
            }

            ctx.Notes.Add("solid: components=" + comps + " candidates=" + produced);
        }
    }
}
