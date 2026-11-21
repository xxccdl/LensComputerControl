// 人类化输入：贝塞尔轨迹 + 缓动 + 微抖动 + 驻留，拖动，按键间隔抖动。
// 目标不是"骗过反作弊"，而是让操作在节奏和轨迹上接近真人，避免机械式的瞬移瞬点。
using System;
using System.Collections.Generic;

namespace Lens
{
    public static class Human
    {
        static readonly Random Rnd = new Random(Environment.TickCount);

        static int Jitter(int span)
        {
            if (span <= 0) return 0;
            return Rnd.Next(-span, span + 1);
        }

        static double Ease(double t)
        {
            // easeInOutCubic：起手慢、中途快、收尾稳，接近人手
            if (t < 0.5) return 4 * t * t * t;
            double f = -2 * t + 2;
            return 1 - (f * f * f) / 2;
        }

        /// <summary>沿二次贝塞尔把人手一样移到目标点。返回实际落点（含抖动）。</summary>
        public static void MoveTo(int x, int y, int durationMs, int jitter)
        {
            Win32.POINT start;
            if (!Win32.GetCursorPos(out start)) { Win32.SetCursorPos(x, y); return; }
            int x0 = start.X, y0 = start.Y;
            int dx = x - x0, dy = y - y0;
            double dist = Math.Sqrt((double)dx * dx + (double)dy * dy);
            if (dist < 2) { Win32.SetCursorPos(x, y); return; }

            if (durationMs <= 0)
                durationMs = (int)Math.Max(120, Math.Min(900, 110 + dist * 0.55 + Rnd.Next(0, 90)));

            // 控制点：垂直于位移方向偏移，制造自然的弧线
            double nx = -dy / Math.Max(1.0, dist), ny = dx / Math.Max(1.0, dist);
            double bow = dist * (0.04 + Rnd.NextDouble() * 0.10) * (Rnd.Next(2) == 0 ? 1 : -1);
            int cx = (int)((x0 + x) / 2.0 + nx * bow);
            int cy = (int)((y0 + y) / 2.0 + ny * bow);

            int steps = Math.Max(8, Math.Min(90, durationMs / 8));
            int sleepTotal = 0;
            for (int i = 1; i <= steps; i++)
            {
                double t = Ease((double)i / steps);
                double one = 1 - t;
                double bx = one * one * x0 + 2 * one * t * cx + t * t * x;
                double by = one * one * y0 + 2 * one * t * cy + t * t * y;
                int jx = (i == steps) ? 0 : Jitter(jitter);
                int jy = (i == steps) ? 0 : Jitter(jitter);
                Win32.SetCursorPos((int)Math.Round(bx) + jx, (int)Math.Round(by) + jy);
                int wait = Math.Max(1, durationMs / steps + Rnd.Next(-2, 3));
                System.Threading.Thread.Sleep(wait);
                sleepTotal += wait;
            }
            // 收尾微校正：真人最后一次移动后常有一个小回摆
            if (jitter > 0 && sleepTotal > 0)
            {
                Win32.SetCursorPos(x + Jitter(1), y + Jitter(1));
                System.Threading.Thread.Sleep(Rnd.Next(20, 55));
                Win32.SetCursorPos(x, y);
            }
            else Win32.SetCursorPos(x, y);
        }

        /// <summary>人类化单击/双击。human=false 时退化为原来的瞬移瞬点。</summary>
        public static bool ClickAt(int x, int y, string button, int count, bool human)
        {
            uint down = Win32.MOUSEEVENTF_LEFTDOWN, up = Win32.MOUSEEVENTF_LEFTUP;
            if (button == "right") { down = Win32.MOUSEEVENTF_RIGHTDOWN; up = Win32.MOUSEEVENTF_RIGHTUP; }
            else if (button == "middle") { down = Win32.MOUSEEVENTF_MIDDLEDOWN; up = Win32.MOUSEEVENTF_MIDDLEUP; }

            if (!human)
            {
                Win32.SetCursorPos(x, y);
                System.Threading.Thread.Sleep(30);
                for (int i = 0; i < count; i++)
                {
                    Win32.mouse_event(down, 0, 0, 0, IntPtr.Zero);
                    System.Threading.Thread.Sleep(20);
                    Win32.mouse_event(up, 0, 0, 0, IntPtr.Zero);
                    if (i < count - 1) System.Threading.Thread.Sleep(60);
                }
                return true;
            }

            MoveTo(x, y, 0, 1);
            System.Threading.Thread.Sleep(Rnd.Next(55, 150));          // 落点后的驻留
            for (int i = 0; i < count; i++)
            {
                Win32.mouse_event(down, 0, 0, 0, IntPtr.Zero);
                System.Threading.Thread.Sleep(Rnd.Next(45, 95));        // 按下时长
                Win32.mouse_event(up, 0, 0, 0, IntPtr.Zero);
                if (i < count - 1) System.Threading.Thread.Sleep(Rnd.Next(40, 85));
            }
            return true;
        }

        /// <summary>拖动：(x1,y1) 压住 → 沿弧线 → (x2,y2) 松开。用于画框、拖放、调整大小。</summary>
        public static bool Drag(int x1, int y1, int x2, int y2, int durationMs, bool human, string button)
        {
            uint down = Win32.MOUSEEVENTF_LEFTDOWN, up = Win32.MOUSEEVENTF_LEFTUP;
            if (button == "right") { down = Win32.MOUSEEVENTF_RIGHTDOWN; up = Win32.MOUSEEVENTF_RIGHTUP; }

            if (!human)
            {
                Win32.SetCursorPos(x1, y1);
                System.Threading.Thread.Sleep(30);
                Win32.mouse_event(down, 0, 0, 0, IntPtr.Zero);
                System.Threading.Thread.Sleep(40);
                int steps = 24;
                for (int i = 1; i <= steps; i++)
                {
                    Win32.SetCursorPos(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps);
                    System.Threading.Thread.Sleep(8);
                }
                System.Threading.Thread.Sleep(40);
                Win32.mouse_event(up, 0, 0, 0, IntPtr.Zero);
                return true;
            }

            if (durationMs <= 0) durationMs = 700 + Rnd.Next(0, 300);
            MoveTo(x1, y1, 0, 1);
            System.Threading.Thread.Sleep(Rnd.Next(70, 160));
            Win32.mouse_event(down, 0, 0, 0, IntPtr.Zero);
            System.Threading.Thread.Sleep(Rnd.Next(60, 130));           // 压住后的小停顿，很多程序靠它判定 drag 开始
            MoveTo(x2, y2, durationMs, 0);
            System.Threading.Thread.Sleep(Rnd.Next(80, 180));
            Win32.mouse_event(up, 0, 0, 0, IntPtr.Zero);
            return true;
        }

        /// <summary>逐字输入，间隔抖动；遇到空格/标点会多停一下，像人在分段思考。</summary>
        public static bool TypeText(string text, bool human)
        {
            if (text == null) return false;
            Win32.ReleaseModifiers();            // 防呆：上次卡住的 Ctrl 会让输入变成快捷键
            if (!human) return Win32.TypeText(text);

            foreach (char ch in text)
            {
                var one = new string(ch, 1);
                Win32.TypeText(one);
                int wait;
                if (ch == ' ' || ch == ',' || ch == '.' || ch == ';' || ch == '，' || ch == '。' || ch == '\n')
                    wait = Rnd.Next(120, 260);
                else wait = Rnd.Next(32, 105);
                if (Rnd.Next(0, 40) == 0) wait += Rnd.Next(150, 420);  // 偶发的长停顿
                System.Threading.Thread.Sleep(wait);
            }
            return true;
        }

        /// <summary>滚轮。WHEEL 事件发给「光标底下」的窗口，所以必须先把光标移到位。
        /// notches 为正 = 向上/向左滚，为负 = 向下/向右滚；逐格发送并带间隔，像人在滚。</summary>
        public static bool ScrollAt(int x, int y, int notches, bool horizontal, bool human)
        {
            if (human) MoveTo(x, y, 150, 1);
            else Win32.SetCursorPos(x, y);
            System.Threading.Thread.Sleep(human ? Rnd.Next(55, 120) : 20);

            uint flag = horizontal ? Win32.MOUSEEVENTF_HWHEEL : Win32.MOUSEEVENTF_WHEEL;
            int sign = notches >= 0 ? 1 : -1;
            int total = Math.Abs(notches);
            for (int i = 0; i < total; i++)
            {
                Win32.mouse_event(flag, 0, 0, unchecked((uint)(120 * sign)), IntPtr.Zero);
                if (human) System.Threading.Thread.Sleep(Rnd.Next(40, 95));
            }
            return true;
        }

        /// <summary>组合键：按下有先后、释放有先后，不是一锅端。</summary>
        public static bool KeyCombo(string combo, bool human)
        {
            if (!human) return Win32.KeyCombo(combo);
            Win32.ReleaseModifiers();
            string[] parts = combo.ToLower().Split('+');
            var vks = new List<ushort>();
            foreach (string p in parts)
            {
                ushort vk = Win32.VkOf(p.Trim());
                if (vk == 0) return Win32.KeyCombo(combo);
                vks.Add(vk);
            }
            foreach (ushort vk in vks)
            {
                Win32.SendKeyStruct(vk, false);
                System.Threading.Thread.Sleep(Rnd.Next(18, 45));
            }
            System.Threading.Thread.Sleep(Rnd.Next(45, 95));
            for (int i = vks.Count - 1; i >= 0; i--)
            {
                Win32.SendKeyStruct(vks[i], true);
                System.Threading.Thread.Sleep(Rnd.Next(15, 40));
            }
            return true;
        }
    }
}
