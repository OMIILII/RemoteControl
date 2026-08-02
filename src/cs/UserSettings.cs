// UserSettings.cs - 用户可自定义的设置（非管理员专属）。
// 持久化到 %LOCALAPPDATA%/RemoteControl/settings.json，登录后随主机加载。
// 涵盖：服务器/网络、画质、共享选项、同账号连接策略、远程协助偏好、系统行为。
using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RemoteControl
{
    // 同账号设备连接策略：控制端请求控制本机时，本机如何响应。
    public enum SameAccountPolicy : int
    {
        AutoAccept = 0,  // 自动允许（无需确认）
        Ask = 1,         // 每次弹窗询问
        Block = 2,       // 拒绝所有同账号控制请求
    }

    public sealed class UserSettings
    {
        // ---- 服务器与网络 ----
        public string Server { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 25498;

        // ---- 画质 ----
        public int Fps { get; set; } = 30;
        public int Quality { get; set; } = 1;     // 0=流畅 1=均衡 2=清晰
        public int ScaleIdx { get; set; } = 0;    // 0=原始 1=85% 2=70%
        public bool Adaptive { get; set; } = true;
        public bool Compression { get; set; } = true;

        // ---- 共享选项 ----
        public bool Clipboard { get; set; } = true;
        public bool Audio { get; set; } = false;
        public bool ViewOnly { get; set; } = false;
        public bool P2P { get; set; } = true;

        // ---- 同账号连接策略 ----
        public SameAccountPolicy SameAccount { get; set; } = SameAccountPolicy.AutoAccept;
        public bool SameAccountBypassPerms { get; set; } = true;  // 同账号设备跳过权限检查
        public bool SameAccountNotify { get; set; } = true;        // 连接时桌面通知

        // ---- 远程协助偏好 ----
        public int AssistTtl { get; set; } = 600;        // 协助码有效期（秒）
        public bool AssistReusable { get; set; } = false;

        // ---- 系统行为 ----
        public bool Autostart { get; set; } = false;
        public bool Retry { get; set; } = true;

        // ---- 实验性功能 ----
        // 圆角/直角界面开关：默认 false（直角）；用户可在「设置 → 实验性功能」开启。
        public bool RoundedCorners { get; set; } = false;
        // 键盘映射：控制端键盘输入实时发送到被控端。默认开启。
        public bool KeyboardMapping { get; set; } = true;

        // ---- 权限控制（被控端限制控制端可执行的操作） ----
        public bool AllowFileTransfer { get; set; } = true;
        public bool AllowTerminal { get; set; } = true;
        public bool AllowCommand { get; set; } = true;       // 执行命令 / 启动程序
        public bool AllowRebootShutdown { get; set; } = true;
        public bool AllowRemoteInput { get; set; } = true;   // 键盘/鼠标
        public bool AllowClipboard { get; set; } = true;

        private static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "RemoteControl", "settings.json");

        private static UserSettings _current = Load();
        public static UserSettings Current => _current ?? (_current = new UserSettings());

        public static UserSettings Load()
        {
            try
            {
                var p = FilePath;
                if (!File.Exists(p)) return new UserSettings();
                var s = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(p, Encoding.UTF8));
                return s ?? new UserSettings();
            }
            catch { return new UserSettings(); }
        }

        public static void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }),
                    Encoding.UTF8);
            }
            catch { }
        }
    }
}
