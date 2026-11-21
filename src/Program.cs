// lens.exe —— 零第三方依赖的电脑控制引擎。
// 用法见 PrintUsage()。核心原则：坐标永远由代码从这个候选表里算出来，模型只负责引用 id。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace Lens
{
    public static class Scanner
    {
        static Win32.RECT ToRect(Rect r)
        {
            var rc = new Win32.RECT();
            rc.Left = r.X; rc.Top = r.Y; rc.Right = r.Right; rc.Bottom = r.Bottom;
            return rc;
        }

        public static ScanContext Scan(string windowKey, string channels, bool wantOverlay, string overlayPath, bool autoRestore)
        {
            var sw = Stopwatch.StartNew();
            var ctx = new ScanContext();
            WindowInfo wi = Win32.FindWindow(windowKey);
            if (wi == null) { ctx.Notes.Add("window not found: " + windowKey); return ctx; }

            // 隐藏窗口（托盘里）：PrintWindow 仍能抓到上次绘制的表面，但点击一定失败。
            // 这里先尝试把它显出来，并把"曾经隐藏"如实写进 notes，避免调用方以为一切正常。
            if (!wi.Visible)
            {
                ctx.Notes.Add("window was hidden (tray?) -> ShowWindow(SW_SHOW) and retry");
                try { Win32.ShowWindow(new IntPtr(wi.Hwnd), Win32.SW_SHOW); } catch { }
                System.Threading.Thread.Sleep(280);
                WindowInfo again = Win32.BuildInfo(new IntPtr(wi.Hwnd), wi.Title, wi.Class, ToRect(wi.Frame));
                if (again != null)
                {
                    wi = again;
                    if (!wi.Visible) ctx.Notes.Add("WINDOW_HIDDEN: still not visible after show attempt");
                }
            }
            else if (wi.IsHost)
            {
                ctx.Notes.Add("WARNING: matched window belongs to the host process");
            }

            if (wi.Minimized)
            {
                if (autoRestore)
                {
                    ctx.Notes.Add("window minimized -> restoring before capture");
                    WindowInfo r = Win32.RestoreAndFocus(wi.Hwnd.ToString(), 320);
                    if (r != null) wi = r;
                }
                else
                {
                    ctx.Window = wi;
                    ctx.Hwnd = new IntPtr(wi.Hwnd);
                    ctx.Notes.Add("window minimized and --no-restore given: cannot capture");
                    ctx.Cap.Level = "guide";
                    return ctx;
                }
            }

            ctx.Window = wi;
            ctx.Hwnd = new IntPtr(wi.Hwnd);
            ctx.Popups = Win32.ListOwnedPopups(ctx.Hwnd);

            string method;
            ctx.Image = Capture.Acquire(wi, ctx.Cap, out method, ctx.Popups);
            foreach (string n in ctx.Cap.Notes) ctx.Notes.Add(n);
            if (ctx.Popups.Count > 0) ctx.Notes.Add("popups=" + ctx.Popups.Count);
            if (ctx.Image != null)
            {
                // DPI-unaware 窗口只会画它的逻辑尺寸，PrintWindow 出来的图右侧/下方是空白哨兵色。
                // 这里裁掉空白并把「图像像素 → 屏幕物理像素」的缩放算出来，否则坐标会整体偏移。
                Rect content = Win32.ContentBox(ctx.Image);
                if (content.IsEmpty) content = new Rect(0, 0, ctx.Image.Width, ctx.Image.Height);
                bool needsCrop = content.X > 0 || content.Y > 0 || content.W < ctx.Image.Width || content.H < ctx.Image.Height;
                double sx = content.W > 0 ? (double)wi.Frame.W / content.W : 1.0;
                double sy = content.H > 0 ? (double)wi.Frame.H / content.H : 1.0;
                if (Math.Abs(sx - 1.0) < 0.02 && Math.Abs(sy - 1.0) < 0.02) { sx = 1.0; sy = 1.0; }
                else ctx.Notes.Add("dpi-unaware scaling x=" + sx.ToString("0.000") + " y=" + sy.ToString("0.000"));

                if (needsCrop)
                {
                    ctx.Notes.Add("content crop " + content.X + "," + content.Y + " " + content.W + "x" + content.H + " from " + ctx.Image.Width + "x" + ctx.Image.Height);
                    var cropped = ctx.Image.Clone(new Rectangle(content.X, content.Y, content.W, content.H), PixelFormat.Format32bppArgb);
                    ctx.Image.Dispose();
                    ctx.Image = cropped;
                }

                ctx.ScaleX = sx;
                ctx.ScaleY = sy;
                ctx.OffsetX = wi.Frame.X + (int)Math.Round(content.X * sx);
                ctx.OffsetY = wi.Frame.Y + (int)Math.Round(content.Y * sy);
                ctx.Content = new Rect(0, 0, ctx.Image.Width, ctx.Image.Height);

                if (Has(channels, "uia")) SafeRun("uia", delegate { Det.UiaDetector.Run(ctx); }, ctx);
                if (Has(channels, "solid")) SafeRun("solid", delegate { Det.SolidDetector.Run(ctx); }, ctx);
                if (Has(channels, "ocr")) SafeRun("ocr", delegate { Det.OcrDetector.Run(ctx); }, ctx);
                if (Has(channels, "table")) SafeRun("table", delegate { Det.TableDetector.Run(ctx); }, ctx);
                // textgrid 仅在显式指定时启用：它在两栏布局（会话列表+消息区）上会误报成表格
                if (channels != "all" && Has(channels, "textgrid")) SafeRun("textgrid", delegate { Det.TextGridDetector.Run(ctx); }, ctx);
                if (Has(channels, "template")) SafeRun("template", delegate { Det.TemplateDetector.Run(ctx); }, ctx);
            }

            FilterNoise(ctx);
            RankAndId(ctx);
            ctx.Cap.Level = DecideLevel(ctx);

            if (wantOverlay && ctx.Image != null)
            {
                ctx.Notes.Add("overlay=" + RenderOverlay(ctx, overlayPath));
            }
            sw.Stop();
            ctx.Notes.Add("elapsed=" + sw.ElapsedMilliseconds + "ms");
            return ctx;
        }

        /// <summary>能力分级。只描述引擎"实际能做且真的会去做"的事：
        /// guide=截不到图只能引导 / L1=看得见但没候选 / L2=点得中（像素层闭环）。
        /// 语义调用(Invoke)只作为单独的能力标记暴露，不计入等级 —— 引擎目前不用它，
        /// 若仅凭标题栏按钮上的 Invoke 就自称更高等级，那是虚报。</summary>
        static string DecideLevel(ScanContext ctx)
        {
            if (!ctx.Cap.Capturable) return "guide";
            if (ctx.Candidates.Count == 0) return "L1";
            return "L2";
        }

        static void SafeRun(string name, Action a, ScanContext ctx)
        {
            try { a(); }
            catch (Exception e) { ctx.Notes.Add(name + ": EXCEPTION " + e.GetType().Name + ": " + e.Message); }
        }

        static bool Has(string list, string name)
        {
            if (string.IsNullOrEmpty(list) || list == "all") return true;
            foreach (string s in list.Split(',')) if (s.Trim() == name) return true;
            return false;
        }

        static int ChannelPriority(string ch, string kind)
        {
            if (ch == "uia") return 0;
            if (ch == "solid" && kind == "button") return 1;
            if (ch == "template") return 2;
            if (ch == "ocr") return 3;
            if (ch == "table") return 4;
            if (ch == "solid") return 6;   // block：表格底纹之类，排最后
            return 5;
        }

        /// <summary>候选噪声过滤：OCR 的单字符/纯符号碎片、solid 的低置信色块。
        /// 这些和真候选同分混排会把模型带偏（实测微信主窗出现过 "0=" "囗" "O" "9"）。</summary>
        public static void FilterNoise(ScanContext ctx)
        {
            int before = ctx.Candidates.Count;
            // 整窗容器（Electron/Chromium 的 Document 常占据整个窗口）不可能是"要点的东西"：
            // 面积 ≥ 窗口 92% 且没有文字、类型是容器/unknown 的候选一律剔除。
            double winArea = ctx.Window != null ? (double)ctx.Window.Frame.W * ctx.Window.Frame.H : 0;
            ctx.Candidates.RemoveAll(delegate(Candidate c)
            {
                if (winArea > 0)
                {
                    double area = (double)c.Box.W * c.Box.H;
                    bool containerish = c.Kind == "unknown" || c.Kind == "container" ||
                                        string.IsNullOrEmpty(c.Text);
                    if (area >= winArea * 0.92 && containerish) return true;
                }
                return false;
            });
            ctx.Candidates.RemoveAll(delegate(Candidate c)
            {
                if (c.Channel == "solid" && c.Kind == "block") return true;   // 色块只保留 button
                if (c.Channel != "ocr" || c.Text == null) return false;
                string t = c.Text.Trim();
                if (t.Length == 0) return true;
                // 单字符候选（O / Q / 9 / 囗 …）对"点哪里"没有任何价值，一律丢弃
                if (t.Length == 1) return true;
                // 两字符且"数字+标点"混排（0=）或纯符号，是 OCR 碎片
                if (t.Length == 2)
                {
                    bool hasDigit = false, hasPunct = false;
                    foreach (char ch in t)
                    {
                        if (char.IsDigit(ch)) hasDigit = true;
                        else if (!char.IsLetter(ch)) hasPunct = true;
                    }
                    if (hasPunct) return true;
                    if (hasDigit && !char.IsLetter(t[0]) && !char.IsLetter(t[1])) return true;
                }
                return false;
            });
            int removed = before - ctx.Candidates.Count;
            if (removed > 0) ctx.Notes.Add("noise filter removed " + removed);
        }
        /// <summary>去重 + 排序 + 编号。模型看到的就是这些 id。</summary>
        public static void RankAndId(ScanContext ctx)
        {
            var kept = new List<Candidate>();
            foreach (var c in ctx.Candidates)
            {
                bool dup = false;
                foreach (var k in kept)
                {
                    if (k.Box.Iou(c.Box) > 0.80)
                    {
                        bool sameText = (k.Text == null && c.Text == null) || (k.Text != null && c.Text != null && k.Text == c.Text);
                        if (sameText || k.Box.Iou(c.Box) > 0.92)
                        {
                            dup = true;
                            if (c.Score > k.Score) { k.Score = c.Score; k.Channel = c.Channel; k.Kind = c.Kind; if (c.Text != null) k.Text = c.Text; }
                            break;
                        }
                    }
                }
                if (!dup) kept.Add(c);
            }

            kept.Sort(delegate(Candidate a, Candidate b)
            {
                int pa = ChannelPriority(a.Channel, a.Kind), pb = ChannelPriority(b.Channel, b.Kind);
                if (pa != pb) return pa - pb;
                if (Math.Abs(a.Score - b.Score) > 0.001) return b.Score.CompareTo(a.Score);
                if (a.Box.Y != b.Box.Y) return a.Box.Y - b.Box.Y;
                return a.Box.X - b.Box.X;
            });

            // 按通道配额裁剪：任何单一通道都不许把候选表刷爆（OCR 在文字密集界面上最容易刷屏）
            var quota = new Dictionary<string, int>();
            quota["uia"] = 90; quota["solid"] = 45; quota["ocr"] = 140;
            quota["table"] = 140; quota["template"] = 30;
            var used = new Dictionary<string, int>();
            var limited = new List<Candidate>();
            foreach (Candidate c in kept)
            {
                int q;
                if (!quota.TryGetValue(c.Channel, out q)) q = 60;
                int u;
                used.TryGetValue(c.Channel, out u);
                if (u >= q) continue;
                used[c.Channel] = u + 1;
                limited.Add(c);
            }
            if (limited.Count != kept.Count) ctx.Notes.Add("per-channel quota trimmed " + (kept.Count - limited.Count));
            kept = limited;

            int cap = 320;
            if (kept.Count > cap)
            {
                var trimmed = new List<Candidate>();
                for (int i = 0; i < cap; i++) trimmed.Add(kept[i]);
                kept = trimmed;
                ctx.Notes.Add("candidates truncated to " + cap);
            }

            for (int i = 0; i < kept.Count; i++) kept[i].Id = i + 1;
            ctx.Candidates = kept;
        }

        public static string RenderOverlay(ScanContext ctx, string path)
        {
            if (ctx.Image == null) return null;
            try
            {
                using (var bmp = new Bitmap(ctx.Image))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var penSolid = new Pen(Color.FromArgb(230, 0, 190, 120), 1.4f))
                    using (var penOcr = new Pen(Color.FromArgb(230, 255, 170, 0), 1.4f))
                    using (var penTable = new Pen(Color.FromArgb(230, 80, 160, 255), 1.4f))
                    using (var penUia = new Pen(Color.FromArgb(230, 220, 80, 220), 1.4f))
                    using (var font = new Font("Consolas", 9f, FontStyle.Bold))
                    using (var bg = new SolidBrush(Color.FromArgb(215, 0, 0, 0)))
                    using (var fg = new SolidBrush(Color.White))
                    {
                        foreach (var c in ctx.Candidates)
                        {
                            Pen p = penSolid;
                            if (c.Channel == "ocr") p = penOcr;
                            else if (c.Channel == "table") p = penTable;
                            else if (c.Channel == "uia") p = penUia;

                            g.DrawRectangle(p, c.Box.X, c.Box.Y, Math.Max(1, c.Box.W - 1), Math.Max(1, c.Box.H - 1));
                            string tag = c.Id.ToString();
                            var sz = g.MeasureString(tag, font);
                            g.FillRectangle(bg, c.Box.X, Math.Max(0, c.Box.Y - sz.Height), sz.Width + 2, sz.Height);
                            g.DrawString(tag, font, fg, c.Box.X + 1, Math.Max(0, c.Box.Y - sz.Height));
                        }
                    }
                    if (path == null || path.Length == 0)
                    {
                        path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "overlay.png");
                    }
                    string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (dir != null && dir.Length > 0 && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    bmp.Save(path, ImageFormat.Png);
                    return Path.GetFullPath(path);
                }
            }
            catch (Exception e) { return "overlay failed: " + e.Message; }
        }

        public static string ToJson(ScanContext ctx, bool ok, string overlay)
        {
            var sb = new StringBuilder();
            sb.Append("{\"ok\":").Append(Json.Bool(ok));
            sb.Append(",\"engine\":{\"built\":").Append(Json.Str(Program.EngineBuilt())).Append("}");
            if (ctx.Window != null)
            {
                sb.Append(",\"window\":").Append(ctx.Window.ToJson());
                // 窗口指纹：界面切换后即便标题/句柄看起来一样，几何变了也能被发现（B10）
                sb.Append(",\"fingerprint\":").Append(Json.Str(Program.Fingerprint(ctx.Window)));
            }
            sb.Append(",\"capability\":").Append(ctx.Cap.ToJson());
            sb.Append(",\"overlay\":").Append(Json.Str(overlay));
            sb.Append(",\"candidates\":[");
            for (int i = 0; i < ctx.Candidates.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var c = ctx.Candidates[i];
                // 屏幕物理坐标也一并给出，方便直接点击
                var sc = ctx.ToScreen(c.Box);
                sb.Append("{\"id\":").Append(c.Id);
                sb.Append(",\"channel\":").Append(Json.Str(c.Channel));
                sb.Append(",\"kind\":").Append(Json.Str(c.Kind));
                sb.Append(",\"local\":").Append(c.Box.ToJson());
                sb.Append(",\"screen\":{\"x\":").Append(sc.X).Append(",\"y\":").Append(sc.Y)
                  .Append(",\"w\":").Append(sc.W).Append(",\"h\":").Append(sc.H)
                  .Append(",\"cx\":").Append(sc.Cx).Append(",\"cy\":").Append(sc.Cy).Append("}");
                if (c.Text != null) sb.Append(",\"text\":").Append(Json.Str(c.Text));
                if (c.Color != null) sb.Append(",\"color\":").Append(Json.Str(c.Color));
                sb.Append(",\"score\":").Append(c.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append("}");
            }
            sb.Append("],\"notes\":").Append(Json.StrArray(ctx.Notes));
            sb.Append("}");
            return sb.ToString();
        }

        public static string StatePath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "state", "last-scan.tsv");
        }

        public static void SaveState(ScanContext ctx)
        {
            try
            {
                string p = StatePath();
                string dir = Path.GetDirectoryName(p);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                // B14：候选表必须带"属于哪个窗口、什么时候扫的"，否则跨窗口用 id 会静默点错位置
                string fp = ctx.Window != null ? Program.Fingerprint(ctx.Window) : "";
                long hwnd0 = ctx.Window != null ? ctx.Window.Hwnd : 0;
                sb.AppendLine("#window\thwnd=" + hwnd0 + "\tfingerprint=" + fp + "\tts="
                    + (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds
                    + "\ttitle=" + (ctx.Window != null ? ctx.Window.Title : ""));
                sb.AppendLine("#id\tscreenX\tscreenY\tw\th\tchannel\tkind\ttext\tscore");
                foreach (var c in ctx.Candidates)
                {
                    var sc = ctx.ToScreen(c.Box);
                    string t = (c.Text == null ? "" : c.Text.Replace("\t", " ").Replace("\r", " ").Replace("\n", " "));
                    sb.Append(c.Id).Append('\t').Append(sc.X).Append('\t').Append(sc.Y).Append('\t')
                      .Append(sc.W).Append('\t').Append(sc.H).Append('\t').Append(c.Channel).Append('\t')
                      .Append(c.Kind).Append('\t').Append(t).Append('\t')
                      .Append(c.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).AppendLine();
                }
                File.WriteAllText(p, sb.ToString(), Encoding.UTF8);
                // 基线图，供 verify 用
                if (ctx.Image != null) ctx.Image.Save(Path.Combine(Path.GetDirectoryName(p), "baseline.png"), ImageFormat.Png);
            }
            catch { }
        }

        public static bool TryLoadCandidate(int id, out int cx, out int cy)
        {
            return TryLoadCandidate(id, null, out cx, out cy);
        }

        /// <summary>按 id 取候选中心。给了 expectHwnd 时校验候选表是否来自同一窗口且未过期——
        /// 跨窗口用 id 会静默点到错误位置（B14）。</summary>
        public static bool TryLoadCandidate(int id, int? expectHwnd, out int cx, out int cy)
        {
            cx = cy = 0;
            try
            {
                string p = StatePath();
                if (!File.Exists(p)) return false;
                var lines = File.ReadAllLines(p);
                if (expectHwnd.HasValue && expectHwnd.Value != 0)
                {
                    long stateHwnd = 0, ts = 0;
                    foreach (string h in lines)
                    {
                        if (!h.StartsWith("#window")) continue;
                        foreach (string kv in h.Split('\t'))
                        {
                            if (kv.StartsWith("hwnd=")) long.TryParse(kv.Substring(5), out stateHwnd);
                            else if (kv.StartsWith("ts=")) long.TryParse(kv.Substring(3), out ts);
                        }
                        break;
                    }
                    long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
                    if (stateHwnd != 0 && stateHwnd != expectHwnd.Value) return false;
                    if (ts != 0 && now - ts > 120000) return false;   // 超过 2 分钟的候选表不再可信
                }
                foreach (string line in lines)
                {
                    if (line.StartsWith("#")) continue;
                    string[] f = line.Split('\t');
                    if (f.Length < 5) continue;
                    if (int.Parse(f[0]) != id) continue;
                    cx = int.Parse(f[1]) + int.Parse(f[3]) / 2;
                    cy = int.Parse(f[2]) + int.Parse(f[4]) / 2;
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>点前/点后差分：返回变化的区域列表（窗口局部坐标）。</summary>
        public static List<Rect> Diff(Bitmap a, Bitmap b, int threshold, int minArea)
        {
            var res = new List<Rect>();
            if (a == null || b == null) return res;
            int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
            int gw = 16, gh = 16;
            int cols = (w + gw - 1) / gw, rows = (h + gh - 1) / gh;
            var changed = new bool[cols * rows];

            var bda = a.LockBits(new Rectangle(0, 0, a.Width, a.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var bdb = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var diff = new int[cols * rows];
            unsafe
            {
                byte* pa = (byte*)bda.Scan0;
                byte* pb = (byte*)bdb.Scan0;
                for (int y = 0; y < h; y++)
                {
                    byte* ra = pa + y * bda.Stride;
                    byte* rb = pb + y * bdb.Stride;
                    int gy = y / gh;
                    for (int x = 0; x < w; x++)
                    {
                        int d = Math.Abs(ra[x * 4] - rb[x * 4]) + Math.Abs(ra[x * 4 + 1] - rb[x * 4 + 1]) + Math.Abs(ra[x * 4 + 2] - rb[x * 4 + 2]);
                        if (d > threshold) diff[gy * cols + x / gw]++;
                    }
                }
            }
            a.UnlockBits(bda); b.UnlockBits(bdb);

            for (int i = 0; i < diff.Length; i++)
                if (diff[i] > (gw * gh) / 8) changed[i] = true;

            // 合并相邻变化格成矩形（简单 BFS）
            var visited = new bool[changed.Length];
            for (int i = 0; i < changed.Length; i++)
            {
                if (!changed[i] || visited[i]) continue;
                int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
                var q = new Queue<int>();
                q.Enqueue(i); visited[i] = true;
                while (q.Count > 0)
                {
                    int cur = q.Dequeue();
                    int cx = cur % cols, cy = cur / cols;
                    minX = Math.Min(minX, cx); minY = Math.Min(minY, cy);
                    maxX = Math.Max(maxX, cx); maxY = Math.Max(maxY, cy);
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx, ny = cy + dy;
                            if (nx < 0 || ny < 0 || nx >= cols || ny >= rows) continue;
                            int ni = ny * cols + nx;
                            if (changed[ni] && !visited[ni]) { visited[ni] = true; q.Enqueue(ni); }
                        }
                }
                var r = new Rect(minX * gw, minY * gh, (maxX - minX + 1) * gw, (maxY - minY + 1) * gh);
                if (r.Area >= minArea) res.Add(r);
            }
            return res;
        }
    }

    public static class Program
    {
        static string Arg(string[] a, string name, string def)
        {
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
            return def;
        }

        static bool ChOn(string list, string name)
        {
            if (string.IsNullOrEmpty(list) || list == "all") return true;
            foreach (string s in list.Split(',')) if (s.Trim() == name) return true;
            return false;
        }

        static bool Flag(string[] a, string name)
        {
            foreach (string s in a) if (s == name) return true;
            return false;
        }

        static void Usage()
        {
            Console.WriteLine("lens.exe —— 零依赖电脑控制引擎");
            Console.WriteLine("");
            Console.WriteLine("  lens info [--json]");
            Console.WriteLine("  lens scan   --window <标题|hwnd> [--channels solid,ocr,uia,table,template] [--json] [--overlay <png>]");
            Console.WriteLine("  lens click  --id <n> | --x X --y Y  [--button left|right] [--count 1]");
            Console.WriteLine("  lens move   --x X --y Y");
            Console.WriteLine("  lens key    --combo \"ctrl+a\"");
            Console.WriteLine("  lens type   --text \"你好\"");
            Console.WriteLine("  lens verify --window <标题|hwnd> [--since <png>] [--json]");
            Console.WriteLine("  lens focus  --window <标题|hwnd>");
            Console.WriteLine("  lens detimg --file <png> [--channels ...] [--json] [--overlay <png>]");
            Console.WriteLine("  lens cap    --window <k> --out <png>");
            Console.WriteLine("  lens drag   --x1 X1 --y1 Y1 --x2 X2 --y2 Y2 [--window k] [--duration ms] [--modifiers ctrl]");
            Console.WriteLine("  lens table  --window <k> [--json]                    结构化表格：行列/单元格/表头");
            Console.WriteLine("  lens read   --window <k> --rect x,y,w,h [--json]      区域 OCR 回读");
            Console.WriteLine("  lens fill   --window <k> --plan plan.json [--json]    按计划逐格填写并回读校验");
            Console.WriteLine("  lens grid   --window <k> [--cols 32 --rows 18] [--out png] [--json]   轴刻度网格");
            Console.WriteLine("  lens zoom   --c C --r R [--sub 8] [--out png] [--json]               放大某个格子再细分");
            Console.WriteLine("  lens click  --cell C,R                                按网格单元精确点击");
            Console.WriteLine("  lens run    --flow <plan.txt>                         多步任务编排（脚本化流程）");
            Console.WriteLine("  lens wait   --window <k> [--for exists|visible|foreground] [--timeout ms]");
            Console.WriteLine("  lens vision [--desktop|--window k] [--cells 10] [--path 3.7] [--cols C --rows R] [--out png]");
            Console.WriteLine("              纯视觉递归定位：截图切 N 格并标注每格屏幕坐标，AI 选格后再切那一格");
            Console.WriteLine("  lens check  --x X --y Y [--w 24 --h 24] [--window k|--desktop] [--zoom 8] [--out png]");
            Console.WriteLine("              坐标检验：回一张把这个点击范围框成绿色的图（含放大镜），点之前先自检");
            Console.WriteLine("  全局：--excludePid N 排除宿主窗口 / --includeHidden 含托盘窗口 / --exact 精确标题 / --className <类名>");
            Console.WriteLine("  说明：click/type/key/drag 默认人类化节奏，加 --instant 关闭。");
            Console.WriteLine("  lens anchors add --name <n> --window <标题> --x X --y Y --w W --h H");
            Console.WriteLine("");
            Console.WriteLine("原则：模型只引用候选 id，坐标一律由本程序计算。");
        }

        public static int Main(string[] rawArgs)
        {
            try { Win32.SetProcessDPIAware(); } catch { }
            // 必须显式设成 UTF-8：重定向输出时 .NET 默认用 OEM 代码页(936)，Node 侧按 UTF-8 解会全变乱码
            try
            {
                var utf8 = new System.Text.UTF8Encoding(false);
                Console.OutputEncoding = utf8;
                Console.InputEncoding = utf8;
            }
            catch { }
            try
            {
                if (rawArgs.Length == 0) { Usage(); return 1; }
                try { HudCtl.Say(HudCtl.LabelFor(rawArgs[0]), null); } catch { }

                // 窗口解析选项（全局）：宿主排除、可见性、精确匹配、类名过滤
                string ep = Arg(rawArgs, "--excludePid", null);
                int epid;
                if (ep != null && int.TryParse(ep, out epid)) Win32.ExcludePid = epid;
                if (Flag(rawArgs, "--includeHidden")) Win32.IncludeHidden = true;
                if (Flag(rawArgs, "--exact")) Win32.ExactMatch = true;
                string cn = Arg(rawArgs, "--className", null);
                if (cn != null) Win32.ClassFilter = cn;
                string cmd = rawArgs[0];
                if (cmd == "info") return CmdInfo(rawArgs);
                if (cmd == "scan") return CmdScan(rawArgs);
                if (cmd == "click") return CmdClick(rawArgs);
                if (cmd == "move")
                {
                    int x = int.Parse(Arg(rawArgs, "--x", "0")), y = int.Parse(Arg(rawArgs, "--y", "0"));
                    if (Flag(rawArgs, "--instant")) Win32.Move(x, y); else Human.MoveTo(x, y, 0, 1);
                    Console.Write("{\"ok\":true,\"x\":" + x + ",\"y\":" + y + "}");
                    return 0;
                }
                if (cmd == "key")
                {
                    string combo = Arg(rawArgs, "--combo", "");
                    string kwin = Arg(rawArgs, "--window", null);
                    IntPtr ktarget = IntPtr.Zero;
                    if (kwin != null)
                    {
                        var wi = Win32.FindWindow(kwin);
                        if (wi == null) { Console.Write(WindowErrorJson(kwin)); return 3; }
                        ktarget = new IntPtr(wi.Hwnd);
                        if (!wi.Visible) { Win32.ShowWindow(ktarget, Win32.SW_SHOW); System.Threading.Thread.Sleep(280); }
                        string kerr;
                        if (!EnsureForeground(ktarget, out kerr)) { LogInput("key", ktarget, IntPtr.Zero); Console.Write(kerr); return 6; }
                    }
                    bool ok = Human.KeyCombo(combo, !Flag(rawArgs, "--instant"));
                    LogInput("key", ktarget, IntPtr.Zero);
                    Console.Write("{\"ok\":" + Json.Bool(ok) + ",\"combo\":" + Json.Str(combo) + "}");
                    return ok ? 0 : 2;
                }
                if (cmd == "type")
                {
                    string tw = Arg(rawArgs, "--text", "");
                    string twin = Arg(rawArgs, "--window", null);
                    IntPtr target = IntPtr.Zero;
                    if (twin != null)
                    {
                        var wi = Win32.FindWindow(twin);
                        if (wi == null) { Console.Write(WindowErrorJson(twin)); return 3; }
                        target = new IntPtr(wi.Hwnd);
                        if (!wi.Visible)
                        {
                            Win32.ShowWindow(target, Win32.SW_SHOW);
                            System.Threading.Thread.Sleep(280);
                        }
                        string err;
                        if (!EnsureForeground(target, out err))
                        {
                            LogInput("type", target, IntPtr.Zero);
                            Console.Write(err);
                            return 6;
                        }
                    }
                    bool ok = Human.TypeText(tw, !Flag(rawArgs, "--instant"));
                    LogInput("type", target, IntPtr.Zero);
                    Console.Write("{\"ok\":" + Json.Bool(ok) + ",\"chars\":" + tw.Length
                        + ",\"foreground\":" + Json.Bool(twin == null || Win32.GetForegroundWindow() == target) + "}");
                    return ok ? 0 : 2;
                }
                if (cmd == "verify") return CmdVerify(rawArgs);
                if (cmd == "focus")
                {
                    string fkey = Arg(rawArgs, "--window", "");
                    var w = Win32.RestoreAndFocus(fkey, 320);
                    if (w == null) { Console.Write(WindowErrorJson(fkey)); return 3; }
                    bool restored = Win32.LastRestoredFromTray;
                    bool isFg = Win32.GetForegroundWindow().ToInt64() == w.Hwnd;
                    Console.Write("{\"ok\":true" + (restored ? ",\"restoredFromTray\":true" : "")
                        + ",\"foreground\":" + Json.Bool(isFg)
                        + (isFg ? "" : ",\"warning\":\"not_foreground\",\"hint\":\"窗口已显示但没抢到前台，接下来的键盘/粘贴会打到别的窗口；先手动点一下目标窗口，或让用户切过来\"")
                        + ",\"window\":" + w.ToJson() + "}");
                    return 0;
                }
                if (cmd == "anchors") return CmdAnchors(rawArgs);
                if (cmd == "detimg") return CmdDetImg(rawArgs);
                if (cmd == "cap") return CmdCap(rawArgs);
                if (cmd == "drag") return CmdDrag(rawArgs);
                if (cmd == "table") return CmdTable(rawArgs);
                if (cmd == "read") return CmdRead(rawArgs);
                if (cmd == "fill") return CmdFill(rawArgs);
                if (cmd == "grid") return GridTools.CmdGrid(rawArgs);
                if (cmd == "run") return Flow.Run(rawArgs);
                if (cmd == "wait") return CmdWait(rawArgs);
                if (cmd == "scroll") return ScrollCmd.Run(rawArgs);
                if (cmd == "paste") return Clip.CmdPaste(rawArgs);
                if (cmd == "window") return WindowCmd.Run(rawArgs);
                if (cmd == "vision") return Vision.CmdVision(rawArgs);
                if (cmd == "check") return CheckTools.CmdCheck(rawArgs);
                if (cmd == "hud")
                {
                    string act = Arg(rawArgs, "--action", "start");
                    if (act == "stop") { HudCtl.Stop(); Console.Write("{\"ok\":true,\"stopped\":true}"); return 0; }
                    if (act == "status") { Console.Write("{\"ok\":true,\"hud\":" + HudCtl.Status() + "}"); return 0; }
                    if (act == "say")
                    {
                        HudCtl.SetEnabled(true);
                        HudCtl.Say(Arg(rawArgs, "--text", "正在工作"), Arg(rawArgs, "--title", null));
                        Console.Write("{\"ok\":true}");
                        return 0;
                    }
                    HudCtl.SetEnabled(true);
                    bool started = HudCtl.EnsureStarted();
                    HudCtl.Say(Arg(rawArgs, "--text", "已就绪"), Arg(rawArgs, "--title", null));
                    Console.Write("{\"ok\":" + Json.Bool(started) + ",\"enabled\":true}");
                    return started ? 0 : 4;
                }
                if (cmd == "zoom") return GridTools.CmdZoom(rawArgs);
                Usage();
                return 1;
            }
            catch (Exception e)
            {
                Console.Write("{\"ok\":false,\"error\":" + Json.Str(e.GetType().Name + ": " + e.Message) + "}");
                return 9;
            }
        }

        static int CmdInfo(string[] a)
        {
            var list = Win32.ListWindows();
            bool json = Flag(a, "--json");
            if (json)
            {
                var sb = new StringBuilder("[");
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append(list[i].ToJson());
                }
                sb.Append("]");
                Console.Write(sb.ToString());
                return 0;
            }
            Console.WriteLine("{0,-10} {1,-6} {2,-9} {3,-22} {4}", "HWND", "DPI", "STATE", "CLASS", "TITLE");
            foreach (var w in list)
                Console.WriteLine("{0,-10} {1,-6} {2,-9} {3,-22} {4}", w.Hwnd, w.Dpi,
                    w.Minimized ? "minimized" : (w.Max ? "maximized" : "normal"),
                    Trunc(w.Class, 22), Trunc(w.Title, 60));
            return 0;
        }

        /// <summary>引擎二进制构建时间，用于发现"源码改了但 exe 没重编"（B12）。</summary>
        public static string EngineBuilt()
        {
            try
            {
                var p = System.Reflection.Assembly.GetExecutingAssembly().Location;
                return File.GetLastWriteTime(p).ToString("yyyy-MM-dd HH:mm");
            }
            catch { return "unknown"; }
        }

        /// <summary>窗口指纹：hwnd+类名+几何+dpi+标题哈希，界面换了就能看出来。</summary>
        public static string Fingerprint(WindowInfo w)
        {
            if (w == null) return "";
            uint h = 2166136261;
            string s = w.Hwnd + "|" + (w.Class ?? "") + "|" + w.Frame.X + "," + w.Frame.Y + "," + w.Frame.W + "," + w.Frame.H + "|" + w.Dpi + "|" + (w.Title ?? "");
            foreach (char ch in s) { h ^= ch; h *= 16777619; }
            return h.ToString("x8");
        }

        static string Trunc(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n - 1) + "~";
        }

        static int CmdScan(string[] a)
        {
            string key = Arg(a, "--window", null);
            if (key == null) { Console.Write("{\"ok\":false,\"error\":\"missing --window\"}"); return 2; }
            string channels = Arg(a, "--channels", "all");
            bool json = Flag(a, "--json");
            string overlay = Arg(a, "--overlay", null);
            bool autoRestore = !Flag(a, "--no-restore");

            var ctx = Scanner.Scan(key, channels, true, overlay, autoRestore);
            bool ok = ctx.Window != null && ctx.Image != null;
            if (ok) Scanner.SaveState(ctx);

            if (json)
            {
                if (ctx.Window == null) { Console.Write(WindowErrorJson(key)); return 3; }
                Console.Write(Scanner.ToJson(ctx, ok, overlay));
                return ok ? 0 : 3;
            }

            if (ctx.Window == null) { Console.WriteLine(WindowErrorJson(key)); return 3; }
            Console.WriteLine("window  : hwnd={0} dpi={1} frame={2}x{3} @ ({4},{5})  {6}",
                ctx.Window.Hwnd, ctx.Window.Dpi, ctx.Window.Frame.W, ctx.Window.Frame.H, ctx.Window.Frame.X, ctx.Window.Frame.Y, Trunc(ctx.Window.Title, 40));
            Console.WriteLine("capture : {0} nonblack={1:P0}   level={2}", ctx.Cap.CaptureMethod, ctx.Cap.NonBlackRatio, ctx.Cap.Level);
            Console.WriteLine("channels: " + string.Join(" | ", ctx.Notes.ToArray()));
            Console.WriteLine("");
            Console.WriteLine("{0,-4} {1,-8} {2,-8} {3,-26} {4,-24} {5}", "ID", "CHANNEL", "KIND", "BOX", "CENTER", "TEXT");
            foreach (var c in ctx.Candidates)
            {
                var sc = ctx.ToScreen(c.Box);
                Console.WriteLine("{0,-4} {1,-8} {2,-8} {3,-26} {4,-24} {5}",
                    c.Id, c.Channel, c.Kind,
                    "(" + c.Box.X + "," + c.Box.Y + ") " + c.Box.W + "x" + c.Box.H,
                    "(" + sc.Cx + "," + sc.Cy + ")",
                    Trunc(c.Text, 30));
            }
            return ok ? 0 : 3;
        }

        static int CmdClick(string[] a)
        {
            int x = 0, y = 0;
            string wkey0 = Arg(a, "--window", null);
            string cellArg = Arg(a, "--cell", null);
            string id = Arg(a, "--id", null);
            if (cellArg != null)
            {
                var st = GridTools.LoadState();
                if (st.Count == 0) { Console.Write("{\"ok\":false,\"error\":\"run grid first\"}"); return 2; }
                string[] p = cellArg.Split(',');
                if (p.Length != 2) { Console.Write("{\"ok\":false,\"error\":\"--cell C,R\"}"); return 2; }
                Rect scr = GridTools.CellToScreen(st, int.Parse(p[0]), int.Parse(p[1]));
                x = scr.Cx; y = scr.Cy;
                if (wkey0 == null) wkey0 = st.ContainsKey("hwnd") ? st["hwnd"] : null;
            }
            else if (id != null)
            {
                // B14：带上目标窗口做一致性校验；候选表若来自别的窗口或已过期，宁可报错也不点
                int? expectHwnd = null;
                if (wkey0 != null)
                {
                    var tmp = Win32.FindWindow(wkey0);
                    if (tmp != null) expectHwnd = (int)tmp.Hwnd;
                }
                if (!Scanner.TryLoadCandidate(int.Parse(id), expectHwnd, out x, out y))
                {
                    Console.Write("{\"ok\":false,\"code\":\"STALE_CANDIDATE\",\"message\":" + Json.Str("候选 #" + id + " 不属于当前目标窗口，或候选表已过期")
                        + ",\"hint\":\"重新 computer_scan 拿最新候选 id；候选只在同一窗口、2 分钟内有效\"}");
                    return 4;
                }
            }
            else
            {
                x = int.Parse(Arg(a, "--x", "0"));
                y = int.Parse(Arg(a, "--y", "0"));
            }

            string button = Arg(a, "--button", "left");
            int count = int.Parse(Arg(a, "--count", "1"));
            string wkey = Arg(a, "--window", null);
            if (wkey == null) wkey = wkey0;
            bool bg = Flag(a, "--bg");
            var notes = new List<string>();
            bool ok;

            if (wkey == null)
            {
                ok = Human.ClickAt(x, y, button, count, !Flag(a, "--instant"));
            }
            else
            {
                var wi = Win32.FindWindow(wkey);
                if (wi == null) { Console.Write(WindowErrorJson(wkey)); return 3; }
                IntPtr h = new IntPtr(wi.Hwnd);

                // B10：指纹校验。界面切换（登录窗→主窗、页面跳转）后几何全变，
                // 模型缓存的坐标会直接打偏；调用方带上扫描时的指纹就能在这里拦住。
                string expectFp = Arg(a, "--expectFingerprint", null);
                if (expectFp != null)
                {
                    string nowFp = Program.Fingerprint(wi);
                    if (!string.Equals(expectFp, nowFp, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Write("{\"ok\":false,\"code\":\"WINDOW_CHANGED\",\"message\":\"界面已变化，坐标不再可信\","
                            + "\"expected\":" + Json.Str(expectFp) + ",\"actual\":" + Json.Str(nowFp)
                            + ",\"window\":" + wi.ToJson()
                            + ",\"hint\":\"重新 computer_scan 拿新候选与指纹，再按新坐标操作\"}");
                        return 9;
                    }
                }

                // 隐藏窗口（托盘）先显出来 —— 否则 PrintWindow 能扫、点击永远失败
                if (!wi.Visible)
                {
                    Win32.ShowWindow(h, Win32.SW_SHOW);
                    System.Threading.Thread.Sleep(300);
                    var chk = Win32.BuildInfo(h, wi.Title, wi.Class, new Win32.RECT { Left = wi.Frame.X, Top = wi.Frame.Y, Right = wi.Frame.Right, Bottom = wi.Frame.Bottom });
                    if (chk != null) wi = chk;
                    notes.Add(chk != null && chk.Visible ? "window was hidden -> shown" : "WINDOW_HIDDEN: still not visible");
                    if (chk == null || !chk.Visible)
                    {
                        Console.Write("{\"ok\":false,\"code\":\"WINDOW_HIDDEN\",\"message\":\"目标窗口不可见（可能在托盘里）\",\"target\":"
                            + DescribeWindow(wi.Hwnd) + ",\"hint\":\"先 focus 或手动把窗口显出来再点\"}");
                        return 6;
                    }
                }

                long top = Win32.TopWindowAt(x, y);
                bool covered = top != 0 && top != wi.Hwnd;
                if (covered) notes.Add("target point covered by window " + top);

                if (bg)
                {
                    ok = Win32.PostClick(h, x, y, button);
                    notes.Add("mode=background PostMessage (no focus change, no real cursor move)");
                }
                else
                {
                    bool fg = Win32.ForceForeground(h);
                    System.Threading.Thread.Sleep(140);
                    long cover = Win32.TopWindowAt(x, y);
                    if (cover != 0 && cover != wi.Hwnd)
                    {
                        // 前台可能被别的窗口/置顶窗口抢回去，再试一次
                        fg = Win32.ForceForeground(h) || fg;
                        System.Threading.Thread.Sleep(140);
                        cover = Win32.TopWindowAt(x, y);
                    }
                    notes.Add("mode=foreground forceForeground=" + fg + " coveredBy=" + cover);

                    if (cover != 0 && cover != wi.Hwnd && !Flag(a, "--force"))
                    {
                        // 宁可拒绝，也不要把点击打到用户正在用的别的窗口上
                        Console.Write("{\"ok\":false,\"code\":\"TARGET_COVERED\",\"message\":\"目标点被其他窗口遮挡，已拒绝点击\","
                            + "\"x\":" + x + ",\"y\":" + y + ",\"target\":" + DescribeWindow(wi.Hwnd)
                            + ",\"coveredBy\":" + DescribeWindow(cover)
                            + ",\"hint\":\"把目标窗口置前再试；确认无误可加 --force 强制\",\"notes\":" + Json.StrArray(notes) + "}");
                        return 6;
                    }

                    for (int i = 0; i < count; i++)
                    {
                        Human.ClickAt(x, y, button, 1, !Flag(a, "--instant"));
                        if (i < count - 1) System.Threading.Thread.Sleep(80);
                    }
                    ok = true;

                    // 动作把窗口点没了（例如"登录"成功后登录窗关闭）——这是成功，不是失败
                    System.Threading.Thread.Sleep(120);
                    if (!Win32.IsWindow(h))
                    {
                        Console.Write("{\"ok\":true,\"warning\":\"window_gone\",\"message\":\"动作已执行，目标窗口随后关闭\","
                            + "\"x\":" + x + ",\"y\":" + y + ",\"button\":" + Json.Str(button)
                            + ",\"hint\":\"这通常意味着动作生效了；继续前请重新定位窗口\",\"notes\":" + Json.StrArray(notes) + "}");
                        return 0;
                    }
                }
            }

            Console.Write("{\"ok\":" + Json.Bool(ok) + ",\"x\":" + x + ",\"y\":" + y
                + ",\"button\":" + Json.Str(button) + ",\"notes\":" + Json.StrArray(notes) + "}");
            return ok ? 0 : 5;
        }

        static int CmdVerify(string[] a)
        {
            string key = Arg(a, "--window", null);
            if (key == null) { Console.Write("{\"ok\":false,\"error\":\"missing --window\"}"); return 2; }
            var wi = Win32.FindWindow(key);
            if (wi == null) { Console.Write(WindowErrorJson(key)); return 3; }

            string since = Arg(a, "--since", null);
            if (since == null) since = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "state", "baseline.png");
            if (!File.Exists(since)) { Console.Write("{\"ok\":false,\"error\":\"no baseline; run scan first\"}"); return 4; }

            var cap = new Capability();
            string method;
            var now = Capture.Acquire(wi, cap, out method, Win32.ListOwnedPopups(new IntPtr(wi.Hwnd)));
            if (now == null) { Console.Write("{\"ok\":false,\"error\":\"capture blocked\",\"notes\":" + Json.StrArray(cap.Notes) + "}"); return 5; }

            Bitmap before;
            try { before = new Bitmap(since); }
            catch { Console.Write("{\"ok\":false,\"error\":\"baseline unreadable\"}"); return 4; }

            var regions = Scanner.Diff(before, now, 28, 256);
            before.Dispose();

            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"changed\":").Append(regions.Count > 0 ? "true" : "false");
            sb.Append(",\"regions\":[");
            for (int i = 0; i < regions.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var r = regions[i];
                sb.Append("{\"x\":").Append(r.X).Append(",\"y\":").Append(r.Y)
                  .Append(",\"w\":").Append(r.W).Append(",\"h\":").Append(r.H).Append("}");
            }
            sb.Append("]}");
            Console.Write(sb.ToString());
            now.Dispose();
            return 0;
        }

        // ================= 拖动 =================
        static int CmdDrag(string[] a)
        {
            int x1 = int.Parse(Arg(a, "--x1", "0")), y1 = int.Parse(Arg(a, "--y1", "0"));
            int x2 = int.Parse(Arg(a, "--x2", "0")), y2 = int.Parse(Arg(a, "--y2", "0"));
            int dur = int.Parse(Arg(a, "--duration", "0"));
            bool human = !Flag(a, "--instant");
            string button = Arg(a, "--button", "left");
            string wkey = Arg(a, "--window", null);
            string mods = Arg(a, "--modifiers", null);
            var notes = new List<string>();

            if (wkey != null)
            {
                var wi = Win32.FindWindow(wkey);
                if (wi == null) { Console.Write(WindowErrorJson(wkey)); return 3; }
                if (!wi.Visible) { Win32.ShowWindow(new IntPtr(wi.Hwnd), Win32.SW_SHOW); System.Threading.Thread.Sleep(260); }
                Win32.ForceForeground(new IntPtr(wi.Hwnd));
                System.Threading.Thread.Sleep(140);   // 等窗口真正上台再做遮挡判定，否则偶发误判 TARGET_COVERED
                long cover = Win32.TopWindowAt(x1, y1);
                if (cover != 0 && cover != wi.Hwnd && !Flag(a, "--force"))
                {
                    Console.Write("{\"ok\":false,\"code\":\"TARGET_COVERED\",\"message\":\"拖拽起点被其他窗口遮挡\",\"target\":" + DescribeWindow(wi.Hwnd) + ",\"coveredBy\":" + DescribeWindow(cover) + ",\"hint\":\"把目标窗口置前，或换一个未被遮挡的起点\"}");
                    return 6;
                }
            }

            var pressed = new List<ushort>();
            if (!string.IsNullOrEmpty(mods))
            {
                foreach (string m in mods.Split(','))
                {
                    ushort vk = Win32.VkOf(m.Trim());
                    if (vk != 0) { Win32.SendKeyStruct(vk, false); pressed.Add(vk); System.Threading.Thread.Sleep(40); }
                }
            }

            bool ok = Human.Drag(x1, y1, x2, y2, dur, human, button);

            for (int i = pressed.Count - 1; i >= 0; i--)
            {
                System.Threading.Thread.Sleep(40);
                Win32.SendKeyStruct(pressed[i], true);
            }
            notes.Add("human=" + human + " from=(" + x1 + "," + y1 + ") to=(" + x2 + "," + y2 + ")"
                + (mods != null ? " modifiers=" + mods : ""));

            Console.Write("{\"ok\":" + Json.Bool(ok) + ",\"x1\":" + x1 + ",\"y1\":" + y1
                + ",\"x2\":" + x2 + ",\"y2\":" + y2 + ",\"notes\":" + Json.StrArray(notes) + "}");
            return ok ? 0 : 5;
        }

        // ================= 结构化表格 =================
        public class CellInfo
        {
            public int R, C;
            public Rect Box;
            public string Text;
        }

        public class TableModel
        {
            public ScanContext Ctx;
            public List<Rect> RowBands = new List<Rect>();
            public List<Rect> ColBands = new List<Rect>();
            public List<string> ColHeaders = new List<string>();
            public List<CellInfo> Cells = new List<CellInfo>();
        }

        public static TableModel BuildTable(ScanContext ctx)
        {
            var m = new TableModel();
            m.Ctx = ctx;
            var rows = new List<Candidate>();
            var cols = new List<Candidate>();
            var words = new List<Candidate>();
            foreach (Candidate c in ctx.Candidates)
            {
                if (c.Channel == "table" && c.Kind == "row") rows.Add(c);
                else if (c.Channel == "table" && c.Kind == "col") cols.Add(c);
                else if (c.Channel == "ocr" && c.Text != null) words.Add(c);
            }
            rows.Sort(delegate(Candidate x, Candidate y) { return x.Box.Y.CompareTo(y.Box.Y); });
            cols.Sort(delegate(Candidate x, Candidate y) { return x.Box.X.CompareTo(y.Box.X); });

            foreach (Candidate r in rows) m.RowBands.Add(r.Box);
            foreach (Candidate c in cols) m.ColBands.Add(c.Box);
            if (m.RowBands.Count == 0 || m.ColBands.Count == 0) return m;

            int maxRows = Math.Min(m.RowBands.Count, 120);
            int maxCols = Math.Min(m.ColBands.Count, 40);
            for (int r = 0; r < maxRows; r++)
            {
                Rect rb = m.RowBands[r];
                for (int c = 0; c < maxCols; c++)
                {
                    Rect cb = m.ColBands[c];
                    var box = new Rect(cb.X, rb.Y, cb.W, rb.H);
                    if (box.W < 6 || box.H < 5) continue;
                    var ci = new CellInfo();
                    ci.R = r + 1; ci.C = c + 1; ci.Box = box;
                    // 单元格里的文字：中心落在框内的 OCR 词，按 x 排序拼接
                    var inside = new List<Candidate>();
                    foreach (Candidate w in words)
                        if (box.Contains(w.Box.Cx, w.Box.Cy)) inside.Add(w);
                    if (inside.Count > 0)
                    {
                        inside.Sort(delegate(Candidate x, Candidate y) { return x.Box.X.CompareTo(y.Box.X); });
                        var sb = new System.Text.StringBuilder();
                        foreach (Candidate w in inside) { if (sb.Length > 0) sb.Append(' '); sb.Append(w.Text); }
                        ci.Text = sb.ToString();
                    }
                    m.Cells.Add(ci);
                }
            }

            // 表头：第一行各列的文字
            for (int c = 0; c < maxCols; c++)
            {
                string h = null;
                foreach (CellInfo ci in m.Cells) if (ci.R == 1 && ci.C == c + 1) { h = ci.Text; break; }
                m.ColHeaders.Add(h);
            }
            return m;
        }

        static string TableToJson(TableModel m)
        {
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"window\":").Append(m.Ctx.Window.ToJson());
            sb.Append(",\"rows\":").Append(m.RowBands.Count).Append(",\"cols\":").Append(m.ColBands.Count);
            sb.Append(",\"headers\":[");
            for (int i = 0; i < m.ColHeaders.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(Json.Str(m.ColHeaders[i]));
            }
            sb.Append("],\"cells\":[");
            for (int i = 0; i < m.Cells.Count; i++)
            {
                CellInfo ci = m.Cells[i];
                Rect sc = m.Ctx.ToScreen(ci.Box);
                if (i > 0) sb.Append(",");
                sb.Append("{\"r\":").Append(ci.R).Append(",\"c\":").Append(ci.C);
                sb.Append(",\"box\":").Append(ci.Box.ToJson());
                sb.Append(",\"screen\":{\"x\":").Append(sc.X).Append(",\"y\":").Append(sc.Y)
                  .Append(",\"w\":").Append(sc.W).Append(",\"h\":").Append(sc.H)
                  .Append(",\"cx\":").Append(sc.Cx).Append(",\"cy\":").Append(sc.Cy).Append("}");
                sb.Append(",\"text\":").Append(Json.Str(ci.Text)).Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static int CmdTable(string[] a)
        {
            string key = Arg(a, "--window", null);
            if (key == null) { Console.Write("{\"ok\":false,\"error\":\"missing --window\"}"); return 2; }
            var ctx = Scanner.Scan(key, "ocr,table,textgrid", false, null, !Flag(a, "--no-restore"));
            if (ctx.Image == null || ctx.Window == null) { Console.Write("{\"ok\":false,\"error\":\"capture failed\"}"); return 3; }
            var m = BuildTable(ctx);
            if (Flag(a, "--json")) { Console.Write(TableToJson(m)); return 0; }

            Console.WriteLine("table  : rows={0} cols={1}", m.RowBands.Count, m.ColBands.Count);
            Console.WriteLine("headers: " + string.Join(" | ", m.ColHeaders.ToArray()));
            Console.WriteLine("");
            Console.WriteLine("{0,-4} {1,-4} {2,-28} {3}", "R", "C", "SCREEN CENTER", "TEXT");
            foreach (CellInfo ci in m.Cells)
            {
                Rect sc = m.Ctx.ToScreen(ci.Box);
                Console.WriteLine("{0,-4} {1,-4} {2,-28} {3}", ci.R, ci.C,
                    "(" + sc.Cx + "," + sc.Cy + ")", Trunc(ci.Text, 40));
            }
            return 0;
        }

        // ================= 区域 OCR 回读 =================
        /// <summary>给 Flow 用的区域回读入口（复用同一套 OCR 逻辑）。</summary>
        public static string OcrRegionPublic(Bitmap img, Rect r)
        {
            return OcrRegionText(img, r);
        }

        static string OcrRegionText(Bitmap img, Rect r)
        {
            if (img == null || r.IsEmpty) return "";
            var clipped = r.Intersect(new Rect(0, 0, img.Width, img.Height));
            if (clipped.W < 3 || clipped.H < 3) return "";
            try
            {
                using (var crop = img.Clone(new Rectangle(clipped.X, clipped.Y, clipped.W, clipped.H), PixelFormat.Format32bppArgb))
                using (var up = new Bitmap(crop.Width * 3, crop.Height * 3, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(up))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        g.DrawImage(crop, 0, 0, up.Width, up.Height);
                    }
                    var sub = new ScanContext();
                    var wi = new WindowInfo();
                    wi.Frame = new Rect(0, 0, up.Width, up.Height);
                    sub.Window = wi;
                    sub.Image = up;
                    Det.OcrDetector.Run(sub);
                    var ws = new List<Candidate>();
                    foreach (Candidate c in sub.Candidates) if (c.Text != null) ws.Add(c);
                    if (ws.Count == 0) return "";
                    ws.Sort(delegate(Candidate x, Candidate y)
                    {
                        int dy = (x.Box.Cy / 18) - (y.Box.Cy / 18);
                        if (dy != 0) return dy;
                        return x.Box.X.CompareTo(y.Box.X);
                    });
                    var sb = new System.Text.StringBuilder();
                    foreach (Candidate w in ws) { if (sb.Length > 0) sb.Append(' '); sb.Append(w.Text); }
                    // B9：回读时剥掉 OCR 常粘连的时间前缀（"昨天 21:43"、"10/25"、"星期三"），
                    // 只影响读回来的文本，不改候选语义。
                    string txt = sb.ToString();
                    string[] pre = { "昨天", "今天", "前天", "星期天", "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };
                    bool stripped = true;
                    while (stripped)
                    {
                        stripped = false;
                        foreach (string p in pre)
                        {
                            if (txt.StartsWith(p) && txt.Length > p.Length + 1 && char.IsDigit(txt[p.Length]) == false && txt[p.Length] != ' ')
                            {
                                // 例如「星期三2」这种没有分隔的粘连
                                txt = txt.Substring(p.Length);
                                stripped = true;
                                break;
                            }
                        }
                        if (stripped) continue;
                        int k = 0;
                        while (k < txt.Length && (char.IsDigit(txt[k]) || txt[k] == '/' || txt[k] == '-')) k++;
                        if (k >= 3 && k < txt.Length && txt.IndexOf('/') >= 0 && txt.IndexOf('/') < k)
                        {
                            txt = txt.Substring(k).TrimStart();
                            stripped = true;
                        }
                    }
                    return txt.Replace('：', ':').Trim();
                }
            }
            catch { return ""; }
        }

        static int CmdRead(string[] a)
        {
            string key = Arg(a, "--window", null);
            string rectArg = Arg(a, "--rect", null);
            if (key == null) { Console.Write("{\"ok\":false,\"error\":\"missing --window\"}"); return 2; }
            var ctx = Scanner.Scan(key, "ocr", false, null, !Flag(a, "--no-restore"));
            if (ctx.Image == null) { Console.Write("{\"ok\":false,\"error\":\"capture failed\"}"); return 3; }

            Rect r;
            if (rectArg != null)
            {
                string[] p = rectArg.Split(',');
                if (p.Length != 4) { Console.Write("{\"ok\":false,\"error\":\"--rect x,y,w,h\"}"); return 2; }
                r = new Rect(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
            }
            else r = new Rect(0, 0, ctx.Image.Width, ctx.Image.Height);

            string txt = OcrRegionText(ctx.Image, r);

            // B9：可选「期望值比对」。OCR 会认错字（老师→老丝），所以比对走归一化 + 编辑距离，
            // 而不是让调用方拿整句做精确比较。只影响比较结果，不篡改返回的原文。
            string expect = Arg(a, "--expect", null);
            string extra = "";
            if (expect != null)
            {
                string g = Canon(txt), w = Canon(expect);
                int d = Levenshtein(g, w);
                int max = Math.Max(g.Length, w.Length);
                double sim = max == 0 ? 1.0 : 1.0 - (double)d / max;
                bool match = g.Contains(w) || w.Contains(g) || sim >= 0.75;
                extra = ",\"expect\":" + Json.Str(expect)
                    + ",\"canonText\":" + Json.Str(g) + ",\"canonExpect\":" + Json.Str(w)
                    + ",\"similarity\":" + sim.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"match\":" + Json.Bool(match);
            }
            Console.Write("{\"ok\":true,\"rect\":" + r.ToJson() + ",\"text\":" + Json.Str(txt) + extra + "}");
            return 0;
        }

        // ================= 批量填表 =================
        static int CmdFill(string[] a)
        {
            string key = Arg(a, "--window", null);
            string planFile = Arg(a, "--plan", null);
            if (key == null || planFile == null) { Console.Write("{\"ok\":false,\"error\":\"usage: fill --window k --plan plan.txt\"}"); return 2; }
            if (!File.Exists(planFile)) { Console.Write("{\"ok\":false,\"error\":\"plan not found\"}"); return 2; }

            var plan = new List<string[]>();
            foreach (string raw in File.ReadAllLines(planFile))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] f = line.Split(new char[] { ',' }, 3);
                if (f.Length < 3) f = line.Split(new char[] { '\t' }, 3);
                if (f.Length < 3) { Console.Write("{\"ok\":false,\"error\":\"bad plan line: " + line.Replace("\"", "'") + "\"}"); return 2; }
                plan.Add(f);
            }

            bool human = !Flag(a, "--instant");
            var ctx = Scanner.Scan(key, "ocr,table,textgrid", false, null, true);
            if (ctx.Image == null) { Console.Write("{\"ok\":false,\"error\":\"capture failed\"}"); return 3; }
            var m = BuildTable(ctx);
            if (m.Cells.Count == 0) { Console.Write("{\"ok\":false,\"error\":\"no table detected\"}"); return 4; }

            var wi = ctx.Window;
            Win32.ForceForeground(new IntPtr(wi.Hwnd));
                System.Threading.Thread.Sleep(140);   // 等窗口真正上台再做遮挡判定，否则偶发误判 TARGET_COVERED

            var results = new List<string>();
            int okCount = 0;
            foreach (string[] f in plan)
            {
                int r = int.Parse(f[0]), c = int.Parse(f[1]);
                string want = f[2];
                CellInfo cell = null;
                foreach (CellInfo ci in m.Cells) if (ci.R == r && ci.C == c) { cell = ci; break; }
                if (cell == null) { results.Add("{\"r\":" + r + ",\"c\":" + c + ",\"ok\":false,\"error\":\"cell not found\"}"); continue; }

                Rect sc = ctx.ToScreen(cell.Box);
                long cover = Win32.TopWindowAt(sc.Cx, sc.Cy);
                if (cover != 0 && cover != wi.Hwnd && !Flag(a, "--force"))
                {
                    results.Add("{\"r\":" + r + ",\"c\":" + c + ",\"ok\":false,\"error\":\"TARGET_COVERED\"}");
                    continue;
                }

                Human.ClickAt(sc.Cx, sc.Cy, "left", 1, human);
                System.Threading.Thread.Sleep(Rnd2(120, 220));
                if (Flag(a, "--clear")) { Human.KeyCombo("ctrl+a", human); System.Threading.Thread.Sleep(60); Human.KeyCombo("delete", human); System.Threading.Thread.Sleep(60); }
                Human.TypeText(want, human);
                System.Threading.Thread.Sleep(Rnd2(120, 200));

                results.Add("{\"r\":" + r + ",\"c\":" + c + ",\"want\":" + Json.Str(want)
                    + ",\"box\":" + cell.Box.ToJson() + "}");
            }

            // 回读必须在写完之后重新截一张图 —— 拿填之前那张图去读，永远是空的。
            var final = new List<string>();
            if (Flag(a, "--noread"))
            {
                okCount = plan.Count;
                final = results;
            }
            else
            {
                Human.KeyCombo("escape", human);       // 清掉选中高亮，否则选中格里是白字压蓝底，OCR 读不出
                System.Threading.Thread.Sleep(320);
                var cap = new Capability();
                string method;
                Bitmap fresh = Capture.Acquire(wi, cap, out method, Win32.ListOwnedPopups(new IntPtr(wi.Hwnd)));
                if (fresh != null)
                {
                    Rect content = Win32.ContentBox(fresh);
                    if (!content.IsEmpty && (content.X > 0 || content.Y > 0 || content.W < fresh.Width || content.H < fresh.Height))
                    {
                        var cropped = fresh.Clone(new Rectangle(content.X, content.Y, content.W, content.H), PixelFormat.Format32bppArgb);
                        fresh.Dispose();
                        fresh = cropped;
                    }
                }
                foreach (string item in results)
                {
                    int r2 = ExtractJsonInt(item, "\"r\":");
                    int c2 = ExtractJsonInt(item, "\"c\":");
                    string want = ExtractJsonString(item, "\"want\":");
                    Rect box = ParseRect(ExtractJsonObject(item, "\"box\":"));
                    string got = fresh == null ? "" : OcrRegionText(fresh, box.Inflate(3));
                    bool ok = fresh != null && FuzzyMatch(Norm(got), Norm(want));
                    if (ok) okCount++;
                    final.Add("{\"r\":" + r2 + ",\"c\":" + c2 + ",\"want\":" + Json.Str(want)
                        + ",\"got\":" + Json.Str(got) + ",\"ok\":" + Json.Bool(ok) + "}");
                }
                if (fresh != null) fresh.Dispose();
            }
            results = final;

            var sb = new StringBuilder();
            sb.Append("{\"ok\":" + Json.Bool(okCount == plan.Count) + ",\"filled\":" + okCount + "/" + plan.Count);
            sb.Append(",\"cells\":[");
            for (int i = 0; i < results.Count; i++) { if (i > 0) sb.Append(","); sb.Append(results[i]); }
            sb.Append("]}");
            Console.Write(sb.ToString());
            return okCount == plan.Count ? 0 : 7;
        }

        // --- 极简 JSON 取值助手（只服务 fill 内部循环，避免引第三方） ---
        static string ExtractJsonString(string s, string key)
        {
            int i = s.IndexOf(key);
            if (i < 0) return "";
            i += key.Length;
            if (i >= s.Length || s[i] != '"') return "";
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char ch = s[i];
                if (ch == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    if (n == '"') { sb.Append('"'); i += 2; continue; }
                    if (n == 'n') { sb.Append('\n'); i += 2; continue; }
                    if (n == 't') { sb.Append('\t'); i += 2; continue; }
                    if (n == 'r') { sb.Append('\r'); i += 2; continue; }
                    if (n == '\\') { sb.Append('\\'); i += 2; continue; }
                    if (n == 'u' && i + 5 < s.Length)
                    {
                        int code;
                        if (int.TryParse(s.Substring(i + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                            sb.Append((char)code);
                        i += 6; continue;
                    }
                    sb.Append(n); i += 2; continue;
                }
                if (ch == '"') break;
                sb.Append(ch); i++;
            }
            return sb.ToString();
        }

        static int ExtractJsonInt(string s, string key)
        {
            int i = s.IndexOf(key);
            if (i < 0) return 0;
            i += key.Length;
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-')) i++;
            int v;
            return int.TryParse(s.Substring(start, i - start), out v) ? v : 0;
        }

        static string ExtractJsonObject(string s, string key)
        {
            int i = s.IndexOf(key);
            if (i < 0) return null;
            i += key.Length;
            int depth = 0, start = i;
            for (; i < s.Length; i++)
            {
                if (s[i] == '{') depth++;
                else if (s[i] == '}') { depth--; if (depth == 0) return s.Substring(start, i - start + 1); }
            }
            return null;
        }

        static Rect ParseRect(string json)
        {
            if (string.IsNullOrEmpty(json)) return new Rect(0, 0, 0, 0);
            return new Rect(ExtractJsonInt(json, "\"x\":"), ExtractJsonInt(json, "\"y\":"),
                ExtractJsonInt(json, "\"w\":"), ExtractJsonInt(json, "\"h\":"));
        }


        /// <summary>回读判定：OCR 会把 1 读成 I、0 读成 O，所以不能用严格相等。
        /// 先归一化易混字符，再要求编辑距离相似度 ≥ 0.75（或互相包含）。</summary>
        static bool FuzzyMatch(string got, string want)
        {
            if (want.Length == 0) return true;
            if (got == null) return false;
            string g = Canon(got), w = Canon(want);
            if (g.Length == 0) return false;
            if (g.Contains(w) || w.Contains(g)) return true;
            int d = Levenshtein(g, w);
            int max = Math.Max(g.Length, w.Length);
            double sim = max == 0 ? 1.0 : 1.0 - (double)d / max;
            return sim >= 0.75;
        }

        static string Canon(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                char c = ch;
                // 全角 → 半角：OCR 对中文输入法下的数字/字母常常输出全角
                if (c >= '０' && c <= '９') c = (char)('0' + (c - '０'));
                else if (c >= 'Ａ' && c <= 'Ｚ') c = (char)('A' + (c - 'Ａ'));
                else if (c >= 'ａ' && c <= 'ｚ') c = (char)('a' + (c - 'ａ'));
                else if (c == '：') c = ':';
                else if (c == '，') c = ',';
                else if (c == '。') c = '.';
                else if (c == '（') c = '(';
                else if (c == '）') c = ')';

                // 空白一律丢弃：OCR 会在字与字之间随机插空格
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '　' || c == '\u00A0') continue;

                // ASCII 易混字归一（0/O、1/I/l、5/S、8/B）
                if (c == 'I' || c == 'i' || c == 'l' || c == 'L' || c == '|') c = '1';
                else if (c == 'O' || c == 'o') c = '0';
                else if (c == 'S' || c == 's') c = '5';
                else if (c == 'B' || c == 'b') c = '8';

                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        static int Levenshtein(string a, string b)
        {
            int n = a.Length, m = b.Length;
            if (n == 0) return m;
            if (m == 0) return n;
            var prev = new int[m + 1];
            var cur = new int[m + 1];
            for (int j = 0; j <= m; j++) prev[j] = j;
            for (int i = 1; i <= n; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= m; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int v = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                    cur[j] = v;
                }
                var t = prev; prev = cur; cur = t;
            }
            return prev[m];
        }
        static readonly Random GuiRnd = new Random(Environment.TickCount);
        static int Rnd2(int lo, int hi) { lock (GuiRnd) return GuiRnd.Next(lo, hi); }

        static string Norm(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char ch in s)
            {
                if (char.IsWhiteSpace(ch)) continue;
                if (ch == '，') sb.Append(',');
                else if (ch == '：') sb.Append(':');
                else if (ch == '．') sb.Append('.');
                else sb.Append(ch);
            }
            return sb.ToString().ToLowerInvariant();
        }

        /// <summary>把一个 hwnd 描述成 JSON，用于报错时说清「被谁挡了 / 目标是哪个」。</summary>
        /// <summary>把目标窗口弄到真正的前台。键盘事件发给"当前前台窗口"，
        /// 所以打字/按键前必须先确认前台就是目标——否则会往别的应用里输入（往终端里打中文可能直接执行命令）。</summary>
        static bool EnsureForeground(IntPtr h, out string errJson)
        {
            errJson = null;
            if (Win32.GetForegroundWindow() == h) return true;
            Win32.ForceForeground(h);
            System.Threading.Thread.Sleep(180);
            if (Win32.GetForegroundWindow() == h) return true;
            Win32.ForceForeground(h);
            System.Threading.Thread.Sleep(240);
            if (Win32.GetForegroundWindow() == h) return true;
            Win32.SwitchToThisWindow(h, true);        // 托盘应用专用：绕开前台锁
            System.Threading.Thread.Sleep(260);
            if (Win32.GetForegroundWindow() == h) return true;

            long fg = Win32.GetForegroundWindow().ToInt64();
            errJson = "{\"ok\":false,\"code\":\"NOT_FOREGROUND\",\"message\":\"目标窗口抢不到前台，已拒绝输入\","
                + "\"target\":" + DescribeWindow(h.ToInt64()) + ",\"foreground\":" + DescribeWindow(fg)
                + ",\"hint\":\"盲打会打到别的应用里，所以这里直接拒绝；先把目标窗口手动切到前台再试\"}";
            return false;
        }

        /// <summary>输入类动作落盘：出事时能回溯"当时前台到底是谁"。</summary>
        static void LogInput(string what, IntPtr target, IntPtr fg)
        {
            try
            {
                string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lens-hud");
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                IntPtr cur = Win32.GetForegroundWindow();
                long t = target == IntPtr.Zero ? -1 : target.ToInt64();
                long f = fg == IntPtr.Zero ? cur.ToInt64() : fg.ToInt64();
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                    + " " + what + " target=" + t + " fgAtSend=" + f + " fgAfter=" + cur.ToInt64() + "\r\n";
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "input.log"), line);
            }
            catch { }
        }
        public static string DescribeWindow(long hwnd)
        {
            try
            {
                var h = new IntPtr(hwnd);
                var sb = new StringBuilder(512);
                Win32.GetWindowTextW(h, sb, 512);
                var ci = new StringBuilder(256);
                Win32.GetClassNameW(h, ci, 256);
                uint pid;
                Win32.GetWindowThreadProcessId(h, out pid);
                string pn = "";
                try { pn = Process.GetProcessById((int)pid).ProcessName; } catch { }
                return "{\"hwnd\":" + hwnd + ",\"title\":" + Json.Str(sb.ToString())
                    + ",\"class\":" + Json.Str(ci.ToString()) + ",\"pid\":" + pid
                    + ",\"process\":" + Json.Str(pn) + ",\"visible\":" + Json.Bool(Win32.IsWindowVisible(h)) + "}";
            }
            catch { return "{\"hwnd\":" + hwnd + "}"; }
        }
        /// <summary>窗口定位失败时的结构化错误：区分「找不到 / 有歧义 / 句柄已失效」，都带 hint。</summary>
        public static string WindowErrorJson(string key)
        {
            var r = Win32.LastResolve;
            if (r != null && r.Ambiguous)
                return "{\"ok\":false,\"code\":\"AMBIGUOUS_WINDOW\",\"message\":" + Json.Str(r.Message)
                    + ",\"detail\":" + r.ToJson() + ",\"hint\":\"用 hwnd，或加 --exact / --className / --excludePid 钉死唯一目标\"}";
            if (r != null && r.Error == "WINDOW_GONE")
                return "{\"ok\":false,\"code\":\"WINDOW_GONE\",\"message\":" + Json.Str(r.Message)
                    + ",\"hint\":\"窗口已在操作后关闭；需要继续请重新定位窗口\"}";
            return "{\"ok\":false,\"code\":\"WINDOW_NOT_FOUND\",\"message\":" + Json.Str("找不到窗口: " + key)
                + ",\"hint\":\"先 computer_windows 查看可用窗口；托盘里的窗口加 --includeHidden\"}";
        }
        /// <summary>等窗口出现/可见/到前台。用于「登录窗→主窗」这类句柄漂移的场景，
        /// 让调用方不用自己 sleep 轮询。</summary>
        static int CmdWait(string[] a)
        {
            string key = Arg(a, "--window", null);
            if (key == null) { Console.Write("{\"ok\":false,\"code\":\"BAD_ARGS\",\"message\":\"missing --window\"}"); return 2; }
            string cond = Arg(a, "--for", "exists");
            int timeout = int.Parse(Arg(a, "--timeout", "8000"));
            int interval = int.Parse(Arg(a, "--interval", "300"));

            var sw = Stopwatch.StartNew();
            string last = "none";
            while (sw.ElapsedMilliseconds < timeout)
            {
                var r = Win32.ResolveWindow(key, true, Win32.ExcludePid, Win32.ExactMatch, Win32.ClassFilter);
                var w = r.Best;
                if (w != null)
                {
                    last = "hwnd=" + w.Hwnd + " visible=" + w.Visible + " title=" + Trunc(w.Title, 40);
                    bool ok = false;
                    if (cond == "exists") ok = true;
                    else if (cond == "visible") ok = w.Visible;
                    else if (cond == "foreground") ok = Win32.GetForegroundWindow().ToInt64() == w.Hwnd;
                    if (ok)
                    {
                        Console.Write("{\"ok\":true,\"for\":" + Json.Str(cond) + ",\"elapsedMs\":" + sw.ElapsedMilliseconds
                            + ",\"window\":" + w.ToJson() + "}");
                        return 0;
                    }
                }
                System.Threading.Thread.Sleep(interval);
            }
            Console.Write("{\"ok\":false,\"code\":\"WAIT_TIMEOUT\",\"message\":" + Json.Str("等待超时: " + cond)
                + ",\"elapsedMs\":" + sw.ElapsedMilliseconds + ",\"last\":" + Json.Str(last)
                + ",\"hint\":\"窗口可能还没出现、在托盘里（--includeHidden），或标题已变化\"}");
            return 8;
        }

        static int CmdCap(string[] a)
        {
            string key = Arg(a, "--window", null);
            string outp = Arg(a, "--out", null);
            bool desktop = Flag(a, "--desktop") || key == null;
            if (outp == null) { Console.Write("{\"ok\":false,\"error\":\"usage: cap [--window <k>|--desktop] --out <png>\"}"); return 2; }

            string dir0 = Path.GetDirectoryName(Path.GetFullPath(outp));
            if (dir0 != null && dir0.Length > 0 && !Directory.Exists(dir0)) Directory.CreateDirectory(dir0);

            // 桌面截图：按虚拟屏幕取整块，多显示器也覆盖
            if (desktop)
            {
                int vx = Win32.GetSystemMetrics(76), vy = Win32.GetSystemMetrics(77);
                int vw = Win32.GetSystemMetrics(78), vh = Win32.GetSystemMetrics(79);
                if (vw <= 0 || vh <= 0) { vx = 0; vy = 0; vw = Win32.GetSystemMetrics(0); vh = Win32.GetSystemMetrics(1); }
                // --rect：只截这一块（屏幕物理坐标）。没有这一步，调用方传了区域仍拿整屏图，换算全错。
                int oX = vx, oY = vy, oW = vw, oH = vh;
                string dRect = Arg(a, "--rect", null);
                using (var bmp = new Bitmap(vw, vh, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        IntPtr dst = g.GetHdc();
                        IntPtr src = Win32.GetDC(IntPtr.Zero);
                        Win32.BitBlt(dst, 0, 0, vw, vh, src, vx, vy, 0x00CC0020);
                        Win32.ReleaseDC(IntPtr.Zero, src);
                        g.ReleaseHdc(dst);
                    }
                    Bitmap toSave = bmp;
                    Bitmap piece = null;
                    if (!string.IsNullOrEmpty(dRect))
                    {
                        string[] rp = dRect.Split(',');
                        int rx, ry, rw, rh;
                        if (rp.Length == 4 && int.TryParse(rp[0], out rx) && int.TryParse(rp[1], out ry)
                            && int.TryParse(rp[2], out rw) && int.TryParse(rp[3], out rh) && rw > 0 && rh > 0)
                        {
                            int cx = rx - vx, cy = ry - vy;
                            int cw = rw, ch = rh;
                            if (cx < 0) { cw += cx; cx = 0; }
                            if (cy < 0) { ch += cy; cy = 0; }
                            if (cx + cw > vw) cw = vw - cx;
                            if (cy + ch > vh) ch = vh - cy;
                            if (cw > 0 && ch > 0)
                            {
                                piece = bmp.Clone(new Rectangle(cx, cy, cw, ch), PixelFormat.Format32bppArgb);
                                toSave = piece;
                                oX = rx; oY = ry; oW = cw; oH = ch;
                            }
                        }
                    }
                    toSave.Save(outp, ImageFormat.Png);
                    if (piece != null) piece.Dispose();
                }
                Console.Write("{\"ok\":true,\"file\":" + Json.Str(Path.GetFullPath(outp))
                    + ",\"w\":" + oW + ",\"h\":" + oH + ",\"x\":" + oX + ",\"y\":" + oY + ",\"method\":\"desktop\"}");
                return 0;
            }

            var wi = Win32.FindWindow(key);
            if (wi == null) { Console.Write(WindowErrorJson(key)); return 3; }

            var cap = new Capability();
            string method;
            var img = Capture.Acquire(wi, cap, out method, Win32.ListOwnedPopups(new IntPtr(wi.Hwnd)));
            if (img == null) { Console.Write("{\"ok\":false,\"error\":\"capture blocked\",\"notes\":" + Json.StrArray(cap.Notes) + "}"); return 5; }

            // 与 scan 一致：裁掉 DPI-unaware 留下的空白
            Rect content = Win32.ContentBox(img);
            if (!content.IsEmpty && (content.X > 0 || content.Y > 0 || content.W < img.Width || content.H < img.Height))
            {
                var cropped = img.Clone(new Rectangle(content.X, content.Y, content.W, content.H), PixelFormat.Format32bppArgb);
                img.Dispose();
                img = cropped;
            }
            // --rect x,y,w,h（屏幕物理坐标）：只截这一块。
            // 之前 cap 完全忽略 --rect，调用方传了区域仍拿整窗图，图内像素与屏幕坐标的换算因此全错。
            int outX = wi.Frame.X, outY = wi.Frame.Y;
            string rectArg = Arg(a, "--rect", null);
            if (!string.IsNullOrEmpty(rectArg))
            {
                string[] rp = rectArg.Split(',');
                int rx, ry, rw, rh;
                if (rp.Length == 4 && int.TryParse(rp[0], out rx) && int.TryParse(rp[1], out ry)
                    && int.TryParse(rp[2], out rw) && int.TryParse(rp[3], out rh) && rw > 0 && rh > 0)
                {
                    // 窗口图是内容裁剪后的，先算出内容原点在屏幕上的位置，再换算到图内坐标
                    int originX = wi.Frame.X + (img.Width - wi.Frame.W > 0 ? 0 : 0);
                    int cx = rx - originX, cy = ry - wi.Frame.Y;
                    int cw = Math.Min(rw, img.Width - cx), ch = Math.Min(rh, img.Height - cy);
                    if (cx < 0) { cw += cx; cx = 0; }
                    if (cy < 0) { ch += cy; cy = 0; }
                    if (cw > 0 && ch > 0)
                    {
                        var piece = img.Clone(new Rectangle(cx, cy, cw, ch), PixelFormat.Format32bppArgb);
                        img.Dispose();
                        img = piece;
                        outX = rx; outY = ry;
                    }
                    else
                    {
                        Console.Write("{\"ok\":false,\"code\":\"RECT_OUTSIDE\",\"message\":\"requested rect is outside the captured image\",\"window\":" + wi.ToJson() + "}");
                        img.Dispose();
                        return 2;
                    }
                }
            }
            img.Save(outp, ImageFormat.Png);
            int w = img.Width, h = img.Height;
            img.Dispose();
            Console.Write("{\"ok\":true,\"file\":" + Json.Str(Path.GetFullPath(outp)) + ",\"w\":" + w + ",\"h\":" + h
                + ",\"x\":" + outX + ",\"y\":" + outY
                + ",\"method\":" + Json.Str(method) + ",\"window\":" + wi.ToJson() + "}");
            return 0;
        }

        static int CmdDetImg(string[] a)
        {
            string file = Arg(a, "--file", null);
            if (file == null || !File.Exists(file)) { Console.Write("{\"ok\":false,\"error\":\"missing --file\"}"); return 2; }
            string channels = Arg(a, "--channels", "all");
            string overlay = Arg(a, "--overlay", null);
            bool json = Flag(a, "--json");

            var sw = Stopwatch.StartNew();
            var bmp = new Bitmap(file);
            var ctx = new ScanContext();
            var wi = new WindowInfo();
            wi.Hwnd = 0; wi.Title = Path.GetFileName(file); wi.Class = "image"; wi.Pid = 0; wi.Process = "file";
            wi.Dpi = 96; wi.Visible = true;
            wi.Frame = new Rect(0, 0, bmp.Width, bmp.Height);
            wi.Client = wi.Frame;
            ctx.Window = wi;
            ctx.Hwnd = IntPtr.Zero;
            ctx.Image = bmp;
            ctx.OffsetX = 0; ctx.OffsetY = 0;
            ctx.Cap.Capturable = true;
            ctx.Cap.CaptureMethod = "file";
            ctx.Cap.NonBlackRatio = Win32.NonBlackRatio(bmp);

            if (ChOn(channels, "solid")) { try { Det.SolidDetector.Run(ctx); } catch (Exception e) { ctx.Notes.Add("solid EX " + e.Message); } }
            if (ChOn(channels, "ocr") || ChOn(channels, "textgrid")) { try { Det.OcrDetector.Run(ctx); } catch (Exception e) { ctx.Notes.Add("ocr EX " + e.Message); } }
            if (ChOn(channels, "table")) { try { Det.TableDetector.Run(ctx); } catch (Exception e) { ctx.Notes.Add("table EX " + e.Message); } }
            if (ChOn(channels, "textgrid")) { try { Det.TextGridDetector.Run(ctx); } catch (Exception e) { ctx.Notes.Add("textgrid EX " + e.Message); } }
            if (ChOn(channels, "template")) { try { Det.TemplateDetector.Run(ctx); } catch (Exception e) { ctx.Notes.Add("template EX " + e.Message); } }

            Scanner.FilterNoise(ctx);
            Scanner.RankAndId(ctx);
            ctx.Cap.Level = ctx.Candidates.Count > 0 ? "L2" : "L1";
            if (overlay != null) ctx.Notes.Add("overlay=" + Scanner.RenderOverlay(ctx, overlay));
            sw.Stop();
            ctx.Notes.Add("elapsed=" + sw.ElapsedMilliseconds + "ms");

            if (json) { Console.Write(Scanner.ToJson(ctx, true, overlay)); bmp.Dispose(); return 0; }

            Console.WriteLine("image  : {0} {1}x{2}", wi.Title, bmp.Width, bmp.Height);
            Console.WriteLine("notes  : " + string.Join(" | ", ctx.Notes.ToArray()));
            Console.WriteLine("");
            Console.WriteLine("{0,-4} {1,-8} {2,-8} {3,-28} {4}", "ID", "CHANNEL", "KIND", "BOX", "TEXT");
            foreach (var c in ctx.Candidates)
                Console.WriteLine("{0,-4} {1,-8} {2,-8} {3,-28} {4}", c.Id, c.Channel, c.Kind,
                    "(" + c.Box.X + "," + c.Box.Y + ") " + c.Box.W + "x" + c.Box.H, Trunc(c.Text, 34));
            bmp.Dispose();
            return 0;
        }

        static int CmdAnchors(string[] a)
        {
            if (a.Length < 2 || a[1] != "add") { Console.Write("{\"ok\":false,\"error\":\"usage: anchors add --name n --window t --x X --y Y --w W --h H\"}"); return 2; }
            string name = Arg(a, "--name", null);
            string key = Arg(a, "--window", null);
            if (name == null || key == null) { Console.Write("{\"ok\":false,\"error\":\"missing --name/--window\"}"); return 2; }
            var wi = Win32.FindWindow(key);
            if (wi == null) { Console.Write(WindowErrorJson(key)); return 3; }
            int x = int.Parse(Arg(a, "--x", "0")), y = int.Parse(Arg(a, "--y", "0"));
            int w = int.Parse(Arg(a, "--w", "0")), h = int.Parse(Arg(a, "--h", "0"));
            if (w <= 4 || h <= 4) { Console.Write("{\"ok\":false,\"error\":\"bad w/h\"}"); return 2; }

            var cap = new Capability();
            string method;
            var img = Capture.Acquire(wi, cap, out method, Win32.ListOwnedPopups(new IntPtr(wi.Hwnd)));
            if (img == null) { Console.Write("{\"ok\":false,\"error\":\"capture blocked\"}"); return 5; }

            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "anchors");
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);

            var lr = new Rect(x - wi.Frame.X, y - wi.Frame.Y, w, h).Intersect(new Rect(0, 0, img.Width, img.Height));
            if (lr.IsEmpty) { img.Dispose(); Console.Write("{\"ok\":false,\"error\":\"anchor outside window\"}"); return 4; }
            using (var patch = img.Clone(new Rectangle(lr.X, lr.Y, lr.W, lr.H), PixelFormat.Format32bppArgb))
            {
                patch.Save(Path.Combine(dir, "patch.png"), ImageFormat.Png);
            }
            img.Dispose();

            File.WriteAllText(Path.Combine(dir, "meta.json"),
                "{\"name\":\"" + name + "\",\"w\":" + lr.W + ",\"h\":" + lr.H + ",\"channel\":\"template\",\"kind\":\"button\"}", Encoding.UTF8);
            Console.Write("{\"ok\":true,\"anchor\":\"" + name + "\",\"dir\":\"" + dir.Replace("\\", "\\\\") + "\",\"w\":" + lr.W + ",\"h\":" + lr.H + "}");
            return 0;
        }
    }
}
