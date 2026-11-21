// 多步任务编排：行式 DSL（verb k=v），零第三方解析。
// 设计要点：
//   1) 每次动作都重新扫描——上一轮的候选 id 一执行动作就失效，所以定位用「谓词」而不是 id；
//   2) wait/assert 让流程能等界面稳定再继续，而不是靠 sleep 猜时间；
//   3) repeat/end 支持重复块；每步都有结构化轨迹，便于复盘是哪一步出了问题。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

namespace Lens
{
    public static class Flow
    {
        class Step
        {
            public int Line;
            public string Verb;
            public Dictionary<string, string> Args = new Dictionary<string, string>();
        }

        static string Arg(Dictionary<string, string> a, string k, string def)
        {
            string v;
            return a.TryGetValue(k, out v) ? v : def;
        }

        static int Int(Dictionary<string, string> a, string k, int def)
        {
            string v;
            int r;
            if (a.TryGetValue(k, out v) && int.TryParse(v, out r)) return r;
            return def;
        }

        // ---------- 行解析 ----------
        static List<string> Tokenize(string s)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            bool inQuote = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') { inQuote = !inQuote; continue; }
                if (!inQuote && char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0) { list.Add(sb.ToString()); sb.Length = 0; }
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) list.Add(sb.ToString());
            return list;
        }

        static bool ParseStep(string line, int lineNo, out Step st, out string err)
        {
            st = null;
            err = null;
            var tok = Tokenize(line);
            if (tok.Count == 0) return false;
            st = new Step();
            st.Line = lineNo;
            st.Verb = tok[0].ToLowerInvariant();
            for (int i = 1; i < tok.Count; i++)
            {
                string t = tok[i];
                int eq = t.IndexOf('=');
                if (eq <= 0) { st.Args["_pos" + i] = t; continue; }
                st.Args[t.Substring(0, eq).ToLowerInvariant()] = t.Substring(eq + 1);
            }
            return true;
        }

        // ---------- 选择器 ----------
        static bool Match(Candidate c, string selector)
        {
            if (string.IsNullOrEmpty(selector)) return false;
            foreach (string part in selector.Split(','))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                int i = p.IndexOf(':');
                bool exact = false;
                string key, val;
                if (i > 0) { key = p.Substring(0, i); val = p.Substring(i + 1); }
                else
                {
                    int e = p.IndexOf('=');
                    if (e <= 0) continue;
                    key = p.Substring(0, e); val = p.Substring(e + 1); exact = true;
                }
                key = key.ToLowerInvariant();
                if (key == "text")
                {
                    if (c.Text == null) return false;
                    if (exact) { if (!string.Equals(c.Text.Trim(), val, StringComparison.OrdinalIgnoreCase)) return false; }
                    else if (c.Text.IndexOf(val, StringComparison.OrdinalIgnoreCase) < 0) return false;
                }
                else if (key == "kind")
                {
                    if (!string.Equals(c.Kind, val, StringComparison.OrdinalIgnoreCase)) return false;
                }
                else if (key == "channel")
                {
                    if (!string.Equals(c.Channel, val, StringComparison.OrdinalIgnoreCase)) return false;
                }
                else if (key == "color")
                {
                    if (c.Color == null || c.Color.IndexOf(val, StringComparison.OrdinalIgnoreCase) < 0) return false;
                }
                else if (key == "index")
                {
                    // index 不参与匹配，由调用方按序取
                }
            }
            return true;
        }

        static Candidate FindIn(ScanContext ctx, string selector)
        {
            int idx = 0;
            string idxRaw = null;
            foreach (string part in selector.Split(','))
            {
                string p = part.Trim().ToLowerInvariant();
                if (p.StartsWith("index:")) int.TryParse(p.Substring(6), out idx);
                if (p.StartsWith("index=")) int.TryParse(p.Substring(6), out idx);
            }
            var hits = new List<Candidate>();
            foreach (Candidate c in ctx.Candidates) if (Match(c, selector)) hits.Add(c);
            if (hits.Count == 0) return null;
            if (idx >= hits.Count) idx = hits.Count - 1;
            return hits[idx];
        }

        // ---------- 执行上下文 ----------
        class Ctx
        {
            public string WindowKey;
            public ScanContext LastScan;
            public Dictionary<string, string> Vars = new Dictionary<string, string>();
            public List<string> Trace = new List<string>();
            public int Ok, Fail;
            public bool Stopped;
        }

        static ScanContext ScanNow(Ctx cx, string channels)
        {
            var ctx = Scanner.Scan(cx.WindowKey, channels ?? "all", false, null, true);
            cx.LastScan = ctx;
            return ctx;
        }

        static string Esc(string s)
        {
            return Json.Str(s);
        }

        static void AddTrace(Ctx cx, int line, string verb, bool ok, long ms, string detail)
        {
            var sb = new StringBuilder();
            sb.Append("{\"i\":").Append(cx.Ok + cx.Fail + 1);
            sb.Append(",\"line\":").Append(line);
            sb.Append(",\"verb\":").Append(Esc(verb));
            sb.Append(",\"ok\":").Append(Json.Bool(ok));
            sb.Append(",\"ms\":").Append(ms);
            sb.Append(",\"detail\":").Append(Esc(detail));
            sb.Append("}");
            cx.Trace.Add(sb.ToString());
            if (ok) cx.Ok++; else cx.Fail++;
        }

        // ---------- 单步执行 ----------
        static bool RunStep(Ctx cx, Step st, out string detail)
        {
            detail = "";
            string v = st.Verb;
            var a = st.Args;

            if (v == "window")
            {
                string w = Arg(a, "name", null);
                if (w == null) { string p; if (a.TryGetValue("_pos1", out p)) w = p; }
                if (w == null) { detail = "missing name="; return false; }
                cx.WindowKey = w;
                var wi = Win32.FindWindow(w);
                if (wi == null) { detail = "window not found: " + w; return false; }
                detail = wi.Title;
                return true;
            }

            if (v == "focus")
            {
                if (cx.WindowKey == null) { detail = "no window set"; return false; }
                var wi = Win32.RestoreAndFocus(cx.WindowKey, 350);
                detail = wi == null ? "focus failed" : "ok";
                return wi != null;
            }

            if (v == "scan")
            {
                if (cx.WindowKey == null) { detail = "no window set"; return false; }
                string ch = Arg(a, "channels", "all");
                var ctx = ScanNow(cx, ch);
                if (ctx.Image == null) { detail = "capture failed"; return false; }
                detail = "candidates=" + ctx.Candidates.Count;
                return true;
            }

            if (v == "click")
            {
                string find = Arg(a, "find", null);
                string cell = Arg(a, "cell", null);
                string id = Arg(a, "id", null);
                int retry = Int(a, "retry", 2);
                for (int attempt = 0; attempt <= retry; attempt++)
                {
                    int x = 0, y = 0;
                    if (find != null)
                    {
                        var ctx = ScanNow(cx, Arg(a, "channels", "all"));
                        if (ctx.Image == null) { detail = "capture failed"; return false; }
                        var c = FindIn(ctx, find);
                        if (c == null) { detail = "not found: " + find; }
                        else
                        {
                            Rect sc = ctx.ToScreen(c.Box);
                            x = sc.Cx; y = sc.Cy;
                            if (Win32.ForceForeground(new IntPtr(ctx.Window.Hwnd))) System.Threading.Thread.Sleep(140);
                            long cover = Win32.TopWindowAt(x, y);
                            if (cover != 0 && cover != ctx.Window.Hwnd) { detail = "covered by " + cover; }
                            else
                            {
                                Human.ClickAt(x, y, Arg(a, "button", "left"), Int(a, "count", 1), !Has(a, "instant"));
                                if (Int(a, "settle", 250) > 0) System.Threading.Thread.Sleep(Int(a, "settle", 250));
                                detail = "#" + c.Id + " " + c.Channel + "/" + c.Kind + "(" + x + "," + y + ")";
                                return true;
                            }
                        }
                    }
                    else if (cell != null || id != null)
                    {
                        var ctx = ScanNow(cx, Arg(a, "channels", "all"));
                        if (ctx.Image == null) { detail = "capture failed"; return false; }
                        Rect sc;
                        if (cell != null)
                        {
                            var m = Program.BuildTable(ctx);
                            string[] p = cell.Split(',');
                            int rr = int.Parse(p[0]), cc2 = int.Parse(p[1]);
                            Program.CellInfo target = null;
                            foreach (var ci in m.Cells) if (ci.R == rr && ci.C == cc2) { target = ci; break; }
                            if (target == null) { detail = "cell not found " + cell; }
                            else
                            {
                                sc = ctx.ToScreen(target.Box);
                                Win32.ForceForeground(new IntPtr(ctx.Window.Hwnd));
                                System.Threading.Thread.Sleep(140);
                                Human.ClickAt(sc.Cx, sc.Cy, "left", 1, !Has(a, "instant"));
                                System.Threading.Thread.Sleep(Int(a, "settle", 200));
                                detail = "cell " + cell + "(" + sc.Cx + "," + sc.Cy + ")";
                                return true;
                            }
                        }
                        else
                        {
                            int want = int.Parse(id);
                            foreach (Candidate c in ctx.Candidates)
                            {
                                if (c.Id != want) continue;
                                sc = ctx.ToScreen(c.Box);
                                Win32.ForceForeground(new IntPtr(ctx.Window.Hwnd));
                                System.Threading.Thread.Sleep(140);
                                Human.ClickAt(sc.Cx, sc.Cy, "left", 1, !Has(a, "instant"));
                                System.Threading.Thread.Sleep(Int(a, "settle", 200));
                                detail = "#" + c.Id + "(" + sc.Cx + "," + sc.Cy + ")";
                                return true;
                            }
                            detail = "id not found " + id;
                        }
                    }
                    else
                    {
                        string sx = Arg(a, "x", null), sy = Arg(a, "y", null);
                        int px, py;
                        if (sx == null || sy == null || !TryPoint(cx, sx + "," + sy, out px, out py))
                        { detail = "bad x/y"; return false; }
                        Human.ClickAt(px, py, "left", Int(a, "count", 1), !Has(a, "instant"));
                        detail = "(" + px + "," + py + ")";
                        return true;
                    }
                    if (attempt < retry) System.Threading.Thread.Sleep(Int(a, "interval", 400));
                }
                return false;
            }

            if (v == "type")
            {
                string txt = Arg(a, "text", null);
                if (txt == null) { detail = "missing text="; return false; }
                Human.TypeText(txt, !Has(a, "instant"));
                System.Threading.Thread.Sleep(Int(a, "settle", 150));
                detail = txt;
                return true;
            }

            if (v == "key")
            {
                string combo = Arg(a, "combo", null);
                if (combo == null) { detail = "missing combo="; return false; }
                bool ok = Human.KeyCombo(combo, !Has(a, "instant"));
                System.Threading.Thread.Sleep(Int(a, "settle", 120));
                detail = combo;
                return ok;
            }

            if (v == "sleep")
            {
                int ms = Int(a, "ms", 300);
                if (ms > 0) System.Threading.Thread.Sleep(Math.Min(ms, 60000));
                detail = ms + "ms";
                return true;
            }

            if (v == "scroll")
            {
                string sx = Arg(a, "x", null), sy = Arg(a, "y", null);
                int px = 0, py = 0;
                if (sx != null && sy != null) { if (!TryPoint(cx, sx + "," + sy, out px, out py)) { detail = "bad x/y"; return false; } }
                else if (cx.WindowKey != null)
                {
                    var w1 = Win32.FindWindow(cx.WindowKey);
                    if (w1 == null) { detail = "window not found before scroll: " + cx.WindowKey; return false; }
                    px = w1.Client.X + w1.Client.W / 2;
                    py = w1.Client.Y + w1.Client.H / 2;
                }
                else { detail = "scroll needs x=/y= or window="; return false; }
                int delta = Int(a, "delta", -3);
                int times = Int(a, "times", 1);
                bool horiz = Has(a, "horizontal");
                for (int k = 0; k < times; k++)
                {
                    Human.ScrollAt(px, py, delta, horiz, !Has(a, "instant"));
                    if (k < times - 1) System.Threading.Thread.Sleep(110);
                }
                System.Threading.Thread.Sleep(Int(a, "settle", 180));
                detail = "(" + px + "," + py + ") delta=" + delta + " x" + times;
                return true;
            }

            if (v == "drag")
            {
                string from = Arg(a, "from", null), to = Arg(a, "to", null);
                if (from == null || to == null) { detail = "missing from=/to="; return false; }
                int fx, fy, tx, ty;
                if (!TryPoint(cx, from, out fx, out fy) || !TryPoint(cx, to, out tx, out ty))
                { detail = "bad from/to"; return false; }
                if (cx.WindowKey != null)
                {
                    var w0 = Win32.FindWindow(cx.WindowKey);
                    if (w0 == null) { detail = "window not found before drag: " + cx.WindowKey; return false; }
                    Win32.ForceForeground(new IntPtr(w0.Hwnd));
                }
                System.Threading.Thread.Sleep(140);
                bool ok = Human.Drag(fx, fy, tx, ty,
                    Int(a, "duration", 650), !Has(a, "instant"), Arg(a, "button", "left"));
                System.Threading.Thread.Sleep(Int(a, "settle", 250));
                detail = fx + "," + fy + " -> " + tx + "," + ty;
                return ok;
            }

            if (v == "wait")
            {
                string what = Arg(a, "for", "changed");
                int timeout = Int(a, "timeout", 5000);
                int interval = Int(a, "interval", 350);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Bitmap baseline = null;
                if (what == "changed" || what == "stable")
                {
                    var c0 = ScanNow(cx, "none");
                    if (c0.Image != null) baseline = new Bitmap(c0.Image);
                }
                while (sw.ElapsedMilliseconds < timeout)
                {
                    if (what == "changed" && baseline != null)
                    {
                        var c1 = ScanNow(cx, "none");
                        if (c1.Image != null)
                        {
                            var regions = Scanner.Diff(baseline, c1.Image, 28, 256);
                            if (regions.Count > 0) { detail = "changed regions=" + regions.Count; baseline.Dispose(); return true; }
                        }
                    }
                    else if (what == "stable" && baseline != null)
                    {
                        var c1 = ScanNow(cx, "none");
                        if (c1.Image != null && Scanner.Diff(baseline, c1.Image, 28, 256).Count == 0)
                        { detail = "stable"; baseline.Dispose(); return true; }
                        if (c1.Image != null) { baseline.Dispose(); baseline = new Bitmap(c1.Image); }
                    }
                    else
                    {
                        string sel = what.StartsWith("text:") ? what.Substring(5) : what;
                        var ctx = ScanNow(cx, Arg(a, "channels", "ocr,table,textgrid,uia,solid"));
                        if (ctx.Image != null)
                        {
                            string selector = what.IndexOf(':') > 0 && !what.StartsWith("text:") ? what : "text:" + sel;
                            if (FindIn(ctx, selector) != null) { detail = "found " + what; return true; }
                        }
                    }
                    System.Threading.Thread.Sleep(interval);
                }
                detail = "timeout waiting " + what;
                if (baseline != null) baseline.Dispose();
                return false;
            }

            if (v == "verify")
            {
                string expect = Arg(a, "expect", "changed");
                var ctx = ScanNow(cx, Arg(a, "channels", "none"));
                if (ctx.Image == null) { detail = "capture failed"; return false; }
                string since = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "state", "baseline.png");
                if (!File.Exists(since)) { detail = "no baseline"; return false; }
                Bitmap before;
                try { before = new Bitmap(since); } catch { detail = "baseline unreadable"; return false; }
                var regions = Scanner.Diff(before, ctx.Image, 28, 256);
                before.Dispose();
                if (expect == "unchanged")
                {
                    detail = "regions=" + regions.Count;
                    return regions.Count == 0;
                }
                detail = "regions=" + regions.Count;
                return regions.Count > 0;
            }

            if (v == "assert")
            {
                string find = Arg(a, "find", null);
                string text = Arg(a, "text", null);
                var ctx = ScanNow(cx, Arg(a, "channels", "all"));
                if (ctx.Image == null) { detail = "capture failed"; return false; }
                string sel = find ?? ("text:" + (text ?? ""));
                bool hit = FindIn(ctx, sel) != null;
                detail = "assert " + sel + ": " + hit;
                return hit;
            }

            if (v == "fill")
            {
                string plan = Arg(a, "plan", null);
                if (plan == null) { detail = "missing plan="; return false; }
                if (!File.Exists(plan))
                {
                    string alt = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, plan);
                    if (!File.Exists(alt)) { detail = "plan not found: " + plan; return false; }
                    plan = alt;
                }
                var ctx = ScanNow(cx, "ocr,table,textgrid");
                if (ctx.Image == null) { detail = "capture failed"; return false; }
                var m = Program.BuildTable(ctx);
                if (m.Cells.Count == 0) { detail = "no table"; return false; }
                Win32.ForceForeground(new IntPtr(ctx.Window.Hwnd));
                System.Threading.Thread.Sleep(140);
                int done = 0, total = 0;
                foreach (string raw in File.ReadAllLines(plan))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    string[] f = line.Split(new char[] { ',' }, 3);
                    if (f.Length < 3) f = line.Split(new char[] { '\t' }, 3);
                    if (f.Length < 3) continue;
                    total++;
                    int r = int.Parse(f[0]), c2 = int.Parse(f[1]);
                    Program.CellInfo cell = null;
                    foreach (var ci in m.Cells) if (ci.R == r && ci.C == c2) { cell = ci; break; }
                    if (cell == null) continue;
                    Rect sc = ctx.ToScreen(cell.Box);
                    long cover = Win32.TopWindowAt(sc.Cx, sc.Cy);
                    if (cover != 0 && cover != ctx.Window.Hwnd) continue;
                    Human.ClickAt(sc.Cx, sc.Cy, "left", 1, !Has(a, "instant"));
                    System.Threading.Thread.Sleep(160);
                    Human.TypeText(f[2], !Has(a, "instant"));
                    System.Threading.Thread.Sleep(140);
                    done++;
                }
                detail = "filled " + done + "/" + total;
                return done == total && total > 0;
            }

            if (v == "read")
            {
                string rect = Arg(a, "rect", null);
                if (rect == null) { detail = "missing rect="; return false; }
                string[] p = rect.Split(',');
                if (p.Length != 4) { detail = "bad rect"; return false; }
                var ctx = ScanNow(cx, "ocr");
                if (ctx.Image == null) { detail = "capture failed"; return false; }
                string txt = Program.OcrRegionPublic(ctx.Image, new Rect(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3])));
                string name = Arg(a, "as", null);
                if (name != null) cx.Vars[name] = txt;
                detail = txt;
                return true;
            }

            detail = "unknown verb: " + v;
            return false;
        }

        static bool Has(Dictionary<string, string> a, string k)
        {
            return a.ContainsKey(k) && a[k] != "0" && a[k] != "false";
        }

        /// <summary>解析坐标。支持 w 前缀表示「相对窗口客户区原点」，这样流程不随窗口位置失效。
        /// 例：from=w220,514  →  client(220,514)。</summary>
        static bool TryPoint(Ctx cx, string s, out int x, out int y)
        {
            x = 0; y = 0;
            if (string.IsNullOrEmpty(s)) return false;
            bool rel = s.Length > 0 && (s[0] == 'w' || s[0] == 'W');
            if (rel) s = s.Substring(1);
            string[] p = s.Split(',');
            int vx, vy;
            if (p.Length != 2 || !int.TryParse(p[0].Trim(), out vx) || !int.TryParse(p[1].Trim(), out vy)) return false;
            x = vx; y = vy;
            if (rel && cx.WindowKey != null)
            {
                var wi = Win32.FindWindow(cx.WindowKey);
                if (wi != null) { x += wi.Client.X; y += wi.Client.Y; }
            }
            return true;
        }

        // ---------- 主入口 ----------
        public static int Run(string[] args)
        {
            string file = null;
            string inline = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--flow") file = args[i + 1];
                else if (args[i] == "--text") inline = args[i + 1];
            }

            string[] lines;
            if (inline != null)
            {
                // 内联脚本：一次调用执行多步，省掉 N 次往返
                lines = inline.Replace("\r\n", "\n").Split('\n');
            }
            else
            {
                if (file == null) { Console.Write("{\"ok\":false,\"error\":\"usage: run --flow plan.txt | --text \\\"…\\\"\"}"); return 2; }
                if (!File.Exists(file)) { Console.Write("{\"ok\":false,\"error\":\"flow not found\"}"); return 2; }
                lines = File.ReadAllLines(file);
            }

            var cx = new Ctx();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int repeatCount = 0;
            var repeatStart = -1;
            var repeatLines = new List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i].Trim();
                if (raw.Length == 0 || raw.StartsWith("#")) continue;

                Step st;
                string perr;
                if (!ParseStep(raw, i + 1, out st, out perr)) continue;

                if (st.Verb == "repeat")
                {
                    if (repeatStart >= 0) { Console.Write("{\"ok\":false,\"error\":\"nested repeat not supported\"}"); return 2; }
                    repeatCount = Int(st.Args, "n", 1);
                    repeatStart = i;
                    repeatLines.Clear();
                    continue;
                }
                if (st.Verb == "end")
                {
                    if (repeatStart < 0) { Console.Write("{\"ok\":false,\"error\":\"end without repeat\"}"); return 2; }
                    for (int n = 1; n < repeatCount && !cx.Stopped; n++)
                    {
                        int ln = repeatStart + 1;
                        foreach (string body in repeatLines)
                        {
                            Step bs;
                            string e2;
                            if (!ParseStep(body, ln++, out bs, out e2)) continue;
                            var t0 = System.Diagnostics.Stopwatch.StartNew();
                            string det;
                            bool ok = RunStep(cx, bs, out det);
                            AddTrace(cx, bs.Line, bs.Verb, ok, t0.ElapsedMilliseconds, det);
                            if (!ok) cx.Stopped = true;
                        }
                    }
                    repeatStart = -1;
                    repeatLines.Clear();
                    continue;
                }
                if (repeatStart >= 0) { repeatLines.Add(raw); continue; }

                var swStep = System.Diagnostics.Stopwatch.StartNew();
                string detail;
                bool okay = RunStep(cx, st, out detail);
                AddTrace(cx, st.Line, st.Verb, okay, swStep.ElapsedMilliseconds, detail);
                if (!okay) cx.Stopped = true;
            }

            sw.Stop();
            var sb = new StringBuilder();
            sb.Append("{\"ok\":").Append(Json.Bool(cx.Fail == 0));
            sb.Append(",\"steps\":").Append(cx.Ok + cx.Fail);
            sb.Append(",\"okSteps\":").Append(cx.Ok).Append(",\"failed\":").Append(cx.Fail);
            sb.Append(",\"ms\":").Append(sw.ElapsedMilliseconds);
            sb.Append(",\"trace\":[");
            for (int i = 0; i < cx.Trace.Count; i++) { if (i > 0) sb.Append(","); sb.Append(cx.Trace[i]); }
            sb.Append("]}");
            Console.Write(sb.ToString());
            return cx.Fail == 0 ? 0 : 7;
        }
    }
}
