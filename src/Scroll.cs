// 滚轮控制。要点：WHEEL 事件发给「光标当前所在」的窗口，所以必须先把光标移到目标上；
// 直接对窗口 PostMessage(WM_MOUSEWHEEL) 对很多自绘/Qt 应用无效，故走真实输入。
using System;
using System.Collections.Generic;

namespace Lens
{
    public static class ScrollCmd
    {
        static string Arg(string[] a, string name, string def)
        {
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
            return def;
        }

        static bool Flag(string[] a, string name)
        {
            foreach (string s in a) if (s == name) return true;
            return false;
        }

        static bool Has(string[] a, string name)
        {
            foreach (string s in a) if (s == name) return true;
            return false;
        }

        public static int Run(string[] a)
        {
            int notches = int.Parse(Arg(a, "--delta", "-3"));
            int times = int.Parse(Arg(a, "--times", "1"));
            bool horizontal = Flag(a, "--horizontal");
            bool human = !Flag(a, "--instant");
            string wkey = Arg(a, "--window", null);
            var notes = new List<string>();

            int x = int.Parse(Arg(a, "--x", "0"));
            int y = int.Parse(Arg(a, "--y", "0"));

            if (wkey != null)
            {
                var wi = Win32.FindWindow(wkey);
                if (wi == null) { Console.Write(Program.WindowErrorJson(wkey)); return 3; }
                IntPtr h = new IntPtr(wi.Hwnd);

                if (!wi.Visible)
                {
                    Win32.ShowWindow(h, Win32.SW_SHOW);
                    System.Threading.Thread.Sleep(300);
                    var rc = new Win32.RECT();
                    rc.Left = wi.Frame.X; rc.Top = wi.Frame.Y; rc.Right = wi.Frame.Right; rc.Bottom = wi.Frame.Bottom;
                    var chk = Win32.BuildInfo(h, wi.Title, wi.Class, rc);
                    if (chk != null) wi = chk;
                    if (chk == null || !chk.Visible)
                    {
                        Console.Write("{\"ok\":false,\"code\":\"WINDOW_HIDDEN\",\"message\":\"目标窗口不可见（可能在托盘里）\",\"target\":"
                            + Program.DescribeWindow(wi.Hwnd) + ",\"hint\":\"先 focus 或手动把窗口显出来再滚\"}");
                        return 6;
                    }
                }

                Win32.ForceForeground(h);
                System.Threading.Thread.Sleep(160);
                // 置前之后必须重新读一次：最小化的窗口原坐标是 -32000，用旧值会算出屏幕外的点
                var rc2 = new Win32.RECT();
                rc2.Left = wi.Frame.X; rc2.Top = wi.Frame.Y; rc2.Right = wi.Frame.Right; rc2.Bottom = wi.Frame.Bottom;
                var fresh = Win32.BuildInfo(h, wi.Title, wi.Class, rc2);
                if (fresh != null) wi = fresh;

                // 没显式给坐标就滚窗口客户区中心
                if (!Has(a, "--x"))
                {
                    x = wi.Client.X + wi.Client.W / 2;
                    y = wi.Client.Y + wi.Client.H / 2;
                }
                if (x < 0 || y < 0)
                {
                    Console.Write("{\"ok\":false,\"code\":\"BAD_POSITION\",\"message\":\"窗口位置无效（可能仍处于最小化）\","
                        + "\"target\":" + Program.DescribeWindow(wi.Hwnd) + ",\"hint\":\"先用 focus 恢复窗口再滚\"}");
                    return 6;
                }

                long cover = Win32.TopWindowAt(x, y);
                if (cover != 0 && cover != wi.Hwnd && !Flag(a, "--force"))
                {
                    Console.Write("{\"ok\":false,\"code\":\"TARGET_COVERED\",\"message\":\"滚动位置被其他窗口遮挡，已拒绝（滚错窗口会误操作）\","
                        + "\"x\":" + x + ",\"y\":" + y + ",\"target\":" + Program.DescribeWindow(wi.Hwnd)
                        + ",\"coveredBy\":" + Program.DescribeWindow(cover)
                        + ",\"hint\":\"把目标窗口置前再滚；确认无误可加 --force\"}");
                    return 6;
                }
                notes.Add("mode=foreground coveredBy=" + cover);
            }

            bool ok = true;
            int n = Math.Max(1, times);
            for (int i = 0; i < n; i++)
            {
                ok = Human.ScrollAt(x, y, notches, horizontal, human) && ok;
                if (i < n - 1) System.Threading.Thread.Sleep(120);
            }

            Console.Write("{\"ok\":" + Json.Bool(ok) + ",\"x\":" + x + ",\"y\":" + y
                + ",\"delta\":" + notches + ",\"times\":" + n
                + ",\"axis\":" + Json.Str(horizontal ? "horizontal" : "vertical")
                + ",\"notes\":" + Json.StrArray(notes) + "}");
            return ok ? 0 : 5;
        }
    }
}
