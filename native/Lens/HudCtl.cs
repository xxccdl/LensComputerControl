// HUD 的控制端：主引擎每个命令都会在这里"说一句"当前在干什么。
// 用文本文件通信而不是端口/IPC：无需常驻服务、无端口冲突、出问题能直接看文件。
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Lens
{
    public static class HudCtl
    {
        public static string Dir
        {
            get { return Path.Combine(Path.GetTempPath(), "lens-hud"); }
        }

        static string StatePath { get { return Path.Combine(Dir, "state.txt"); } }
        static string FlagPath { get { return Path.Combine(Dir, "enabled"); } }
        static string PidPath { get { return Path.Combine(Dir, "pid.txt"); } }

        public static bool Enabled
        {
            get { try { return File.Exists(FlagPath); } catch { return false; } }
        }

        public static void SetEnabled(bool on)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                if (on) File.WriteAllText(FlagPath, "1", Encoding.UTF8);
                else if (File.Exists(FlagPath)) File.Delete(FlagPath);
            }
            catch { }
        }

        static Process FindRunning()
        {
            try
            {
                if (!File.Exists(PidPath)) return null;
                int pid;
                if (!int.TryParse(File.ReadAllText(PidPath).Trim(), out pid)) return null;
                Process p = Process.GetProcessById(pid);
                if (p.HasExited) return null;
                return p;
            }
            catch { return null; }
        }

        /// <summary>确保 lenshud.exe 在跑。找不到就拉起（同目录）。</summary>
        public static bool EnsureStarted()
        {
            try
            {
                if (FindRunning() != null) return true;
                string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lenshud.exe");
                if (!File.Exists(exe)) return false;
                Directory.CreateDirectory(Dir);
                var psi = new ProcessStartInfo(exe);
                // 必须用 shell 启动：直接 Start 会让子进程继承父进程的 stdio 管道，
                // 调用方（尤其是脚本/基准）会一直等这个常驻进程结束而挂住。
                psi.UseShellExecute = true;
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                Process p = Process.Start(psi);
                if (p == null) return false;
                try { File.WriteAllText(PidPath, p.Id.ToString(), Encoding.UTF8); } catch { }
                System.Threading.Thread.Sleep(180);
                return true;
            }
            catch { return false; }
        }

        /// <summary>说一句"我正在做什么"。只有显式开启（lens hud start）时才生效，
        /// 避免把基准测试和批量调用拖慢。</summary>
        public static void Say(string text, string title)
        {
            if (!Enabled) return;
            try
            {
                EnsureStarted();
                Directory.CreateDirectory(Dir);
                long ts = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
                var sb = new StringBuilder();
                sb.Append("v=1\n");
                sb.Append("title=").Append(string.IsNullOrEmpty(title) ? "Lens AI 正在操作你的电脑" : title).Append('\n');
                sb.Append("text=").Append(text ?? "").Append('\n');
                sb.Append("ts=").Append(ts).Append('\n');
                File.WriteAllText(StatePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        public static void Stop()
        {
            try
            {
                SetEnabled(false);
                if (File.Exists(StatePath)) File.Delete(StatePath);
                Process p = FindRunning();
                if (p != null) { try { p.Kill(); } catch { } }
                if (File.Exists(PidPath)) File.Delete(PidPath);
            }
            catch { }
        }

        public static string Status()
        {
            Process p = FindRunning();
            return "{\"enabled\":" + Json.Bool(Enabled)
                + ",\"running\":" + Json.Bool(p != null)
                + ",\"pid\":" + (p != null ? p.Id.ToString() : "0")
                + ",\"dir\":" + Json.Str(Dir) + "}";
        }

        /// <summary>每个命令对应一句人话，显示在胶囊右边。</summary>
        public static string LabelFor(string cmd)
        {
            switch (cmd)
            {
                case "scan": return "正在查看屏幕";
                case "detimg": return "正在识别图像";
                case "click": return "正在点击";
                case "drag": return "正在拖动";
                case "type": return "正在输入";
                case "key": return "正在按键";
                case "focus": return "正在切换到目标窗口";
                case "vision": return "正在定位界面元素";
                case "grid": case "zoom": return "正在量取坐标";
                case "fill": return "正在填写表格";
                case "read": return "正在读取内容";
                case "wait": return "正在等待窗口";
                case "run": return "正在执行多步任务";
                case "verify": return "正在核验结果";
                case "capture": case "cap": return "正在截取画面";
                default: return "正在工作";
            }
        }
    }
}
