// 文字网格推断：不依赖画出来的表格线，直接用 OCR 词的位置聚类出行/列。
// 为什么需要它：券商行情软件的暗色皮肤里，行分隔线与底色的对比往往只有几个色阶，
// 投影法会漏；但文字位置永远清晰。两条通道互补。
using System;
using System.Collections.Generic;

namespace Lens.Det
{
    public static class TextGridDetector
    {
        public static void Run(ScanContext ctx)
        {
            // 取 OCR 通道产出的词框（Scanner 里 ocr 在本检测器之前运行）
            var words = new List<Candidate>();
            foreach (Candidate c in ctx.Candidates)
            {
                if (c.Channel == "ocr" && c.Text != null && c.Text.Trim().Length > 0 && c.Box.H >= 6 && c.Box.W >= 4)
                    words.Add(c);
            }
            if (words.Count < 30)
            {
                ctx.Notes.Add("textgrid: skipped (words=" + words.Count + ")");
                return;
            }

            // 估字高
            var hs = new List<int>();
            foreach (Candidate w in words) hs.Add(w.Box.H);
            hs.Sort();
            int lineH = hs[hs.Count / 2];
            if (lineH < 6) lineH = 10;
            double rowGap = lineH * 0.75;

            // ---- 按 y 聚成行 ----
            var byY = new List<Candidate>(words);
            byY.Sort(new Comparison<Candidate>(delegate(Candidate a, Candidate b) { return a.Box.Cy.CompareTo(b.Box.Cy); }));
            var rows = new List<List<Candidate>>();
            var cur = new List<Candidate>();
            int curCy = byY[0].Box.Cy;
            foreach (Candidate w in byY)
            {
                if (Math.Abs(w.Box.Cy - curCy) > rowGap && cur.Count > 0)
                {
                    rows.Add(cur);
                    cur = new List<Candidate>();
                }
                cur.Add(w);
                // 用行内均值作为行中心，避免被单个离群词拖走
                int sum = 0;
                foreach (Candidate c in cur) sum += c.Box.Cy;
                curCy = sum / cur.Count;
            }
            if (cur.Count > 0) rows.Add(cur);
            // 两栏布局（会话列表+消息区）只有 2-3 行，不能当表格：要求 >= 6 行才算
            if (rows.Count < 6)
            {
                ctx.Notes.Add("textgrid: skipped (rows=" + rows.Count + ")");
                return;
            }

            // ---- 用「词最多的那一行」当列模板 ----
            List<Candidate> widest = rows[0];
            foreach (var r in rows) if (r.Count > widest.Count) widest = r;
            var cols = new List<Candidate>(widest);
            cols.Sort(new Comparison<Candidate>(delegate(Candidate a, Candidate b) { return a.Box.X.CompareTo(b.Box.X); }));
            // 合并重叠过高的列（同一格里的多个词）
            var colBands = new List<Rect>();
            foreach (Candidate c in cols)
            {
                bool merged = false;
                for (int i = 0; i < colBands.Count; i++)
                {
                    if (colBands[i].Iou(c.Box) > 0.25 || (c.Box.X >= colBands[i].X - 2 && c.Box.X <= colBands[i].Right + 2))
                    {
                        Rect u = colBands[i];
                        int x1 = Math.Min(u.X, c.Box.X), y1 = Math.Min(u.Y, c.Box.Y);
                        int x2 = Math.Max(u.Right, c.Box.Right), y2 = Math.Max(u.Bottom, c.Box.Bottom);
                        colBands[i] = new Rect(x1, y1, x2 - x1, y2 - y1);
                        merged = true;
                        break;
                    }
                }
                if (!merged) colBands.Add(c.Box);
            }

            int rowIdx = 0;
            int producedRows = 0;
            foreach (var r in rows)
            {
                rowIdx++;
                if (r.Count < 2) continue;   // 单行文字多半是标题/说明，不是表格行
                int x1 = int.MaxValue, x2 = -1, y1 = int.MaxValue, y2 = -1;
                foreach (Candidate c in r)
                {
                    if (c.Box.X < x1) x1 = c.Box.X;
                    if (c.Box.Right > x2) x2 = c.Box.Right;
                    if (c.Box.Y < y1) y1 = c.Box.Y;
                    if (c.Box.Bottom > y2) y2 = c.Box.Bottom;
                }
                var band = new Rect(x1, y1, x2 - x1, y2 - y1);
                if (band.W < 60 || band.H < 6) continue;
                ctx.Add("table", "row", band, "row:" + rowIdx, null, 0.42);
                producedRows++;
            }

            int colIdx = 0;
            foreach (Rect cb in colBands)
            {
                colIdx++;
                if (cb.W < 12) continue;
                ctx.Add("table", "col", cb, "col:" + colIdx, null, 0.40);
            }

            ctx.Cap.TableDetected = ctx.Cap.TableDetected || producedRows >= 6;
            ctx.Notes.Add("textgrid: rows=" + producedRows + " cols=" + colBands.Count + " words=" + words.Count + " lineH=" + lineH);
        }
    }
}
