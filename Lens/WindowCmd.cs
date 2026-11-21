// 窗口管理：最大化 / 最小化 / 还原 / 关闭 / 移动 / 缩放 / 置顶 / 贴边。
// 自动化里经常需要先把窗口摆到确定的位置和大小，否则坐标和截图都会随用户拖动而变。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Lens
{
    public static class WindowCmd
    {
        [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_SHOWWINDOW = 0x0040;
        const uint WM_CLOSE = 0x0010;

        static string Arg(string[] a, string name, string def)
        {
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
            return def;
        }

        static bool Has(string[] a, string name)
        {
            foreach (string s in a) if (s == name) return true;
            return false;
        }

        public static int Run(string[] a)
        {
            string key = Arg(a, "--window", null);
            string act = Arg(a, "--action", "info");
            if (key == null) { Console.Write("{\"ok\":false,\"code\":\"BAD_ARGS\",\"message\":\"缺少 --window\"}"); return 2; }

            var wi = Win32.FindWindow(key);
            if (wi == null) { Console.Write(Program.WindowErrorJson(key)); return 3; }
            IntPtr h = new IntPtr(wi.Hwnd);
            var notes = new List<string>();

            switch (act)
            {
                case "maximize":
                    Win32.ShowWindow(h, 3);   // SW_MAXIMIZE
                    notes.Add("maximize");
                    break;
                case "minimize":
                    Win32.ShowWindow(h, 6);   // SW_MINIMIZE
                    notes.Add("minimize");
                    break;
                case "restore":
                    Win32.ShowWindow(h, 9);   // SW_RESTORE
                    notes.Add("restore");
                    break;
                case "close":
                    PostMessageW(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    notes.Add("close requested (WM_CLOSE，不是强杀)");
                    break;
                case "move":
                {
                    int x = int.Parse(Arg(a, "--x", wi.Frame.X.ToString()));
                    int y = int.Parse(Arg(a, "--y", wi.Frame.Y.ToString()));
                    int w = int.Parse(Arg(a, "--w", wi.Frame.W.ToString()));
                    int hh = int.Parse(Arg(a, "--h", wi.Frame.H.ToString()));
                    if (!MoveWindow(h, x, y, w, hh, true))
                    {
                        Console.Write("{\"ok\":false,\"code\":\"MOVE_FAILED\",\"message\":\"MoveWindow 失败\",\"target\":" + Program.DescribeWindow(wi.Hwnd) + "}");
                        return 5;
                    }
                    notes.Add("moved to " + x + "," + y + " " + w + "x" + hh);
                    break;
                }
                case "topmost":
                    SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                    notes.Add("topmost on");
                    break;
                case "untopmost":
                    SetWindowPos(h, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                    notes.Add("topmost off");
                    break;
                case "info":
                    notes.Add("read only");
                    break;
                default:
                    Console.Write("{\"ok\":false,\"code\":\"BAD_ACTION\",\"message\":" + Json.Str("未知 action: " + act)
                        + ",\"hint\":\"maximize | minimize | restore | close | move | topmost | untopmost | info\"}");
                    return 2;
            }

            System.Threading.Thread.Sleep(act == "minimize" || act == "maximize" || act == "restore" ? 260 : 80);

            // 动作后重新读一次，返回真实状态（用户最想知道的是"现在到底什么样"）
            var after = Win32.FindWindow(wi.Hwnd.ToString());
            if (after == null)
            {
                Console.Write("{\"ok\":true,\"warning\":\"window_gone\",\"message\":\"动作已执行，窗口随后关闭\","
                    + "\"notes\":" + Json.StrArray(notes) + "}");
                return 0;
            }
            Console.Write("{\"ok\":true,\"action\":" + Json.Str(act) + ",\"window\":" + after.ToJson()
                + ",\"notes\":" + Json.StrArray(notes) + "}");
            return 0;
        }
    }
}
