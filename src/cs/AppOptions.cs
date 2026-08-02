// AppOptions.cs - 命令行参数解析（静默模式 / 快速启动）。
using System;

namespace RemoteControl
{
    public enum Role
    {
        None,
        Host,    // 被控端
        Viewer,  // 控制端
    }

    /// <summary>
    /// 启动命令行参数。例：
    ///   RemoteControl.exe --host --room 1234 --password abc --hide --autostart
    ///   RemoteControl.exe --viewer --room 1234 --password abc --autostart
    /// 支持 --key value 与 --key=value 两种写法。
    /// </summary>
    public sealed class AppOptions
    {
        public Role Role = Role.None;
        public string Server = null;          // null => 使用窗体默认地址
        public int? Port = null;
        public string Room = null;
        public string Password = null;
        public int? Fps = null;
        public int? Quality = null;            // 0 流畅 / 1 均衡 / 2 清晰
        public int? Monitor = null;            // 显示器序号（0 起）
        public bool ViewOnly = false;
        public bool NoAdapt = false;           // 关闭自适应码率/分辨率
        public bool NoComp = false;            // 关闭链路压缩
        public bool NoP2P = false;             // 关闭 P2P 直连
        public bool NoClip = false;            // 关闭剪贴板同步
        public bool Hide = false;              // 静默：启动后最小化到托盘（被控端）
        public bool AutoStart = false;         // 启动后立即开始共享 / 连接
        public bool Retry = false;             // 建房间失败后每 30 秒定时重试，直到成功
        public bool Advanced = false;          // 显示中继服务器 / 端口输入框
        public bool ShowHelp = false;
        public string ReadOverlay = null;       // --read-overlay <exe>
        public string ApiBase = null;          // 后端 HTTP API 基址，默认 http://127.0.0.1:21363

        public static AppOptions Parse(string[] args)
        {
            var o = new AppOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string key = null, val = null;

                if (a.StartsWith("--"))
                {
                    key = a.Substring(2);
                    int eq = key.IndexOf('=');
                    if (eq >= 0) { val = key.Substring(eq + 1); key = key.Substring(0, eq); }
                }
                else if (a.StartsWith("-") && a.Length > 1 && !IsNum(a))
                {
                    key = a.Substring(1);
                }
                else continue;

                switch (key.ToLowerInvariant())
                {
                    case "host":            o.Role = Role.Host; break;
                    case "viewer":
                    case "client":          o.Role = Role.Viewer; break;

                    case "server":
                    case "s":               o.Server = Next(ref i, args, val); break;
                    case "port":
                    case "p":               o.Port = ParseInt(Next(ref i, args, val)); break;
                    case "room":
                    case "r":               o.Room = Next(ref i, args, val); break;
                    case "password":
                    case "pass":
                    case "pw":              o.Password = Next(ref i, args, val); break;
                    case "fps":             o.Fps = ParseInt(Next(ref i, args, val)); break;
                    case "quality":
                    case "q":               o.Quality = ParseQuality(Next(ref i, args, val)); break;
                    case "monitor":
                    case "m":               o.Monitor = ParseInt(Next(ref i, args, val)); break;

                    case "viewonly":
                    case "view-only":       o.ViewOnly = true; break;
                    case "noadapt":         o.NoAdapt = true; break;
                    case "nocomp":          o.NoComp = true; break;
                    case "nop2p":           o.NoP2P = true; break;
                    case "noclip":          o.NoClip = true; break;

                    case "hide":
                    case "silent":
                    case "min":             o.Hide = true; break;
                    case "autostart":
                    case "start":
                    case "auto":            o.AutoStart = true; break;
                    case "retry":           o.Retry = true; break;
                    case "adv":
                    case "advanced":        o.Advanced = true; break;

                    case "api":             o.ApiBase = Next(ref i, args, val); break;
                    case "read-overlay":
                        o.ReadOverlay = Next(ref i, args, val);
                        break;

                    case "help":
                    case "h":
                    case "?":               o.ShowHelp = true; break;
                    default: break;
                }
            }
            return o;
        }

        private static string Next(ref int i, string[] args, string val)
        {
            if (val != null) return val;
            if (i + 1 < args.Length) return args[++i];
            return "";
        }

        private static bool IsNum(string s) => int.TryParse(s, out _);

        private static int? ParseInt(string s)
            => int.TryParse(s, out int v) ? (int?)v : null;

        private static int? ParseQuality(string s)
        {
            if (int.TryParse(s, out int v) && v >= 0 && v <= 2) return v;
            switch (s?.ToLowerInvariant())
            {
                case "流畅": case "fast":        return 0;
                case "均衡": case "balanced":    return 1;
                case "清晰": case "high":        return 2;
            }
            return null;
        }

        public static string Usage()
        {
            return
@"远程控制 命令行参数

角色（二选一；省略则弹出选择窗口）：
  --host               作为被控端运行
  --viewer             作为控制端运行

连接：
  --server <地址>      中继服务器地址（默认 127.0.0.1）
  --port <端口>        中继服务器端口（默认 25498）
  --room <房间号>      房间名称
  --password <口令>    房间口令（同时作为端到端加密密钥）
  --fps <帧率>         被控端帧率（默认 30）

被控端专用：
  --quality <0|1|2>    画质：0 流畅 / 1 均衡 / 2 清晰
  --monitor <序号>     显示器序号（0 起）
  --viewonly           仅观看模式（控制端不能操作本机）

通用开关：
  --noadapt            关闭自适应码率/分辨率
  --nocomp             关闭链路压缩
  --nop2p              关闭 P2P 直连
  --noclip             关闭剪贴板同步
  --adv                显示中继服务器/端口输入框

静默 / 快速启动：
  --hide               启动后最小化到托盘（被控端静默运行）
  --autostart          启动后立即开始共享 / 连接（配合房间配置，失败按 30 秒重试）
  --retry              建房间失败则每 30 秒重试一次，直到成功（可单独使用）
  --help               显示本帮助

示例：
  RemoteControl.exe --host  --room 1234 --password abc --hide --autostart
  RemoteControl.exe --viewer --room 1234 --password abc --autostart";
        }
    }
}
