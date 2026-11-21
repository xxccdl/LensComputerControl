// 捕获回退链 + 能力判定 + 弹出窗口合成。
// 关键教训：菜单/下拉框/Tooltip 是独立的顶层窗口（#32768 等），窗口级 PrintWindow 看不到它们，
// 所以必须把 owner 弹出窗口一起抓下来贴到合成图上，否则"点了编辑菜单但差分无变化"。
using System;
using System.Collections.Generic;
using System.Drawing;

namespace Lens
{
    public static class Capture
    {
        public static Bitmap Acquire(WindowInfo wi, Capability cap, out string method, List<WindowInfo> popups)
        {
            method = "none";
            cap.Capturable = false;
            cap.NonBlackRatio = 0;

            uint aff;
            if (Win32.GetWindowDisplayAffinity(new IntPtr(wi.Hwnd), out aff))
            {
                cap.Notes.Add("displayAffinity=0x" + aff.ToString("X2") + (aff == 0x11 ? " (EXCLUDEFROMCAPTURE)" : aff == 1 ? " (MONITOR)" : ""));
            }
            if (wi.Minimized)
            {
                cap.Notes.Add("window is minimized: capture impossible without restore");
                return null;
            }
            if (wi.Frame.W <= 0 || wi.Frame.H <= 0)
            {
                cap.Notes.Add("window frame is empty");
                return null;
            }

            // 1) PrintWindow(PW_RENDERFULLCONTENT)：能拿到被遮挡窗口的真实内容
            Bitmap pw = Win32.TryPrintWindow(wi);
            double r = Win32.NonBlackRatio(pw);
            if (pw != null && r > 0.01)
            {
                cap.Capturable = true; cap.CaptureMethod = "printwindow"; cap.NonBlackRatio = r;
                method = "printwindow";
                cap.Notes.Add("capture=printwindow nonblack=" + r.ToString("0.00"));
                ComposePopups(pw, wi, popups, cap);
                return pw;
            }
            if (pw != null) { cap.Notes.Add("printwindow returned black nonblack=" + r.ToString("0.000")); pw.Dispose(); }

            // 2) BitBlt 兜底：只有目标在前台时才可信，否则截到的是遮挡它的窗口（这是点空事故的根源）
            IntPtr fg = Win32.GetForegroundWindow();
            if (fg == new IntPtr(wi.Hwnd))
            {
                Bitmap bl = Win32.TryBitBlt(wi);
                double r2 = Win32.NonBlackRatio(bl);
                if (bl != null && r2 > 0.01)
                {
                    cap.Capturable = true; cap.CaptureMethod = "bitblt"; cap.NonBlackRatio = r2;
                    method = "bitblt";
                    cap.Notes.Add("capture=bitblt(foreground) nonblack=" + r2.ToString("0.00"));
                    ComposePopups(bl, wi, popups, cap);
                    return bl;
                }
                if (bl != null) { bl.Dispose(); }
                cap.Notes.Add("bitblt returned black nonblack=" + r2.ToString("0.000"));
            }
            else
            {
                cap.Notes.Add("bitblt skipped: window is not foreground (occluded capture would be wrong content)");
            }

            cap.Level = "guide";
            cap.Notes.Add("VERDICT: capture blocked -> guide-only mode");
            return null;
        }

        /// <summary>把 owner 弹出窗口（菜单/下拉/Tooltip）按 z 序叠到合成图上。</summary>
        static void ComposePopups(Bitmap canvas, WindowInfo owner, List<WindowInfo> popups, Capability cap)
        {
            if (popups == null || popups.Count == 0) return;
            int painted = 0;
            using (var g = Graphics.FromImage(canvas))
            {
                // EnumWindows 是 z 序从上到下；倒序绘制，让最上层的最后画上去
                for (int i = popups.Count - 1; i >= 0; i--)
                {
                    var p = popups[i];
                    if (p.Frame.W <= 0 || p.Frame.H <= 0) continue;
                    Bitmap pb = Win32.TryPrintWindow(p);
                    if (pb == null) continue;
                    if (Win32.NonBlackRatio(pb) <= 0.01) { pb.Dispose(); continue; }
                    int dx = p.Frame.X - owner.Frame.X;
                    int dy = p.Frame.Y - owner.Frame.Y;
                    g.DrawImage(pb, dx, dy, pb.Width, pb.Height);
                    pb.Dispose();
                    painted++;
                }
            }
            if (painted > 0) cap.Notes.Add("popups composed: " + painted);
        }
    }
}
