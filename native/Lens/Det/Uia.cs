// UIA 通道：窗口作用域枚举 + 能力探测。自绘窗口这里几乎是空的，但常规应用白送像素级精度。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Automation;

namespace Lens.Det
{
    public static class UiaDetector
    {
        static bool IsBadRect(System.Windows.Rect r)
        {
            if (double.IsNaN(r.X) || double.IsNaN(r.Y) || double.IsNaN(r.Width) || double.IsNaN(r.Height)) return true;
            if (r.X <= -2e9 || r.Y <= -2e9) return true;     // 实测微信部分元素返回 int.MinValue
            if (r.Width <= 2 || r.Height <= 2) return true;
            if (r.Width > 20000 || r.Height > 20000) return true;
            return false;
        }

        static string KindOf(ControlType ct)
        {
            if (ct == ControlType.Button || ct == ControlType.SplitButton) return "button";
            if (ct == ControlType.Edit) return "input";
            if (ct == ControlType.Hyperlink) return "link";
            if (ct == ControlType.MenuItem) return "menuitem";
            if (ct == ControlType.TabItem) return "tab";
            if (ct == ControlType.ListItem || ct == ControlType.DataItem || ct == ControlType.TreeItem) return "item";
            if (ct == ControlType.CheckBox || ct == ControlType.RadioButton) return "check";
            if (ct == ControlType.ComboBox) return "combobox";
            if (ct == ControlType.Text) return "text";
            if (ct == ControlType.Menu || ct == ControlType.ToolBar || ct == ControlType.List) return "container";
            return "unknown";
        }

        static bool IsInteractive(ControlType ct)
        {
            return ct == ControlType.Button || ct == ControlType.Edit || ct == ControlType.Hyperlink
                || ct == ControlType.MenuItem || ct == ControlType.TabItem || ct == ControlType.ListItem
                || ct == ControlType.DataItem || ct == ControlType.TreeItem || ct == ControlType.CheckBox
                || ct == ControlType.RadioButton || ct == ControlType.ComboBox || ct == ControlType.SplitButton
                || ct == ControlType.Text || ct == ControlType.Slider || ct == ControlType.Document;
        }

        /// <summary>枚举窗口内（以及合成进画面的 owner 弹出窗口内）的 UIA 元素。</summary>
        public static void Run(ScanContext ctx)
        {
            var sw = Stopwatch.StartNew();
            var tot = new int[] { 0, 0, 0 };   // nodes, named, produced
            bool inv = false, leg = false;

            ScanRoot(ctx, ctx.Hwnd, ctx.Window.Frame, tot, ref inv, ref leg);
            int popupRoots = 0;
            foreach (WindowInfo pw in ctx.Popups)
            {
                ScanRoot(ctx, new IntPtr(pw.Hwnd), ctx.Window.Frame, tot, ref inv, ref leg);
                popupRoots++;
            }

            sw.Stop();
            ctx.Cap.UiaNodes = tot[0];
            ctx.Cap.UiaUsable = tot[1] >= 8;
            ctx.Cap.InvokeSupported = inv;
            ctx.Cap.MsaaUsable = leg;
            ctx.Notes.Add("uia: nodes=" + tot[0] + " named=" + tot[1] + " candidates=" + tot[2]
                + (popupRoots > 0 ? " popupRoots=" + popupRoots : "")
                + " invoke=" + inv + " legacy=" + leg + " elapsed=" + sw.ElapsedMilliseconds + "ms");
        }

        static void ScanRoot(ScanContext ctx, IntPtr hwnd, Rect frame, int[] tot, ref bool inv, ref bool leg)
        {
            int nodes = 0, produced = 0, named = 0;
            bool invokeSupported = inv, legacySupported = leg;

            AutomationElement root = null;
            try { root = AutomationElement.FromHandle(hwnd); }
            catch (Exception e) { ctx.Notes.Add("uia: FromHandle failed " + e.GetType().Name); }

            if (root == null)
            {
                inv = invokeSupported; leg = legacySupported;
                return;
            }

            var walker = TreeWalker.ControlViewWalker;
            var queue = new Queue<object[]>();
            queue.Enqueue(new object[] { root, 0 });
            var seen = new Dictionary<string, bool>();
            var budget = Stopwatch.StartNew();

            while (queue.Count > 0)
            {
                object[] cur = queue.Dequeue();
                AutomationElement el = (AutomationElement)cur[0];
                int depth = (int)cur[1];
                if (nodes > 1200 || budget.ElapsedMilliseconds > 1500) break;

                AutomationElement child = null;
                try { child = walker.GetFirstChild(el); }
                catch { continue; }

                while (child != null && nodes <= 1200 && budget.ElapsedMilliseconds <= 1500)
                {
                    nodes++;
                    try
                    {
                        var cr = child.Current;
                        ControlType ct = cr.ControlType;
                        string name = cr.Name;
                        if (!string.IsNullOrWhiteSpace(name)) named++;

                        if (!cr.IsOffscreen && !IsBadRect(cr.BoundingRectangle) && IsInteractive(ct))
                        {
                            var br = cr.BoundingRectangle;
                            // UIA 给的是屏幕物理坐标；换算到(已裁剪的)图像坐标，并处理 DPI-unaware 的缩放
                            int lx = (int)Math.Round((br.X - ctx.OffsetX) / ctx.ScaleX);
                            int ly = (int)Math.Round((br.Y - ctx.OffsetY) / ctx.ScaleY);
                            int lw = (int)Math.Round(br.Width / ctx.ScaleX);
                            int lh = (int)Math.Round(br.Height / ctx.ScaleY);
                            var local = new Rect(lx, ly, lw, lh);
                            int imgW = ctx.Image != null ? ctx.Image.Width : frame.W;
                            int imgH = ctx.Image != null ? ctx.Image.Height : frame.H;
                            // 必须落在窗口内，否则是幽灵元素
                            var clipped = local.Intersect(new Rect(0, 0, imgW, imgH));
                            if (!clipped.IsEmpty && clipped.Area > 40)
                            {
                                string key = (int)local.X + "," + (int)local.Y + "," + (int)local.W + "," + (int)local.H;
                                if (!seen.ContainsKey(key))
                                {
                                    seen[key] = true;
                                    double score = IsInteractive(ct) ? 0.85 : 0.5;
                                    if (ct == ControlType.Text) score = 0.6;
                                    ctx.Add("uia", KindOf(ct), local, string.IsNullOrEmpty(name) ? null : name, null, score);
                                    produced++;
                                }
                            }
                        }

                        if (!invokeSupported || !legacySupported)
                        {
                            object pat;
                            if (!invokeSupported && child.TryGetCurrentPattern(InvokePattern.Pattern, out pat)) invokeSupported = true;
                            if (!legacySupported)
                            {
                                try
                                {
                                    AutomationPattern legacyPat = AutomationPattern.LookupById(10031); // LegacyIAccessible
                                    if (legacyPat != null && child.TryGetCurrentPattern(legacyPat, out pat)) legacySupported = true;
                                }
                                catch { }
                            }
                        }

                        if (depth < 25) queue.Enqueue(new object[] { child, depth + 1 });
                    }
                    catch { }

                    try { child = walker.GetNextSibling(child); }
                    catch { break; }
                }
            }

            tot[0] += nodes; tot[1] += named; tot[2] += produced;
            inv = invokeSupported; leg = legacySupported;
        }
    }
}
