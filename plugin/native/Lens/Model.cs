// Lens 共享契约。C# 5 语法（老 csc 只支持到 C# 5）：禁止表达式体成员、字符串插值、?.、nameof、out var、模式匹配。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace Lens
{
    [Serializable]
    public struct Rect
    {
        public int X, Y, W, H;
        public Rect(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
        public int Right { get { return X + W; } }
        public int Bottom { get { return Y + H; } }
        public int Cx { get { return X + W / 2; } }
        public int Cy { get { return Y + H / 2; } }
        public bool IsEmpty { get { return W <= 0 || H <= 0; } }

        public bool Contains(int px, int py)
        {
            return px >= X && px < Right && py >= Y && py < Bottom;
        }

        public Rect Inflate(int d)
        {
            return new Rect(X - d, Y - d, W + 2 * d, H + 2 * d);
        }

        public Rect Intersect(Rect o)
        {
            int x1 = Math.Max(X, o.X), y1 = Math.Max(Y, o.Y);
            int x2 = Math.Min(Right, o.Right), y2 = Math.Min(Bottom, o.Bottom);
            if (x2 <= x1 || y2 <= y1) return new Rect(0, 0, 0, 0);
            return new Rect(x1, y1, x2 - x1, y2 - y1);
        }

        public int Area { get { return Math.Max(0, W) * Math.Max(0, H); } }

        public double Iou(Rect o)
        {
            Rect i = Intersect(o);
            int un = Area + o.Area - i.Area;
            if (un <= 0) return 0;
            return (double)i.Area / un;
        }

        public string ToJson()
        {
            return "{\"x\":" + X + ",\"y\":" + Y + ",\"w\":" + W + ",\"h\":" + H + "}";
        }

        public static Rect FromRectangle(Rectangle r)
        {
            return new Rect(r.X, r.Y, r.Width, r.Height);
        }
    }

    /// <summary>一个"可点的东西"。坐标永远由代码算出，模型只能引用 Id。</summary>
    public class Candidate
    {
        public int Id;
        public string Channel;   // uia | msaa | ocr | solid | table | template | cursor | focus | jtree
        public string Kind;      // button | text | cell | input | icon | tab | menuitem | row | col | unknown
        public Rect Box;
        public string Text;
        public string Color;     // "#RRGGBB"
        public double Score;     // 0..1

        public Candidate() { Score = 0.5; }

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"id\":").Append(Id);
            sb.Append(",\"channel\":").Append(Json.Str(Channel));
            sb.Append(",\"kind\":").Append(Json.Str(Kind));
            sb.Append(",\"box\":").Append(Box.ToJson());
            sb.Append(",\"center\":{\"x\":").Append(Box.Cx).Append(",\"y\":").Append(Box.Cy).Append("}");
            if (Text != null) sb.Append(",\"text\":").Append(Json.Str(Text));
            if (Color != null) sb.Append(",\"color\":").Append(Json.Str(Color));
            sb.Append(",\"score\":").Append(Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append("}");
            return sb.ToString();
        }
    }

    public class WindowInfo
    {
        public long Hwnd;
        public string Title;
        public string Class;
        public int Pid;
        public string Process;
        public int Dpi;
        public Rect Frame;    // DWM 扩展边框（真实可见范围，物理像素）
        public Rect Client;   // 客户区（物理像素）
        public bool Visible;
        public bool Minimized;
        public bool Max;
        /// <summary>宿主自身（DSH）的窗口：绝不能当目标。</summary>
        public bool IsHost;

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"hwnd\":").Append(Hwnd);
            sb.Append(",\"title\":").Append(Json.Str(Title));
            sb.Append(",\"class\":").Append(Json.Str(Class));
            sb.Append(",\"pid\":").Append(Pid);
            sb.Append(",\"process\":").Append(Json.Str(Process));
            sb.Append(",\"dpi\":").Append(Dpi);
            sb.Append(",\"frame\":").Append(Frame.ToJson());
            sb.Append(",\"client\":").Append(Client.ToJson());
            sb.Append(",\"visible\":").Append(Json.Bool(Visible));
            sb.Append(",\"minimized\":").Append(Json.Bool(Minimized));
            sb.Append(",\"maximized\":").Append(Json.Bool(Max));
            sb.Append(",\"isHost\":").Append(Json.Bool(IsHost));
            sb.Append("}");
            return sb.ToString();
        }
    }

    /// <summary>窗口解析结果：不做"猜第一个"，歧义时把候选还给调用方。</summary>
    public class WindowResolve
    {
        public WindowInfo Best;
        public List<WindowInfo> Matches = new List<WindowInfo>();
        public bool Ambiguous;
        /// <summary>匹配到的窗口当前是隐藏的（最小化到托盘），需要还原。</summary>
        public bool WasHidden;
        public string Error;    // NOT_FOUND | AMBIGUOUS_WINDOW | WINDOW_GONE
        public string Message;

        public string ToJson()
        {
            var sb = new System.Text.StringBuilder("{");
            sb.Append("\"error\":").Append(Json.Str(Error));
            sb.Append(",\"message\":").Append(Json.Str(Message));
            sb.Append(",\"ambiguous\":").Append(Json.Bool(Ambiguous));
            sb.Append(",\"wasHidden\":").Append(Json.Bool(WasHidden));
            sb.Append(",\"candidates\":[");
            int n = 0;
            foreach (var w in Matches)
            {
                if (n++ >= 8) break;
                if (n > 1) sb.Append(",");
                sb.Append(w.ToJson());
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }

    /// <summary>探测得出的能力护照。</summary>
    public class Capability
    {
        public bool Capturable;      // 能否拿到非黑图
        public string CaptureMethod; // printwindow | bitblt | none
        public double NonBlackRatio;
        public bool UiaUsable;       // UIA 是否有可用元素
        public int UiaNodes;
        public bool InvokeSupported;
        public bool MsaaUsable;
        public bool OcrUsable;
        public int OcrWords;
        public bool TableDetected;
        public string Level;         // L1 / L2 / L3 / L4 / guide
        public List<string> Notes = new List<string>();

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"capturable\":").Append(Json.Bool(Capturable));
            sb.Append(",\"captureMethod\":").Append(Json.Str(CaptureMethod));
            sb.Append(",\"nonBlackRatio\":").Append(NonBlackRatio.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"uiaUsable\":").Append(Json.Bool(UiaUsable));
            sb.Append(",\"uiaNodes\":").Append(UiaNodes);
            sb.Append(",\"invokeSupported\":").Append(Json.Bool(InvokeSupported));
            sb.Append(",\"msaaUsable\":").Append(Json.Bool(MsaaUsable));
            sb.Append(",\"ocrUsable\":").Append(Json.Bool(OcrUsable));
            sb.Append(",\"ocrWords\":").Append(OcrWords);
            sb.Append(",\"tableDetected\":").Append(Json.Bool(TableDetected));
            sb.Append(",\"level\":").Append(Json.Str(Level));
            sb.Append(",\"notes\":").Append(Json.StrArray(Notes));
            sb.Append("}");
            return sb.ToString();
        }
    }

    /// <summary>所有检测器共享的扫描上下文。检测器只管往 Candidates 里加，编号与去重由 Scanner 统一做。</summary>
    public class ScanContext
    {
        public WindowInfo Window;
        public IntPtr Hwnd;
        public Bitmap Image;          // 窗口图像（物理像素，原点=窗口左上角）
        public int OffsetX;           // 内容区左上角在屏幕物理坐标中的位置
        public int OffsetY;
        /// <summary>DPI-unaware 窗口会被系统按比例拉伸，而 PrintWindow 以逻辑尺寸绘制。
        /// 这里的缩放把「图像像素」映射到「屏幕物理像素」，正常窗口恒为 1。</summary>
        public double ScaleX = 1.0;
        public double ScaleY = 1.0;
        /// <summary>原始截图里真正有内容的那块（去掉拉伸/边框留下的空白边）。</summary>
        public Rect Content;
        public List<Candidate> Candidates = new List<Candidate>();
        public List<string> Notes = new List<string>();
        public Capability Cap = new Capability();
        /// <summary>合成进图像的 owner 弹出窗口（菜单/下拉/Tooltip）。</summary>
        public List<WindowInfo> Popups = new List<WindowInfo>();

        /// <summary>加一个候选。box 用图像局部坐标（相对窗口左上角）。</summary>
        public Candidate Add(string channel, string kind, Rect localBox, string text, string color, double score)
        {
            if (localBox.IsEmpty) return null;
            var c = new Candidate();
            c.Channel = channel;
            c.Kind = kind;
            c.Box = localBox;
            c.Text = text;
            c.Color = color;
            c.Score = score;
            Candidates.Add(c);
            return c;
        }

        /// <summary>屏幕物理坐标版的 box（已处理 DPI-unaware 窗口的缩放）。</summary>
        public Rect ToScreen(Rect local)
        {
            int x = OffsetX + (int)Math.Round(local.X * ScaleX);
            int y = OffsetY + (int)Math.Round(local.Y * ScaleY);
            int w = (int)Math.Round(local.W * ScaleX);
            int h = (int)Math.Round(local.H * ScaleY);
            return new Rect(x, y, w, h);
        }
    }

    public static class Json
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append("\"");
            return sb.ToString();
        }

        public static string Bool(bool b) { return b ? "true" : "false"; }

        public static string StrArray(List<string> items)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(Str(items[i]));
            }
            sb.Append("]");
            return sb.ToString();
        }

        public static string Hex(System.Drawing.Color c)
        {
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }
    }
}
