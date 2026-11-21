// Win32 层：窗口枚举、DWM 精确边界、per-window DPI、捕获、输入注入。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Lens
{
    public static class Win32
    {
        // ---------- 结构 ----------
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        // ---------- 导入 ----------
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr h, uint a);
        [DllImport("user32.dll")] public static extern bool GetWindowDisplayAffinity(IntPtr h, out uint a);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool altTab);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        public static readonly IntPtr HWND_TOP = IntPtr.Zero;
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_SHOWWINDOW = 0x0040;
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetLastActivePopup(IntPtr h);

        public const int SW_RESTORE = 9;
        public const int SW_SHOW = 5;
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
        [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inp, int size);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT val, int size);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
        [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);

        public const uint GW_OWNER = 4;
        public const uint GA_ROOT = 2;
        public const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
        public const uint WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
        public const uint MK_LBUTTON = 0x0001, MK_RBUTTON = 0x0002;

        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const uint PW_RENDERFULLCONTENT = 0x2;
        public const uint WDA_NONE = 0x0, WDA_MONITOR = 0x1, WDA_EXCLUDEFROMCAPTURE = 0x11;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x01000;

        // ---------- 输入结构 ----------
        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }
        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public INPUTUNION u; }

        public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;

        // ---------- 窗口 ----------
        /// <summary>解析参数（由 Program 在启动时从命令行写入）。放在这里是为了让所有命令共用同一套匹配规则。</summary>
        public static int ExcludePid = 0;
        public static bool IncludeHidden = false;
        public static bool ExactMatch = false;
        public static string ClassFilter = null;
        /// <summary>最近一次窗口解析的完整结果，供调用方判断 NOT_FOUND / AMBIGUOUS。</summary>
        public static WindowResolve LastResolve;
        /// <summary>最近一次 RestoreAndFocus 是否是从托盘里还原出来的。</summary>
        public static bool LastRestoredFromTray;

        public static bool IsHostProcess(int pid, string pname)
        {
            if (ExcludePid > 0 && pid == ExcludePid) return true;
            if (string.IsNullOrEmpty(pname)) return false;
            // 兜底：没传 --excludePid 时，至少不要把宿主自己的窗口当成目标
            return string.Equals(pname, "DeepSeek Harness", StringComparison.OrdinalIgnoreCase);
        }

        public static List<WindowInfo> ListWindowsEx(bool includeHidden, out int hiddenCount)
        {
            var list = new List<WindowInfo>();
            int hidden = 0;
            EnumWindows(delegate(IntPtr h, IntPtr p)
            {
                bool vis = IsWindowVisible(h);
                if (!vis && !includeHidden) return true;
                var sb = new StringBuilder(512);
                GetWindowTextW(h, sb, 512);
                string t = sb.ToString();
                if (t.Length == 0) return true;
                var ci = new StringBuilder(256);
                GetClassNameW(h, ci, 256);
                RECT r; GetWindowRect(h, out r);
                if (r.Right - r.Left < 40 || r.Bottom - r.Top < 20) return true;
                var wi = BuildInfo(h, t, ci.ToString(), r);
                if (wi != null)
                {
                    if (!vis) hidden++;
                    list.Add(wi);
                }
                return true;
            }, IntPtr.Zero);
            hiddenCount = hidden;
            return list;
        }

        public static List<WindowInfo> ListWindows()
        {
            int ignored;
            return ListWindowsEx(IncludeHidden, out ignored);
        }

        /// <summary>窗口匹配打分：完全相同 &gt; 前缀 &gt; 子串；可见、前台、标题长度贴近都加分。</summary>
        static int ScoreWindow(WindowInfo w, string key, IntPtr foreground)
        {
            if (w.Title == null && w.Process == null) return 0;
            string title = w.Title ?? "";
            string proc = w.Process ?? "";
            int score = 0;
            if (string.Equals(title, key, StringComparison.OrdinalIgnoreCase)) score = 100;
            else if (title.StartsWith(key, StringComparison.OrdinalIgnoreCase)) score = 60;
            else if (title.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) score = 40;
            else if (string.Equals(proc, key, StringComparison.OrdinalIgnoreCase)) score = 30;
            else if (proc.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) score = 20;
            if (score == 0) return 0;
            if (w.Visible) score += 20;
            if (foreground != IntPtr.Zero && foreground.ToInt64() == w.Hwnd) score += 15;
            // 标题越接近查询词越可能是目标（避免 "微信" 命中 "用微信登录…" 这种长标题）
            int diff = Math.Abs(title.Length - key.Length);
            score -= Math.Min(10, diff / 4);
            return score;
        }

        public static WindowResolve ResolveWindow(string key, bool includeHidden, int excludePid, bool exact, string classFilter)
        {
            var res = new WindowResolve();
            if (string.IsNullOrEmpty(key)) { res.Error = "NOT_FOUND"; return res; }

            int hiddenIgnored;
            var all = ListWindowsEx(includeHidden || IncludeHidden, out hiddenIgnored);

            long asHwnd;
            if (long.TryParse(key, out asHwnd))
            {
                var h = new IntPtr(asHwnd);
                if (!IsWindow(h))
                {
                    res.Error = "WINDOW_GONE";
                    res.Message = "句柄已失效（窗口可能在动作后关闭了）";
                    return res;
                }
                var sb = new StringBuilder(512);
                GetWindowTextW(h, sb, 512);
                var ci = new StringBuilder(256);
                GetClassNameW(h, ci, 256);
                RECT r; GetWindowRect(h, out r);
                var wi = BuildInfo(h, sb.ToString(), ci.ToString(), r);
                res.Best = wi;
                res.Matches.Add(wi);
                return res;
            }

            var foreground = GetForegroundWindow();
            var scored = new List<KeyValuePair<int, WindowInfo>>();
            foreach (var w in all)
            {
                if (w.IsHost) continue;   // 宿主自己的窗口永远不是目标（B1）
                if (!string.IsNullOrEmpty(classFilter) &&
                    (w.Class == null || w.Class.IndexOf(classFilter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                int s = exact
                    ? (string.Equals(w.Title, key, StringComparison.OrdinalIgnoreCase) ? 100 : 0)
                    : ScoreWindow(w, key, foreground);
                if (s > 0) scored.Add(new KeyValuePair<int, WindowInfo>(s, w));
            }
            scored.Sort(delegate(KeyValuePair<int, WindowInfo> a, KeyValuePair<int, WindowInfo> b)
            {
                return b.Key.CompareTo(a.Key);
            });

            foreach (var kv in scored) res.Matches.Add(kv.Value);
            if (scored.Count == 0)
            {
                // 托盘兜底：应用"最小化到系统图标"后，主窗口 IsWindowVisible=false，
                // 但它对用户来说仍然是开着的（微信尤其明显——找不到就重启会弹出登录页）。
                // 所以按标题找不到时，把隐藏窗口也纳入匹配，并标记 WasHidden 让上层去还原。
                if (!includeHidden && !IncludeHidden)
                {
                    int ignored;
                    var hiddenAll = ListWindowsEx(true, out ignored);
                    var hiddenScored = new List<KeyValuePair<int, WindowInfo>>();
                    foreach (var w in hiddenAll)
                    {
                        if (w.Visible) continue;
                        if (w.IsHost) continue;
                        if (!string.IsNullOrEmpty(classFilter) &&
                            (w.Class == null || w.Class.IndexOf(classFilter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                        int s = exact
                            ? (string.Equals(w.Title, key, StringComparison.OrdinalIgnoreCase) ? 100 : 0)
                            : ScoreWindow(w, key, foreground);
                        if (s > 0) hiddenScored.Add(new KeyValuePair<int, WindowInfo>(s, w));
                    }
                    hiddenScored.Sort(delegate(KeyValuePair<int, WindowInfo> a, KeyValuePair<int, WindowInfo> b)
                    {
                        return b.Key.CompareTo(a.Key);
                    });
                    if (hiddenScored.Count > 0)
                    {
                        foreach (var kv in hiddenScored) res.Matches.Add(kv.Value);
                        res.Best = hiddenScored[0].Value;
                        res.WasHidden = true;
                        res.Message = "窗口在托盘里（隐藏状态），需要先还原";
                        return res;
                    }
                }
                res.Error = "NOT_FOUND";
                return res;
            }
            res.Best = scored[0].Value;

            // 唯一性：前两名同分且不是同一个窗口 → 不猜
            if (scored.Count > 1 && scored[0].Key == scored[1].Key &&
                scored[0].Value.Hwnd != scored[1].Value.Hwnd && !exact)
            {
                res.Ambiguous = true;
                res.Error = "AMBIGUOUS_WINDOW";
                res.Message = "标题匹配到多个同分窗口，请用 hwnd / --exact / --className 钉死目标";
            }
            return res;
        }

        /// <summary>兼容旧签名：返回最佳匹配，完整结果放在 LastResolve。</summary>
        public static WindowInfo FindWindow(string key)
        {
            var r = ResolveWindow(key, IncludeHidden, ExcludePid, ExactMatch, ClassFilter);
            LastResolve = r;
            return r.Best;
        }

        public static WindowInfo BuildInfo(IntPtr h, string title, string cls, RECT winRect)
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            string pname = "";
            try { pname = Process.GetProcessById((int)pid).ProcessName; } catch { }
            var wi = new WindowInfo();
            wi.Hwnd = h.ToInt64();
            wi.Title = title;
            wi.Class = cls;
            wi.Pid = (int)pid;
            wi.Process = pname;
            wi.Dpi = (int)GetDpiForWindow(h);
            wi.Visible = IsWindowVisible(h);
            wi.Minimized = IsIconic(h);
            wi.Max = IsZoomed(h);
            wi.IsHost = IsHostProcess((int)pid, pname);

            // DWM 扩展边框：GetWindowRect 会把最大化窗口的隐形边框算进去（实测 1938x1038 vs 屏幕 1920x1080）
            RECT frame = winRect;
            try
            {
                RECT dwm;
                if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out dwm, Marshal.SizeOf(typeof(RECT))) == 0)
                {
                    if (dwm.Right > dwm.Left && dwm.Bottom > dwm.Top) frame = dwm;
                }
            }
            catch { }
            wi.Frame = new Rect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);

            RECT cr;
            if (GetClientRect(h, out cr))
            {
                var pt = new POINT(); pt.X = 0; pt.Y = 0;
                ClientToScreen(h, ref pt);
                wi.Client = new Rect(pt.X, pt.Y, cr.Right - cr.Left, cr.Bottom - cr.Top);
            }
            else wi.Client = wi.Frame;
            return wi;
        }

        /// <summary>找 owner 链上属于目标窗口的所有可见弹出窗口（菜单/下拉/Tooltip），按 z 序从上到下返回。</summary>
        /// <summary>找 owner 链上属于目标窗口的所有可见弹出窗口（菜单/下拉/Tooltip），按 z 序从上到下返回。</summary>
        public static List<WindowInfo> ListOwnedPopups(IntPtr target)
        {
            var res = new List<WindowInfo>();
            var found = new List<IntPtr>();
            EnumWindows(delegate(IntPtr h, IntPtr p)
            {
                if (h == target) return true;
                if (!IsWindowVisible(h)) return true;
                long owner = GetWindow(h, GW_OWNER).ToInt64();
                if (owner == 0) return true;
                if (owner == target.ToInt64() || found.Contains(new IntPtr(owner)))
                {
                    RECT r; GetWindowRect(h, out r);
                    if (r.Right - r.Left > 2 && r.Bottom - r.Top > 2)
                    {
                        found.Add(h);
                        var ci = new StringBuilder(256);
                        GetClassNameW(h, ci, 256);
                        res.Add(BuildInfo(h, "", ci.ToString(), r));
                    }
                }
                return true;
            }, IntPtr.Zero);
            return res;
        }

        /// <summary>所有可见顶层窗口（含无标题的弹出窗口），供 --all 使用。</summary>
        public static List<WindowInfo> ListAllWindows()
        {
            var list = new List<WindowInfo>();
            EnumWindows(delegate(IntPtr h, IntPtr p)
            {
                if (!IsWindowVisible(h)) return true;
                var sb = new StringBuilder(512);
                GetWindowTextW(h, sb, 512);
                var ci = new StringBuilder(256);
                GetClassNameW(h, ci, 256);
                RECT r; GetWindowRect(h, out r);
                if (r.Right - r.Left < 40 || r.Bottom - r.Top < 20) return true;
                var wi = BuildInfo(h, sb.ToString(), ci.ToString(), r);
                if (wi != null) list.Add(wi);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        // ---------- 捕获 ----------
        /// <summary>用哨兵色铺底再 PrintWindow。DPI-unaware 窗口只会画它的逻辑尺寸，
        /// 剩下的哨兵色区域就能被精确识别成"没有内容"。</summary>
        public static readonly Color Sentinel = Color.FromArgb(255, 255, 0, 255);

        static Bitmap NewSentinelBitmap(int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.Clear(Sentinel);
            return bmp;
        }

        /// <summary>返回真正有内容的包围盒；全空返回 empty。</summary>
        public static Rect ContentBox(Bitmap b)
        {
            if (b == null) return new Rect(0, 0, 0, 0);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            try
            {
                var bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                unsafe
                {
                    byte* p0 = (byte*)bd.Scan0;
                    for (int y = 0; y < b.Height; y++)
                    {
                        byte* row = p0 + y * bd.Stride;
                        for (int x = 0; x < b.Width; x++)
                        {
                            int bb = row[x * 4], g2 = row[x * 4 + 1], r = row[x * 4 + 2], a = row[x * 4 + 3];
                            bool isSentinel = (a == 0) || (r == Sentinel.R && g2 == Sentinel.G && bb == Sentinel.B);
                            if (isSentinel) continue;
                            if (x < minX) minX = x;
                            if (y < minY) minY = y;
                            if (x > maxX) maxX = x;
                            if (y > maxY) maxY = y;
                        }
                    }
                }
                b.UnlockBits(bd);
            }
            catch { return new Rect(0, 0, b.Width, b.Height); }
            if (maxX < 0) return new Rect(0, 0, 0, 0);
            return new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        public static Bitmap TryPrintWindow(WindowInfo wi)
        {
            try
            {
                var bmp = NewSentinelBitmap(wi.Frame.W, wi.Frame.H);
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr dc = g.GetHdc();
                    bool ok = PrintWindow(new IntPtr(wi.Hwnd), dc, PW_RENDERFULLCONTENT);
                    g.ReleaseHdc(dc);
                    if (!ok) { bmp.Dispose(); return null; }
                }
                return bmp;
            }
            catch { return null; }
        }

        public static Bitmap TryBitBlt(WindowInfo wi)
        {
            try
            {
                var bmp = NewSentinelBitmap(wi.Frame.W, wi.Frame.H);
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr dst = g.GetHdc();
                    IntPtr src = GetDC(IntPtr.Zero);
                    BitBlt(dst, 0, 0, wi.Frame.W, wi.Frame.H, src, wi.Frame.X, wi.Frame.Y, 0x00CC0020);
                    ReleaseDC(IntPtr.Zero, src);
                    g.ReleaseHdc(dst);
                }
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>非黑像素比例，用于判定"截到了图"还是"截到一张黑图"。</summary>
        public static double NonBlackRatio(Bitmap b)
        {
            if (b == null) return 0;
            try
            {
                var rect = new Rectangle(0, 0, b.Width, b.Height);
                var bd = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                long nonBlack = 0, n = 0;
                unsafe
                {
                    byte* p0 = (byte*)bd.Scan0;
                    int stepY = Math.Max(1, b.Height / 120);
                    int stepX = Math.Max(1, b.Width / 160);
                    for (int y = 0; y < b.Height; y += stepY)
                    {
                        byte* row = p0 + y * bd.Stride;
                        for (int x = 0; x < b.Width; x += stepX)
                        {
                            int v = row[x * 4] + row[x * 4 + 1] + row[x * 4 + 2];
                            if (v > 12) nonBlack++;
                            n++;
                        }
                    }
                }
                b.UnlockBits(bd);
                return n == 0 ? 0 : (double)nonBlack / n;
            }
            catch { return 0; }
        }

        // ---------- 输入 ----------
        public static bool Click(int x, int y, string button, int count)
        {
            SetCursorPos(x, y);
            System.Threading.Thread.Sleep(30);
            uint down = MOUSEEVENTF_LEFTDOWN, up = MOUSEEVENTF_LEFTUP;
            if (button == "right") { down = MOUSEEVENTF_RIGHTDOWN; up = MOUSEEVENTF_RIGHTUP; }
            else if (button == "middle") { down = MOUSEEVENTF_MIDDLEDOWN; up = MOUSEEVENTF_MIDDLEUP; }
            for (int i = 0; i < count; i++)
            {
                mouse_event(down, 0, 0, 0, IntPtr.Zero);
                System.Threading.Thread.Sleep(20);
                mouse_event(up, 0, 0, 0, IntPtr.Zero);
                if (i < count - 1) System.Threading.Thread.Sleep(60);
            }
            return true;
        }

        public static bool Move(int x, int y)
        {
            return SetCursorPos(x, y);
        }

        /// <summary>把窗口从最小化/托盘恢复并置前。只做「让用户看见它」，绝不做任何可能关掉它的操作。</summary>
        public static WindowInfo RestoreAndFocus(string key, int settleMs)
        {
            WindowInfo wi = FindWindow(key);
            LastRestoredFromTray = false;
            if (wi == null) return null;
            IntPtr h = new IntPtr(wi.Hwnd);
            bool wasVisible = wi.Visible;
            if (!wi.Visible) LastRestoredFromTray = true;

            // 托盘窗口：IsWindowVisible=false 时 ForceForeground 里的 ShowWindow 往往不生效，
            // 必须先 RESTORE 再 SHOW 再 SetWindowPos(SWP_SHOWWINDOW)。三连都是"显示类"操作，
            // 不会产生 WM_CLOSE —— 绝不能在这条路径上加任何关闭语义。
            if (!wi.Visible)
            {
                ShowWindow(h, SW_RESTORE);
                System.Threading.Thread.Sleep(80);
                ShowWindow(h, SW_SHOW);
                SetWindowPos(h, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                System.Threading.Thread.Sleep(200);
            }

            ForceForeground(h);
            System.Threading.Thread.Sleep(settleMs <= 0 ? 260 : settleMs);

            WindowInfo after = FindWindow(wi.Hwnd.ToString());

            // 实测：微信（Qt）被强制置前后会自己缩回托盘 —— 原本可见的窗口变成不可见，
            // 用户看到的就是"一操作微信就没了"。这里检测到这种情况就再显一次并重新置前。
            if (after != null && !after.Visible)
            {
                ShowWindow(h, SW_RESTORE);
                System.Threading.Thread.Sleep(90);
                ShowWindow(h, SW_SHOW);
                SetWindowPos(h, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                System.Threading.Thread.Sleep(220);
                ForceForeground(h);
                System.Threading.Thread.Sleep(200);
                after = FindWindow(wi.Hwnd.ToString());
                LastRestoredFromTray = true;
            }

            // 诊断：把每次激活的真实结果落盘，万一出现"激活后窗口没了"能回溯
            LogFocus(key, wi.Hwnd, wasVisible, after != null && after.Visible, after != null);
            return after;
        }

        static void LogFocus(string key, long hwnd, bool wasVisible, bool nowVisible, bool exists)
        {
            try
            {
                string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lens-hud");
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                    + " key=" + key + " hwnd=" + hwnd
                    + " wasVisible=" + wasVisible + " nowVisible=" + nowVisible + " exists=" + exists + "\r\n";
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "focus.log"), line);
            }
            catch { }
        }

        /// <summary>强制把窗口弄到前台。后台进程直接 SetForegroundWindow 会被系统拒绝，
        /// 标准解法：先按一下 ALT 解锁前台锁，再 AttachThreadInput 到当前前台线程。</summary>
        public static bool ForceForeground(IntPtr h)
        {
            if (!IsWindow(h)) return false;
            if (GetForegroundWindow() == h) return true;
            try
            {
                if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                else ShowWindow(h, SW_SHOW);


                // 解除前台锁：系统只允许"正在响应用户输入"的进程抢前台。
                // 按一下 ALT 会让本进程获得前台资格，这是唯一不需要用户配合的办法。
                try { keybd_event(0x12, 0, 0, IntPtr.Zero); keybd_event(0x12, 0, 0x0002, IntPtr.Zero); } catch { }

                IntPtr fg = GetForegroundWindow();
                uint fgTid = 0, pid;
                if (fg != IntPtr.Zero) fgTid = GetWindowThreadProcessId(fg, out pid);
                uint myTid = GetCurrentThreadId();
                bool attached = false;
                if (fgTid != 0 && fgTid != myTid) attached = AttachThreadInput(myTid, fgTid, true);

                BringWindowToTop(h);
                SetForegroundWindow(h);
                if (GetForegroundWindow() != h) SwitchToThisWindow(h, true);   // 后台进程抢前台的第二招

                if (attached) AttachThreadInput(myTid, fgTid, false);
            }
            catch { }
            System.Threading.Thread.Sleep(80);
            return GetForegroundWindow() == h;
        }

        /// <summary>屏幕物理坐标处最上层的顶层窗口。用于判定目标是否被遮挡。</summary>
        public static long TopWindowAt(int x, int y)
        {
            var pt = new POINT(); pt.X = x; pt.Y = y;
            IntPtr h = WindowFromPoint(pt);
            if (h == IntPtr.Zero) return 0;
            IntPtr root = GetAncestor(h, GA_ROOT);
            return (root == IntPtr.Zero ? h : root).ToInt64();
        }

        /// <summary>后台点击：直接往窗口消息队列投递鼠标事件，不移动真鼠标、不抢焦点。
        /// 很多自绘/后台窗口（Qt、Win32）接受这种方式。</summary>
        public static bool PostClick(IntPtr hwnd, int screenX, int screenY, string button)
        {
            try
            {
                var pt = new POINT(); pt.X = screenX; pt.Y = screenY;
                if (!ScreenToClient(hwnd, ref pt)) return false;
                IntPtr lParam = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF));
                bool right = (button == "right");
                uint down = right ? WM_RBUTTONDOWN : WM_LBUTTONDOWN;
                uint up = right ? WM_RBUTTONUP : WM_LBUTTONUP;
                IntPtr downW = (IntPtr)(right ? MK_RBUTTON : MK_LBUTTON);

                PostMessageW(hwnd, WM_MOUSEMOVE, IntPtr.Zero, lParam);
                System.Threading.Thread.Sleep(12);
                PostMessageW(hwnd, down, downW, lParam);
                System.Threading.Thread.Sleep(30);
                PostMessageW(hwnd, up, IntPtr.Zero, lParam);
                return true;
            }
            catch { return false; }
        }

        static INPUT KeyInput(ushort vk, ushort scan, uint flags)
        {
            var inp = new INPUT();
            inp.type = INPUT_KEYBOARD;
            inp.u.ki.wVk = vk;
            inp.u.ki.wScan = scan;
            inp.u.ki.dwFlags = flags;
            return inp;
        }

        /// <summary>单键按下/抬起，供人类化组合键使用（分开控制时序）。</summary>
        public static void SendKeyStruct(ushort vk, bool keyUp)
        {
            var one = new INPUT[1];
            one[0] = KeyInput(vk, 0, keyUp ? KEYEVENTF_KEYUP : 0);
            SendInput(1, one, Marshal.SizeOf(typeof(INPUT)));
        }

        public const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

        /// <summary>先把所有修饰键强制弹起。如果上一次操作把 Ctrl/Alt 卡在按下状态，
        /// 后续的字母输入就会变成 Ctrl+字母 快捷键（足以关掉窗口），这是很难查的一类故障。</summary>
        public static void ReleaseModifiers()
        {
            ushort[] mods = { VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN };
            var arr = new INPUT[mods.Length];
            for (int i = 0; i < mods.Length; i++) arr[i] = KeyInput(mods[i], 0, KEYEVENTF_KEYUP);
            SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(INPUT)));
            System.Threading.Thread.Sleep(12);
        }
        /// <summary>组合键，如 "ctrl+shift+s" / "alt+tab" / "enter"。</summary>
        public static bool KeyCombo(string combo)
        {
            if (string.IsNullOrEmpty(combo)) return false;
            string[] parts = combo.ToLower().Split('+');
            var vks = new List<ushort>();
            for (int i = 0; i < parts.Length; i++)
            {
                ushort vk = VkOf(parts[i].Trim());
                if (vk == 0) return false;
                vks.Add(vk);
            }
            var downs = new List<INPUT>();
            for (int i = 0; i < vks.Count; i++) downs.Add(KeyInput(vks[i], 0, 0));
            var ups = new List<INPUT>();
            for (int i = vks.Count - 1; i >= 0; i--) ups.Add(KeyInput(vks[i], 0, KEYEVENTF_KEYUP));
            downs.AddRange(ups);
            var arr = downs.ToArray();
            return SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(INPUT))) > 0;
        }

        /// <summary>输入任意 Unicode 文本（支持中文）。</summary>
        public static bool TypeText(string text)
        {
            if (text == null) return false;
            var list = new List<INPUT>();
            foreach (char ch in text)
            {
                list.Add(KeyInput(0, ch, KEYEVENTF_UNICODE));
                list.Add(KeyInput(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
            }
            if (list.Count == 0) return true;
            var arr = list.ToArray();
            for (int i = 0; i < arr.Length; i += 64)
            {
                int n = Math.Min(64, arr.Length - i);
                var chunk = new INPUT[n];
                Array.Copy(arr, i, chunk, 0, n);
                SendInput((uint)n, chunk, Marshal.SizeOf(typeof(INPUT)));
                System.Threading.Thread.Sleep(8);
            }
            return true;
        }

        public static ushort VkOf(string name)
        {
            switch (name)
            {
                case "ctrl": case "control": return 0x11;
                case "shift": return 0x10;
                case "alt": return 0x12;
                case "win": return 0x5B;
                case "enter": case "return": return 0x0D;
                case "tab": return 0x09;
                case "esc": case "escape": return 0x1B;
                case "space": return 0x20;
                case "back": case "backspace": return 0x08;
                case "delete": case "del": return 0x2E;
                case "home": return 0x24;
                case "end": return 0x23;
                case "pageup": case "pgup": return 0x21;
                case "pagedown": case "pgdn": return 0x22;
                case "up": return 0x26;
                case "down": return 0x28;
                case "left": return 0x25;
                case "right": return 0x27;
                case "insert": return 0x2D;
                case "f1": return 0x70; case "f2": return 0x71; case "f3": return 0x72;
                case "f4": return 0x73; case "f5": return 0x74; case "f6": return 0x75;
                case "f7": return 0x76; case "f8": return 0x77; case "f9": return 0x78;
                case "f10": return 0x79; case "f11": return 0x7A; case "f12": return 0x7B;
            }
            if (name.Length == 1)
            {
                char c = char.ToUpperInvariant(name[0]);
                if (c >= 'A' && c <= 'Z') return (ushort)c;
                if (c >= '0' && c <= '9') return (ushort)c;
            }
            return 0;
        }
    }
}
