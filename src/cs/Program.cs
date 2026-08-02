// Program.cs - Entry point + a tiny launcher to pick a role.
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace RemoteControl
{
    // Picks a CJK-capable UI font that is actually present on this machine.
    // Lives in Program so HostForm / ViewerForm can read the same instance
    // and apply it explicitly (we no longer rely solely on
    // Application.SetDefaultFont, which proved unreliable in practice).
    internal static class CjkFontHolder
    {
        // CJK-capable UI fonts, in order of preference. The first one that is
        // actually installed on this machine is used. The list is broad enough
        // to cover normal Windows, Windows Server, and stripped-down images.
        private static readonly string[] Candidates =
        {
            "Microsoft YaHei UI",
            "Microsoft YaHei",
            "微软雅黑",
            "PingFang SC",
            "Hiragino Sans GB",
            "Source Han Sans SC",
            "Noto Sans CJK SC",
            "WenQuanYi Micro Hei",
            "SimHei",
            "黑体",
            "SimSun",
            "宋体",
            "NSimSun",
            "MS Gothic",
            "MS Mincho",
        };

        public static readonly Font Font;
        public static readonly string FontName;
        public static readonly string FontSource; // "Microsoft Sans Serif" if we fell back
        public static readonly string DebugLogPath;

        static CjkFontHolder()
        {
            // 1) Try the proper enumeration path.
            string chosen = null;
            try
            {
                var installed = FontFamily.Families
                    .Select(f => f.Name).ToArray();
                foreach (var name in Candidates)
                {
                    if (installed.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                    { chosen = name; break; }
                }
            }
            catch { /* enumeration failed, fall through */ }

            // 2) Hard probe of known file paths (covers the case where GDI+
            //    enumeration silently skips fonts the system has on disk but
            //    hasn't registered into the GDI font table, e.g. on some
            //    Windows Server / minimal images).
            if (chosen == null)
            {
                string fontsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
                var fileProbes = new (string font, string file)[]
                {
                    ("SimHei",  "simhei.ttf"),
                    ("SimSun",  "simsun.ttc"),
                    ("NSimSun", "simsun.ttc"),
                };
                foreach (var (font, file) in fileProbes)
                {
                    try
                    {
                        if (File.Exists(Path.Combine(fontsDir, file)))
                        { chosen = font; break; }
                    }
                    catch { }
                }
            }

            // 3) Last resort: if even SimHei/SimSun aren't on disk, log it and
            //    keep whatever the system default is. We still try to create
            //    a Font with the requested name so that the .NET font cache
            //    has a chance to do its own fallback.
            string resolvedName = chosen;
            if (resolvedName == null) resolvedName = "Microsoft Sans Serif";

            try
            {
                Font = new Font(resolvedName, 9f);
                // If .NET silently substituted a different family, prefer the
                // substitute's name (otherwise Form.Font will lie).
                FontName = Font.Name;
            }
            catch
            {
                Font = new Font(SystemFonts.MessageBoxFont?.Name ?? "Segoe UI", 9f);
                FontName = Font.Name;
            }

            FontSource = chosen ?? "(none of the candidates was found)";
            DebugLogPath = Path.Combine(
                Path.GetDirectoryName(Application.ExecutablePath) ?? ".",
                "font-debug.log");
            try
            {
                File.WriteAllText(DebugLogPath,
                    $"[font] requested: {(chosen ?? "(none)")}\n" +
                    $"[font] resolved : {FontName}\n" +
                    $"[font] candidates: {string.Join(", ", Candidates)}\n" +
                    $"[font] installed: {string.Join(", ", FontFamily.Families.Select(f => f.Name))}\n");
            }
            catch { /* best effort */ }
        }
    }

    internal static class Program
    {
        // 退出登录后由 MainForm 置位，主循环据此退回登录界面。
        public static bool NeedLogin = false;

        // 单实例互斥：Global 前缀跨桌面会话（同一用户下不重复加载）。
        private static Mutex _singleInstance;
        private const string AppMutexName = @"Global\RemoteControl_SingleInstance_v1.0";

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [STAThread]
        private static void Main(string[] args)
        {
            // 单实例保护：已有实例运行则激活已有窗口并退出
            _singleInstance = new Mutex(true, AppMutexName, out bool createdNew);
            if (!createdNew)
            {
                // 已有进程：尝试将已有窗口拉到前台
                try
                {
                    var hwnd = FindWindow(null, "RemoteControl"); // 旧模式
                    if (hwnd == IntPtr.Zero) FindWindow(null, "远程控制"); // 主界面
                    if (hwnd == IntPtr.Zero)
                    {
                        // 遍历查找 RemoteControl 窗口
                        EnumWindows((h, _) =>
                        {
                            var sb = new System.Text.StringBuilder(256);
                            GetWindowText(h, sb, sb.Capacity);
                            var title = sb.ToString();
                            if (title.Contains("RemoteControl") || title.Contains("远程控制")
                                || title.Contains("远程连接") || title.Contains("远程协助"))
                            {
                                ShowWindow(h, 9); // SW_RESTORE
                                SetForegroundWindow(h);
                                return false; // stop enum
                            }
                            return true;
                        }, IntPtr.Zero);
                    }
                    if (hwnd != IntPtr.Zero)
                    {
                        ShowWindow(hwnd, 9);
                        SetForegroundWindow(hwnd);
                    }
                }
                catch { }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.SetDefaultFont(CjkFontHolder.Font); } catch { }

            // 启动原生库自检：核心原生组件缺失/损坏时提前弹窗说明，
            // 避免运行到某个 P/Invoke 调用才抛出 DllNotFoundException。
            if (!NativeLibChecker.Verify(out var libProblems))
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("程序检测到核心原生组件缺失或无法加载，远程控制功能将无法使用。");
                sb.AppendLine("请确认安装目录完整（不要只复制 RemoteControl.exe，应整体复制解压后的目录），");
                sb.AppendLine("或重新运行官方安装包进行修复。");
                sb.AppendLine();
                sb.AppendLine("缺失/异常的库：");
                foreach (var p in libProblems) sb.AppendLine("  - " + p);
                System.Windows.Forms.MessageBox.Show(
                    sb.ToString(), "远程控制 - 启动检查失败",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
                return;
            }

            // Phase 7E / Phase 9: 读取 exe 尾部覆层中的 JSON 配置。
            // 覆层 JSON 示例: {"role":"host","server":"127.0.0.1","port":25498,"room":"123","password":"abc","hide":true}
            // 覆层配置作为默认值，命令行参数可覆盖。
            string overlayJson = null;
            try { overlayJson = Common.ReadOverlayConfig(Application.ExecutablePath); } catch { }

            var opts = AppOptions.Parse(args);

            // 覆层自动注入：如果命令行没指定角色，从覆层补充
            if (!string.IsNullOrEmpty(overlayJson))
            {
                try
                {
                    var cfg = System.Text.Json.Nodes.JsonNode.Parse(overlayJson);
                    if (cfg is System.Text.Json.Nodes.JsonObject obj)
                    {
                        // role: host/viewer
                        if (opts.Role == Role.None && obj.TryGetPropertyValue("role", out var roleNode))
                        {
                            var r = roleNode?.ToString()?.ToLower();
                            if (r == "host")      opts.Role = Role.Host;
                            else if (r == "viewer") opts.Role = Role.Viewer;
                        }
                        // room
                        if (string.IsNullOrEmpty(opts.Room) && obj.TryGetPropertyValue("room", out var roomNode))
                            opts.Room = roomNode?.ToString() ?? "";
                        // password
                        if (string.IsNullOrEmpty(opts.Password) && obj.TryGetPropertyValue("password", out var pwNode))
                            opts.Password = pwNode?.ToString() ?? "";
                        // server
                        if (string.IsNullOrEmpty(opts.Server) && obj.TryGetPropertyValue("server", out var srvNode))
                            opts.Server = srvNode?.ToString() ?? "";
                        // port
                        if (opts.Port == null && obj.TryGetPropertyValue("port", out var portNode) &&
                                int.TryParse(portNode?.ToString(), out int p))
                            opts.Port = p;
                        // api
                        if (string.IsNullOrEmpty(opts.ApiBase) && obj.TryGetPropertyValue("api", out var apiNode))
                            opts.ApiBase = apiNode?.ToString() ?? "";
                        // hide - 静默启动
                        if (!opts.Hide && obj.TryGetPropertyValue("hide", out var hideNode))
                            opts.Hide = hideNode?.ToString()?.ToLower() == "true";
                        // autostart
                        if (!opts.AutoStart && obj.TryGetPropertyValue("autostart", out var autoNode))
                            opts.AutoStart = autoNode?.ToString()?.ToLower() == "true";
                    }
                }
                catch { /* overlay parse failed, ignore */ }
            }

            // --read-overlay <exe>
            if (!string.IsNullOrEmpty(opts.ReadOverlay))
            {
                var json = Common.ReadOverlayConfig(opts.ReadOverlay);
                Console.WriteLine(json ?? "(no overlay)");
                return;
            }

            // 同步实验性功能开关（圆角/直角，默认直角）到全局，确保任意入口都生效。
            RoundedUI.UseRoundedCorners = UserSettings.Current.RoundedCorners;
            if (opts.ShowHelp)
            {
                System.Windows.Forms.MessageBox.Show(
                    AppOptions.Usage(), "远程控制 - 命令行帮助",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information);
                return;
            }

            // 启动即做版本检测（含强制升级拦截）。离线/服务器不可达时静默跳过。

            // 显式指定角色（--host/--viewer/--room）走命令行直接启动。
            if (opts.Role == Role.Host || opts.Role == Role.Viewer)
            {
                if (opts.Role == Role.Host)
                    Application.Run(new HostForm(opts));
                else
                    Application.Run(new ViewerForm(opts));
                return;
            }

            // 无角色指定：弹角色选择器
            using var picker = new Form
            {
                Text = "RemoteControl",
                Width = 320, Height = 200,
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false,
            };
            var lbl = new Label { Text = "选择运行模式", AutoSize = true, Location = new Point(100, 20), Font = new Font(CjkFontHolder.FontName, 12f, FontStyle.Bold) };
            var btnHost = new Button { Text = "被控端", Width = 120, Location = new Point(30, 70), Height = 40 };
            var btnViewer = new Button { Text = "控制端", Width = 120, Location = new Point(170, 70), Height = 40 };
            btnHost.Click += (s, ev) => { picker.DialogResult = DialogResult.Yes; picker.Close(); };
            btnViewer.Click += (s, ev) => { picker.DialogResult = DialogResult.No; picker.Close(); };
            picker.Controls.Add(lbl); picker.Controls.Add(btnHost); picker.Controls.Add(btnViewer);
            picker.AcceptButton = btnHost;
            var result = picker.ShowDialog();
            if (result == DialogResult.Yes)
                Application.Run(new HostForm(opts));
            else if (result == DialogResult.No)
                Application.Run(new ViewerForm(opts));
        }
    }

    public sealed class LauncherForm : Form
    {
        // 由按钮点击写入，Main 据此启动对应角色窗体。
        public Role ChosenRole = Role.None;
        public AppOptions ChosenOptions = null;

        public LauncherForm()
        {
            // Apply the CJK font explicitly on this form (and therefore on
            // every child control we add below), and embed the chosen font
            // name in the title so it's visible for diagnosis.
            Font = CjkFontHolder.Font;
            Text = $"远程控制   [font: {CjkFontHolder.FontName}]";
            Width = 360; Height = 200; FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            var lp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(20) };
            lp.Controls.Add(new Label { Text = "请选择一个角色：", AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
            var host = new Button { Text = "作为被控端 (Host)", Dock = DockStyle.Fill };
            var viewer = new Button { Text = "作为控制端 (Viewer)", Dock = DockStyle.Fill };
            lp.Controls.Add(host, 0, 1);
            lp.Controls.Add(viewer, 0, 2);
            Controls.Add(lp);

            // 选完角色即关闭选择窗口；由 Main 以该角色窗体作为主窗体运行，
            // 因此被控端在后台运行时永远不会再弹出这个选择窗口。
            host.Click += (s, e) => { ChosenRole = Role.Host; ChosenOptions = new AppOptions(); Close(); };
            viewer.Click += (s, e) => { ChosenRole = Role.Viewer; ChosenOptions = new AppOptions(); Close(); };
        }
    }
}
