// HostForm.cs - The controlled machine. Captures the screen with the C++
// core, encodes to H.264, streams frames to the viewer, and injects the
// input events it receives back.
//
// Experience improvements in this version:
//   * Multi-monitor selection (rc_capture_init + rc_capture_get_bounds ->
//     rc_input_set_bounds so input lands on the right monitor).
//   * Quality tiers (流畅/均衡/清晰) that scale the target bitrate, plus
//     adaptive bitrate that steps down when the encoder can't keep up.
//   * Resolution / device-loss self-heal: if capture fails mid-session we
//     rebuild capture + encoder and resend the VideoConfig header.
//   * RTT support: echoes Ping messages back so the viewer can measure it.
//   * Bidirectional clipboard text sync (polled, length-capped).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Windows.Forms;

namespace RemoteControl
{
    public sealed class HostForm : Form
    {
        private TextBox _roomBox, _serverBox, _portBox, _fpsBox, _pwBox;
        private ComboBox _monitorBox, _qualityBox;
        private CheckBox _clipboardChk;
        private Button _startBtn, _stopBtn, _kickBtn;
        private Button _chatBtn, _fileBtn, _switchBtn, _sysBtn, _blackBtn;
        private ListBox _viewerList;
        private Label _status;
        private NotifyIcon _tray;
        // 云模式（LaunchCloud）下：托盘"显示"/双击应恢复 MainForm 而不是本窗体
        // （否则露出 legacy 手动模式 UI，用户以为是"旧版本"）。
        public System.Action ShowCloudMain;

        // 云模式下进程内只允许存在一个被控服务 + 一个托盘图标。
        // 重复登录/切换账号时 LaunchCloud 会先把旧实例关掉再建新的，避免托盘图标叠加。
        private static HostForm _cloudSingleton = null;
        private CheckBox _autoStartChk;
        private CheckBox _retryChk;          // 建房间失败自动重试（30秒），独立开关
        private Transport _transport;
        private CancellationTokenSource _cts;
        private volatile bool _running;
        private bool _autoRetry;            // 建房间失败按 30 秒定时重试（--autostart/--retry）
        private bool _joinedOnce;           // 是否已成功建过房间（之后掉线走普通退避重连）
        private int _fpsCounter;
        private DateTime _fpsStamp = DateTime.UtcNow;
        private string _pwHash = "";
        private Aead _aead;                 // E2E key (null => plaintext)

        // ---- 远程终端（被控端隐藏 Shell）----------------------------------
        // 每个连接中的 viewer 可开一个隐藏终端；对方完全看不到窗口与过程。
        private readonly Dictionary<int, TerminalSession> _terminals = new();
        private readonly object _termLock = new();

        // ---- 同账号免密控制------------------------------------
        // _cloud=true 时：JOIN 用 "JOIN v2 <device_token> host"，
        // E2E 密钥 = PBKDF2(account_key, sessionId)，两端由账号信息独立派生，
        // 服务器零知识。confirm 授权模式下服务端会推 CtrlReq，这里弹窗确认。
        private bool _cloud;
        private string _cloudToken = "";        // device_token（TCP JOIN 鉴权）
        private string _cloudAccountKey = "";   // DPAPI 解出的 account_key
        private string _cloudUsername = "";     // 当前账号用户名（云模式聊天显示用）
        private string _cloudSessionId = "";    // u{user}_h{device}，与控制端一致
        private bool _forceClose;               // 程序性关闭（登出/退出），绕过“最小化到托盘”
        private CheckBox _audioChk;         // share system audio
        private volatile bool _audioOn;     // audio capture active
        private string _encName = "";       // active encoder (for status)
        private CheckBox _viewOnlyChk;       // host toggles viewer "view-only" mode
        private CheckBox _adaptChk, _compChk, _p2pChk; // 自适应码率/分辨率；链路压缩；P2P 直连
        private CheckBox _keyMonChk;                  // Phase 1D：键盘监视（向控制端广播本机按键）
        private volatile bool _viewOnly;     // when true, viewers may watch + chat only

        // Current stream parameters (needed to rebuild the encoder on the fly).
        private int _curW, _curH, _curFps, _curBitrate, _baseBitrate, _displayIndex;
        private double _qualityMul = 1.0;
        private readonly object _sendLock = new object();

        // Phase 3 缩略图墙：SendLoop 抽帧生成的低分辨率 PNG 快照缓存（带锁）。
        // 绕开单例 H264 编码器，供控制端设备卡片常驻预览；~2s 刷新一张。
        private byte[] _thumbPng;            // 最新快照（PNG 字节），null=尚未生成
        private int _thumbW, _thumbH;
        private readonly object _thumbLock = new object();
        private readonly System.Diagnostics.Stopwatch _thumbSw = System.Diagnostics.Stopwatch.StartNew();
        private const int ThumbIntervalMs = 2000;

        // Adaptive bitrate bookkeeping.
        private DateTime _lastAdapt = DateTime.UtcNow;
        private int _lowStreak, _goodStreak;
        private DateTime _connectAt = DateTime.MinValue;   // 启动期用，6s 内用保底码率

        // Clipboard sync.
        private System.Windows.Forms.Timer _clipTimer;
        private string _lastClipboard = "";
        private int _lastClipImgHash;   // loop-guard for image clipboard sync

        // Connected viewers (relay assigns the ids). UI-thread access only.
        private readonly System.Collections.Generic.Dictionary<int, DateTime> _viewers
            = new System.Collections.Generic.Dictionary<int, DateTime>();
        private readonly System.Collections.Generic.Dictionary<int, string> _viewerNames
            = new System.Collections.Generic.Dictionary<int, string>();  // vid -> name
        private System.Windows.Forms.Timer _viewerHearbeatTimer;  // Phase 7D: 清理死 viewer
        private readonly HashSet<int> _liteViewers = new();   // 不需要视频的 viewer（轻量会话）
        private readonly object _liteLock = new();
        private volatile int _viewerCount;                // read by SendLoop
        private DateTime _lastHeader = DateTime.MinValue; // rate-limit header resends

        // 远程协助：本机可同时作为多个协助房间的主机（每个房间一个独立中继连接）。
        // 协助连接的 viewers 与（_viewers）相互独立，vId 用 ASSIST_VID_BASE 偏移避免冲突。
        private const int ASSIST_VID_BASE = 100000;
        private readonly System.Collections.Generic.List<AssistLink> _assistLinks
            = new System.Collections.Generic.List<AssistLink>();
        private readonly object _assistLock = new object();

        // File transfer / chat / black screen.
        private readonly FileTransfer _ft = new FileTransfer();
        private FileTransferForm _ftForm;
        private readonly System.Collections.Generic.List<string> _chat = new System.Collections.Generic.List<string>();
        private ChatForm _chatForm;

        // Phase 2 远程文件浏览器：host 侧传输会话。
        // 与聊天发文件(_ft) 完全独立，使用自己的 id 空间与字典。
        private readonly Dictionary<int, FsXfer> _fsXfers = new Dictionary<int, FsXfer>();
        private readonly object _fsLock = new object();
        private int _fsXferSeq = 1;

        /// <summary>Phase 2 文件浏览器的一次传输（下载=host 发 viewer；上传=viewer 发 host）。</summary>
        private sealed class FsXfer
        {
            public int Id;
            public int Vid;
            public string Path = "";
            public FileStream Stream;     // 下载=读；上传=写
            public long Total;
            public long Done;
            public bool IsUpload;
            public bool Aborted;
        }

        // 服务端下发的「版本过时」提示只弹一次，避免重连循环反复刷屏。
        private bool _versionNoticeShown;
        private BlackScreenForm _blackForm;
        private bool _blackOn;

        // 一个远程协助房间：独立的中继连接 + 独立的 E2E 密钥 + 自己的 viewer 列表。
        private sealed class AssistLink
        {
            public Transport T;                 // 到中继的连接（含本房间 E2E 密钥）
            public string Room;                 // 6 位房间号
            public string Key;                  // 协助密钥（E2E 种子 = FromPassword(Key, Room)）
            public readonly System.Collections.Generic.Dictionary<int, DateTime> Viewers
                = new System.Collections.Generic.Dictionary<int, DateTime>();
            // 控制端显示名（base64 由服务端在 T_VJOIN 中转发），用于协助管理面板。
            public readonly System.Collections.Generic.Dictionary<int, string> ViewerNames
                = new System.Collections.Generic.Dictionary<int, string>();
            public CancellationTokenSource Cts;
            public volatile bool Stopped;
        }

        // Host-side local recording (mux the encoded stream we send out).
        private Button _recBtn;
        private volatile bool _hostRecording;
        private readonly System.Diagnostics.Stopwatch _recSw = new System.Diagnostics.Stopwatch();
        private byte[] _curExtra = Array.Empty<byte>();  // latest SPS/PPS

        // ---- dynamic resolution / adaptive bitrate -------------------------
        private int _natW, _natH;                  // native capture resolution
        private int _scaleIdx = 0;                 // index into Scales (0 => native)
        private int _viewerScaleCap = -1;          // Phase 1B：控制端指定的最低画质档（-1=无限制）

        // ---- Phase 1D：键盘监视（向控制端广播本机按键）-------------------
        private IntPtr _kbHook = IntPtr.Zero;
        private LowLevelKeyboardProc _kbDelegate;
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT { public int vkCode; public int scanCode; public int flags; public int time; public IntPtr dwExtraInfo; }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
        private double _sendStallEma;              // EMA of frame send latency (ms) — congestion proxy
        // 适应性参数（带迟滞+恢复冷却，避免码率上下乱跳）：
        //   DOWN_RATIO 0.70 : 拥堵时一步砍 30%
        //   UP_RATIO   1.08 : 顺畅时只升 8%（不对称，优先稳）
        //   DOWN_STREAK 2   : 连续 2s 不行就降
        //   UP_STREAK   8   : 连续 8s 顺畅才升（防“降了立刻拉回去”）
        //   COOLDOWN    7s  : 两次适应至少隔 7s
        //   RECOVERY_HOLD 18s: 降过码率后至少 18s 不许回升（让链路真的稳下来）
        private const double DOWN_RATIO = 0.70;
        private const double UP_RATIO   = 1.08;
        private const int DOWN_STREAK   = 2;
        private const int UP_STREAK     = 8;
        private const int COOLDOWN_SEC  = 7;
        private const int RECOVERY_HOLD_SEC = 18;
        private DateTime _recoveryHoldUntil = DateTime.MinValue;
        private bool _adaptOn = true;              // adaptive bitrate / resolution enabled?

        // P2P 直连（TCP hole punch）：视频/输入改走两端用户机器之间的直连 TCP，
        // 把延迟交给用户自己的网络承担，绕开中转。仅在单 viewer 时启用；多 viewer
        // 或打洞失败自动退回中转，绝不退化现有功能。
        private volatile bool _p2pOn = true;
        private Transport _p2p;               // 单 viewer 的直连通道（null => 走中转）
        private int _p2pVid = -1;             // 直连对应的 viewer id
        private volatile bool _p2pReady;      // 双方都收到对方 Hello 才置真
        private readonly object _p2pLock = new object();
        private CancellationTokenSource _p2pCts;
        private double _rttViewerEma = -1;    // 控制端回传的真实 RTT（ms），用于自适应
        private int _p2pEpVid = -1;           // 直连对端 viewer（用于断线自动重连）
        private List<(string ip, int port)> _p2pCandidates;
        private bool _p2pEverConnected;       // 曾连上过才自动重连
        private bool _p2pRetrying;            // 防止重连任务叠罗汉
        // 动态分辨率档位。之前 {1.0,0.8,0.65,0.5,0.4} 太狠——一拥塞就掉到 0.4x
        // （1080p 变 768x432）是"糊"的主因。改温和两档，且自适应优先降码率、把
        // 码率压到地板后才考虑降分辨率，好链路上基本不会触发。
        // Phase 1B：扩展为 5 档，使控制端可请求的 100%/75%/50% 都能精确命中。
        private static readonly double[] Scales = { 1.0, 0.85, 0.75, 0.6, 0.5 };
        private int _recW, _recH, _recPart;
        private string _recDir = "";

        public HostForm(AppOptions opts = null)
        {
            opts = opts ?? new AppOptions();
            bool showAdv = opts.Advanced || Common.IsAdvancedUi();
            // Force the form to use the CJK font we picked at startup. Setting
            // it explicitly (instead of only via Application.SetDefaultFont)
            // guarantees every child control below also renders with it.
            Font = CjkFontHolder.Font;
            Text = $"远程控制 - 被控端 (Host)   [font: {CjkFontHolder.FontName}]";
            ClientSize = new Size(560, 700);
            MinimumSize = new Size(500, 560);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true; StartPosition = FormStartPosition.CenterScreen;

            // 内容高度会随字体、DPI 和复选框换行变化。外层允许滚动，
            // 避免固定窗口高度把底部选项（例如 P2P）裁掉。
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var lp = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 16,
                Padding = new Padding(10),
                GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            };
            lp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _serverBox = new TextBox { Text = "127.0.0.1", Dock = DockStyle.Fill };
            _portBox   = new TextBox { Text = "25498", Dock = DockStyle.Fill };
            _roomBox   = new TextBox { Text = "", Dock = DockStyle.Fill };
            _fpsBox    = new TextBox { Text = "30", Dock = DockStyle.Fill };
            _pwBox     = new TextBox { Text = "", Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "可选，设置后控制端需密码" };

            _monitorBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            PopulateMonitors();

            _qualityBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            _qualityBox.Items.AddRange(new object[] { "流畅 (省带宽)", "均衡", "清晰 (高码率)" });
            _qualityBox.SelectedIndex = 1;
            _qualityBox.SelectedIndexChanged += (s, e) => OnQualityChanged();

            _clipboardChk = new CheckBox { Text = "同步剪贴板", Dock = DockStyle.Fill, Checked = true };
            _audioChk = new CheckBox { Text = "共享系统声音", Dock = DockStyle.Fill, Checked = false };

            _status    = new Label { Text = "未启动", Dock = DockStyle.Fill, ForeColor = Color.Gray, AutoEllipsis = true, AutoSize = true, MinimumSize = new Size(0, 24) };
            _startBtn  = new Button { Text = "开始共享", Dock = DockStyle.Fill };
            _stopBtn   = new Button { Text = "停止", Dock = DockStyle.Fill, Enabled = false };

            _viewerList = new ListBox { Dock = DockStyle.Fill, Height = 90, MinimumSize = new Size(0, 90), IntegralHeight = false };
            _kickBtn    = new Button { Text = "断开选中的控制端", Dock = DockStyle.Fill, Enabled = false };

            // 这些按钮位于 FlowLayoutPanel 中，使用 AutoSize 才能按文字正确计算宽度，
            // 不会因 Dock=Fill 与换行布局冲突而被挤成不可见。
            _chatBtn   = new Button { Text = "聊天", AutoSize = true, Enabled = false };
            _fileBtn   = new Button { Text = "发文件", AutoSize = true, Enabled = false };
            _switchBtn = new Button { Text = "切换显示器", AutoSize = true, Enabled = false };
            _sysBtn    = new Button { Text = "系统控制", AutoSize = true, Enabled = false };
            _blackBtn  = new Button { Text = "本地黑屏", AutoSize = true, Enabled = false };
            _recBtn    = new Button { Text = "录制", AutoSize = true, Enabled = false };
            _autoStartChk = new CheckBox { Text = "开机自启", AutoSize = true, Checked = Common.IsAutostartEnabled() };
            _retryChk = new CheckBox { Text = "建房间失败自动重试(30秒)", AutoSize = true, Checked = false };
            _viewOnlyChk = new CheckBox { Text = "仅观看模式（控制端只能看，不能操作）", AutoSize = true, Checked = false };
            _keyMonChk = new CheckBox { Text = "键盘监视（向控制端实时广播本机按键）", AutoSize = true, Checked = false };
            _adaptChk = new CheckBox { Text = "自适应码率/分辨率", AutoSize = true, Checked = true };
            _compChk  = new CheckBox { Text = "链路压缩", AutoSize = true, Checked = true };
            _p2pChk   = new CheckBox { Text = "P2P 直连（降低延迟）", AutoSize = true, Checked = true };

            int r = 0;
            // 中继服务器 / 端口：默认隐藏，仅 --adv 时显示。
            if (showAdv)
            {
                lp.Controls.Add(new Label { Text = "中继服务器", Dock = DockStyle.Fill }, 0, r); lp.Controls.Add(_serverBox, 1, r++);
                lp.Controls.Add(new Label { Text = "端口", Dock = DockStyle.Fill }, 0, r); lp.Controls.Add(_portBox, 1, r++);
            }
            lp.Controls.Add(new Label { Text = "房间号", Dock = DockStyle.Fill }, 0, r); lp.Controls.Add(_roomBox, 1, r++);
            lp.Controls.Add(new Label { Text = "口令", Dock = DockStyle.Fill }, 0, r); lp.Controls.Add(_pwBox, 1, r++);
            lp.Controls.Add(new Label { Text = "帧率", Dock = DockStyle.Fill }, 0, r); lp.Controls.Add(_fpsBox, 1, r++);
            lp.Controls.Add(new Label { Text = "显示器", Dock = DockStyle.Fill }, 0, r); lp.Controls.Add(_monitorBox, 1, r++);
            lp.Controls.Add(new Label { Text = "画质", Dock = DockStyle.Fill }, 0, r); lp.Controls.Add(_qualityBox, 1, r++);
            lp.Controls.Add(_startBtn, 0, r); lp.Controls.Add(_stopBtn, 1, r++);
            lp.Controls.Add(_clipboardChk, 0, r); lp.Controls.Add(_audioChk, 1, r++);
            lp.Controls.Add(new Label { Text = "已连接的控制端", Dock = DockStyle.Fill }, 0, r);
            lp.Controls.Add(_viewerList, 1, r++);
            lp.Controls.Add(_kickBtn, 1, r++);

            // 操作按钮单独成区，并让面板按换行后的实际高度增长。
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            actions.Controls.Add(_chatBtn); actions.Controls.Add(_fileBtn); actions.Controls.Add(_switchBtn);
            actions.Controls.Add(_sysBtn); actions.Controls.Add(_blackBtn); actions.Controls.Add(_recBtn);
            lp.Controls.Add(actions, 0, r++);
            lp.SetColumnSpan(actions, 2);

            // 选项也单独成区，显式分行，避免 FlowLayoutPanel 高度不足时裁掉最后一项。
            var options = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 4,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int i = 0; i < 5; i++)
                options.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            options.Controls.Add(_viewOnlyChk, 0, 0); options.SetColumnSpan(_viewOnlyChk, 2);
            options.Controls.Add(_autoStartChk, 0, 1);
            options.Controls.Add(_retryChk, 1, 1);
            options.Controls.Add(_adaptChk, 0, 2);
            options.Controls.Add(_compChk, 1, 2);
            options.Controls.Add(_p2pChk, 0, 3); options.SetColumnSpan(_p2pChk, 2);
            options.Controls.Add(_keyMonChk, 0, 4); options.SetColumnSpan(_keyMonChk, 2);
            lp.Controls.Add(options, 0, r++);
            lp.SetColumnSpan(options, 2);

            lp.Controls.Add(_status, 0, r); lp.SetColumnSpan(_status, 2);
            lp.RowCount = r + 1;
            lp.RowStyles.Clear();
            for (int i = 0; i < lp.RowCount; i++)
                lp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            scroll.Controls.Add(lp);
            Controls.Add(scroll);

            _startBtn.Click += (s, e) => _ = StartAsync();
            _stopBtn.Click  += (s, e) => Stop();
            _keyMonChk.CheckedChanged += (s, e) => UpdateKeyHook();
            _kickBtn.Click  += (s, e) => KickSelected();
            _chatBtn.Click  += (s, e) => ShowChat();
            _fileBtn.Click  += (s, e) => SendFileDialog();
            _switchBtn.Click += (s, e) => SwitchDisplayDialog();
            _sysBtn.Click   += (s, e) => SystemControlDialog();
            _blackBtn.Click += (s, e) => ToggleBlackScreen();
            _recBtn.Click   += (s, e) => ToggleHostRecording();
            _autoStartChk.CheckedChanged += (s, e) =>
            {
                if (_autoStartChk.Checked)
                {
                    // 保存当前房间配置，注册"开机静默自动建房间"的启动项。
                    // Run 键只写 --host --autostart --hide；房间细节从 profile 文件读。
                    // 开机自启默认也开启失败重试（开机时网络可能未就绪）。
                    if (!_retryChk.Checked) _retryChk.Checked = true;
                    SaveHostProfile();
                    Common.SetAutostart(true, "--host --autostart --hide");
                    SetStatus("已设置开机自启（自动建房间，静默）", Color.Gray);
                }
                else
                {
                    Common.SetAutostart(false);
                    SetStatus("已取消开机自启", Color.Gray);
                }
            };
            _viewOnlyChk.CheckedChanged += (s, e) =>
            {
                _viewOnly = _viewOnlyChk.Checked;
                if (_running) SendToAll(MessageType.ViewOnly, Codec.BuildViewOnly(_viewOnly));
                SetStatus(_viewOnly ? "已开启仅观看：控制端只能观看与聊天，无法操作本机"
                                    : "已关闭仅观看：控制端恢复完整控制", Color.DarkOrange);
            };
            _adaptChk.CheckedChanged += (s, e) => { _adaptOn = _adaptChk.Checked; };
            _compChk.CheckedChanged += (s, e) => { Transport.CompressionEnabled = _compChk.Checked; };
            _p2pChk.CheckedChanged += (s, e) =>
            {
                _p2pOn = _p2pChk.Checked;
                if (!_p2pOn) CloseP2P();   // 关闭开关立即退回中转
            };
            _viewerList.SelectedIndexChanged += (s, e) =>
                _kickBtn.Enabled = _running && _viewerList.SelectedIndex >= 0;
            FormClosed += (s, e) =>
            {
                try { if (_tray != null) _tray.Visible = false; } catch { }
                try { _tray?.Dispose(); } catch { }
                Stop();
            };

            // Tray icon: double-click restores, right-click context menu.
            _tray = new NotifyIcon
            {
                Text = "远程控制 - 被控端",
                Icon = SystemIcons.Application,
                Visible = true,
            };
            _tray.MouseDoubleClick += (s, e) =>
            {
                ShowFromTray();
            };
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("显示", null, (s, e) =>
            {
                ShowFromTray();
            });
            trayMenu.Items.Add("退出", null, (s, e) =>
            {
                // 不能在右键菜单的点击分发过程中同步调用 Application.Exit()：
                // 此刻 ContextMenuStrip 仍在枚举自身条目，Exit 触发托盘/菜单释放会修改
                // 正在被枚举的集合，抛 "Collection was modified"。先置 forceClose 绕过
                // "共享中最小化到托盘"，再 BeginInvoke 把 Exit 推迟到本次点击处理完成之后。
                _forceClose = true;
                BeginInvoke((MethodInvoker)(() =>
                {
                    _tray.Visible = false;
                    Application.Exit();
                }));
            });
            _tray.ContextMenuStrip = trayMenu;
            Resize += (s, e) =>
            {
                if (WindowState == FormWindowState.Minimized) Hide();
            };
            FormClosing += (s, e) =>
            {
                if (!_forceClose && e.CloseReason == CloseReason.UserClosing && _running)
                {
                    // Minimize to tray when user clicks X while sharing.
                    e.Cancel = true; WindowState = FormWindowState.Minimized;
                }
            };

            _clipTimer = new System.Windows.Forms.Timer { Interval = 700 };
            _clipTimer.Tick += (s, e) => PollClipboard();

            // ---- 命令行参数预填（静默模式 / 快速启动）---------------------
            // 先载入保存的"开机自动建房间"配置作为默认值（若之前勾选过开机自启），
            // 命令行参数随后覆盖，便于用 --room 等临时指定。
            var saved = Common.LoadHostProfile();
            if (saved != null)
            {
                if (string.IsNullOrEmpty(_serverBox.Text)) _serverBox.Text = saved.Server;
                if (string.IsNullOrEmpty(_portBox.Text) && saved.Port > 0) _portBox.Text = saved.Port.ToString();
                if (string.IsNullOrEmpty(_roomBox.Text)) _roomBox.Text = saved.Room;
                if (string.IsNullOrEmpty(_pwBox.Text)) _pwBox.Text = saved.Password;
                if (string.IsNullOrEmpty(_fpsBox.Text) && saved.Fps > 0) _fpsBox.Text = saved.Fps.ToString();
                if (saved.Quality >= 0 && saved.Quality <= 2) _qualityBox.SelectedIndex = saved.Quality;
                if (saved.Monitor >= 0 && saved.Monitor < _monitorBox.Items.Count) _monitorBox.SelectedIndex = saved.Monitor;
                _viewOnlyChk.Checked = saved.ViewOnly;
                if (saved.NoAdapt) _adaptChk.Checked = false;
                if (saved.NoComp) _compChk.Checked = false;
                if (saved.NoP2P) _p2pChk.Checked = false;
                if (saved.NoClip) _clipboardChk.Checked = false;
                if (saved.Audio) _audioChk.Checked = true;
                if (saved.Retry) _retryChk.Checked = true;
            }

            if (opts.Server != null) _serverBox.Text = opts.Server;
            if (opts.Port.HasValue) _portBox.Text = opts.Port.Value.ToString();
            if (opts.Room != null) _roomBox.Text = opts.Room;
            if (opts.Password != null) _pwBox.Text = opts.Password;
            if (opts.Fps.HasValue) _fpsBox.Text = opts.Fps.Value.ToString();
            if (opts.Quality.HasValue) _qualityBox.SelectedIndex = opts.Quality.Value;
            if (opts.Monitor.HasValue && opts.Monitor.Value >= 0
                && opts.Monitor.Value < _monitorBox.Items.Count)
                _monitorBox.SelectedIndex = opts.Monitor.Value;
            if (opts.ViewOnly) _viewOnlyChk.Checked = true;
            if (opts.NoAdapt) _adaptChk.Checked = false;
            if (opts.NoComp) _compChk.Checked = false;
            if (opts.NoP2P) _p2pChk.Checked = false;
            if (opts.NoClip) _clipboardChk.Checked = false;

            // 失败重试（30秒）：由独立复选框或显式 --retry 决定，不再隐含于开机自启。
            _autoRetry = _retryChk.Checked || opts.Retry;

            // 静默：启动后最小化到托盘；自启：房间非空则立即开始共享。
            if (opts.AutoStart || opts.Hide)
            {
                Shown += (s, e) =>
                {
                    if (opts.AutoStart && !string.IsNullOrWhiteSpace(_roomBox.Text))
                        _ = StartAsync();
                    if (opts.Hide)
                        WindowState = FormWindowState.Minimized; // 触发 Resize -> Hide()
                };
            }
        }

        // 云模式下隐藏运行。托盘"显示"/双击：有回调则 invoke 回调（让调用方恢复
        // 真正的 MainForm 界面），没有则 Show 本窗体（手动模式）。
        private void ShowFromTray()
        {
            if (ShowCloudMain != null) { try { ShowCloudMain(); } catch { } return; }
            Show(); WindowState = FormWindowState.Normal; Activate();
        }

        // ---- 本机作为被控端后台上线 ---------------------------------
        // MainForm 登录成功后调用。窗体隐藏运行（托盘图标保留，双击可查看状态），
        // 自动开始共享并按 30 秒重试保持在线；关闭/登出用 ForceClose()。
        internal static HostForm LaunchCloud(AccountData acc)
        {
            string key = "";
            try { key = AccountStore.Unprotect(acc.AccountKeyEnc); } catch { }

            // 先关闭可能存在的旧云模式实例（其托盘会立即隐藏），确保任意时刻只有一个托盘图标。
            // 否则登出后再登录会新建一个 HostForm，旧实例的托盘又没被回收，图标就叠加上去。
            if (_cloudSingleton != null && !_cloudSingleton.IsDisposed)
            {
                try { _cloudSingleton.ForceClose(); } catch { }
            }
            _cloudSingleton = null;

            var f = new HostForm(new AppOptions());
            f._cloud = true;
            f._cloudToken = acc.DeviceToken ?? "";
            f._cloudAccountKey = key;
            f._cloudUsername = acc.Username ?? "";
            f._cloudSessionId = $"u{acc.UserId}_h{acc.DeviceId}";
            f._roomBox.Text = f._cloudSessionId;   // 仅展示，云模式不参与 JOIN
            f._roomBox.Enabled = false; f._pwBox.Enabled = false;
            f._retryChk.Checked = true;            // 网络未就绪时按 30 秒重试
            f._autoRetry = true;
            f.Text = "远程控制 - 本机被控服务";
            f._tray.Text = "远程控制 - 在线";

            // 应用用户自定义设置（非管理员专属）。
            var s = UserSettings.Current;
            f._serverBox.Text = string.IsNullOrWhiteSpace(s.Server) ? CloudConfig.TcpHost : s.Server;
            f._portBox.Text = s.Port.ToString();
            f._fpsBox.Text = s.Fps.ToString();
            f._qualityBox.SelectedIndex = Math.Max(0, Math.Min(2, s.Quality));
            f._monitorBox.SelectedIndex = 0;
            f._adaptChk.Checked = s.Adaptive;
            f._compChk.Checked = s.Compression;
            f._clipboardChk.Checked = s.Clipboard;
            f._audioChk.Checked = s.Audio;
            f._viewOnlyChk.Checked = s.ViewOnly;
            f._p2pChk.Checked = s.P2P;
            f._autoStartChk.Checked = s.Autostart;
            f._retryChk.Checked = s.Retry;
            try { Common.SetAutostart(s.Autostart, "--autostart --cloud --min"); } catch { }

            // Show 一次以创建窗口句柄（RecvLoop 里的 BeginInvoke 需要），随即隐藏。
            f.Opacity = 0; f.ShowInTaskbar = false;
            f.Show(); f.Hide();
            f.Opacity = 1; f.ShowInTaskbar = true;
            _ = f.StartAsync();
            _cloudSingleton = f;
            return f;
        }

        /// <summary>程序性关闭（登出/切换账号）：停止共享并真正关掉窗体，
        /// 不走“共享中点 X 最小化到托盘”的拦截逻辑。</summary>
        public void ForceClose()
        {
            _forceClose = true;
            try { Stop(); } catch { }
            try { _tray.Visible = false; } catch { }
            try { Close(); } catch { }
            if (_cloudSingleton == this) _cloudSingleton = null;
        }

        /// <summary>当前是否正在共享（房间已开启）。供主界面按钮判断状态。</summary>
        public bool IsSharing => _running;

        /// <summary>停止共享（关闭房间）。与「停止」按钮等效；不退出程序、不关闭窗体，
        /// 可随后用 StartSharing 重新开启。停止后该房间立即从服务器/管理端移除。</summary>
        public void StopSharing() => Stop();

        /// <summary>重新开始共享（房间）。仅当当前未在共享时生效。</summary>
        public void StartSharing() { if (!_running) _ = StartAsync(); }

        private void PopulateMonitors()
        {
            _monitorBox.Items.Clear();
            var screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var b = screens[i].Bounds;
                string tag = screens[i].Primary ? " 主屏" : "";
                _monitorBox.Items.Add($"显示器 {i}: {b.Width}x{b.Height}{tag}");
            }
            if (_monitorBox.Items.Count == 0) _monitorBox.Items.Add("显示器 0");
            _monitorBox.SelectedIndex = 0;
        }

        private double QualityMul() => _qualityBox.SelectedIndex switch
        {
            0 => 0.5,   // 流畅
            2 => 1.3,   // 清晰
            _ => 0.85,  // 均衡
        };

        // 1.0x 画质档下的目标码率基线（kbps）。
        // 经验值：H.264 屏幕共享在 ~0.1 bit/像素/帧 就能比较清晰。
        //   720p  → ~2.8Mbps   1080p → ~6.2Mbps   1440p → 限到 12Mbps（@30fps 计）
        // 关键：码率必须随帧率线性放大——否则高帧率（如 120fps）下每帧只分到 1/4 的
        // 码率预算，画面会瞬间炸成马赛克（"改成 120 后画面非常奇怪"的根因）。
        // 30fps 及以下 fpsMul=1，行为与旧版完全一致，不会动到已调好的 30fps 链路。
        // 下限 1500 保证小屏也不至于糊；上限随帧率放大，防止 4K×高帧率把码率推到离谱。
        // （上游真扛不住时自适应会把码率压下来，所以基线给"清晰所需"而非"链路能扛"。）
        private static int BitrateBase(int w, int h, int fps)
        {
            long px = (long)w * h;
            double fpsMul = Math.Max(1.0, fps / 30.0);   // 高帧率需要成比例更多码率
            int bps = (int)(px * 0.003 * fpsMul);        // ~0.1 bit/pixel/frame @30fps, 单位 kbps
            if (bps < 1500) bps = 1500;
            if (bps > (int)(12000 * fpsMul)) bps = (int)(12000 * fpsMul);
            return bps;
        }

        // 任何画质档都不能超过此上限，避免高分辨率清晰档推爆慢链路。
        // 同样随帧率放大（30fps=6000，120fps=24000）并加 30000 硬顶防失控：
        // 高帧率本来就需要成倍带宽，不让它放大就会重演"码率没涨、画面炸裂"。
        // TCP（中继/P2P）本就可靠，上游有 bufferbloat 风险时自适应会把码率压下来。
        private int BitrateCap => (int)Math.Min(30000, 6000 * Math.Max(1.0, _curFps / 30.0));

        // Encoder target size for the current scale (even dimensions for H.264).
        private int EncW() => (int)Math.Round(_natW * Scales[_scaleIdx] / 2) * 2;
        private int EncH() => (int)Math.Round(_natH * Scales[_scaleIdx] / 2) * 2;
        // Target bitrate at the current scale: pixels scale with scale^2, so the
        // bitrate target shrinks with the resolution. Always capped + floored.
        private int ScaleBitrate() =>
            (int)Math.Min(BitrateCap, Math.Max(300,
                _baseBitrate * _qualityMul * Scales[_scaleIdx] * Scales[_scaleIdx]));

        private async Task StartAsync()
        {
            if (_running) return;

            _joinedOnce = false;
            _displayIndex = Math.Max(0, _monitorBox.SelectedIndex);
            _qualityMul = QualityMul();
            _adaptOn = _adaptChk.Checked;
            Transport.CompressionEnabled = _compChk.Checked;
            _scaleIdx = 0; _sendStallEma = 0;
            _pwHash = Common.HashPassword(_pwBox.Text);
            // A room password doubles as the end-to-end encryption secret.
            // 账号模式：用密钥 + 会话 id 派生，两端独立算出同一把钥匙。
            _aead = _cloud
                ? Aead.FromPassword(_cloudAccountKey, _cloudSessionId)
                : Aead.FromPassword(_pwBox.Text, _roomBox.Text);

            // Init touches GPU/DXGI and can block — run it off the UI thread.
            var init = await Task.Run(() => InitCore(_displayIndex, _qualityMul));
            if (!init.ok)
            {
                _status.Text = init.error ?? "初始化失败";
                _status.ForeColor = Color.Red;
                return;
            }

            _running = true; _cts = new CancellationTokenSource();
            _startBtn.Enabled = false; _stopBtn.Enabled = true;
            _monitorBox.Enabled = false; _pwBox.Enabled = false; _audioChk.Enabled = false;
            _chatBtn.Enabled = _fileBtn.Enabled = _switchBtn.Enabled = _sysBtn.Enabled = _blackBtn.Enabled = _recBtn.Enabled = true;
            _viewers.Clear(); lock (_liteLock) _liteViewers.Clear(); RefreshViewerList();
            _encName = RcNative.EncoderName();
            _status.Text = "正在连接…"; _status.ForeColor = Color.Green;

            _lowStreak = _goodStreak = 0; _lastAdapt = DateTime.UtcNow;
            _lastClipboard = SafeGetClipboard();
            if (_clipboardChk.Checked) _clipTimer.Start();
            UpdateKeyHook();   // Phase 1D：若勾选了键盘监视则安装钩子

            // Phase 7D: Start viewer heartbeat cleanup timer.
            if (_viewerHearbeatTimer == null)
            {
                _viewerHearbeatTimer = new System.Windows.Forms.Timer { Interval = 5000 };
                _viewerHearbeatTimer.Tick += (s, e) => PurgeStaleViewers();
            }
            _viewerHearbeatTimer.Start();

            // Optional: capture system audio (WASAPI loopback) and stream it.
            if (_audioChk.Checked && RcNative.rc_audio_cap_start() == RcNative.RC_OK)
            {
                _audioOn = true;
                _ = Task.Run(() => AudioLoop(_cts.Token));
            }

            // SendLoop runs for the whole session; ConnectionLoop owns the
            // transport and reconnects transparently after a drop.
            _ = Task.Run(() => SendLoop(_cts.Token));
            _ = Task.Run(() => ConnectionLoop(_cts.Token));
        }

        // 把当前被控端配置存档，供"开机自动建房间"在开机后读取。
        private void SaveHostProfile()
        {
            int.TryParse(_portBox.Text, out int port);
            int.TryParse(_fpsBox.Text, out int fps);
            Common.SaveHostProfile(new Common.HostProfile
            {
                Server = _serverBox.Text,
                Port = port,
                Room = _roomBox.Text,
                Password = _pwBox.Text,
                Fps = fps,
                Quality = _qualityBox.SelectedIndex,
                Monitor = _monitorBox.SelectedIndex,
                ViewOnly = _viewOnlyChk.Checked,
                NoAdapt = !_adaptChk.Checked,
                NoComp = !_compChk.Checked,
                NoP2P = !_p2pChk.Checked,
                NoClip = !_clipboardChk.Checked,
                Audio = _audioChk.Checked,
                Retry = _retryChk.Checked,
            });
        }

        // Manages the relay connection: connect, join, (re)send the video
        // header, run RecvLoop until the link drops, then reconnect with
        // backoff. The capture/encoder are kept alive across reconnects.
        // Viewers coming and going never ends the session — sharing only
        // stops when the user clicks 停止.
        private void ConnectionLoop(CancellationToken token)
        {
            int backoff = 800;
            while (_running && !token.IsCancellationRequested)
            {
                Transport t;
                try
                {
                    t = Transport.Connect(_serverBox.Text, int.Parse(_portBox.Text));
                    t.SetCrypto(_aead);
                    if (_cloud) t.SendJoinV2(_cloudToken, "host",
                                version: UpgradeCheck.CurrentVersion(),
                                computerName: Environment.MachineName,
                                lanIp: Common.GetLanIP());
                    else t.SendJoin(_roomBox.Text, "host", _pwHash, version: UpgradeCheck.CurrentVersion());
                    // Relay answers with a RESULT frame; a reject here means the
                    // room is occupied by another host with a different password.
                    if (t.TryReceive(out var ht, out var hp) && ht == MessageType.Result)
                    {
                        Codec.ParseResult(hp, out int code, out string text);
                        if (code != 0)
                        {
                            t.Dispose();
                            if (code == 2)
                            {
                                // Relay 拒绝：版本过低，强制升级
                                BeginInvoke((MethodInvoker)(() =>
                                    MessageBox.Show(text, "强制升级", MessageBoxButtons.OK, MessageBoxIcon.Stop)));
                                _running = false;
                                break;
                            }
                            // 自动建房间模式：房间被拒（被占用/口令错）也按 30 秒重试，
                            // 不再永久停止——等对方退出或口令改对后即可建成功。
                            if (_autoRetry && !_joinedOnce)
                            {
                                SetStatus("加入房间被拒绝: " + text + "，30 秒后重试…", Color.Red);
                                if (Sleep(30000, token)) break;
                                continue;
                            }
                            SetStatus("加入房间被拒绝: " + text, Color.Red);
                            _running = false;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 自动建房间模式：连接失败（如开机时网络未就绪）按固定 30 秒重试，
                    // 直到成功建房间为止；非自动模式则走原有指数退避。
                    if (_autoRetry && !_joinedOnce)
                    {
                        SetStatus("连接中继服务器失败，30 秒后重试… " + ex.Message, Color.DarkOrange);
                        if (Sleep(30000, token)) break;
                        continue;
                    }
                    SetStatus("连接中继服务器失败，重连中… " + ex.Message, Color.DarkOrange);
                    if (Sleep(backoff, token)) break;
                    backoff = Math.Min(backoff * 2, 8000);
                    continue;
                }
                _transport = t; backoff = 800;
                // 上报本机公网候选（STUN），供对端 P2P 打洞直连、绕开慢中继。
                _ = Task.Run(() => { try { StunProbe.SendPubCand(t); } catch { } });
                _joinedOnce = true;   // 首次成功建房间后，后续掉线走普通退避重连

                // The relay will replay VJoin for every waiting viewer right
                // after we join; ResyncViewers() then sends header + IDR.
                BeginInvoke((MethodInvoker)(() => { _viewers.Clear(); RefreshViewerList(); }));
                lock (_sendLock) { RebuildEncoder(_curBitrate); }
                SetStatus($"共享中 {_curW}x{_curH}@{_curFps} | {SecTag()} | {EncTag()} | 等待控制端", Color.Green);

                RecvLoop(token);
                try { _transport?.Dispose(); } catch { }
                _transport = null;
                if (!_running || token.IsCancellationRequested)
                {
                    SetStatus(_running ? "任务取消" : "停止共享", Color.Gray);
                    break;
                }

                SetStatus("连接断开，重连中…", Color.DarkOrange);
                if (Sleep(1000, token)) break;
            }
        }

        // Returns true when cancellation fired during the wait.
        private static bool Sleep(int ms, CancellationToken token)
        {
            try { return token.WaitHandle.WaitOne(ms); } catch { return true; }
        }

        // Returns extra (SPS/PPS) so the caller can send the VideoConfig header.
        private (bool ok, byte[] extra, string error) InitCore(int displayIndex, double qualityMul)
        {
            if (RcNative.rc_capture_init(displayIndex) != RcNative.RC_OK)
                return (false, null, "初始化抓屏失败（需要 Windows 8+ 与支持的显卡）");

            // Learn the shared monitor's virtual-desktop rectangle so injected
            // input lands on the correct monitor.
            if (RcNative.rc_capture_get_bounds(out int ml, out int mt, out _, out _) == RcNative.RC_OK)
                RcNative.rc_input_set_bounds(ml, mt, 0, 0); // width/height filled after first frame

            // Grab one frame to learn the desktop size, then open the encoder.
            IntPtr ptr; int w = 0, h = 0; ulong pts;
            for (int i = 0; i < 200; i++)
            {
                int r = RcNative.rc_capture_frame(out ptr, out w, out h, out pts);
                if (r == RcNative.RC_OK && w > 0) break;
                Thread.Sleep(10);
            }
            if (w <= 0)
            {
                RcNative.rc_capture_free();
                return (false, null, "获取桌面尺寸失败");
            }

            // Now we know width/height — set the full input bounds.
            if (RcNative.rc_capture_get_bounds(out int bl, out int bt, out int bw, out int bh) == RcNative.RC_OK)
                RcNative.rc_input_set_bounds(bl, bt, bw, bh);

            int fps = int.TryParse(_fpsBox.Text, out var f) && f > 0 ? f : 30;
            _natW = w; _natH = h; _scaleIdx = 0; _curFps = fps;
            _baseBitrate = BitrateBase(w, h, fps);

            // Open the encoder at the (scaled) target size; RebuildEncoder also
            // pushes the initial VideoConfig once a viewer is connected.
            if (!RebuildEncoder(ScaleBitrate()))
            {
                RcNative.rc_capture_free();
                return (false, null, "初始化编码器失败（ffmpeg/x264 缺失？）");
            }
            return (true, _curExtra, null);
        }

        // Rebuild the encoder at the current scale (native * Scales[_scaleIdx])
        // with the given bitrate, and resend VideoConfig. Used for quality
        // changes, error recovery and the post-connect header. Runs under
        // _sendLock by its callers.
        private bool RebuildEncoder(int bitrate)
        {
            int ew = EncW(), eh = EncH();
            RcNative.rc_encoder_free();
            int rc = RcNative.rc_encoder_init(ew, eh, _curFps, bitrate, out IntPtr extra, out int extraSize);
            if (rc != RcNative.RC_OK) return false;
            byte[] extraBytes = extraSize > 0 ? new byte[extraSize] : Array.Empty<byte>();
            if (extraSize > 0) { Marshal.Copy(extra, extraBytes, 0, extraSize); RcNative.rc_free(extra); }
            _curW = ew; _curH = eh; _curBitrate = bitrate; _curExtra = extraBytes;
            SendToAll(MessageType.VideoConfig, Codec.BuildVideoConfig(ew, eh, _curFps, extraBytes));
            return true;
        }

        // Hot re-parameterisation (no full teardown): change resolution and/or
        // bitrate via rc_encoder_set, re-send VideoConfig (fresh SPS/PPS), and
        // roll the local recording over if the frame size changed mid-capture.
        // Runs under _sendLock. fps 默认 -1 => 沿用当前 _curFps（Phase 1B 让控制端
        // 也能即时改帧率）。
        private void HotEncoder(int sIdx, int bitrate, int fps = -1)
        {
            int useFps = fps < 0 ? _curFps : Math.Max(1, Math.Min(120, fps));
            int ew = (int)Math.Round(_natW * Scales[sIdx] / 2) * 2;
            int eh = (int)Math.Round(_natH * Scales[sIdx] / 2) * 2;
            int br = (int)Math.Min(BitrateCap, Math.Max(300, bitrate));
            if (ew == _curW && eh == _curH && br == _curBitrate && useFps == _curFps) return;
            lock (_sendLock)
            {
                int rc = RcNative.rc_encoder_set(ew, eh, useFps, br, out IntPtr extra, out int extraSize);
                if (rc != RcNative.RC_OK) { RebuildEncoder(_curBitrate); return; }
                byte[] eb = extraSize > 0 ? new byte[extraSize] : Array.Empty<byte>();
                if (extraSize > 0) { Marshal.Copy(extra, eb, 0, extraSize); RcNative.rc_free(extra); }
                _scaleIdx = sIdx; _curW = ew; _curH = eh; _curBitrate = br; _curFps = useFps; _curExtra = eb;
                SendToAll(MessageType.VideoConfig, Codec.BuildVideoConfig(ew, eh, _curFps, eb));
                if (_hostRecording && (_curW != _recW || _curH != _recH)) HostRecordRollover();
            }
        }

        // Full self-heal after a capture error (resolution change / device
        // lost / display topology change): rebuild capture + encoder + input
        // bounds and resend VideoConfig.
        private bool RebuildAll()
        {
            RcNative.rc_encoder_free();
            RcNative.rc_capture_free();
            if (RcNative.rc_capture_init(_displayIndex) != RcNative.RC_OK) return false;

            IntPtr ptr; int w = 0, h = 0; ulong pts;
            for (int i = 0; i < 200; i++)
            {
                if (RcNative.rc_capture_frame(out ptr, out w, out h, out pts) == RcNative.RC_OK && w > 0) break;
                Thread.Sleep(10);
            }
            if (w <= 0) return false;

            if (RcNative.rc_capture_get_bounds(out int bl, out int bt, out int bw, out int bh) == RcNative.RC_OK)
                RcNative.rc_input_set_bounds(bl, bt, bw, bh);

            _natW = w; _natH = h; _scaleIdx = 0;
            _baseBitrate = BitrateBase(w, h, _curFps);
            if (!RebuildEncoder(ScaleBitrate())) return false;
            // Resolution changed mid-recording: MP4 can't switch size, roll over.
            if (_hostRecording && (_curW != _recW || _curH != _recH)) HostRecordRollover();
            SetStatus($"分辨率变化，已重建 {_curW}x{_curH}", Color.Green);
            return true;
        }

        private void OnQualityChanged()
        {
            if (!_running) return;
            _qualityMul = QualityMul();
            lock (_sendLock) { RebuildEncoder(ScaleBitrate()); }
        }

        // Phase 1B：控制端实时画质协商。收到 ViewerPref 后立刻按请求重建编码器，
        // 并把该档位设为自适应控制器的基线（_qualityMul / _scaleIdx / _curFps）与
        // 下限（_viewerScaleCap，自适应升档时不得越过控制端指定的最低画质）。
        // 这样既有"即时手感"，又保留"码率稳如老狗"的自愈能力——拥塞时仍会自动降档。
        private void OnViewerPref(byte resScale, byte fps, byte quality)
        {
            if (!_running) return;
            double frac = resScale >= 75 ? 1.0 : (resScale >= 50 ? 0.75 : 0.5);
            int sIdx = 0; double best = double.MaxValue;
            for (int i = 0; i < Scales.Length; i++)
            {
                double d = Math.Abs(Scales[i] - frac);
                if (d < best) { best = d; sIdx = i; }
            }
            if (fps >= 5 && fps <= 60) _curFps = fps;
            _qualityMul = quality switch { 1 => 0.45, 2 => 0.6, 3 => 0.85, 4 => 1.0, 5 => 1.4, _ => _qualityMul };
            _viewerScaleCap = sIdx;   // 自适应升档不得越过此最低画质档
            lock (_sendLock)
            {
                HotEncoder(sIdx, ScaleBitrate(), _curFps);
            }
            SetStatus($"控制端调整画质：{resScale}% / {_curFps}fps / 档{quality}", Color.Green);
        }

        // Phase 3 缩略图墙：把当前 RGBA 帧缩小为 240px 宽的 PNG 快照并缓存。
        // 绕开单例 H264 编码器——直接读 capture 指针像素，缩放后编码 PNG，
        // 由 DispatchFromViewer(ThumbReq) 取走回传给控制端的设备卡片。
        private void UpdateThumbCache(IntPtr ptr, int w, int h)
        {
            if (ptr == IntPtr.Zero || w <= 0 || h <= 0) return;
            const int tw = 240;
            int th = Math.Max(1, (int)Math.Round(h * (double)tw / w));
            int slen = w * h * 4;
            var rgba = new byte[slen];
            Marshal.Copy(ptr, rgba, 0, slen);
            // 原生 RGBA -> .NET BGRA（Bitmap 内存序），避免 unsafe。
            var bgra = new byte[slen];
            for (int i = 0; i < slen; i += 4)
            {
                bgra[i] = rgba[i + 2]; bgra[i + 1] = rgba[i + 1]; bgra[i + 2] = rgba[i]; bgra[i + 3] = rgba[i + 3];
            }
            using var src = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var sd = src.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(bgra, 0, sd.Scan0, slen);
            src.UnlockBits(sd);
            using var dst = new Bitmap(tw, th, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, tw, th);
            }
            using var ms = new MemoryStream();
            dst.Save(ms, ImageFormat.Png);
            byte[] png = ms.ToArray();
            lock (_thumbLock) { _thumbPng = png; _thumbW = tw; _thumbH = th; }
        }

        // ---- Phase 1D：键盘监视 -------------------------------------------
        // 被控端按下的每一个物理键都通过 KeyEvent 实时广播给所有控制端，
        // 控制端在其"键盘监视"面板里看到 abcd 流。低层钩子只"读"不"改"，
        // 不影响本机任何输入；仅在共享中、已勾选且存在控制端时才广播（隐私默认关）。
        private void UpdateKeyHook()
        {
            bool want = _running && _keyMonChk != null && _keyMonChk.Checked;
            if (want && _kbHook == IntPtr.Zero)
            {
                try
                {
                    _kbDelegate = KbHookProc;   // 必须持有引用，否则被 GC 回收后回调崩溃
                    using var cur = System.Diagnostics.Process.GetCurrentProcess();
                    using var mod = cur.MainModule;
                    _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbDelegate, GetModuleHandle(mod.ModuleName), 0);
                }
                catch { _kbHook = IntPtr.Zero; }
            }
            else if (!want && _kbHook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_kbHook); } catch { }
                _kbHook = IntPtr.Zero; _kbDelegate = null;
            }
        }

        private IntPtr KbHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _keyMonChk != null && _keyMonChk.Checked && _running && TotalViewers() > 0)
            {
                int vk = Marshal.ReadInt32(lParam);
                int msg = (int)wParam;
                bool down = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
                try { SendToAll(MessageType.KeyEvent, Codec.BuildKeyEvent(vk, down ? (byte)1 : (byte)0)); } catch { }
            }
            return CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        private void SendLoop(CancellationToken token)
        {
            // Even when the desktop is static, keep the stream alive by
            // re-sending the latest frame at this cadence so the viewer never
            // sits on a stale frame. ~10fps floor.
            const int refreshMs = 100;
            int lastW = 0, lastH = 0;
            IntPtr lastPtr = IntPtr.Zero;
            int errStreak = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while (_running && !token.IsCancellationRequested)
            {
                IntPtr ptr; int w, h; ulong pts;
                int r = RcNative.rc_capture_frame(out ptr, out w, out h, out pts);

                if (r == RcNative.RC_ERR)
                {
                    // Capture broke (resolution change / device lost / the
                    // desktop is paused while the window is minimised or the
                    // session is locked). Self-heal by rebuilding the
                    // capture+encoder, but NEVER give up permanently: keep
                    // retrying so the stream resumes automatically the moment
                    // the desktop is available again. A long sleep avoids
                    // spinning while we wait. (This used to break the loop
                    // after 25 failures, which froze the viewer forever —
                    // e.g. it would never recover after the host was restored
                    // from the tray.)
                    errStreak++;
                    if (errStreak == 1) SetStatus("画面中断，正在自愈…", Color.DarkOrange);
                    bool ok;
                    lock (_sendLock) { ok = RebuildAll(); }
                    // Drop any stale frame reference so we never encode freed
                    // memory; we wait for a fresh frame before sending again.
                    lastPtr = IntPtr.Zero; lastW = 0; lastH = 0; sw.Restart();
                    if (ok) { errStreak = 0; SetStatus("画面已恢复，继续共享", Color.Green); }
                    else
                    {
                        if (errStreak == 30) SetStatus("画面暂不可用，恢复后自动继续…", Color.DarkOrange);
                        Thread.Sleep(500);
                    }
                    continue;
                }
                errStreak = 0;

                bool changed = (r == RcNative.RC_OK && w > 0);
                if (changed) { lastPtr = ptr; lastW = w; lastH = h; }

                // Phase 3 缩略图墙：抓到新帧时按 ~2s 周期生成一张 PNG 快照缓存，
                // 供控制端设备卡片预览（独立于视频 viewer 是否存在）。
                if (changed && _thumbSw.ElapsedMilliseconds >= ThumbIntervalMs)
                {
                    _thumbSw.Restart();
                    try { UpdateThumbCache(ptr, w, h); } catch { }
                }

                bool tick = sw.ElapsedMilliseconds >= refreshMs;
                if (!changed && !tick) { Thread.Sleep(4); continue; }
                if (lastW <= 0 || lastH <= 0) { Thread.Sleep(4); continue; }

                // Nobody needs video — skip encoding to save CPU/bandwidth.
                // Lightweight sessions (NoVideo) still count as "connected"
                // (so a joining full viewer gets a frame fast) but don't force
                // us to encode. Capture keeps running regardless.
                if (TotalVideoViewers() == 0) { sw.Restart(); Thread.Sleep(30); continue; }

                IntPtr encPtr = changed ? ptr : lastPtr;
                int er;
                lock (_sendLock)
                {
                    er = RcNative.rc_encoder_encode(encPtr, lastW, lastH,
                                                    out IntPtr nal, out int nalSize, out int key);
                    sw.Restart();
                    if (er == RcNative.RC_OK && nalSize > 0)
                    {
                        var buf = new byte[nalSize];
                        Marshal.Copy(nal, buf, 0, nalSize);
                        RcNative.rc_free(nal);
                        // 测量本帧在 socket 里花多久（bufferbloat 时会被卡住），
                        // 用作拥塞代理指标。
                        var ssw = System.Diagnostics.Stopwatch.StartNew();
                        SendVideoFrame((byte)key, buf);   // P2P 直连优先，否则走中转
                        long sdt = ssw.ElapsedMilliseconds;
                        _sendStallEma = _sendStallEma * 0.85 + sdt * 0.15;
                        Interlocked.Increment(ref _fpsCounter);
                        // 本地录制：同一帧 mux 进 MP4。
                        if (_hostRecording)
                        {
                            try { RcNative.rc_record_write(buf, buf.Length, _recSw.ElapsedMilliseconds, key); }
                            catch { }
                        }

                        // 发送节流（pacing）：仅在拥塞时生效。
                        // 链路健康（_sendStallEma<=12ms）时立刻直发，把延迟压到最低；
                        // 只有 socket 开始堆积（拥塞）才把本帧按"比特数/码率"摊匀，
                        // 避免 kernel send buffer 被一坨灌满 → Send() 阻塞 → RTT 飙高。
                        // 健康链路不该为"防bufferbloat"而牺牲延迟。
                        if (_curBitrate > 0 && _sendStallEma > 12)
                        {
                            long targetMs = (buf.LongLength * 8L * 1000L) / (long)(_curBitrate * 1024L);
                            long pad = targetMs - sdt;
                            if (pad > 1) Thread.Sleep((int)Math.Min(pad, 15));   // 上限 15ms，免得关键帧拉爆延迟
                        }
                    }
                }
                if (er == RcNative.RC_ERR)
                {
                    // A single bad frame must not kill the whole stream.
                    // Rebuild the encoder and continue; the next frame will
                    // re-establish a clean IDR.
                    lock (_sendLock) { RebuildEncoder(_curBitrate); }
                    Thread.Sleep(100);
                    continue;
                }

                if (DateTime.UtcNow - _fpsStamp > TimeSpan.FromSeconds(1))
                {
                    int fpsNow = _fpsCounter; _fpsCounter = 0; _fpsStamp = DateTime.UtcNow;
                    AdaptBitrate(fpsNow);
                    string atag = _audioOn ? " | 🔊" : "";
                    string ptag = _p2pReady ? " | 🔗直连" : "";
                    string txt = $"共享中 {_curW}x{_curH} ({(Scales[_scaleIdx] * 100):0}%) | {fpsNow}fps | {_curBitrate}kbps | {SecTag()} | {EncTag()}{atag}{ptag} | 控制端 {TotalViewers()}";
                    SetStatus(txt, Color.Green);
                }
            }
        }

        // Adaptive controller: driven by the frame-send stall (congestion
        // proxy) and the achieved fps. On sustained congestion it first drops
        // resolution (scale), then sheds bitrate; when the link is healthy it
        // steps resolution back up and raises bitrate toward the quality target.
        //
        // 设计目标：**码率稳如老狗**。一旦因为拥塞降下来，至少 RECOVERY_HOLD_SEC
        // 内不许再升；上行链路不是赛车道，频繁"降-升-降"是 RTT 飙高的元凶。
        // 步下/步上也不对称：降 30% 一步到位，升一次只加 8%。
        private void AdaptBitrate(int fpsNow)
        {
            if (!_adaptOn) return;
            // 启动期（前 6s）用保底码率，不做任何适应，等链路热起来再放开。
            if (_connectAt != DateTime.MinValue && (DateTime.UtcNow - _connectAt).TotalSeconds < 6) return;
            // 静帧时不要动（sendStallEma 也是 0，会误判为畅通）。
            if (fpsNow <= 11) return;
            int target = _curFps;
            // 控制端实测回传的真实 RTT（网络条件决定的延迟），比本机"发送阻塞"
            // 代理更准：用它判断链路是否真拥塞，避免误判导致的码率震荡（震荡本身
            // 就是 RTT 尖峰的元凶）。没有回传时退化成只看发送阻塞。
            bool rttBad  = _rttViewerEma > 200;
            bool rttGood = _rttViewerEma >= 0 && _rttViewerEma < 120;
            bool congested = rttBad || (_sendStallEma > 30) || (fpsNow < target * 0.65);
            bool clear     = rttGood && (_sendStallEma < 12) && (fpsNow >= target * 0.88);
            if (congested)      { _lowStreak++;  _goodStreak = 0; }
            else if (clear)     { _goodStreak++; _lowStreak = 0; }
            else                { _lowStreak = 0; _goodStreak = 0; }

            if ((DateTime.UtcNow - _lastAdapt).TotalSeconds < COOLDOWN_SEC) return;

            int qt = (int)(_baseBitrate * _qualityMul);                  // 不带 scale 的画质目标
            int targetWithScale = Math.Min(BitrateCap, Math.Max(300, (int)(qt * Scales[_scaleIdx] * Scales[_scaleIdx])));
            int floor = Math.Max(1000, targetWithScale / 3);             // 再低也别低于 1000kbps，画面就崩了

            if (_lowStreak >= DOWN_STREAK && _curBitrate > floor)
            {
                // 拥堵：优先降码率（步大），只在码率已触底时再降分辨率（保分辨率优先）。
                int newBr = Math.Max(floor, (int)(_curBitrate * DOWN_RATIO));
                if (newBr == _curBitrate && _scaleIdx < Scales.Length - 1)
                {
                    HotEncoder(_scaleIdx + 1, targetWithScale);
                    SetStatus($"链路拥堵，分辨率降一档到 {EncW()}x{EncH()}", Color.DarkOrange);
                }
                else
                {
                    HotEncoder(_scaleIdx, newBr);
                    SetStatus($"链路拥堵，码率 {_curBitrate}→{newBr} kbps", Color.DarkOrange);
                }
                _lowStreak = 0; _goodStreak = 0;
                _lastAdapt = DateTime.UtcNow;
                _recoveryHoldUntil = DateTime.UtcNow.AddSeconds(RECOVERY_HOLD_SEC);
            }
            else if (_goodStreak >= UP_STREAK
                  && DateTime.UtcNow >= _recoveryHoldUntil
                  && _curBitrate < targetWithScale)
            {
                // 顺畅且已度过恢复期：升码率，永远不越过当前档的画质目标。
                int newBr = Math.Min(targetWithScale, Math.Max(_curBitrate + 50, (int)(_curBitrate * UP_RATIO)));
                HotEncoder(_scaleIdx, newBr);
                _goodStreak = 0; _lastAdapt = DateTime.UtcNow;
            }
            else if (_goodStreak >= UP_STREAK
                  && DateTime.UtcNow >= _recoveryHoldUntil
                  && _scaleIdx > 0
                  && _scaleIdx > _viewerScaleCap   // Phase 1B：不得越过控制端指定的最低画质档
                  && _curBitrate >= targetWithScale)
            {
                // 当前档码率已顶满且链路仍健康：把分辨率升一档（从该档画质目标起步，
                // 再由上面的分支慢慢往上爬），逐步把画质还回来。
                int sIdx = _scaleIdx - 1;
                int upTarget = Math.Min(BitrateCap, Math.Max(300, (int)(qt * Scales[sIdx] * Scales[sIdx])));
                HotEncoder(sIdx, upTarget);
                SetStatus($"链路恢复，分辨率升一档到 {EncW()}x{EncH()}", Color.Green);
                _goodStreak = 0; _lastAdapt = DateTime.UtcNow;
            }
        }

        // Processes relay traffic until the socket drops. Every viewer message
        // arrives wrapped in a FromViewer(id) envelope; VJoin/VLeave keep the
        // viewer list current. Viewer departures never end the session.
        private void RecvLoop(CancellationToken token)
        {
            while (_running && !token.IsCancellationRequested)
            {
                if (_transport == null) return;
                if (!_transport.TryReceive(out var type, out var payload)) return;

                if (type == MessageType.VJoin)
                {
                    int id = Codec.ParseViewerId(payload);
                    if (id > 0)
                    {
                        string vname = Codec.ParseViewerName(payload);
                        // _viewerCount 是 volatile，读即最新值；首位 viewer 时它仍为 0。
                        bool firstViewer = (_viewerCount == 0);
                        BeginInvoke((MethodInvoker)(() =>
                        {
                            _viewers[id] = DateTime.Now;
                            if (!string.IsNullOrEmpty(vname)) _viewerNames[id] = vname;
                            RefreshViewerList();
                        }));
                        ResyncViewers(); // header + fresh IDR for the newcomer
                        BroadcastMonitorList();
                        // 多 viewer 时不走直连（直连只服务单 viewer），退回中转。
                        if (_viewers.Count > 1) CloseP2P();
                        if (_viewOnly) SendToViewer(id, MessageType.ViewOnly, Codec.BuildViewOnly(true));
                        if (_audioOn) SendToAll(MessageType.AudioConfig, Codec.BuildAudioConfig(48000, 2));
                        // 第一个 viewer 刚连上：保守启动。前 6s 用保底码率，撑过 18s 内禁止
                        // 升码率（让链路真的稳下来再放开）。这样可以避免"连上立刻按 3Mbps
                        // 推，被冲爆后又砍，反复震荡"的开局噩梦。
                        if (firstViewer)
                        {
                            _connectAt = DateTime.UtcNow;
                            _recoveryHoldUntil = _connectAt.AddSeconds(RECOVERY_HOLD_SEC);
                            _lowStreak = 0; _goodStreak = 0; _sendStallEma = 0;
                            int startBr = Math.Min(2500, Math.Max(1500, ScaleBitrate() / 2));
                            lock (_sendLock) { HotEncoder(0, startBr); }
                            SetStatus($"新连接接入，前 6s 用保底码率 {startBr} kbps（避免开局冲爆）", Color.DarkOrange);
                        }
                    }
                }
                else if (type == MessageType.VLeave)
                {
                    int id = Codec.ParseViewerId(payload);
                    lock (_liteLock) _liteViewers.Remove(id);
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        _viewers.Remove(id);
                _viewerNames.Remove(id);
                        _viewerNames.Remove(id);
                        RefreshViewerList();
                    }));
                }
                else if (type == MessageType.FromViewer)
                {
                    if (!Codec.ParseFromViewer(payload, out int vid, out var it, out var inner))
                        continue;
                    // The inner content payload was encrypted by the viewer;
                    // the envelope arrived plaintext, so decrypt it here.
                    if (_transport != null && _transport.Encrypted && Codec.ShouldEncrypt(it) && inner.Length > 0)
                        inner = _transport.DecryptPayload(inner) ?? Array.Empty<byte>();
                    // The viewer also flag-prefixed (and possibly zlib-compressed)
                    // content payloads; strip that layer after decryption.
                    if (inner.Length > 0 && Codec.ShouldEncrypt(it))
                        inner = Transport.UnwrapCompressed(inner);
                    DispatchFromViewer(vid, it, inner);
                    // Phase 7D: update last-receive heartbeat for this viewer.
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (_viewers.ContainsKey(vid)) _viewers[vid] = DateTime.UtcNow;
                    }));
                }
                else if (type == MessageType.PeerAddr)
                {
                    // The relay hands us the OTHER peer's public (ip, port); we
                    // try a direct TCP connection (hole punch) so the two user
                    // machines talk to each other and stop bouncing through the
                    // relay.
                    Codec.ParsePeerAddr(payload, out int prole, out int pvid, out string pip, out int pport, out var cands);
                    if (prole == 1 && pvid > 0) TryStartP2P(pvid, BuildCandidates(pip, pport, cands));
                }
                else if (type == MessageType.Notice)
                {
                    // 服务端推送的「版本过时」等系统通知（明文中继帧）。只提示一次。
                    if (!_versionNoticeShown)
                    {
                        _versionNoticeShown = true;
                        string text = System.Text.Encoding.UTF8.GetString(payload ?? System.Array.Empty<byte>());
                        if (!string.IsNullOrEmpty(text))
                            BeginInvoke((MethodInvoker)(() => OnChat(text, true)));
                    }
                }
                else if (type == MessageType.AdminMsg)
                {
                    // 管理端发来的明文消息（中继帧 84，非加密）。
                    string text = System.Text.Encoding.UTF8.GetString(payload ?? System.Array.Empty<byte>());
                    if (!string.IsNullOrEmpty(text))
                        BeginInvoke((MethodInvoker)(() => OnChat("[管理员] " + text, true)));
                }
                else if (type == MessageType.CtrlReq)
                {
                    // payload 是明文 UTF-8 "reqId|请求方设备名"；60 秒不回自动拒绝
                    //（服务端有同样的超时兜底）。回包走明文（不在 ShouldEncrypt 白名单）。
                    var txt = System.Text.Encoding.UTF8.GetString(payload ?? Array.Empty<byte>());
                    int bar = txt.IndexOf('|');
                    string reqId = bar >= 0 ? txt.Substring(0, bar) : txt;
                    string who = bar >= 0 && bar + 1 < txt.Length ? txt.Substring(bar + 1) : "未知设备";
                    var tp = _transport;   // 弹窗期间连接可能重建，锁定当前连接
                    // 同账号连接策略（用户可在「设置」自定义，非管理员专属）：
                    //   AutoAccept 直接允许；Block 直接拒绝；Ask 弹窗确认。
                    var policy = UserSettings.Current.SameAccount;
                    if (policy == SameAccountPolicy.AutoAccept)
                    {
                        try { tp?.Send(MessageType.CtrlAck, System.Text.Encoding.UTF8.GetBytes(reqId)); } catch { }
                        SetStatus($"已自动允许同账号设备「{who}」的控制请求", Color.Green);
                        continue;
                    }
                    if (policy == SameAccountPolicy.Block)
                    {
                        try { tp?.Send(MessageType.CtrlNak, System.Text.Encoding.UTF8.GetBytes(reqId)); } catch { }
                        SetStatus($"已拒绝同账号设备「{who}」的控制请求（策略：拒绝所有）", Color.Red);
                        continue;
                    }
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        // DefaultDesktopOnly：即使宿主窗体隐藏在托盘也强制置顶显示。
                        bool ok = MessageBox.Show(
                            $"同账号设备「{who}」请求控制本机，是否允许？\n\n（60 秒内不确认将自动拒绝）",
                            "远程控制请求",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                            MessageBoxDefaultButton.Button2,
                            MessageBoxOptions.DefaultDesktopOnly) == DialogResult.Yes;
                        try
                        {
                            tp?.Send(ok ? MessageType.CtrlAck : MessageType.CtrlNak,
                                     System.Text.Encoding.UTF8.GetBytes(reqId));
                        }
                        catch { }
                    }));
                }
            }
        }

        // Shared handler for every viewer -> host message. Fed by BOTH the
        // relay path (inner frame, already decrypted + decompressed) and the
        // P2P direct path (bare frame, the direct Transport decrypted it).
        // vid identifies the sending viewer.
        private void DispatchFromViewer(int vid, MessageType it, byte[] inner)
        {
            // View-only mode: the viewer may watch and chat, but must not
            // operate this machine. Drop every operation-bearing message.
            if (_viewOnly)
            {
                switch (it)
                {
                    case MessageType.InputEvent:
                    case MessageType.Ctrl:
                    case MessageType.Cmd:
                    case MessageType.Clipboard:
                    case MessageType.ClipImage:
                case MessageType.TerminalOpen:
                case MessageType.FOpen:
                case MessageType.FsGet:
                case MessageType.FsPut:
                case MessageType.FsDelete:
                case MessageType.FsRename:
                case MessageType.FsMkdir:
                    case MessageType.ViewerPref:
                    case MessageType.KeyEvent:
                    case MessageType.FsList:
                    case MessageType.ActRun:
                    case MessageType.AnnoFrame:
                        return; // 仅观看：禁止任何操控
                }
            }
            // 权限控制：被控端可逐个关闭控制功能
            if (!CheckPermission(it, inner)) return;
            if (it == MessageType.LinkStat)
            {
                // 控制端实测回传的真实链路质量（RTT/jitter/解码帧率/带宽）。
                // 被控端自适应控制器据此判断真实拥塞，比本机发送阻塞代理更准。
                Codec.ParseLinkStat(inner, out int rtt, out int jit, out int dfps, out int bw);
                if (rtt > 0)
                    _rttViewerEma = _rttViewerEma < 0 ? rtt : (_rttViewerEma * 0.8 + rtt * 0.2);
                return;
            }
            if (it == MessageType.InputEvent) HandleInput(inner);
            else if (it == MessageType.Hello) ResyncViewers();
            else if (it == MessageType.Ping)
            {
                // Targeted pong so each viewer measures its own RTT. Routes via
                // P2P when available so the measurement stays on the direct path.
                SendToViewer(vid, MessageType.Ping, inner);
                // Phase 7A: Also send Pong for pure connection-health checks (no payload).
                SendToViewer(vid, MessageType.Pong, Array.Empty<byte>());
            }
            else if (it == MessageType.Clipboard)
            {
                string text = Codec.ParseClipboard(inner);
                if (!string.IsNullOrEmpty(text) && text != _lastClipboard)
                {
                    _lastClipboard = text;
                    BeginInvoke((MethodInvoker)(() => SafeSetClipboard(text)));
                }
            }
            else if (it == MessageType.ClipImage)
            {
                if (inner != null && inner.Length > 0)
                {
                    int h = ComputeClipHash(inner);
                    if (h != _lastClipImgHash)
                    {
                        _lastClipImgHash = h;
                        var png = inner;
                        BeginInvoke((MethodInvoker)(() => ApplyClipboardImage(png)));
                    }
                }
            }
            else if (it == MessageType.Chat)
            {
                string text = Codec.ParseChat(inner);
                if (!string.IsNullOrEmpty(text))
                    BeginInvoke((MethodInvoker)(() => OnChat("[控制端#" + vid + "] " + text, true)));
            }
            else if (it == MessageType.Ctrl)
            {
                int cmd = Codec.ParseCtrl(inner);
                // 远程关机/重启需被控端确认（防误触与同账号他人滥用）。
                if (cmd == 3 || cmd == 4)
                {
                    string name = cmd == 3 ? "重启" : "关闭";
                    if (!ConfirmDangerous("控制端请求" + name + "本计算机。\n\n允许执行？"))
                    {
                        BeginInvoke((MethodInvoker)(() => OnChat("[系统] 已拒绝控制端的" + name + "请求", true)));
                        return;
                    }
                }
                BeginInvoke((MethodInvoker)(() => ExecSystemCommand(cmd)));
            }
            else if (it == MessageType.Cmd)
            {
                int idx = Codec.ParseCtrl(inner);
                BeginInvoke((MethodInvoker)(() => SwitchDisplay(idx)));
            }
            else if (it == MessageType.FOpen)
            {
                Codec.ParseFOpen(inner, out int fid, out int dir, out string name, out long size);
                BeginInvoke((MethodInvoker)(() => OnIncomingFile(vid, fid, name, size)));
            }
            else if (it == MessageType.FResp)
            {
                Codec.ParseFResp(inner, out int id, out int accept);
                BeginInvoke((MethodInvoker)(() => OnSendAccepted(vid, id, accept == 1)));
            }
            else if (it == MessageType.FData)
            {
                Codec.ParseFData(inner, out int id, out var chunk);
                if (!_ft.ReceiveData(id, chunk))
                    SendToViewer(vid, MessageType.FCancel, Codec.BuildId(id));
            }
            else if (it == MessageType.FEnd)
            {
                int id = Codec.ParseId(inner);
                var tt = _ft.Find(id);
                string saved = tt?.Path;
                _ft.ReceiveEnd(id);
                if (!string.IsNullOrEmpty(saved))
                    BeginInvoke((MethodInvoker)(() => { SetStatus("文件已接收: " + saved, Color.Green); NotifyFileSaved(saved); }));
                else
                    SetStatus("文件接收完成", Color.Green);
            }
            else if (it == MessageType.FCancel)
            {
                int id = Codec.ParseId(inner);
                _ft.ReceiveCancel(id);
            }
            else if (it == MessageType.TerminalOpen)
            {
                // 危险操作：被控端弹确认（防止同账号他人/误触在不知情下执行命令）。
                Codec.ParseTerminalOpen(inner, out int cols, out int rows, out byte shell);
                string who = "[控制端#" + vid + "]";
                if (!ConfirmDangerous(who + " 请求打开远程终端。\n\n对方可在你毫无察觉的情况下执行任意命令（关机、删文件等）。\n\n允许打开？"))
                {
                    SendToViewer(vid, MessageType.TerminalClose, Codec.BuildTerminalClose(1));
                    BeginInvoke((MethodInvoker)(() => OnChat("[系统] 已拒绝 #" + vid + " 的终端请求", true)));
                    return;
                }
                OpenTerminalFor(vid, cols, rows, shell);
            }
            else if (it == MessageType.TerminalData)
            {
                byte[] data = Codec.ParseTerminalData(inner);
                WriteTerminal(vid, data);
            }
            else if (it == MessageType.TerminalResize)
            {
                Codec.ParseTerminalResize(inner, out int cols, out int rows);
                ResizeTerminal(vid, cols, rows);
            }
            else if (it == MessageType.TerminalClose)
            {
                CloseTerminal(vid, Codec.ParseTerminalClose(inner));
            }
            else if (it == MessageType.NoVideo)
            {
                // 轻量会话：该 viewer 不需要视频，跳过编码与发送。
                lock (_liteLock) _liteViewers.Add(vid);
            }
            else if (it == MessageType.ViewerPref)   // Phase 1B：控制端实时画质协商
            {
                Codec.ParseViewerPref(inner, out byte resScale, out byte fps, out byte quality);
                OnViewerPref(resScale, fps, quality);
            }
            else if (it == MessageType.ThumbReq)   // Phase 3 缩略图墙：回当前缓存快照
            {
                Codec.ParseThumbReq(inner, out _);
                byte[] png; int tw, th;
                lock (_thumbLock) { png = _thumbPng; tw = _thumbW; th = _thumbH; }
                if (png != null && png.Length > 0)
                    SendToViewer(vid, MessageType.ThumbFrame, Codec.BuildThumbFrame(tw, th, png));
            }
            else if (it == MessageType.ActRun)   // Phase 4 动作编排：执行动作并回结果
            {
                OnActRun(vid, inner);
            }
            else if (it == MessageType.AnnoFrame)   // Phase 5 会话内标注：在被控端屏幕上显示箭头/文字
            {
                OnAnno(inner);
            }
            // ---- Phase 2 远程文件浏览器 ----
            else if (it == MessageType.FsList)      OnFsList(vid, Codec.ParsePath(inner));
            else if (it == MessageType.FsGet)       { Codec.ParseFsGet(inner, out int gid, out string gpath); long off = Codec.ParseFsGetOffset(inner); OnFsGet(vid, gid, gpath, off); }
            else if (it == MessageType.FsChunk)     OnFsChunk(vid, inner);     // 上传方向（viewer->host）
            else if (it == MessageType.FsPut)       { Codec.ParseFsPut(inner, out int pid, out string pput, out long sz); long poff = Codec.ParseFsPutOffset(inner); OnFsPut(vid, pid, pput, sz, poff); }
            else if (it == MessageType.FsPutEnd)    OnFsPutEnd(Codec.ParseId(inner));
            else if (it == MessageType.FsCancel)    OnFsCancel(Codec.ParseId(inner), vid);
            else if (it == MessageType.FsDelete)    OnFsDelete(vid, Codec.ParsePath(inner));
            else if (it == MessageType.FsRename)    { Codec.ParseFsRename(inner, out string o, out string n); OnFsRename(vid, o, n); }
            else if (it == MessageType.FsMkdir)     OnFsMkdir(vid, Codec.ParsePath(inner));
            // A viewer's Bye is informational only — VLeave follows.
        }

        // ---- Phase 4 动作编排：被控端执行控制端下发的动作并返回结果 --------
        // Phase 9 权限拦截：对照 UserSettings 检查每个操作是否被允许。
        private bool CheckPermission(MessageType it, byte[] inner)
        {
            var s = UserSettings.Current;
            // 同账号连接自动接受 + 跳过权限：不做任何限制
            if (s.SameAccount == SameAccountPolicy.AutoAccept && s.SameAccountBypassPerms)
                return true;
            switch (it)
            {
                case MessageType.FsGet:
                case MessageType.FsPut:
                case MessageType.FsDelete:
                case MessageType.FsRename:
                case MessageType.FsMkdir:
                case MessageType.FsList:
                case MessageType.FOpen:
                    return s.AllowFileTransfer;
                case MessageType.TerminalOpen:
                case MessageType.TerminalData:
                case MessageType.TerminalClose:
                case MessageType.TerminalResize:
                    return s.AllowTerminal;
                case MessageType.InputEvent:
                case MessageType.KeyEvent:
                    return s.AllowRemoteInput;
                case MessageType.Clipboard:
                case MessageType.ClipImage:
                    return s.AllowClipboard;
                case MessageType.ActRun:
                    if (!s.AllowCommand && !s.AllowRebootShutdown) return false;
                    try
                    {
                        Codec.ParseActRun(inner, out _, out byte kind, out _, out _);
                        if (kind == 6 || kind == 7) return s.AllowRebootShutdown;
                        return s.AllowCommand;
                    }
                    catch { return false; }
                default:
                    return true;
            }
        }
        // 控制端把动作编码进 ActRun 推过来；这里解析后在后台线程执行，
        // 执行完毕通过 SendToViewer(ActResult) 把退出码/输出回传。
        private void OnActRun(int vid, byte[] inner)
        {
            Codec.ParseActRun(inner, out int actionId, out byte kind, out byte silent, out byte[] payload);
            string param = Encoding.UTF8.GetString(payload ?? Array.Empty<byte>());
            // 后台执行，避免阻塞收包循环（Exec 可能跑几十秒）。
            _ = Task.Run(() => RunAction(vid, actionId, kind, silent != 0, param));
        }

        // ---- Phase 5 会话内标注：被控端全屏透明覆盖层显示箭头/文字 --------
        // 纯视觉引导，不影响本机操作；收到新标注后自动续期，超时或 Clear 后消失。
        private AnnotationOverlay _annoOverlay;
        private void OnAnno(byte[] inner)
        {
            Codec.ParseAnno(inner, out var a);
            BeginInvoke((MethodInvoker)(() =>
            {
                if (_annoOverlay == null)
                {
                    _annoOverlay = new AnnotationOverlay();
                    _annoOverlay.Show();
                }
                _annoOverlay.Add(a);
            }));
        }

        private void RunAction(int vid, int actionId, byte kind, bool silent, string param)
        {
            int code = 0; string output = "";
            try
            {
                switch (kind)
                {
                    case 1: // Exec：跑 cmd /c，捕获合并输出 + 退出码
                        RunCmdCapture(param, 30000, out int exit, out string o);
                        code = exit; output = o;
                        break;
                    case 2: // Launch：启动程序（不阻塞等待）
                        SplitFirst(param, '\t', out string path, out string args);
                        if (string.IsNullOrWhiteSpace(path)) { code = 1; output = "未指定程序路径"; break; }
                        var psi = new ProcessStartInfo
                        {
                            FileName = path,
                            Arguments = args,
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Normal,
                        };
                        Process.Start(psi);
                        code = 0; output = "已启动: " + path;
                        break;
                    case 3: // Keys：在本机前台窗口键入文本
                        var keys = param;
                        BeginInvoke((MethodInvoker)(() => { try { System.Windows.Forms.SendKeys.SendWait(keys); } catch { } }));
                        code = 0; output = "已键入 " + keys.Length + " 字符";
                        break;
                    case 4: // Lock：锁定工作站
                        BeginInvoke((MethodInvoker)(() => RcNative.rc_system_lock()));
                        code = 0; output = "已锁屏";
                        break;
                    case 5: // Message：弹窗提示（模态，用户点确定后动作才算完成）
                        SplitFirst(param, '\t', out string title, out string body);
                        BeginInvoke((MethodInvoker)(() =>
                            MessageBox.Show(body, string.IsNullOrEmpty(title) ? "消息" : title,
                                MessageBoxButtons.OK, MessageBoxIcon.Information)));
                        code = 0; output = "已弹窗";
                        break;
                    case 6: // Reboot
                    case 7: // Shutdown
                        int cmd = kind == 6 ? 3 : 4;
                        string nm = kind == 6 ? "重启" : "关机";
                        bool ok = silent || ConfirmDangerous("控制端请求" + nm + "本计算机。\n\n允许执行？");
                        if (ok) { int c = cmd; BeginInvoke((MethodInvoker)(() => ExecSystemCommand(c))); code = 0; output = "已" + nm; }
                        else { code = 2; output = "主机已拒绝" + nm; }
                        break;
                    default:
                        code = 3; output = "未知动作类型: " + kind;
                        break;
                }
            }
            catch (Exception ex) { code = 1; output = "执行异常: " + ex.Message; }
            try { SendToViewer(vid, MessageType.ActResult, Codec.BuildActResult(actionId, code, output)); } catch { }
        }

        // 跑 cmd /c <cmd>，最多 timeoutMs，捕获 stdout+stderr（合并、截断到 16KB）。
        private void RunCmdCapture(string cmd, int timeoutMs, out int exitCode, out string output)
        {
            exitCode = -1; output = "";
            if (string.IsNullOrWhiteSpace(cmd)) { exitCode = 0; output = ""; return; }
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c " + cmd,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var sb = new StringBuilder();
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                if (!proc.WaitForExit(timeoutMs))
                {
                    try { proc.Kill(); } catch { }
                    exitCode = -2;
                    output = sb.ToString();
                    if (output.Length > 16000) output = output.Substring(0, 16000);
                    output += "\n[命令执行超时，已被终止]";
                    return;
                }
                exitCode = proc.ExitCode;
                output = sb.ToString();
                if (output.Length > 16000) output = output.Substring(0, 16000);
            }
            catch (Exception ex)
            {
                exitCode = -1; output = "启动失败: " + ex.Message;
            }
        }

        // 按首个 sep 把 s 切成 (a, b)。b 在 sep 不存在时为空串。
        private static void SplitFirst(string s, char sep, out string a, out string b)
        {
            a = ""; b = "";
            if (string.IsNullOrEmpty(s)) return;
            int i = s.IndexOf(sep);
            if (i < 0) { a = s; return; }
            a = s.Substring(0, i);
            b = s.Substring(i + 1);
        }

        // ---- Phase 2 远程文件浏览器：host 端文件系统处理 -----------------
        private const int FS_CHUNK = 256 * 1024;   // 单帧传输块 256KB

        private int FsNewId() { lock (_fsLock) return _fsXferSeq++; }

        private void SendFs(int vid, MessageType t, byte[] p)
        {
            try { SendToViewer(vid, t, p); } catch { }
        }

        // 列目录：空路径=盘符根；否则枚举该目录的文件夹与文件。
        private void OnFsList(int vid, string path)
        {
            if (!_running) return;
            _ = Task.Run(() =>
            {
                try
                {
                    var items = new List<FsEntry>();
                    int err = 0; string errMsg = "";
                    if (string.IsNullOrEmpty(path))
                    {
                        foreach (var d in DriveInfo.GetDrives())
                        {
                            items.Add(new FsEntry { IsDir = true, Name = d.RootDirectory.FullName, Mtime = 0 });
                        }
                    }
                    else
                    {
                        var di = new DirectoryInfo(path);
                        if (!di.Exists) { err = 1; errMsg = "路径不存在: " + path; }
                        else
                        {
                            try
                            {
                                var dirs = di.EnumerateDirectories();
                                var files = di.EnumerateFiles();
                                foreach (var d in dirs.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                                    items.Add(new FsEntry { IsDir = true, Name = d.Name, Mtime = d.LastWriteTimeUtc.ToFileTimeUtc() });
                                foreach (var f in files.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                                    items.Add(new FsEntry { IsDir = false, Name = f.Name, Size = f.Length, Mtime = f.LastWriteTimeUtc.ToFileTimeUtc() });
                            }
                            catch (UnauthorizedAccessException ue) { err = 2; errMsg = "无权限访问部分内容: " + ue.Message; }
                            catch (Exception ex) { err = 3; errMsg = ex.Message; }
                        }
                    }
                    SendFs(vid, MessageType.FsListResp, Codec.BuildFsListResp(path ?? "", err, items));
                    if (err != 0)
                        BeginInvoke((MethodInvoker)(() => OnChat("[文件浏览器] 列目录失败: " + errMsg, true)));
                }
                catch (Exception ex)
                {
                    SendFs(vid, MessageType.FsListResp, Codec.BuildFsListResp(path ?? "", 3, new List<FsEntry>()));
                    BeginInvoke((MethodInvoker)(() => OnChat("[文件浏览器] 列目录异常: " + ex.Message, true)));
                }
            });
        }

        // 下载：viewer -> host 请求，host 读文件并分块推送给 viewer。
        private void OnFsGet(int vid, int id, string path, long offset = 0)
        {
            if (!_running) return;
            FileStream fs = null;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, FS_CHUNK, FileOptions.SequentialScan);
                if (offset > 0 && offset < fs.Length) fs.Seek(offset, SeekOrigin.Begin);
            }
            catch (Exception ex)
            {
                SendFs(vid, MessageType.FsGetErr, Codec.BuildFsGetErr(id, 1, "打开失败: " + ex.Message));
                return;
            }
            var xfer = new FsXfer { Id = id, Vid = vid, Path = path, Stream = fs, Total = fs.Length, Done = offset, IsUpload = false };
            lock (_fsLock) _fsXfers[id] = xfer;
            SendFs(vid, MessageType.FsGetReady, Codec.BuildFsGetReady(id, 0, fs.Length, Path.GetFileName(path)));
            _ = Task.Run(() => FsDownloadPump(xfer));
        }

        private void FsDownloadPump(FsXfer x)
        {
            try
            {
                var buf = new byte[FS_CHUNK];
                int n;
                while ((n = x.Stream.Read(buf, 0, buf.Length)) > 0)
                {
                    if (x.Aborted) break;
                    var chunk = new byte[n];
                    Array.Copy(buf, chunk, n);
                    SendFs(x.Vid, MessageType.FsChunk, Codec.BuildFData(x.Id, chunk));
                    x.Done += n;
                }
                if (!x.Aborted)
                    SendFs(x.Vid, MessageType.FsGetEnd, Codec.BuildId(x.Id));
            }
            catch (Exception ex)
            {
                if (!x.Aborted)
                    SendFs(x.Vid, MessageType.FsGetErr, Codec.BuildFsGetErr(x.Id, 2, "传输中断: " + ex.Message));
            }
            finally
            {
                try { x.Stream?.Dispose(); } catch { }
                lock (_fsLock) _fsXfers.Remove(x.Id);
            }
        }

        // 上传：viewer -> host，host 建文件并接收分块。
        private void OnFsPut(int vid, int id, string path, long size, long offset = 0)
        {
            if (!_running) return;
            FileStream fs = null;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                // Phase 7H: 续传时用追加模式
                var mode = offset > 0 ? FileMode.OpenOrCreate : FileMode.Create;
                fs = new FileStream(path, mode, FileAccess.Write, FileShare.None, FS_CHUNK, FileOptions.SequentialScan);
                if (offset > 0 && fs.Length != offset) fs.Seek(offset, SeekOrigin.Begin);
            }
            catch (Exception ex)
            {
                SendFs(vid, MessageType.FsPutReady, Codec.BuildFsPutReady(id, 1, "无法写入: " + ex.Message));
                try { fs?.Dispose(); } catch { }
                return;
            }
            lock (_fsLock) _fsXfers[id] = new FsXfer { Id = id, Vid = vid, Path = path, Stream = fs, Total = size, Done = offset, IsUpload = true };
            SendFs(vid, MessageType.FsPutReady, Codec.BuildFsPutReady(id, 0, ""));
        }

        // FsChunk 双向复用：上传方向（viewer->host）在此落地写盘。
        private void OnFsChunk(int vid, byte[] inner)
        {
            Codec.ParseFData(inner, out int id, out var chunk);
            FsXfer x;
            lock (_fsLock) _fsXfers.TryGetValue(id, out x);
            if (x == null || !x.IsUpload) return;   // 找不到/非上传：忽略（可能是已结束的旧块）
            try { x.Stream.Write(chunk, 0, chunk.Length); x.Done += chunk.Length; }
            catch (Exception ex)
            {
                SendFs(vid, MessageType.FsGetErr, Codec.BuildFsGetErr(id, 3, "写入失败: " + ex.Message));
                try { x.Stream?.Dispose(); } catch { }
                lock (_fsLock) _fsXfers.Remove(id);
            }
        }

        private void OnFsPutEnd(int id)
        {
            FsXfer x;
            lock (_fsLock) { if (!_fsXfers.TryGetValue(id, out x)) return; }
            try { x.Stream.Flush(); x.Stream.Dispose(); }
            catch (Exception ex)
            {
                SendFs(x.Vid, MessageType.FsPutAck, Codec.BuildFsPutAck(id, 1, "收尾失败: " + ex.Message));
                lock (_fsLock) _fsXfers.Remove(id);
                return;
            }
            SendFs(x.Vid, MessageType.FsPutAck, Codec.BuildFsPutAck(id, 0, ""));
            lock (_fsLock) _fsXfers.Remove(id);
        }

        private void OnFsCancel(int id, int vid)
        {
            FsXfer x;
            lock (_fsLock) { if (!_fsXfers.TryGetValue(id, out x)) return; x.Aborted = true; }
            try { x.Stream?.Dispose(); } catch { }
            lock (_fsLock) _fsXfers.Remove(id);
            if (!x.IsUpload) SendFs(vid, MessageType.FsGetErr, Codec.BuildFsGetErr(id, 4, "已取消"));
        }

        private void OnFsDelete(int vid, string path)
        {
            if (!_running) return;
            try
            {
                if (Directory.Exists(path)) { Directory.Delete(path, true); }
                else if (File.Exists(path)) { File.Delete(path); }
                else { SendFs(vid, MessageType.FsDeleteResp, Codec.BuildFsDeleteResp(1, "路径不存在: " + path)); return; }
                SendFs(vid, MessageType.FsDeleteResp, Codec.BuildFsDeleteResp(0, ""));
            }
            catch (Exception ex) { SendFs(vid, MessageType.FsDeleteResp, Codec.BuildFsDeleteResp(2, ex.Message)); }
        }

        private void OnFsRename(int vid, string oldPath, string newPath)
        {
            if (!_running) return;
            try
            {
                if (Directory.Exists(oldPath)) Directory.Move(oldPath, newPath);
                else if (File.Exists(oldPath)) File.Move(oldPath, newPath);
                else { SendFs(vid, MessageType.FsRenameResp, Codec.BuildFsRenameResp(1, "源不存在: " + oldPath)); return; }
                SendFs(vid, MessageType.FsRenameResp, Codec.BuildFsRenameResp(0, ""));
            }
            catch (Exception ex) { SendFs(vid, MessageType.FsRenameResp, Codec.BuildFsRenameResp(2, ex.Message)); }
        }

        private void OnFsMkdir(int vid, string path)
        {
            if (!_running) return;
            try
            {
                Directory.CreateDirectory(path);
                SendFs(vid, MessageType.FsMkdirResp, Codec.BuildFsMkdirResp(0, ""));
            }
            catch (Exception ex) { SendFs(vid, MessageType.FsMkdirResp, Codec.BuildFsMkdirResp(1, ex.Message)); }
        }

        // ---- 远程终端：被控端隐藏 Shell + I/O 中继 ------------------------
        private sealed class TerminalSession
        {
            public int Vid;
            public Process Proc;
            public Stream Stdin;          // 直接写底层流，避免 StreamWriter 编码干扰
            public CancellationTokenSource Cts = new();
            public int _cols = 80;
        }

        /// <summary>被控端弹确认框（阻塞调用线程直到用户响应）。</summary>
        private bool ConfirmDangerous(string text)
        {
            bool res = false;
            var tcs = new TaskCompletionSource<bool>();
            BeginInvoke((MethodInvoker)(() =>
            {
                try { res = MessageBox.Show(this, text, "安全确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes; }
                catch { res = false; }
                tcs.TrySetResult(true);
            }));
            try { tcs.Task.GetAwaiter().GetResult(); } catch { }
            return res;
        }

        private void OpenTerminalFor(int vid, int cols, int rows, byte shell)
        {
            TerminalSession sess = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = shell == 1 ? "powershell.exe" : "cmd.exe",
                    Arguments = shell == 1 ? "-NoLogo -NoExit" : "/K",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    // 显式声明 stdout 编码为 UTF-8：否则 .NET 默认按系统 ANSI（中文系统=GBK）
                    // 去读 cmd 字节流，控制端用 UTF-8 解码后非 ASCII 字节会被替换成 U+FFFD，
                    // 中文 banner/prompt 直接乱码消失。设成 UTF-8 后控制端收到的字节流就是干净的 UTF-8。
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.Start();
                sess = new TerminalSession
                {
                    Vid = vid,
                    Proc = proc,
                    Stdin = proc.StandardInput.BaseStream,
                };
                lock (_termLock) _terminals[vid] = sess;

                // cmd 切换到 UTF-8 代码页，保证中文输出/输入不乱码；powershell 直接走 UTF-8。
                // 注意：必须用 \r\n（CRLF）而不是 \n（LF）。Windows console 期待 CRLF 作为行结束符，
                // 单 LF 在某些 cmd 版本下会被缓冲/吞掉，导致后续 prompt 一直不打印、banner 也没换行。
                if (shell != 1)
                {
                    try { var b = System.Text.Encoding.UTF8.GetBytes("chcp 65001 >nul" + Environment.NewLine); sess.Stdin.Write(b, 0, b.Length); sess.Stdin.Flush(); } catch { }
                }

                int v = vid; var s = sess;
                proc.Exited += (sender, e) =>
                {
                    try { SendToViewer(v, MessageType.TerminalClose, Codec.BuildTerminalClose(0)); } catch { }
                    lock (_termLock) { try { _terminals.Remove(v); } catch { } }
                };

                // 异步泵出 stdout/stderr，原样（UTF-8 字节）回传控制端。
                _ = Task.Run(() => PumpTerminalOutput(v, s, 0), s.Cts.Token);
                _ = Task.Run(() => PumpTerminalOutput(v, s, 1), s.Cts.Token);
            }
            catch (Exception ex)
            {
                try { SendToViewer(vid, MessageType.TerminalClose, Codec.BuildTerminalClose(2)); } catch { }
                BeginInvoke((MethodInvoker)(() => OnChat("[系统] 终端启动失败: " + ex.Message, true)));
                if (sess != null) { try { sess.Proc?.Kill(); } catch { } lock (_termLock) _terminals.Remove(vid); }
            }
        }

        private void PumpTerminalOutput(int vid, TerminalSession s, int streamId)
        {
            Stream src = streamId == 0 ? s.Proc.StandardOutput.BaseStream : s.Proc.StandardError.BaseStream;
            var buf = new byte[4096];
            while (!s.Cts.IsCancellationRequested)
            {
                int n;
                try { n = src.Read(buf, 0, buf.Length); }
                catch { break; }
                if (n <= 0) break;
                var chunk = new byte[n];
                Array.Copy(buf, chunk, n);
                try { SendToViewer(vid, MessageType.TerminalOut, Codec.BuildTerminalOut(streamId, chunk)); } catch { break; }
            }
        }

        private void WriteTerminal(int vid, byte[] data)
        {
            if (data == null || data.Length == 0) return;
            TerminalSession s;
            lock (_termLock) _terminals.TryGetValue(vid, out s);
            if (s == null || s.Proc == null || s.Proc.HasExited) return;
            // 写入前把 \n 规整为 \r\n：用户在 _in 输入 Enter 时 SendLine 已经拼了 \n，
            // 但 Windows console 的 stdin reader 只认 CRLF。光 LF 在某些情况下会导致整行被吞，
            // prompt 不会换行，看上去就像"卡住"。
            // 这里用最小开销做一次替换（已含 \r 的不动），避免重复加 \r 产生 \r\r\n。
            try
            {
                if (data.Length >= 1 && data[data.Length - 1] == (byte)'\n' &&
                    (data.Length < 2 || data[data.Length - 2] != (byte)'\r'))
                {
                    var norm = new byte[data.Length + 1];
                    Array.Copy(data, norm, data.Length);
                    norm[data.Length] = (byte)'\r';
                    s.Stdin.Write(norm, 0, norm.Length);
                }
                else
                {
                    s.Stdin.Write(data, 0, data.Length);
                }
                s.Stdin.Flush();
            }
            catch { }
        }

        private void ResizeTerminal(int vid, int cols, int rows)
        {
            // 隐藏进程无可见窗口，列宽变化主要用于控制端做换行/排版；
            // 此处仅保留意图，实际缓冲尺寸调整交由控制端显示层处理。
            TerminalSession s;
            lock (_termLock) _terminals.TryGetValue(vid, out s);
            if (s != null) s._cols = cols;
        }

        private void CloseTerminal(int vid, int code)
        {
            TerminalSession s;
            lock (_termLock) { _terminals.TryGetValue(vid, out s); _terminals.Remove(vid); }
            if (s == null) return;
            try { s.Cts.Cancel(); } catch { }
            try { if (!s.Proc.HasExited) s.Proc.Kill(); } catch { }
            try { s.Proc.Dispose(); } catch { }
            BeginInvoke((MethodInvoker)(() => OnChat("[系统] 终端 #" + vid + " 已关闭", true)));
        }

        // ---- P2P 直连（TCP hole punch）------------------------------------
        // 中转只负责交换两端公网地址；两端用户机器自己协商直连 TCP，把视频/输入
        // 流量从"绕中转"改成"用户网络直连"。打洞失败或多了 viewer 自动退回中转。
        private void TryStartP2P(int vid, List<(string ip, int port)> cands)
        {
            if (!_p2pOn || !_running) return;
            if (_viewers.Count != 1) return;          // 仅单 viewer 走直连
            if (_p2pVid == vid && _p2p != null) return;
            _p2pEpVid = vid; _p2pCandidates = cands;
            var cts = new CancellationTokenSource();
            _p2pCts = cts;
            var token = cts.Token;
            _ = Task.Run(() => P2PConnect(vid, cands, token), token);
        }

        // 合并中继看到的地址与对端 STUN 公网候选，去重后作为打洞候选列表。
        private List<(string ip, int port)> BuildCandidates(string ip, int port, List<(string ip, int port)> cands)
        {
            var list = new List<(string ip, int port)>();
            if (!string.IsNullOrEmpty(ip) && port > 0) list.Add((ip, port));
            if (cands != null) foreach (var c in cands) if (!list.Contains(c)) list.Add(c);
            return list;
        }

        private void P2PConnect(int vid, List<(string ip, int port)> cands, CancellationToken token)
        {
            // 反复尝试同时 connect：提高两端 SYN 交叉的概率（TCP 打洞靠的就是
            // 两端几乎同时外向连接，让各自 NAT 放行对方进来的 SYN）。
            foreach (var c in cands)
            {
                for (int i = 0; i < 8 && !token.IsCancellationRequested; i++)
                {
                    if (!_running || _viewers.Count != 1) return;
                    var tc = TryConnectTcp(c.ip, c.port, 1500);
                    if (tc != null) { SetupP2P(vid, tc, token); return; }
                    Sleep(250, token);
                }
            }
        }

        private void SetupP2P(int vid, TcpClient tc, CancellationToken token)
        {
            if (!_running || _viewers.Count != 1 || token.IsCancellationRequested)
            { try { tc.Close(); } catch { } return; }
            var t = new Transport(tc);
            t.SetCrypto(_aead);                       // 复用会话 E2E 密钥
            lock (_p2pLock)
            {
                try { _p2p?.Dispose(); } catch { }
                _p2p = t; _p2pVid = vid;
            }
            _p2pEverConnected = true;
            SetStatus($"已与控制端 #{vid} 建立直连 TCP（绕开中转）", Color.Green);
            // 先启动直连接收循环，再发 Hello，确保能收到对方回应的 Hello（双方
            // 都确认后才真正切换流量，避免单边连上却对端没在收导致丢帧）。
            var link = t;
            _ = Task.Run(() => P2PRecvLoop(vid, token, link), token);
            try { t.Send(MessageType.Hello, Array.Empty<byte>()); }
            catch { CloseP2P(); return; }
        }

        private void P2PRecvLoop(int vid, CancellationToken token, Transport link)
        {
            try
            {
                while (_running && !token.IsCancellationRequested && link == _p2p)
                {
                    if (!link.TryReceive(out var type, out var payload)) break;
                    if (type == MessageType.Hello)
                    {
                        // 对端确认直连：双方都收到 Hello 后才真正把流量切到直连。
                        if (!_p2pReady)
                        {
                            _p2pReady = true;
                            SetStatus($"P2P 直连已确认（控制端 #{vid}）", Color.Green);
                            // 推一帧关键帧，让控制端解码器在直连通道上同步。
                            lock (_sendLock) { ResyncViewers(); }
                        }
                        continue;
                    }
                    // viewer -> host 的内层消息（裸消息，直连 Transport 已解密）。
                    DispatchFromViewer(vid, type, payload);
                }
            }
            catch { }
            // 直连断开：自动退回中转（单 viewer 时尝试自动重连）。
            if (link == _p2p) CloseP2P();
        }

        private void CloseP2P()
        {
            try { _p2pCts?.Cancel(); } catch { }
            Transport old;
            lock (_p2pLock) { old = _p2p; _p2p = null; }
            _p2pVid = -1; _p2pReady = false;
            try { old?.Dispose(); } catch { }
            if (_running && _p2pOn && _viewers.Count == 1 && _p2pEverConnected && !_p2pRetrying)
            {
                // 曾经连上过却掉了，单 viewer 时静默重试（NAT 临时失效等）。
                _p2pRetrying = true;
                var vid = _p2pEpVid; var cands = _p2pCandidates;
                _ = Task.Run(() =>
                {
                    Sleep(2000, CancellationToken.None);
                    _p2pRetrying = false;
                    if (cands != null) TryStartP2P(vid, cands);
                });
            }
        }

        private static TcpClient? TryConnectTcp(string ip, int port, int timeoutMs)
        {
            try
            {
                var tc = new TcpClient();
                var ar = tc.BeginConnect(ip, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    try { tc.Close(); } catch { }
                    return null;
                }
                tc.EndConnect(ar);
                return tc;
            }
            catch { return null; }
        }

        // Rebuild the encoder (=> resend VideoConfig + emit a fresh IDR) so a
        // newly joined viewer can sync. Rate-limited: several viewers joining
        // at once (e.g. after a host reconnect) trigger only one rebuild.
        private void ResyncViewers()
        {
            if ((DateTime.UtcNow - _lastHeader).TotalMilliseconds < 300) return;
            _lastHeader = DateTime.UtcNow;
            lock (_sendLock) { RebuildEncoder(_curBitrate); }
        }

        // ---- viewer management ----------------------------------------------
        private void RefreshViewerList()
        {
            _viewerCount = _viewers.Count;
            int sel = _viewerList.SelectedIndex;
            _viewerList.BeginUpdate();
            _viewerList.Items.Clear();
            foreach (var kv in _viewers)
                _viewerList.Items.Add($"控制端 #{kv.Key}  (加入 {kv.Value:HH:mm:ss})");
            _viewerList.EndUpdate();
            if (sel >= 0 && sel < _viewerList.Items.Count) _viewerList.SelectedIndex = sel;
            _kickBtn.Enabled = _running && _viewerList.SelectedIndex >= 0;
            if (_running)
            {
                string txt = TotalViewers() == 0
                    ? $"共享中 {_curW}x{_curH} | 等待控制端连接…"
                    : $"共享中 {_curW}x{_curH} | 控制端 {TotalViewers()} 个";
                _status.Text = txt; _status.ForeColor = Color.Green;
            }
        }

        // Phase 7D: Periodically remove viewers that haven't sent any data
        // (including Pings) for 25+ seconds. This catches dead control-side
        // connections that didn't send a proper VLeave.
        private void PurgeStaleViewers()
        {
            if (!_running) return;
            var now = DateTime.UtcNow;
            var stale = new List<int>();
            foreach (var kv in _viewers)
                if ((now - kv.Value).TotalSeconds > 25)
                    stale.Add(kv.Key);
            if (stale.Count == 0) return;
            foreach (int id in stale)
            {
                _viewers.Remove(id);
                _viewerNames.Remove(id);
                lock (_liteLock) _liteViewers.Remove(id);
            }
            RefreshViewerList();
            // If all viewers are gone, reset the adaptive controller so a fresh
            // connect doesn't inherit stale state.
            if (_viewers.Count == 0)
            {
                _lowStreak = _goodStreak = 0;
                _sendStallEma = 0;
                _lastAdapt = now;
            }
        }

        private void KickSelected()
        {
            if (_viewerList.SelectedIndex < 0) return;
            var item = (string)_viewerList.SelectedItem;
            // "控制端 #<id>  (…)" -> extract the id
            int hash = item.IndexOf('#');
            int sp = item.IndexOf(' ', hash);
            if (hash < 0 || sp < 0) return;
            if (!int.TryParse(item.Substring(hash + 1, sp - hash - 1), out int id)) return;
            try { _transport?.Send(MessageType.Kick, Codec.BuildViewerId(id)); } catch { }
            // The relay sends Bye to that viewer, closes it, and VLeave comes
            // back to us which removes it from the list.
        }

        // ---- 供主界面（被控端本人）调用的入口：在被控状态下主动聊天/发文件/断开/黑屏 ----
        internal void OwnerShowChat() => ShowChat();
        internal void OwnerSendFile() => SendFileDialog();
        internal void OwnerToggleBlack() => ToggleBlackScreen();
        internal bool BlackOn => _blackOn;
        internal bool IsControlled => TotalViewers() > 0;
        internal void OwnerKickAll()
        {
            List<int> ids;
            lock (_liteLock) ids = _viewers.Keys.ToList();
            foreach (var id in ids)
            {
                try { _transport?.Send(MessageType.Kick, Codec.BuildViewerId(id)); } catch { }
            }
            // 远程协助房间里的控制端也一并断开
            AssistLink[] snap;
            lock (_assistLock) { snap = _assistLinks.ToArray(); }
            foreach (var link in snap)
            {
                int[] vids; lock (link.Viewers) vids = link.Viewers.Keys.ToArray();
                foreach (var vid in vids)
                {
                    try { link.T?.Send(MessageType.Kick, Codec.BuildViewerId(vid)); } catch { }
                }
            }
            BeginInvoke((MethodInvoker)(() => OnChat("[系统] 已断开所有控制端", true)));
        }

        private void HandleInput(byte[] p)
        {
            var kind = (InputKind)p[0];
            using var ms = new MemoryStream(p, 1, p.Length - 1);
            using var br = new BinaryReader(ms);
            switch (kind)
            {
                case InputKind.Move:
                    int x = br.ReadInt32(), y = br.ReadInt32();
                    RcNative.rc_input_mouse_move(x, y); break;
                case InputKind.Button:
                    byte b = br.ReadByte(); byte d = br.ReadByte();
                    RcNative.rc_input_mouse_button(b, d); break;
                case InputKind.Wheel:
                    int delta = br.ReadInt32();
                    RcNative.rc_input_wheel(delta); break;
                case InputKind.Key:
                    uint vk = br.ReadUInt32(); byte kd = br.ReadByte();
                    RcNative.rc_input_key(vk, kd); break;
            }
        }

        // ---- clipboard -----------------------------------------------------
        private void PollClipboard()
        {
            if (!_running || !_clipboardChk.Checked) return;
            string cur = SafeGetClipboard();
            if (!string.IsNullOrEmpty(cur) && cur != _lastClipboard)
            {
                _lastClipboard = cur;
                try { _transport?.Send(MessageType.Clipboard, Codec.BuildClipboard(cur)); } catch { }
            }
            PollClipboardImage();
        }

        // Broadcast the local clipboard image (PNG) to all viewers when it
        // changes. Hash-guarded so an image set from a viewer isn't echoed.
        private void PollClipboardImage()
        {
            byte[] png = SafeGetClipboardImagePng();
            if (png == null || png.Length == 0 || png.Length > 8 * 1024 * 1024) return;
            int h = ComputeClipHash(png);
            if (h == _lastClipImgHash) return;
            _lastClipImgHash = h;
            try { _transport?.Send(MessageType.ClipImage, png); } catch { }
        }

        private static int ComputeClipHash(byte[] data)
        {
            unchecked
            {
                int h = 17 ^ data.Length;
                int step = Math.Max(1, data.Length / 512);
                for (int i = 0; i < data.Length; i += step) h = h * 31 + data[i];
                return h;
            }
        }

        private static byte[] SafeGetClipboardImagePng()
        {
            try
            {
                if (!Clipboard.ContainsImage()) return null;
                using var img = Clipboard.GetImage();
                if (img == null) return null;
                using var ms = new System.IO.MemoryStream();
                img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }
            catch { return null; }
        }

        // Applies a received PNG to the clipboard, then re-reads it so the
        // loop-guard hash matches the *re-encoded* bytes (clipboard round-trips
        // through DIB, so the PNG we would poll differs from what arrived).
        private void ApplyClipboardImage(byte[] png)
        {
            try
            {
                using var ms = new System.IO.MemoryStream(png);
                using var img = Image.FromStream(ms);
                Clipboard.SetImage(img);
                var back = SafeGetClipboardImagePng();
                if (back != null) _lastClipImgHash = ComputeClipHash(back);
            }
            catch { }
        }

        private static string SafeGetClipboard()
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch { return ""; }
        }

        private static void SafeSetClipboard(string text)
        {
            try { Clipboard.SetText(text); } catch { }
        }

        private void SetStatus(string text, Color color)
        {
            if (IsDisposed) return;
            try { BeginInvoke((MethodInvoker)(() => { _status.Text = text; _status.ForeColor = color; })); }
            catch { }
        }

        // ---- status tags ---------------------------------------------------
        private string SecTag() => _aead != null ? "🔒加密" : "🔓未加密";
        private string EncTag()
        {
            if (string.IsNullOrEmpty(_encName)) return "编码?";
            if (_encName.Contains("nvenc")) return "NVENC硬件";
            if (_encName.Contains("qsv"))   return "QSV硬件";
            if (_encName.Contains("amf"))   return "AMF硬件";
            if (_encName.Contains("_mf"))   return "MF硬件";
            if (_encName.Contains("x264"))  return "x264软件";
            return _encName;
        }

        // ---- audio streaming (host loopback -> broadcast) ------------------
        private void AudioLoop(CancellationToken token)
        {
            // Announce format once we have a link; re-announced on viewer join.
            bool announced = false;
            while (_running && !token.IsCancellationRequested)
            {
                if (_transport == null || TotalViewers() == 0)
                {
                    announced = false;
                    if (Sleep(50, token)) break;
                    continue;
                }
                if (!announced)
                {
                    SendToAll(MessageType.AudioConfig, Codec.BuildAudioConfig(48000, 2));
                    announced = true;
                }
                int r = RcNative.rc_audio_cap_read(out IntPtr p, out int size);
                if (r == RcNative.RC_OK && size > 0)
                {
                    var buf = new byte[size];
                    Marshal.Copy(p, buf, 0, size);
                    RcNative.rc_afree(p);
                    SendToAll(MessageType.AudioFrame, Codec.BuildAudioFrame(buf));
                }
                else
                {
                    if (Sleep(4, token)) break;
                }
            }
        }

        // ---- relay routing helpers -----------------------------------------
        // 远程协助房间 viewers 合计（用于“是否有人在观看”判断）。
        private int TotalViewers()
        {
            int n = _viewers.Count;
            lock (_assistLock) { foreach (var l in _assistLinks) n += l.Viewers.Count; }
            return n;
        }
        // 需要视频的 viewer 数（轻量会话声明 NoVideo 的不算）。仅当为 0 时跳过编码。
        private int TotalVideoViewers()
        {
            int n;
            lock (_liteLock) n = _viewers.Count - _liteViewers.Count;
            if (n < 0) n = 0;
            lock (_assistLock) { foreach (var l in _assistLinks) n += l.Viewers.Count; }
            return n;
        }

        private void SendToAll(MessageType t, byte[] p)
        {
            // 单 viewer 且已 P2P 直连：视频/音频配置走直连，绕开中转。
            if (TrySendP2P(_p2pVid, t, p)) return;
            try { _transport?.Send(t, p); } catch { }
            // 远程协助房间：广播到各协助连接（各自独立 E2E 密钥）。
            AssistLink[] snap;
            lock (_assistLock) { snap = _assistLinks.ToArray(); }
            foreach (var link in snap)
            {
                if (link.Viewers.Count == 0 || link.T == null) continue;
                try { link.T.Send(t, p); } catch { }
            }
        }
        private void SendToViewer(int vid, MessageType t, byte[] p)
        {
            // 远程协助 viewer 的 vId 带 ASSIST_VID_BASE 偏移，路由到对应协助连接。
            if (vid >= ASSIST_VID_BASE)
            {
                int raw = vid - ASSIST_VID_BASE;
                AssistLink target = null;
                foreach (var l in _assistLinks) if (l.Viewers.ContainsKey(raw)) { target = l; break; }
                if (target != null && target.T != null)
                {
                    var pp = p;
                    if (Transport.CompressionEnabled && Codec.ShouldEncrypt(t) && pp != null && pp.Length > 0)
                        pp = Transport.WrapCompressed(pp);
                    if (target.T.Encrypted && Codec.ShouldEncrypt(t) && pp != null && pp.Length > 0)
                        pp = target.T.EncryptPayload(pp);
                    try { target.T.Send(MessageType.ToViewer, Codec.BuildToViewer(raw, t, pp)); } catch { }
                }
                return;
            }
            if (vid < 0) { SendToAll(t, p); return; }
            // P2P 直连优先（裸消息，直连 Transport 自动压缩+加密）。
            if (TrySendP2P(vid, t, p)) return;
            // 否则走中转：ToViewer 是明文信封，内层需手动压缩+加密。
            if (Transport.CompressionEnabled && Codec.ShouldEncrypt(t) && p != null && p.Length > 0)
                p = Transport.WrapCompressed(p);
            if (_transport != null && _transport.Encrypted && Codec.ShouldEncrypt(t) && p != null && p.Length > 0)
                p = _transport.EncryptPayload(p);
            try { _transport?.Send(MessageType.ToViewer, Codec.BuildToViewer(vid, t, p)); } catch { }
        }

        // 直连优先发送：仅对视频/音频这种高带宽、高延迟敏感的类型走直连。
        // 返回 true 表示已走直连（调用方不要再走中转）。
        private bool TrySendP2P(int vid, MessageType t, byte[] p)
        {
            if (!_p2pOn || !_p2pReady || _p2p == null || _p2pVid != vid) return false;
            if (t != MessageType.VideoFrame && t != MessageType.VideoConfig
             && t != MessageType.AudioFrame && t != MessageType.AudioConfig
             && t != MessageType.Ping) return false;
            try { _p2p.Send(t, p); return true; }
            catch { CloseP2P(); return false; }
        }

        // 视频帧发送：单 viewer 直连优先，否则广播走中转。
        private void SendVideoFrame(byte key, byte[] buf)
        {
            if (_p2pOn && _p2pReady && _p2p != null && _viewers.Count == 1)
            {
                try { _p2p.Send(MessageType.VideoFrame, Codec.BuildVideoFrame(key, buf)); return; }
                catch { CloseP2P(); }
            }
            try { _transport?.Send(MessageType.VideoFrame, Codec.BuildVideoFrame(key, buf)); } catch { }
            // 远程协助房间：同一帧也发给各协助连接（各自独立 E2E 密钥）。
            AssistLink[] snap;
            lock (_assistLock) { snap = _assistLinks.ToArray(); }
            foreach (var link in snap)
            {
                if (link.Viewers.Count == 0 || link.T == null) continue;
                try { link.T.Send(MessageType.VideoFrame, Codec.BuildVideoFrame(key, buf)); } catch { }
            }
        }

        // ---- chat -----------------------------------------------------------
        private void ShowChat()
        {
            // Only load the full history when the window is freshly created;
            // re-clicking the chat button on an already-open window must NOT
            // re-append the whole history (that would duplicate every line).
            bool created = false;
            if (_chatForm == null || _chatForm.IsDisposed) { _chatForm = new ChatForm(OnChatSend); created = true; }
            if (!_chatForm.Visible) _chatForm.Show();   // create handle first
            if (created) _chatForm.Append(_chat);
            try { _chatForm.Activate(); _chatForm.BringToFront(); } catch { }
        }
        private void OnChat(string line, bool incoming = false)
        {
            _chat.Add(line);
            if (_chat.Count > 200) _chat.RemoveAt(0);
            if (incoming)
            {
                // Auto-surface the chat window so an incoming message is never missed.
                bool fresh = (_chatForm == null || _chatForm.IsDisposed);
                if (fresh) { _chatForm = new ChatForm(OnChatSend); _chatForm.Show(); _chatForm.Append(_chat); }
                else _chatForm.Append(new[] { line });
                try { _chatForm.Activate(); _chatForm.BringToFront(); } catch { }
            }
            else
            {
                // Ensure the sender always sees their own message, even if they
                // somehow sent before the window existed.
                if (_chatForm == null || _chatForm.IsDisposed) { _chatForm = new ChatForm(OnChatSend); _chatForm.Show(); }
                _chatForm.Append(new[] { line });
            }
        }
        private void OnChatSend(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string me = string.IsNullOrEmpty(_cloudUsername) ? "我" : _cloudUsername;
            OnChat("[" + me + "] " + text);
            SendToAll(MessageType.Chat, Codec.BuildChat(text));
        }

        // ---- file transfer --------------------------------------------------
        private void SendFileDialog()
        {
            using var d = new OpenFileDialog { Title = "选择要发送的文件（可多选）", Multiselect = true };
            if (d.ShowDialog() != DialogResult.OK) return;
            if (d.FileNames == null || d.FileNames.Length == 0) return;
            int target = -1;
            if (_viewers.Count > 1)
            {
                var names = new System.Collections.Generic.List<string> { "全部控制端" };
                foreach (var kv in _viewers)
                {
                    string label = _viewerNames.TryGetValue(kv.Key, out var nm) && !string.IsNullOrEmpty(nm)
                        ? nm : ("控制端 #" + kv.Key);
                    names.Add(label);
                }
                using var pick = new Form { Text = "选择接收方", Width = 280, Height = 140, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog };
                var cb = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
                cb.Items.AddRange(names.ToArray()); cb.SelectedIndex = 0;
                var ok = new Button { Text = "确定", Dock = DockStyle.Bottom };
                pick.Controls.Add(cb); pick.Controls.Add(ok);
                int res = -2; ok.Click += (s, e) => { res = cb.SelectedIndex; pick.Close(); };
                pick.ShowDialog();
                if (res == -2) return;
                if (res > 0) target = System.Linq.Enumerable.ElementAt(_viewers.Keys, res - 1);
            }
            // Send files sequentially in background.
            Task.Run(() =>
            {
                int n = 0;
                foreach (var file in d.FileNames)
                {
                    if (!_running) break;
                    n++;
                    try { BeginInvoke((MethodInvoker)(() => SetStatus($"发送文件 {n}/{d.FileNames.Length}: {System.IO.Path.GetFileName(file)}", Color.Green))); } catch { }
                    var t = _ft.BeginSend(file, target);
                    try { BeginInvoke((MethodInvoker)(() => { ShowFtForm(); _ftForm.Add(t); })); } catch { }
                    SendToViewer(target, MessageType.FOpen, Codec.BuildFOpen(t.Id, 1, t.Name, t.Size));
                    SendFileToViewer(t, target);
                }
                try { BeginInvoke((MethodInvoker)(() => SetStatus($"文件发送完成（{d.FileNames.Length} 个）", Color.Green))); } catch { }
            });
        }

        private void SendFileToViewer(FileTransfer.Transfer t, int target)
        {
            // Wait for the viewer to accept. The viewer walks through TWO modal
            // dialogs (accept, then choose a save folder), which can exceed a
            // fixed short timeout and previously left the sender stuck at 0%.
            // Wait until accepted/canceled/disconnect, with a generous safety
            // timeout that cancels and notifies the peer if FResp is truly lost.
            int waited = 0;
            const int safeLimit = 180000;
            while (!t.Accepted && !t.Canceled && _running)
            {
                if (Sleep(100, _cts.Token)) return;
                waited += 100;
                if (waited >= safeLimit) break;
            }
            if (!t.Accepted)
            {
                // Not accepted: either the viewer denied (t.Canceled) or never
                // responded in time. Mark as canceled so the window reports
                // failure, and notify the peer (unless it already did).
                bool denied = t.Canceled;
                if (!denied)
                {
                    t.Canceled = true;
                    try { SendToViewer(target, MessageType.FCancel, Codec.BuildId(t.Id)); } catch { }
                }
                _ft.EndOutgoing(t);
                SetStatus(denied ? "对方拒绝接收: " + t.Name : "对方长时间未响应，已取消: " + t.Name, Color.DarkOrange);
                return;
            }
            byte[] chunk;
            while ((chunk = _ft.SendNext(t, out int id)) != null)
            {
                if (_running && !t.Canceled) SendToViewer(target, MessageType.FData, Codec.BuildFData(id, chunk));
                else break;
                if (Sleep(0, _cts.Token)) break;
            }
            if (!t.Canceled) SendToViewer(target, MessageType.FEnd, Codec.BuildId(t.Id));
            _ft.EndOutgoing(t);
        }

        private void OnIncomingFile(int vid, int fid, string name, long size)
        {
            using var ask = new Form { Text = "收到文件请求", Width = 360, Height = 160, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog };
            var lb = new Label { Text = $"控制端 #{vid} 想发送：\n{name}  ({size / 1024} KB)", Dock = DockStyle.Top, Height = 60 };
            var acc = new Button { Text = "接收", Dock = DockStyle.Left, Width = 80 };
            var den = new Button { Text = "拒绝", Dock = DockStyle.Right, Width = 80 };
            ask.Controls.Add(lb); ask.Controls.Add(acc); ask.Controls.Add(den);
            var saveDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            int choice = 0; // 1 accept, 2 deny
            acc.Click += (s, e) => { choice = 1; ask.Close(); };
            den.Click += (s, e) => { choice = 2; ask.Close(); };
            ask.ShowDialog();
            if (choice != 1)
            {
                SendToViewer(vid, MessageType.FCancel, Codec.BuildId(fid));
                return;
            }
            string path = Common.AutoSaveDialog("保存到", saveDir, name, "所有文件|*.*");
            if (string.IsNullOrEmpty(path)) { SendToViewer(vid, MessageType.FCancel, Codec.BuildId(fid)); return; }
            // Adopt the sender's transfer id so incoming FData/FEnd resolve.
            var t = _ft.ReceiveOpen(fid, vid, 1, name, size, Path.GetDirectoryName(path));
            t.Path = path;                 // honour the user's chosen filename
            ShowFtForm(); _ftForm.Add(t);
            _ft.Accept(t);
            SendToViewer(vid, MessageType.FResp, Codec.BuildFResp(t.Id, 1));
            SetStatus("正在接收文件，保存到：" + path, Color.Green);
        }

        // Ensure the transfer window is created AND visible (its Add() uses the
        // window handle, which only exists once the form has been shown).
        private void ShowFtForm()
        {
            if (_ftForm == null || _ftForm.IsDisposed) _ftForm = new FileTransferForm();
            try { if (!_ftForm.Visible) _ftForm.Show(); _ftForm.BringToFront(); } catch { }
        }

        private void NotifyFileSaved(string path)
        {
            try
            {
                var r = MessageBox.Show(this, "文件已保存到：\n" + path + "\n\n是否打开所在文件夹？",
                    "文件接收完成", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (r == DialogResult.Yes)
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch { }
        }

        private void OnSendAccepted(int vid, int id, bool accept)
        {
            var t = _ft.Find(id);
            if (t == null) return;
            if (accept) t.Accepted = true;
            else { _ft.CancelOutgoing(t); SetStatus("对方拒绝了文件发送", Color.DarkOrange); }
        }

        // ---- display switching --------------------------------------------
        private void SwitchDisplayDialog()
        {
            int n = RcNative.rc_monitor_count();
            if (n <= 1) { SetStatus("只有一块显示器", Color.Gray); return; }
            using var pick = new Form { Text = "切换显示器", Width = 260, Height = 60 + n * 30, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog };
            var flp = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown };
            for (int i = 0; i < n; i++)
            {
                int idx = i;
                var b = new Button { Text = "显示器 " + i + (i == _displayIndex ? " (当前)" : ""), Width = 220 };
                b.Click += (s, e) => { SwitchDisplay(idx); pick.Close(); };
                flp.Controls.Add(b);
            }
            pick.Controls.Add(flp);
            pick.ShowDialog();
        }

        private void SwitchDisplay(int idx)
        {
            if (RcNative.rc_capture_reinit(idx) != RcNative.RC_OK)
            {
                SetStatus("切换显示器失败", Color.Red); return;
            }
            _displayIndex = idx;
            // Re-learn size + input bounds, rebuild the encoder, resend config.
            IntPtr ptr; int w = 0, h = 0; ulong pts;
            for (int i = 0; i < 200; i++)
            {
                if (RcNative.rc_capture_frame(out ptr, out w, out h, out pts) == RcNative.RC_OK && w > 0) break;
                Thread.Sleep(10);
            }
            if (w <= 0) { SetStatus("获取新显示器尺寸失败", Color.Red); return; }
            if (RcNative.rc_capture_get_bounds(out int bl, out int bt, out int bw, out int bh) == RcNative.RC_OK)
                RcNative.rc_input_set_bounds(bl, bt, bw, bh);
            _natW = w; _natH = h; _scaleIdx = 0;
            lock (_sendLock) { RebuildEncoder(ScaleBitrate()); }
            if (_monitorBox.Items.Count > idx) _monitorBox.SelectedIndex = idx;
            BroadcastMonitorList();
            SetStatus($"已切换到显示器 {idx} ({w}x{h})", Color.Green);
        }

        private void BroadcastMonitorList()
        {
            int n = RcNative.rc_monitor_count();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++) sb.Append(i).Append(':').Append("?x?\n");
            SendToAll(MessageType.MonitorList, Codec.BuildMonitorList(sb.ToString()));
        }

        // ---- system control -------------------------------------------------
        private void SystemControlDialog()
        {
            using var d = new Form { Text = "系统控制", Width = 280, Height = 260, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog };
            var flp = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown };
            void Add(string label, int cmd, string confirm)
            {
                var b = new Button { Text = label, Width = 220 };
                b.Click += (s, e) =>
                {
                    if (MessageBox.Show(confirm, label, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                        ExecSystemCommand(cmd);
                    d.Close();
                };
                flp.Controls.Add(b);
            }
            Add("锁定屏幕", 1, "锁定本机屏幕？");
            Add("睡眠 (待机)", 11, "让本机进入睡眠？会断开当前会话。");
            Add("关闭显示器", 12, "关闭本机显示器？移动鼠标可唤醒。");
            Add("注销当前用户", 2, "注销当前用户？未保存的工作会丢失。");
            Add("重启计算机", 3, "重启计算机？");
            Add("关闭计算机", 4, "关闭计算机？");
            d.Controls.Add(flp);
            d.ShowDialog();
        }

        private void ExecSystemCommand(int cmd)
        {
            string note = "";
            switch (cmd)
            {
                case 1: RcNative.rc_system_lock(); note = "已锁定"; break;
                case 2: RcNative.rc_system_logoff(); note = "正在注销"; break;
                case 3: RcNative.rc_system_reboot(); note = "正在重启"; break;
                case 4: RcNative.rc_system_shutdown(); note = "正在关机"; break;
                case 10:
                    note = RcNative.rc_input_send_cad() != 0
                        ? "已发送 Ctrl+Alt+Del"
                        : "Ctrl+Alt+Del 发送失败（需被控端以管理员运行，或启用软件 SAS 策略）";
                    break;
                case 11: RcNative.rc_system_sleep(); note = "正在进入睡眠"; break;
                case 12: RcNative.rc_system_monitor_off(); note = "已关闭被控端显示器（移动鼠标可唤醒）"; break;
                case 13: SetBlackScreen(true);  note = "已开启隐私黑屏"; break;
                case 14: SetBlackScreen(false); note = "已关闭隐私黑屏"; break;
            }
            if (!string.IsNullOrEmpty(note)) SendToAll(MessageType.Chat, Codec.BuildChat("[系统] " + note));
        }

        // ---- black screen (hide local display) -----------------------------
        private void ToggleBlackScreen() => SetBlackScreen(!_blackOn);

        // Show/hide the privacy black screen. Callable locally (button) or
        // remotely (viewer Ctrl 13/14). Idempotent.
        private void SetBlackScreen(bool on)
        {
            if (on == _blackOn) return;
            if (on)
            {
                _blackForm = new BlackScreenForm();
                _blackForm.FormClosed += (s, e) => { _blackOn = false; try { _blackBtn.Text = "本地黑屏"; } catch { } };
                _blackForm.Show();
                _blackOn = true;
                _blackBtn.Text = "关闭黑屏";
            }
            else
            {
                _blackForm?.Close(); _blackForm = null; _blackOn = false;
                try { _blackBtn.Text = "本地黑屏"; } catch { }
            }
        }

        // ---- host-side local recording -------------------------------------
        private void ToggleHostRecording()
        {
            if (_hostRecording) { StopHostRecording(); return; }
            if (!_running || _curW <= 0 || _curH <= 0)
            {
                SetStatus("尚未开始共享，无法录制", Color.DarkOrange); return;
            }
            using var dlg = new FolderBrowserDialog
            {
                Description = "选择录像保存目录",
                SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _recDir = dlg.SelectedPath; _recPart = 0;
            if (StartHostRecordingFile())
            {
                _hostRecording = true;
                _recBtn.Text = "停止录制";
                SetStatus("本地录制中 → " + _recDir, Color.Red);
            }
            else SetStatus("录制启动失败", Color.Red);
        }

        private bool StartHostRecordingFile()
        {
            string name = "本机会话_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                          + (_recPart > 0 ? $"_part{_recPart + 1}" : "") + ".mp4";
            string path = System.IO.Path.Combine(_recDir, name);
            _recW = _curW; _recH = _curH;
            _recSw.Restart();
            int rc = RcNative.rc_record_start(path, _recW, _recH, _curFps,
                                              _curExtra ?? Array.Empty<byte>(),
                                              _curExtra?.Length ?? 0);
            return rc == 0;
        }

        // Close the current part and start the next (resolution change mid-rec).
        private void HostRecordRollover()
        {
            try { RcNative.rc_record_stop(); } catch { }
            _recPart++;
            if (!StartHostRecordingFile())
            {
                _hostRecording = false;
                try { BeginInvoke((MethodInvoker)(() => _recBtn.Text = "录制")); } catch { }
                SetStatus("分辨率变化后录制重启失败，已停止录制", Color.Red);
            }
        }

        private void StopHostRecording()
        {
            _hostRecording = false;
            try { RcNative.rc_record_stop(); } catch { }
            try { _recBtn.Text = "录制"; } catch { }
            SetStatus("录制已保存到 " + _recDir, Color.Green);
        }

        private void Stop()
        {
            if (!_running) return;
            _running = false;
            UpdateKeyHook();   // Phase 1D：卸载键盘钩子
            if (_hostRecording) { _hostRecording = false; try { RcNative.rc_record_stop(); } catch { } try { _recBtn.Text = "录制"; } catch { } }
            SetBlackScreen(false);
            try { _clipTimer?.Stop(); } catch { }
            try { _cts?.Cancel(); } catch { }
            if (_audioOn) { try { RcNative.rc_audio_cap_stop(); } catch { } _audioOn = false; }
            try { _transport?.Send(MessageType.Bye, Array.Empty<byte>()); } catch { }
            try { _transport?.Dispose(); } catch { }
            _transport = null;
            Thread.Sleep(150); // let SendLoop notice cancellation before freeing
            lock (_sendLock) { RcNative.rc_encoder_free(); RcNative.rc_capture_free(); }
            _startBtn.Enabled = true; _stopBtn.Enabled = false;
            CloseP2P(); _p2pEverConnected = false; _rttViewerEma = -1;
            _monitorBox.Enabled = true; _pwBox.Enabled = true; _audioChk.Enabled = true;
            _chatBtn.Enabled = _fileBtn.Enabled = _switchBtn.Enabled = _sysBtn.Enabled = _blackBtn.Enabled = _recBtn.Enabled = false;
            _viewers.Clear(); lock (_liteLock) _liteViewers.Clear(); _viewerCount = 0;
            _viewerList.Items.Clear(); _kickBtn.Enabled = false;
            StopAllAssist();
            _status.Text = "已停止"; _status.ForeColor = Color.Gray;
        }

        // ---- 即时应用用户设置（设置窗口「确定」后调用）----
        public void ReloadSettings()
        {
            var s = UserSettings.Current;
            _serverBox.Text = string.IsNullOrWhiteSpace(s.Server) ? CloudConfig.TcpHost : s.Server;
            _portBox.Text = s.Port.ToString();
            _fpsBox.Text = s.Fps.ToString();
            _qualityBox.SelectedIndex = Math.Max(0, Math.Min(2, s.Quality));
            _adaptChk.Checked = s.Adaptive;
            _compChk.Checked = s.Compression;
            _clipboardChk.Checked = s.Clipboard;
            _audioChk.Checked = s.Audio;
            _viewOnlyChk.Checked = s.ViewOnly;
            _p2pChk.Checked = s.P2P;
            _autoStartChk.Checked = s.Autostart;
            _retryChk.Checked = s.Retry;
            try { Common.SetAutostart(s.Autostart, "--autostart --cloud --min"); } catch { }
            // 运行中则重启以应用（含服务器/端口变更需要重连）。
            if (_running) { Stop(); _ = StartAsync(); }
        }

        // ---- 远程协助：本机作为协助主机，监听指定房间 ----
        public void AddAssistSession(string room, string key)
        {
            if (string.IsNullOrWhiteSpace(room) || string.IsNullOrWhiteSpace(key)) return;
            lock (_assistLock)
            {
                foreach (var l in _assistLinks) if (l.Room == room) return; // 重复忽略
                if (!_running)
                {
                    SetStatus("协助需本机被控服务在线，请稍候重试。", Color.Red);
                    return;
                }
                var link = new AssistLink { Room = room, Key = key, Cts = new CancellationTokenSource() };
                _assistLinks.Add(link);
                _ = Task.Run(() => AssistConnectLoop(link, link.Cts.Token));
            }
        }

        private void AssistConnectLoop(AssistLink link, CancellationToken token)
        {
            int backoff = 800;
            while (_running && !token.IsCancellationRequested && !link.Stopped)
            {
                Transport t;
                try
                {
                    t = Transport.Connect(_serverBox.Text, int.Parse(_portBox.Text));
                    t.SetCrypto(Aead.FromPassword(link.Key, link.Room));
                    t.SendJoin(link.Room, "host", Common.HashPassword(link.Key), version: UpgradeCheck.CurrentVersion());
                    if (t.TryReceive(out var ht, out var hp) && ht == MessageType.Result)
                    {
                        Codec.ParseResult(hp, out int code, out string text);
                        if (code != 0)
                        {
                            t.Dispose();
                            if (code == 2)
                            {
                                BeginInvoke((MethodInvoker)(() =>
                                    MessageBox.Show(text, "强制升级", MessageBoxButtons.OK, MessageBoxIcon.Stop)));
                                break;
                            }
                            SetStatus($"协助房间 {link.Room} 加入被拒绝: {text}", Color.Red);
                            if (Sleep(backoff, token)) break;
                            backoff = Math.Min(backoff * 2, 8000);
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    SetStatus($"协助房间 {link.Room} 连接失败，重连中… " + ex.Message, Color.DarkOrange);
                    if (Sleep(backoff, token)) break;
                    backoff = Math.Min(backoff * 2, 8000);
                    continue;
                }
                link.T = t; backoff = 800;
                SetStatus($"协助会话 {link.Room} 已就绪，等待对方加入…", Color.Green);
                AssistRecvLoop(link, token);
                try { link.T?.Dispose(); } catch { }
                link.T = null;
                lock (link.Viewers) { link.Viewers.Clear(); link.ViewerNames.Clear(); } // 断线后清空成员，重连由 VJoin 重建
                if (!_running || token.IsCancellationRequested || link.Stopped) break;
                SetStatus($"协助会话 {link.Room} 断开，重连中…", Color.DarkOrange);
                if (Sleep(1000, token)) break;
            }
            lock (_assistLock) { _assistLinks.Remove(link); }
        }

        private void AssistRecvLoop(AssistLink link, CancellationToken token)
        {
            while (_running && !token.IsCancellationRequested && !link.Stopped)
            {
                if (link.T == null) return;
                if (!link.T.TryReceive(out var type, out var payload)) return;

                if (type == MessageType.VJoin)
                {
                    int id = Codec.ParseViewerId(payload);
                    if (id > 0)
                    {
                        var nm = Codec.ParseViewerName(payload);
                        lock (link.Viewers) { link.Viewers[id] = DateTime.Now; if (!string.IsNullOrEmpty(nm)) link.ViewerNames[id] = nm; }
                        SetStatus($"协助房间 {link.Room}: 控制端 {nm} (#{id}) 已加入", Color.Green);
                        // 发送头 + 强制 IDR，使协助端可立即解码。
                        lock (_sendLock) { RebuildEncoder(_curBitrate); }
                        if (_viewOnly) SendToViewer(ASSIST_VID_BASE + id, MessageType.ViewOnly, Codec.BuildViewOnly(true));
                        if (_audioOn) SendToViewer(ASSIST_VID_BASE + id, MessageType.AudioConfig, Codec.BuildAudioConfig(48000, 2));
                        BroadcastMonitorList();
                    }
                }
                else if (type == MessageType.VLeave)
                {
                    int id = Codec.ParseViewerId(payload);
                    lock (link.Viewers) { link.Viewers.Remove(id); link.ViewerNames.Remove(id); }
                    SetStatus($"协助房间 {link.Room}: 控制端 #{id} 已离开", Color.Gray);
                }
                else if (type == MessageType.FromViewer)
                {
                    if (!Codec.ParseFromViewer(payload, out int vid, out var it, out var inner))
                        continue;
                    // 协助连接用独立 E2E 密钥解密。
                    if (link.T != null && link.T.Encrypted && Codec.ShouldEncrypt(it) && inner.Length > 0)
                        inner = link.T.DecryptPayload(inner) ?? Array.Empty<byte>();
                    if (inner.Length > 0 && Codec.ShouldEncrypt(it))
                        inner = Transport.UnwrapCompressed(inner);
                    DispatchFromViewer(ASSIST_VID_BASE + vid, it, inner);
                }
                // 协助房间不走 P2P 直连（PeerAddr 忽略）。
            }
        }

        private void StopAllAssist()
        {
            AssistLink[] snap;
            lock (_assistLock) { snap = _assistLinks.ToArray(); _assistLinks.Clear(); }
            foreach (var l in snap)
            {
                l.Stopped = true;
                try { l.Cts?.Cancel(); } catch { }
                try { l.T?.Send(MessageType.Bye, Array.Empty<byte>()); } catch { }
                try { l.T?.Dispose(); } catch { }
                l.T = null;
            }
        }

        // ---- 协助房间管理（MainForm「协助管理」界面调用）--------------------

        /// <summary>一个协助房间的对外快照（房间号 / 是否已连上中继 / 成员列表）。</summary>
        public sealed class AssistRoomInfo
        {
            public string Room = "";
            public bool Connected;
            public (int vid, DateTime since, string name)[] Viewers = Array.Empty<(int, DateTime, string)>();
        }

        /// <summary>当前是否存在协助房间（决定主界面「协助管理」按钮是否显示）。</summary>
        public bool HasAssistSessions { get { lock (_assistLock) return _assistLinks.Count > 0; } }

        /// <summary>协助房间快照：每个房间的连接状态与已加入的控制端列表。</summary>
        public AssistRoomInfo[] GetAssistSessions()
        {
            lock (_assistLock)
            {
                var arr = new AssistRoomInfo[_assistLinks.Count];
                for (int i = 0; i < _assistLinks.Count; i++)
                {
                    var l = _assistLinks[i];
                    (int, DateTime, string)[] vs;
                    lock (l.Viewers)
                    {
                        vs = new (int, DateTime, string)[l.Viewers.Count];
                        int k = 0;
                        foreach (var kv in l.Viewers)
                            vs[k++] = (kv.Key, kv.Value, l.ViewerNames.TryGetValue(kv.Key, out var n) ? n : "");
                    }
                    arr[i] = new AssistRoomInfo { Room = l.Room, Connected = l.T != null, Viewers = vs };
                }
                return arr;
            }
        }

        /// <summary>把某协助房间内的指定控制端踢出（中继 T_KICK，服务端断开该 viewer）。</summary>
        public void KickAssistViewer(string room, int vid)
        {
            AssistLink link = null;
            lock (_assistLock) { foreach (var l in _assistLinks) if (l.Room == room) { link = l; break; } }
            if (link?.T == null) return;
            try { link.T.Send(MessageType.Kick, Codec.BuildViewerId(vid)); } catch { }
            lock (link.Viewers) { link.Viewers.Remove(vid); }
            SetStatus($"协助房间 {room}: 已断开控制端 #{vid}", Color.DarkOrange);
        }

        /// <summary>关闭单个协助房间（断开该房间全部成员）。</summary>
        public void StopAssistSession(string room)
        {
            AssistLink link = null;
            lock (_assistLock)
            {
                for (int i = 0; i < _assistLinks.Count; i++)
                    if (_assistLinks[i].Room == room) { link = _assistLinks[i]; _assistLinks.RemoveAt(i); break; }
            }
            if (link == null) return;
            link.Stopped = true;
            try { link.Cts?.Cancel(); } catch { }
            try { link.T?.Send(MessageType.Bye, Array.Empty<byte>()); } catch { }
            try { link.T?.Dispose(); } catch { }
            link.T = null;
            SetStatus($"协助房间 {room} 已关闭。", Color.Gray);
        }

        /// <summary>关闭全部协助房间。</summary>
        public void StopAllAssistSessions() => StopAllAssist();
    }
}
