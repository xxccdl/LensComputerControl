// 剪贴板粘贴 + 窗口管理。
//
// 为什么需要粘贴：逐字输入长文本又慢又脆——中间任何一次焦点漂移都会把后半句打进别的窗口。
// 粘贴是一次原子操作，成功率高一到两个数量级。代价是临时占用剪贴板，所以用完要还原。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Lens
{
    public static class Clip
    {
        // ---------- 剪贴板 ----------
        const uint CF_UNICODETEXT = 13;
        const uint GMEM_MOVEABLE = 0x0002;

        [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool CloseClipboard();
        [DllImport("user32.dll")] static extern bool EmptyClipboard();
        [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint fmt);
        [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint fmt, IntPtr mem);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr mem);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr mem);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr mem);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalSize(IntPtr mem);

        /// <summary>读剪贴板文本；打不开或不是文本就返回 null。</summary>
        public static string GetText()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (!OpenClipboard(IntPtr.Zero)) { System.Threading.Thread.Sleep(30); continue; }
                try
                {
                    IntPtr h = GetClipboardData(CF_UNICODETEXT);
                    if (h == IntPtr.Zero) return null;
                    IntPtr p = GlobalLock(h);
                    if (p == IntPtr.Zero) return null;
                    try { return Marshal.PtrToStringUni(p); }
                    finally { GlobalUnlock(h); }
                }
                finally { CloseClipboard(); }
            }
            return null;
        }

        /// <summary>写剪贴板文本。剪贴板被别的程序占用时会重试几次。</summary>
        public static bool SetText(string text)
        {
            if (text == null) text = "";
            for (int attempt = 0; attempt < 6; attempt++)
            {
                if (!OpenClipboard(IntPtr.Zero)) { System.Threading.Thread.Sleep(40); continue; }
                try
                {
                    if (!EmptyClipboard()) return false;
                    int bytes = (text.Length + 1) * 2;
                    IntPtr mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                    if (mem == IntPtr.Zero) return false;
                    IntPtr p = GlobalLock(mem);
                    if (p == IntPtr.Zero) { GlobalFree(mem); return false; }
                    try
                    {
                        var buf = Encoding.Unicode.GetBytes(text + "\0");
                        Marshal.Copy(buf, 0, p, buf.Length);
                    }
                    finally { GlobalUnlock(mem); }
                    if (SetClipboardData(CF_UNICODETEXT, mem) == IntPtr.Zero)
                    {
                        GlobalFree(mem);
                        return false;
                    }
                    // 成功后所有权归系统，不能再 free
                    return true;
                }
                finally { CloseClipboard(); }
            }
            return false;
        }

        static void Mark(string step, IntPtr h)
        {
            try
            {
                string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lens-hud");
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                string line = DateTime.Now.ToString("HH:mm:ss.fff") + " paste/" + step
                    + " visible=" + Win32.IsWindowVisible(h)
                    + " fg=" + Win32.GetForegroundWindow().ToInt64()
                    + " target=" + h.ToInt64() + "\r\n";
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "input.log"), line);
            }
            catch { }
        }
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

        /// <summary>lens paste --window X --text "…"：临时替换剪贴板 → Ctrl+V → 还原。</summary>
        public static int CmdPaste(string[] a)
        {
            string text = Arg(a, "--text", null);
            if (text == null) { Console.Write("{\"ok\":false,\"code\":\"BAD_ARGS\",\"message\":\"缺少 --text\"}"); return 2; }
            string wkey = Arg(a, "--window", null);
            bool keep = Flag(a, "--keep");      // 不还原原剪贴板
            var notes = new List<string>();

            IntPtr h = IntPtr.Zero;
            if (wkey != null)
            {
                var wi = Win32.FindWindow(wkey);
                if (wi == null) { Console.Write(Program.WindowErrorJson(wkey)); return 3; }
                h = new IntPtr(wi.Hwnd);
                if (!wi.Visible)
                {
                    Win32.ShowWindow(h, Win32.SW_SHOW);
                    System.Threading.Thread.Sleep(280);
                }
                Win32.ForceForeground(h);
                System.Threading.Thread.Sleep(140);
                notes.Add("mode=foreground");
                Mark("after-focus", h);
            }

            string old = keep || Flag(a, "--instant") ? null : GetText();
            if (!SetText(text))
            {
                Console.Write("{\"ok\":false,\"code\":\"CLIPBOARD_BUSY\",\"message\":\"剪贴板被占用，写不进去\","
                    + "\"hint\":\"重试一次；或者改用 computer_act 的 text 逐字输入\"}");
                return 7;
            }
            System.Threading.Thread.Sleep(60);
            Mark("after-setclip", h);
            bool ok = Human.KeyCombo("ctrl+v", !Flag(a, "--instant"));
            Mark("after-ctrlv", h);
            System.Threading.Thread.Sleep(120);

            if (!keep && old != null)
            {
                // 还原原剪贴板：不还原会把用户正在复制的东西弄丢
                if (SetText(old)) notes.Add("clipboard restored");
                else notes.Add("clipboard restore failed (left new text)");
            }

            Console.Write("{\"ok\":" + Json.Bool(ok) + ",\"pasted\":" + text.Length + ","
                + "\"kept\":" + Json.Bool(keep)
                + ",\"notes\":" + Json.StrArray(notes) + "}");
            return ok ? 0 : 5;
        }
    }
}
