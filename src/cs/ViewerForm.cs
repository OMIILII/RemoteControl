﻿﻿﻿// ViewerForm.cs - The controlling machine. Connects through the relay,
// decodes the H.264 stream into a Bitmap and paints it, and sends back
// mouse/keyboard input captured over the rendered image.
//
// Experience improvements in this version:
//   * Correct mouse mapping under PictureBox "Zoom" letterboxing (maps via
//     the actual displayed image rectangle, not the raw client size).
//   * A local cursor overlay so the pointer feels instant regardless of the
//     network round-trip.
//   * Live RTT measurement (Ping/Pong) plus resolution / decode-fps readout.
//   * Automatic reconnect with backoff; sends Hello so the host re-emits its
//     header + a fresh IDR after re-pairing.
//   * Bidirectional clipboard text sync.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Collections.Concurrent;

namespace RemoteControl
{
    public sealed class ViewerForm : Form, IMessageFilter
    {
        private TextBox _serverBox, _portBox, _roomBox;
        private Button _connectBtn, _disconnectBtn;
        private CheckBox _clipboardChk;
        private Label _status;

        // Phase 1A 底部多列专业状态栏：FPS / 带宽 / 单帧大小 / 视频编码 / RTT / 分辨率 / 已连接时长
        private TableLayoutPanel _statusBar;
        private readonly Label[] _statCell = new Label[7];
        private int _lastFrameBytes;
        private DateTime _connStart = DateTime.MinValue;
        private Label _viewOnlyLabel;

        // Phase 1B 实时画质调解面板（右侧可折叠）
        private Panel _sidePanel;
        private Button _panelBtn;
        private ComboBox _resCbo, _fpsCbo, _qualCbo;
        // Phase 1D 键盘监视：被控端按键实时流
        private TextBox _keyLog;
        private PictureBox _screen;
        private Transport _transport;
        private CancellationTokenSource _cts;
        private volatile bool _running;
        /// <summary>是否已建立连接（QuickOpsForm 等外部组件可查询）。</summary>
        public bool IsRunning => _running;
        private volatile bool _reconnecting;
        private volatile bool _viewOnly;      // host enforced: watch + chat only, no control
        private string _pwHash = "";
        private Aead _aead;                 // E2E key (null => plaintext)

        // 远程终端：当前会话至多一个终端窗体
        private TerminalForm _termForm;
        private bool _lite;                 // 轻量会话：不抓屏，仅终端/文件/命令

        // Phase 2 远程文件浏览器：控制端浏览/管理被控端文件系统
        private Button _fsBtn;
        private FileBrowserForm _fileBrowser;

        // ---- 同账号免密控制------------------------------------
        // JOIN 用 "JOIN v2 <device_token> viewer <target_device_id>"；E2E 密钥
        // 由 account_key + sessionId 派生，与被控端各自独立算出同一把钥匙。
        private bool _cloud;
        private string _cloudToken = "";     // 本机 device_token（JOIN 鉴权）
        private int _cloudTarget;            // 目标设备 id
        private string _cloudKey = "";       // account_key（E2E 种子）
        private string _cloudSession = "";   // u{user}_h{target}
        private string _cloudUsername = "";  // 当前账号用户名（聊天显示用）
        private Bitmap _bmp;
        private int _hostW, _hostH;
        // Serialises access to the native decoder (rc_core.dll keeps a single
        // global DecCtx). Without this, Disconnect() freeing the decoder on the
        // UI thread can race with DecodeAndPaint() decoding on the recv thread
        // -> native use-after-free -> the app crashes on disconnect.
        private readonly object _decLock = new object();

        // Drop-to-latest rendering: the recv thread decodes EVERY frame (so the
        // decoder's P/B references stay valid), but only the NEWEST decoded frame
        // is ever painted. Under network bursts the UI thread can't paint as fast
        // as frames arrive; without this, frames queue and the screen shows stale
        // frames in receive order -> perceived latency. Keeping only the latest
        // cuts display latency to ~1 frame with zero image-quality loss.
        private readonly object _paintLock = new object();
        private byte[] _latestBgra;
        private int _latestW, _latestH;
        private bool _paintPending;

        // BGRA 缓冲池：避免每帧 new byte[w*h*4]（1600x900 => 5.76MB）进入 LOH
        // 触发 Gen2 全暂停（GC STW 冻结解码线程，是 6fps 卡顿的根因）。
        // 解码与接收解耦后同一时刻最多 2 个缓冲被占用（1 个待绘制 + 1 个解码中），
        // 因此 3 个固定缓冲足够，永不进入 LOH。
        private readonly ConcurrentStack<byte[]> _freeBufs = new ConcurrentStack<byte[]>();
        private int _poolW, _poolH;

        // 解码线程：接收线程只负责拆包入队（满则丢最旧），解码在独立线程进行，
        // 避免慢速解码阻塞网络接收、造成中继 TCP 反压。
        private readonly ConcurrentQueue<byte[]> _nalQueue = new ConcurrentQueue<byte[]>();
        private Thread _decodeThread;
        private CancellationTokenSource _decCts;
        private bool _decodeRunning;

        // Audio playback state.
        private Button _audioBtn;
        private volatile bool _audioWant;    // user wants to hear remote audio
        private volatile bool _audioStarted; // playback engine running
        private volatile bool _audioCfgSeen; // host announced an AudioConfig

        // RTT / fps instrumentation.
        private readonly Stopwatch _pingSw = Stopwatch.StartNew();
        private volatile int _rttMs = -1;
        private int _decCounter, _decFps;
        private DateTime _decStamp = DateTime.UtcNow;
        private System.Windows.Forms.Timer _pingTimer;
        // Phase 7A: last time a Pong/heartbeat was received; if >15s elapsed, connection is dead.
        private DateTime _lastPong = DateTime.UtcNow;

        // Connection-quality instrumentation.
        private long _bytesRecv;             // payload bytes since last tick
        private double _bwKbps;              // last computed receive bandwidth
        private int _jitterMs = -1;          // RTT variation
        private readonly System.Collections.Generic.Queue<int> _rttHist
            = new System.Collections.Generic.Queue<int>();
        private StatsForm _statForm;

        // Local cursor overlay.
        private Point _mousePos;
        private bool _cursorInside;

        // Clipboard sync.
        private System.Windows.Forms.Timer _clipTimer;
        private string _lastClipboard = "";

        private string _stateText = "未连接";
        private Color _stateColor = Color.Gray;

        // P2P 直连（TCP hole punch）：视频/输入改走两端用户机器之间的直连 TCP（绕开中继）
        // 绕开中转，把延迟交给用户自己的网络承担。打洞失败自动退回中继。
        private CheckBox _p2pChk;
        private volatile bool _p2pOn = true;
        private Transport _p2p;               // 到 host 的直连通道（null => 走中转）
        private volatile bool _p2pReady;      // P2P 直连建立后，通过 Hello 确认过的连接状态
        private readonly object _p2pLock = new object();
        private CancellationTokenSource _p2pCts;
        private List<(string ip, int port)> _p2pCandidates;
        private bool _p2pEverConnected;
        private bool _p2pRetrying;

        // Feature controls.
        private TextBox _pwBox;
        private ComboBox _favBox;
        private Button _favSaveBtn, _chatBtn, _fileBtn, _termBtn, _monBtn, _fullBtn, _recBtn, _specialBtn, _statBtn, _wolBtn;
        private bool _filterHooked;

        // Session recording (MP4 remux in rc_core).
        private volatile bool _recording;
        private readonly Stopwatch _recSw = new Stopwatch();
        private byte[] _recExtra = Array.Empty<byte>();
        private int _recW, _recH;
        private string _recDir = "";
        private int _recPart;

        // Clipboard image sync loop-guard (hash of the last PNG we saw/sent).
        private int _lastClipImgHash;
        private FileTransfer _ft = new FileTransfer();
        private FileTransferForm _ftForm;
        private readonly System.Collections.Generic.List<string> _chat = new System.Collections.Generic.List<string>();
        private ChatForm _chatForm;
        // 服务端下发的「版本过时」提示只弹一次，避免重连循环反复刷屏。
        private bool _versionNoticeShown;
        private System.Collections.Generic.List<string> _hostMonitors = new System.Collections.Generic.List<string>();

        // Phase 5 会话内标注（箭头/文字，对方可见）
        private bool _annoMode;
        private int _annoColor = unchecked((int)0xFFFF0000); // 默认红色
        private readonly System.Collections.Generic.List<Anno> _annos = new System.Collections.Generic.List<Anno>();
        private Anno _annoDraft;
        private bool _annoDragging;
        private Button _annoToggleBtn, _annoClearBtn;
        private ComboBox _annoTools, _annoColors;

        public ViewerForm(AppOptions opts = null)
        {
            // Same CJK font as Launcher/Host so the entire Viewer UI renders
            // Chinese characters correctly even on systems without Microsoft YaHei.
            Font = CjkFontHolder.Font;
            Text = $"远程控制 - 控制端 (Viewer)   [font: {CjkFontHolder.FontName}]";
            Width = 1000; Height = 620; StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            Application.AddMessageFilter(this);
            bool showAdv = Common.IsAdvancedUi();

            // 注意：此处不能用固定 Height。top 内控件很多（连接前的服务器/端口/房间/
            // 口令/收藏等输入框 + 连接后的聊天/文件/终端/显示器…操作按钮），WrapContents
            // 下会换行成 3~4 行；若写死 72px 只够约 2 行，第 3 行及之后的按钮（终端按钮
            // 排在第 3 行附近）会被裁切 → 表现为“按钮不显示/错位/变形”。改用 AutoSize
            // 让工具栏按内容自动撑高，所有按钮都能完整显示。
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6), WrapContents = true };
            _serverBox = new TextBox { Text = "127.0.0.1", Width = 110 };
            _portBox   = new TextBox { Text = "25498", Width = 50 };
            _roomBox   = new TextBox { Text = "", Width = 90 };
            _pwBox     = new TextBox { Text = "", Width = 90, UseSystemPasswordChar = true, PlaceholderText = "口令" };
            _favBox    = new ComboBox { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name" };
            _favSaveBtn = new Button { Text = "保存", AutoSize = true };
            _wolBtn     = new Button { Text = "远程唤醒", AutoSize = true };
            _connectBtn = new Button { Text = "连接" };
            _disconnectBtn = new Button { Text = "断开", Enabled = false };
            _clipboardChk = new CheckBox { Text = "剪贴板", AutoSize = true, Checked = true, Anchor = AnchorStyles.Left };
            _chatBtn = new Button { Text = "聊天", AutoSize = true, Enabled = false };
            _fileBtn = new Button { Text = "接收/发送文件", AutoSize = true, Enabled = false };
            _termBtn = new Button { Text = "终端", AutoSize = true, Enabled = false };
            _monBtn  = new Button { Text = "切换显示器", AutoSize = true, Enabled = false };
            _fullBtn = new Button { Text = "全屏", AutoSize = true, Enabled = false };
            _recBtn  = new Button { Text = "录制", AutoSize = true, Enabled = false };
            _specialBtn = new Button { Text = "特殊按键", AutoSize = true, Enabled = false };
            _statBtn = new Button { Text = "连接质量", AutoSize = true, Enabled = false };
            _audioBtn = new Button { Text = "🔈声音", AutoSize = true, Enabled = false };
            _panelBtn = new Button { Text = "面板", AutoSize = true };
            _fsBtn = new Button { Text = "📁文件", AutoSize = true, Enabled = false };
            _p2pChk   = new CheckBox { Text = "P2P 直连", AutoSize = true, Checked = true };
            _status = new Label { Text = "未连接", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft };
            _viewOnlyLabel = new Label { Text = "🔒 仅观看模式：只能观看与聊天，无法操作对方", ForeColor = Color.Red, AutoSize = true, Visible = false, TextAlign = ContentAlignment.MiddleLeft };

            // Phase 1A 底部 7 列专业状态栏（参考 参考项目 的 ScreenTab 底部指标条）。
            _statusBar = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 26,
                ColumnCount = 7,
                BackColor = Color.FromArgb(0xFA, 0xFA, 0xFA),
                Padding = new Padding(8, 4, 8, 4),
            };
            for (int i = 0; i < 7; i++) _statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 7));
            for (int i = 0; i < 7; i++)
            {
                var l = new Label
                {
                    Text = "--",
                    AutoSize = false,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = new Font("Segoe UI", 8.5f),
                    ForeColor = Color.FromArgb(0x55, 0x55, 0x55),
                };
                _statCell[i] = l;
                _statusBar.Controls.Add(l, i, 0);
            }

            foreach (var f in Common.LoadFavorites()) _favBox.Items.Add(f);
            _favBox.SelectedIndexChanged += (s, e) =>
            {
                if (_favBox.SelectedItem is Common.Favorite f)
                {
                    _serverBox.Text = f.Server; _portBox.Text = f.Port.ToString();
                    _roomBox.Text = f.Room; _pwBox.Text = f.Password;
                }
            };

            // 中继服务器 / 端口：默认隐藏，有时显示
            if (showAdv)
            {
                top.Controls.Add(new Label { Text = "服务器", AutoSize = true });
                top.Controls.Add(_serverBox);
                top.Controls.Add(new Label { Text = "端口", AutoSize = true });
                top.Controls.Add(_portBox);
            }
            top.Controls.Add(new Label { Text = "房间", AutoSize = true });
            top.Controls.Add(_roomBox);
            top.Controls.Add(new Label { Text = "口令", AutoSize = true });
            top.Controls.Add(_pwBox);
            top.Controls.Add(new Label { Text = "收藏", AutoSize = true });
            top.Controls.Add(_favBox);
            top.Controls.Add(_favSaveBtn);
            top.Controls.Add(_wolBtn);
            top.Controls.Add(_connectBtn);
            top.Controls.Add(_disconnectBtn);
            top.Controls.Add(_clipboardChk);
            top.Controls.Add(_chatBtn);
            top.Controls.Add(_fileBtn);
            top.Controls.Add(_termBtn);
            top.Controls.Add(_monBtn);
            top.Controls.Add(_fullBtn);
            top.Controls.Add(_recBtn);
            top.Controls.Add(_specialBtn);
            top.Controls.Add(_statBtn);
            top.Controls.Add(_audioBtn);
            top.Controls.Add(_panelBtn);
            top.Controls.Add(_fsBtn);
            top.Controls.Add(_p2pChk);
            top.Controls.Add(_status);
            top.Controls.Add(_viewOnlyLabel);

            _screen = new BufferedPictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom,
                AllowDrop = true,
            };
            _screen.DragEnter += (s, e) => OnDragEnter(e);
            _screen.DragDrop += (s, e) => OnDragDrop(e);

            Controls.Add(_screen);
            Controls.Add(top);
            Controls.Add(_statusBar);

            // Phase 1B / 1D 右侧可折叠控制面板（画质调节 + 键盘监视）。
            BuildSidePanel();

            AttachInput();
            _screen.Paint += DrawCursorOverlay;
            _connectBtn.Click += (s, e) => _ = ConnectAsync();
            _disconnectBtn.Click += (s, e) => Disconnect();
            _favSaveBtn.Click += (s, e) => SaveFavorite();
            _wolBtn.Click += (s, e) => ShowWakeDialog();
            _chatBtn.Click += (s, e) => ShowChat();
            _fileBtn.Click += (s, e) => ViewerFileMenu();
            _termBtn.Click += (s, e) => OpenTerminal();
            _monBtn.Click += (s, e) => PickMonitor();
            _fullBtn.Click += (s, e) => ToggleFullscreen();
            _recBtn.Click += (s, e) => ToggleRecording();
            _specialBtn.Click += (s, e) => ShowSpecialKeys();
            _statBtn.Click += (s, e) => ShowStats();
            _audioBtn.Click += (s, e) => ToggleAudio();
            _panelBtn.Click += (s, e) => { _sidePanel.Visible = !_sidePanel.Visible; };
            _fsBtn.Click += (s, e) => OpenFileBrowser();
            _p2pChk.CheckedChanged += (s, e) =>
            {
                _p2pOn = _p2pChk.Checked;
                if (!_p2pOn) CloseP2P();
            };
            FormClosed += (s, e) => { UnhookKeyFilter(); Disconnect(); };
            KeyPreview = true;
            // Capture every physical key (Tab / arrows / Alt combos / F-keys)
            // application-wide so nothing is swallowed by control navigation.
            HookKeyFilter();

            _pingTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _pingTimer.Tick += (s, e) => OnTick();
            _clipTimer = new System.Windows.Forms.Timer { Interval = 700 };
            _clipTimer.Tick += (s, e) => PollClipboard();

            // ---- 命令行参数预填（静默模式 / 快速启动）---------------------
            opts = opts ?? new AppOptions();
            if (opts.Server != null) _serverBox.Text = opts.Server;
            if (opts.Port.HasValue) _portBox.Text = opts.Port.Value.ToString();
            if (opts.Room != null) _roomBox.Text = opts.Room;
            if (opts.Password != null) _pwBox.Text = opts.Password;
            if (opts.NoClip) _clipboardChk.Checked = false;
            if (opts.NoP2P) _p2pChk.Checked = false;

            // 自启：房间非空则启动后立即连接。
            if (opts.AutoStart && !string.IsNullOrWhiteSpace(_roomBox.Text))
            {
                Shown += (s, e) => _ = ConnectAsync();
            }
        }

        // ---- 从主界面“控制”按钮进入 ---------------------------------
        // 免房间号/口令：JOIN 用设备令牌。
        internal static ViewerForm LaunchCloud(CloudSession s, bool lite = false)
        {
            var f = new ViewerForm(new AppOptions());
            f._cloud = true;
            f._lite = lite;
            f._cloudToken = s.DeviceToken ?? "";
            f._cloudTarget = s.TargetDeviceId;
            f._cloudKey = s.AccountKey ?? "";
            f._cloudSession = s.SessionId ?? "";
            f._cloudUsername = s.Username ?? "";
            f._serverBox.Text = s.Server;
            f._portBox.Text = s.Port.ToString();
            f._roomBox.Text = s.SessionId;   // 仅展示，云模式不参与 JOIN
            f._roomBox.Enabled = false; f._pwBox.Enabled = false;
            f.Text = $"{(lite ? "快捷操作" : "远程控制")} - {s.TargetName}";
            if (lite) f.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            f.Show();
            _ = f.ConnectAsync();
            return f;
        }

        // ---- 远程协助：用对方给的房间号 + 密钥（legacy JOIN）连接对方指定设备 ----
        // 密钥既作中继配对凭证（HashPassword(key) == 对方主机 JOIN 的 hash），
        // 也作 E2E 种子（Aead.FromPassword(key, room)），两端一致即可解密。
        internal static ViewerForm LaunchAssist(string room, string key, string hostName)
        {
            var f = new ViewerForm(new AppOptions());
            f._cloud = false;
            f._serverBox.Text = string.IsNullOrWhiteSpace(UserSettings.Current.Server)
                ? CloudConfig.TcpHost : UserSettings.Current.Server;
            f._portBox.Text = UserSettings.Current.Port.ToString();
            f._roomBox.Text = room;
            f._pwBox.Text = key;
            f._roomBox.Enabled = false; f._pwBox.Enabled = false;
            f.Text = $"远程协助 - {hostName}";
            f.Show();
            _ = f.ConnectAsync();
            return f;
        }

        private void SaveFavorite()
        {
            if (!int.TryParse(_portBox.Text, out int port)) port = 25498;
            var f = new Common.Favorite
            {
                Name = _roomBox.Text + "@" + _serverBox.Text,
                Server = _serverBox.Text,
                Port = port,
                Room = _roomBox.Text,
                Password = _pwBox.Text,
            };
            var list = new System.Collections.Generic.List<Common.Favorite>(Common.LoadFavorites());
            int i = list.FindIndex(x => x.Name == f.Name);
            if (i >= 0) list[i] = f; else list.Add(f);
            Common.SaveFavorites(list.ToArray());
            _favBox.Items.Clear();
            foreach (var x in list) _favBox.Items.Add(x);
            _favBox.SelectedItem = f;
            SetState("已保存收藏: " + f.Name, Color.Gray);
        }

        private Task ConnectAsync()
        {
            if (_running) return Task.CompletedTask;
            _running = true; _reconnecting = false;
            _viewOnly = false; // fresh session starts in full-control unless host says otherwise
            _connStart = DateTime.UtcNow; // Phase 1A：已连接时长起点
            _cts = new CancellationTokenSource();
            StartDecodeThread();   // 独立解码线程：接收与解码解耦
            _pwHash = Common.HashPassword(_pwBox.Text);
            // A room password doubles as the end-to-end encryption secret;
            // it must match the host's for frames to decrypt.
            // 账号模式：用密钥 + 会话 id 派生（与被控端一致）。
            _aead = _cloud
                ? Aead.FromPassword(_cloudKey, _cloudSession)
                : Aead.FromPassword(_pwBox.Text, _roomBox.Text);
            _connectBtn.Enabled = false; _disconnectBtn.Enabled = true;
            SetState("正在连接…", Color.Green);
            _lastClipboard = SafeGetClipboard();
            _pingTimer.Start();
            if (_clipboardChk.Checked) _clipTimer.Start();
            var token = _cts.Token;
            return Task.Run(() => SessionLoop(token));
        }

        // Connects, runs RecvLoop, and reconnects with backoff after a drop.
        // A password rejection from the relay stops the session for good.
        private void SessionLoop(CancellationToken token)
        {
            int backoff = 800;
            bool bye = false;
            while (_running && !token.IsCancellationRequested)
            {
                Transport t;
                try
                {
                    t = Transport.Connect(_serverBox.Text, int.Parse(_portBox.Text));
                    t.SetCrypto(_aead);
                    if (_cloud)
                    {
                        t.SendJoinV2(_cloudToken, "viewer", _cloudTarget.ToString(), version: UpgradeCheck.CurrentVersion(),
                            displayName: AccountStore.Load()?.Username ?? Environment.UserName);
                        // confirm 授权模式下服务端会挂起等待被控端确认（最长 60s），
                        // 期间不回 RESULT；给出可理解的等待提示。
                        SetState("已请求控制，等待对方响应…", Color.DarkOrange);
                    }
                    else
                    {
                        t.SendJoin(_roomBox.Text, "viewer", _pwHash, version: UpgradeCheck.CurrentVersion(),
                            displayName: AccountStore.Load()?.Username ?? Environment.UserName);
                    }
                    // The relay answers with a RESULT frame (ok / reject).
                    if (!RelayHandshake(t, out string reject, out bool forceUpgrade))
                    {
                        t.Dispose();
                        _running = false;
                        if (forceUpgrade)
                        {
                            BeginInvoke((MethodInvoker)(() =>
                                MessageBox.Show(this, reject, "强制升级", MessageBoxButtons.OK, MessageBoxIcon.Stop)));
                        }
                        SetIdleUI(string.IsNullOrEmpty(reject) ? "连接被拒绝" : reject, Color.Red);
                        break;
                    }
                    t.Send(MessageType.Hello, Array.Empty<byte>()); // ask host for header + IDR
                    // 轻量会话：声明不需要视频，被控端据此跳过编码/发送。
                    if (_lite) t.Send(MessageType.NoVideo, Array.Empty<byte>());
                    // 上报本机公网候选（STUN），供对端 P2P 打洞直连、绕开慢中继。
                    _ = Task.Run(() => { try { StunProbe.SendPubCand(t); } catch { } });
                }
                catch (Exception ex)
                {
                    _reconnecting = true;
                    SetState("连接失败，重连中… " + ex.Message, Color.DarkOrange);
                    if (Sleep(backoff, token)) break;
                    backoff = Math.Min(backoff * 2, 8000);
                    continue;
                }
                _transport = t; backoff = 800; _reconnecting = false;
                SetState(_lite ? "已连接（轻量会话）" : "已连接，等待画面…", Color.Green);
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (_lite)
                        {
                            // 轻量会话：弹快捷操作面板，隐藏完整控制界面。
                            try { new QuickOpsForm(this, _cloudSession).Show(this); } catch { }
                            try { this.Hide(); } catch { }
                        }
                        else
                        {
                            _chatBtn.Enabled = _fileBtn.Enabled = _fsBtn.Enabled = _termBtn.Enabled = _fullBtn.Enabled = _recBtn.Enabled = _audioBtn.Enabled = _specialBtn.Enabled = _statBtn.Enabled = true;
                        }
                    }));
                }
                catch { }

                bye = RecvLoop(token);

            try { _transport?.Dispose(); } catch { }
            _transport = null;
            lock (_decLock) RcNative.rc_decoder_free();  // reset; reinitialised on next VideoConfig
                if (!_running || token.IsCancellationRequested || bye) break;

                _reconnecting = true;
                SetState("连接断开，重连中…", Color.DarkOrange);
                if (Sleep(1000, token)) break;
            }

            if (bye || !_running) FinishFromRemote();
        }

        // Returns true if the host sent an intentional Bye.
        private bool RecvLoop(CancellationToken token)
        {
            while (_running && !token.IsCancellationRequested)
            {
                if (_transport == null) return false;
                if (!_transport.TryReceive(out var type, out var payload)) return false;
                _bytesRecv += payload?.Length ?? 0;   // for bandwidth estimate
                if (type == MessageType.PeerAddr)
                {
                    // The relay hands us the host's address candidates (its
                    // STUN-discovered public address + the relay-seen address);
                    // try a direct TCP connection (hole punch) to stop bouncing
                    // through the relay.
                    Codec.ParsePeerAddr(payload, out int prole, out int pvid, out string pip, out int pport, out var cands);
                    if (prole == 0) TryStartP2P(BuildCandidates(pip, pport, cands));
                    continue;
                }
                if (type == MessageType.Bye) return true;
                // host -> viewer messages (relay OR P2P direct) share one handler.
                DispatchFromHost(type, payload);
            }
            return false;
        }

        // Shared handler for every host -> viewer message. Fed by BOTH the
        // relay path (the relay strips the outer envelope and forwards the
        // bare frame) and the P2P direct path (the direct Transport decrypts).
        private void DispatchFromHost(MessageType type, byte[] payload)
        {
            // 轻量会话不渲染画面/播放声音，直接丢弃相关帧（省 CPU）。
            if (_lite && (type == MessageType.VideoConfig || type == MessageType.VideoFrame
                       || type == MessageType.AudioConfig || type == MessageType.AudioFrame))
                return;
            // Phase 2 文件浏览器：把相关消息转发给文件浏览器窗体。
            switch (type)
            {
                case MessageType.FsListResp:
                case MessageType.FsGetReady:
                case MessageType.FsChunk:
                case MessageType.FsGetEnd:
                case MessageType.FsGetErr:
                case MessageType.FsPutReady:
                case MessageType.FsDeleteResp:
                case MessageType.FsRenameResp:
                case MessageType.FsMkdirResp:
                case MessageType.FsPutAck:
                    if (_fileBrowser != null && !_fileBrowser.IsDisposed) _fileBrowser.HandleFs(type, payload);
                    return;
            }
            if (type == MessageType.VideoConfig)
            {
                Codec.ParseVideoConfig(payload, out _hostW, out _hostH, out _, out var extra);
                lock (_decLock)
                {
                    if (extra.Length > 0)
                    {
                        IntPtr p = MarshalToUnmanaged(extra);
                        RcNative.rc_decoder_init(p, extra.Length);
                        Marshal.FreeHGlobal(p);
                    }
                    else RcNative.rc_decoder_init(IntPtr.Zero, 0);
                }
                _recExtra = extra;
                if (_recording && (_hostW != _recW || _hostH != _recH))
                    RecordRollover();
            }
            else if (type == MessageType.VideoFrame)
            {
                Codec.ParseVideoFrame(payload, out byte key, out var nal);
                if (_recording && nal != null && nal.Length > 0)
                {
                    try { RcNative.rc_record_write(nal, nal.Length, _recSw.ElapsedMilliseconds, key); }
                    catch { }
                }
                EnqueueNal(nal);
            }
            else if (type == MessageType.Ping)
            {
                if (payload != null && payload.Length >= 8)
                {
                    long sent = BitConverter.ToInt64(payload, 0);
                    _rttMs = (int)Math.Max(0, _pingSw.ElapsedMilliseconds - sent);
                }
            }
            else if (type == MessageType.Pong)  // Phase 7A: host acknowledged keep-alive
            {
                _lastPong = DateTime.UtcNow;
            }
            else if (type == MessageType.Clipboard)
            {
                string text = Codec.ParseClipboard(payload);
                if (!string.IsNullOrEmpty(text) && text != _lastClipboard)
                {
                    _lastClipboard = text;
                    BeginInvoke((MethodInvoker)(() => SafeSetClipboard(text)));
                }
            }
            else if (type == MessageType.ClipImage)
            {
                if (payload != null && payload.Length > 0)
                {
                    int h = ComputeClipHash(payload);
                    if (h != _lastClipImgHash)
                    {
                        _lastClipImgHash = h;
                        var png = payload;
                        BeginInvoke((MethodInvoker)(() => ApplyClipboardImage(png)));
                    }
                }
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
            else if (type == MessageType.Chat)
            {
                string text = Codec.ParseChat(payload);
                if (!string.IsNullOrEmpty(text))
                    BeginInvoke((MethodInvoker)(() => OnChat("[对方] " + text, true)));
            }
            else if (type == MessageType.FOpen)        // host -> viewer: incoming file
            {
                Codec.ParseFOpen(payload, out int fid, out int dir, out string name, out long size);
                BeginInvoke((MethodInvoker)(() => OnIncomingFile(fid, name, size)));
            }
            else if (type == MessageType.FResp)        // host -> viewer: accept/deny our send
            {
                Codec.ParseFResp(payload, out int id, out int accept);
                BeginInvoke((MethodInvoker)(() => OnSendAccepted(id, accept == 1)));
            }
            else if (type == MessageType.FData)
            {
                Codec.ParseFData(payload, out int id, out var chunk);
                if (_ft.ReceiveData(id, chunk)) { /* progress handled by callback */ }
                else SendToHost(MessageType.FCancel, Codec.BuildId(id));
            }
            else if (type == MessageType.FEnd)
            {
                int id = Codec.ParseId(payload);
                var tt = _ft.Find(id);
                string saved = tt?.Path;
                _ft.ReceiveEnd(id);
                if (!string.IsNullOrEmpty(saved))
                    BeginInvoke((MethodInvoker)(() => { SetState("文件已接收: " + saved, Color.Green); NotifyFileSaved(saved); }));
                else
                    BeginInvoke((MethodInvoker)(() => SetState("文件接收完成", Color.Green)));
            }
            else if (type == MessageType.FCancel)
            {
                int id = Codec.ParseId(payload);
                _ft.ReceiveCancel(id);
            }
            else if (type == MessageType.MonitorList)
            {
                ParseMonitorList(payload);
            }
            else if (type == MessageType.ViewOnly)
            {
                bool on = Codec.ParseViewOnly(payload);
                BeginInvoke((MethodInvoker)(() => SetViewOnly(on)));
            }
            else if (type == MessageType.AudioConfig)
            {
                _audioCfgSeen = true;
                if (_audioWant && !_audioStarted) StartAudioPlayback();
            }
            else if (type == MessageType.AudioFrame)
            {
                if (_audioStarted && payload != null && payload.Length > 0)
                {
                    try { RcNative.rc_audio_play_write(payload, payload.Length); } catch { }
                }
            }
            else if (type == MessageType.TerminalOut)
            {
                Codec.ParseTerminalOut(payload, out int stream, out var data);
                _termForm?.OnOutput(data, stream);
            }
            else if (type == MessageType.TerminalClose)
            {
                int code = Codec.ParseTerminalClose(payload);
                _termForm?.OnClosed(code);
                _termForm = null;
            }
            else if (type == MessageType.KeyEvent)   // Phase 1D：被控端按键实时流
            {
                Codec.ParseKeyEvent(payload, out int vk, out byte down);
                string s = VkToText(vk, down);
                if (!string.IsNullOrEmpty(s))
                    BeginInvoke((MethodInvoker)(() => AppendKeyLog(s)));
            }
            // Hello (P2P confirm) / Kick (relay converts to Bye) need no action.
        }

        // ---- Phase 1B / 1D：右侧控制面板 ---------------------------------
        private static FlowLayoutPanel MakeRow(string text, Control ctl)
        {
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = new Padding(0, 2, 0, 2),
            };
            row.Controls.Add(new Label { Text = text, AutoSize = true, Width = 52, TextAlign = ContentAlignment.MiddleLeft });
            row.Controls.Add(ctl);
            return row;
        }

        private void BuildSidePanel()
        {
            _sidePanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 250,
                Visible = false,
                BackColor = Color.FromArgb(0xF5, 0xF5, 0xF5),
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(8),
            };
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                AutoScroll = true,
                WrapContents = false,
                Padding = new Padding(0),
            };
            flow.Controls.Add(new Label { Text = "控制面板", Font = new Font("Segoe UI", 11f, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 8) });

            // 画质调节（Phase 1B）
            var g1 = new GroupBox { Text = "画质调节（实时）", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = 224 };
            var g1flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = 204, Padding = new Padding(6) };
            _resCbo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            _resCbo.Items.AddRange(new object[] { "100%", "75%", "50%" });
            _resCbo.SelectedIndex = 0;
            _fpsCbo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            _fpsCbo.Items.AddRange(new object[] { "10", "15", "20", "30", "60" });
            _fpsCbo.SelectedIndex = 3;
            _qualCbo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            _qualCbo.Items.AddRange(new object[] { "流畅", "标准", "均衡", "清晰", "超清" });
            _qualCbo.SelectedIndex = 2;
            g1flow.Controls.Add(MakeRow("分辨率", _resCbo));
            g1flow.Controls.Add(MakeRow("帧率", _fpsCbo));
            g1flow.Controls.Add(MakeRow("画质档", _qualCbo));
            g1.Controls.Add(g1flow);
            flow.Controls.Add(g1);

            // 键盘监视（Phase 1D）
            var g2 = new GroupBox { Text = "键盘监视（被控端按键）", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = 224 };
            var g2flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = 204, Padding = new Padding(6) };
            g2flow.Controls.Add(new Label { Text = "被控端实时按键流：", AutoSize = true });
            _keyLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Width = 200,
                Height = 170,
                Font = new Font("Consolas", 9f),
                BackColor = Color.White,
            };
            g2flow.Controls.Add(_keyLog);
            g2.Controls.Add(g2flow);
            flow.Controls.Add(g2);

            // Phase 5 会话内标注：控制端在对方屏幕上画箭头/文字，被控端实时可见。
            var g3 = new GroupBox { Text = "会话内标注（对方可见）", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = 224 };
            var g3flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = 204, Padding = new Padding(6) };
            _annoToggleBtn = new Button { Text = "进入标注模式", AutoSize = true, Width = 190 };
            _annoToggleBtn.Click += (s, e) => ToggleAnnoMode();
            _annoTools = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
            _annoTools.Items.AddRange(new object[] { "箭头（按住拖拽）", "文字（点击放置）" });
            _annoTools.SelectedIndex = 0;
            _annoColors = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
            _annoColors.Items.AddRange(new object[] { "红", "黄", "绿", "蓝", "白" });
            _annoColors.SelectedIndex = 0;
            _annoColors.SelectedIndexChanged += (s, e) =>
            {
                int[] colors = new int[] {
                    unchecked((int)0xFFFF0000), unchecked((int)0xFFFFFF00),
                    unchecked((int)0xFF00FF00), unchecked((int)0xFF0080FF),
                    unchecked((int)0xFFFFFFFF) };
                if (_annoColors.SelectedIndex >= 0 && _annoColors.SelectedIndex < colors.Length)
                    _annoColor = colors[_annoColors.SelectedIndex];
            };
            _annoClearBtn = new Button { Text = "清除所有标注", AutoSize = true, Width = 190 };
            _annoClearBtn.Click += (s, e) => AnnoClear();
            g3flow.Controls.Add(_annoToggleBtn);
            g3flow.Controls.Add(MakeRow("工具", _annoTools));
            g3flow.Controls.Add(MakeRow("颜色", _annoColors));
            g3flow.Controls.Add(_annoClearBtn);
            g3.Controls.Add(g3flow);
            flow.Controls.Add(g3);

            _sidePanel.Controls.Add(flow);
            Controls.Add(_sidePanel);

            // 任一画质项改变即实时下发 ViewerPref（Phase 1B）。
            EventHandler onPref = (s, e) => SendViewerPref();
            _resCbo.SelectedIndexChanged += onPref;
            _fpsCbo.SelectedIndexChanged += onPref;
            _qualCbo.SelectedIndexChanged += onPref;
        }

        // Phase 1B：把当前面板选择编码为 ViewerPref 发给被控端。
        private void SendViewerPref()
        {
            if (!_running) return;
            byte res = (byte)(_resCbo.SelectedIndex == 1 ? 75 : (_resCbo.SelectedIndex == 2 ? 50 : 100));
            byte fps = byte.TryParse(_fpsCbo.Text, out byte f) ? (byte)Math.Max(5, Math.Min(60, (int)f)) : (byte)30;
            byte q = (byte)(_qualCbo.SelectedIndex + 1);   // 1..5
            try { SendToHost(MessageType.ViewerPref, Codec.BuildViewerPref(res, fps, q)); } catch { }
        }

        // Phase 1D：把按键事件转成可读文本追加到键盘监视面板。
        private void AppendKeyLog(string s)
        {
            if (_keyLog == null || _keyLog.IsDisposed) return;
            if (_keyLog.Text.Length > 8000)
                _keyLog.Text = _keyLog.Text.Substring(_keyLog.Text.Length - 4000);
            _keyLog.AppendText(s);
            _keyLog.SelectionStart = _keyLog.Text.Length;
            _keyLog.ScrollToCaret();
        }

        // vkCode -> 显示文本（仅按下时输出字符；修饰键按下/抬起都标注）。
        private static string VkToText(int vk, byte down)
        {
            bool isDown = down != 0;
            if (vk >= 0x30 && vk <= 0x39) return isDown ? ((char)vk).ToString() : "";   // 0-9
            if (vk >= 0x41 && vk <= 0x5A) return isDown ? ((char)vk).ToString() : "";   // A-Z
            if (vk == 0x20) return isDown ? " " : "";                                 // 空格
            switch (vk)
            {
                case 0x0D: return isDown ? "[Enter]" : "";
                case 0x09: return isDown ? "[Tab]" : "";
                case 0x08: return isDown ? "[Back]" : "";
                case 0x1B: return isDown ? "[Esc]" : "";
                case 0x10: return isDown ? "[Shift]" : "[/Shift]";
                case 0x11: return isDown ? "[Ctrl]" : "[/Ctrl]";
                case 0x12: return isDown ? "[Alt]" : "[/Alt]";
                case 0x5B: case 0x5C: return isDown ? "[Win]" : "[/Win]";
                case 0x2E: return isDown ? "[Del]" : "";
                case 0x2D: return isDown ? "[Ins]" : "";
                case 0x24: return isDown ? "[Home]" : "";
                case 0x23: return isDown ? "[End]" : "";
                case 0x21: return isDown ? "[PgUp]" : "";
                case 0x22: return isDown ? "[PgDn]" : "";
                case 0x26: return isDown ? "[↑]" : "";
                case 0x28: return isDown ? "[↓]" : "";
                case 0x25: return isDown ? "[←]" : "";
                case 0x27: return isDown ? "[→]" : "";
            }
            if (vk >= 0x70 && vk <= 0x87) return isDown ? ("[F" + (vk - 0x6F) + "]") : "";
            if (vk >= 0x60 && vk <= 0x6F) return isDown ? ("[NP" + (vk - 0x60) + "]") : ""; // 小键盘
            try { return isDown ? ("[" + ((System.Windows.Forms.Keys)vk).ToString() + "]") : ""; }
            catch { return ""; }
        }

        // ---- 远程终端（控制端入口）----------------------------------------
        public void OpenTerminal()
        {
            if (_viewOnly) { MessageBox.Show(this, "对方已开启仅观看模式，无法使用终端。", "远程终端", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (_termForm != null) { try { _termForm.Activate(); } catch { } return; }
            if (!_running) { MessageBox.Show(this, "尚未连接，无法打开终端。", "远程终端", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            _termForm = new TerminalForm(
                (t, p) => SendToHost(t, p),
                () => { _termForm = null; });
            _termForm.Show(this);
        }

        // ---- P2P 鐩磋繛通圱CP hole punch通?-------------------------------------
        private void TryStartP2P(List<(string ip, int port)> cands)
        {
            if (!_p2pOn || !_running) return;
            if (_p2p != null) return;
            if (cands == null || cands.Count == 0) return;
            _p2pCandidates = cands;
            var cts = new CancellationTokenSource();
            _p2pCts = cts;
            var token = cts.Token;
            _ = Task.Run(() => P2PConnect(cands, token), token);
        }

        // 合并中继看到的地址与对端 STUN 公网候选，去重后作为打洞候选列表。
        private List<(string ip, int port)> BuildCandidates(string ip, int port, List<(string ip, int port)> cands)
        {
            var list = new List<(string ip, int port)>();
            if (!string.IsNullOrEmpty(ip) && port > 0) list.Add((ip, port));
            if (cands != null) foreach (var c in cands) if (!list.Contains(c)) list.Add(c);
            return list;
        }

        private void P2PConnect(List<(string ip, int port)> cands, CancellationToken token)
        {
            foreach (var c in cands)
            {
                for (int i = 0; i < 8 && !token.IsCancellationRequested; i++)
                {
                    if (!_running) return;
                    var tc = TryConnectTcp(c.ip, c.port, 1500);
                    if (tc != null) { SetupP2P(tc, token); return; }
                    Sleep(250, token);
                }
            }
        }

        private void SetupP2P(TcpClient tc, CancellationToken token)
        {
            if (!_running || token.IsCancellationRequested) { try { tc.Close(); } catch { } return; }
            var t = new Transport(tc);
            t.SetCrypto(_aead);                       // 复用会话 E2E 密钥
            lock (_p2pLock)
            {
                try { _p2p?.Dispose(); } catch { }
                _p2p = t;
            }
            _p2pEverConnected = true;
            SetState("已与对方建立直连 TCP（绕开中继）", Color.Green);
            var link = t;
            _ = Task.Run(() => P2PRecvLoop(token, link), token);
            try { t.Send(MessageType.Hello, Array.Empty<byte>()); }
            catch { CloseP2P(); return; }
        }

        private void P2PRecvLoop(CancellationToken token, Transport link)
        {
            try
            {
                while (_running && !token.IsCancellationRequested && link == _p2p)
                {
                    if (!link.TryReceive(out var type, out var payload)) break;
                    _bytesRecv += payload?.Length ?? 0;     // 带宽统计
                    if (type == MessageType.Hello)
                    {
                        if (!_p2pReady)
                        {
                            _p2pReady = true;
                            SetState("P2P 直连已确认", Color.Green);
                        }
                        continue;
                    }
                    DispatchFromHost(type, payload);
                }
            }
            catch { }
            if (link == _p2p) CloseP2P();
        }

        private void CloseP2P()
        {
            try { _p2pCts?.Cancel(); } catch { }
            Transport old;
            lock (_p2pLock) { old = _p2p; _p2p = null; }
            _p2pReady = false;
            try { old?.Dispose(); } catch { }
            if (_running && _p2pOn && _p2pEverConnected && !_p2pRetrying)
            {
                // 由于连接发生变化（NAT 临时失效等），触发重连重试。
                _p2pRetrying = true;
                var cands = _p2pCandidates;
                _ = Task.Run(() =>
                {
                    Sleep(2000, CancellationToken.None);
                    _p2pRetrying = false;
                    if (cands != null) TryStartP2P(cands);
                });
            }
        }

        // 优先走直连；直连不可用时退回中转。普通消息（输入/聊天/文件等）都走这里率?
        private void SendToHost(MessageType t, byte[] p)
        {
            // 仅对高带宽 / 低延迟类型走 P2P 直连；聊天、文件、剪贴板、控制指令等
            // 协作类消息一律走中转（中继）。原因：被控端在「协助房间」里是纯中转模式，
            // 不读 P2P 直连帧，若把这些消息发到 P2P 直连，对端（被协助的人）会收不到。
            // 这与 HostForm.TrySendP2P 的策略保持一致（它也只让视频/音频/心跳走直连）。
            bool p2pOk = _p2pOn && _p2pReady && _p2p != null;
            if (p2pOk && (t == MessageType.VideoFrame || t == MessageType.VideoConfig
                       || t == MessageType.AudioFrame || t == MessageType.AudioConfig
                       || t == MessageType.InputEvent || t == MessageType.Ping))
            {
                try { _p2p.Send(t, p); return; }
                catch { CloseP2P(); }
            }
            try { _transport?.Send(t, p); } catch { }
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

        // 接收线程调用：把一帧 NAL 拷出来入队（避免引用 payload 缓冲被复用），
        // 队列过长说明解码跟不上，丢最旧以保持"最新"（配合 drop-to-latest）。
        private void EnqueueNal(byte[] nal)
        {
            if (nal == null || nal.Length == 0 || !_running) return;
            Interlocked.Exchange(ref _lastFrameBytes, nal.Length); // Phase 1A：底部状态栏“单帧大小”
            var copy = new byte[nal.Length];
            Buffer.BlockCopy(nal, 0, copy, 0, nal.Length);
            while (_nalQueue.Count > 3) _nalQueue.TryDequeue(out _); // 背压：丢最旧
            _nalQueue.Enqueue(copy);
        }

        // 解码线程消费：NAL -> 解码 -> 写最新 BGRA -> 通知 UI 线程绘制。
        // 解码在 _decLock 内串行（解码器是全局单例），但已不在网络接收路径上，
        // 因此慢速解码不会再阻塞 socket 读取 / 中继 TCP 反压。
        private void DecodeOne(byte[] nal)
        {
            if (nal == null || nal.Length == 0 || !_running) return;
            IntPtr un = Marshal.AllocHGlobal(nal.Length);
            try
            {
                Marshal.Copy(nal, 0, un, nal.Length);
                byte[] bgra = null; int w = 0, h = 0;
                // 解码器全局单例：解码与 rc_decoder_free() 必须串行（Disconnect 在 UI 线程释放）。
                lock (_decLock)
                {
                    int dr = RcNative.rc_decoder_decode(un, nal.Length, out IntPtr rgba, out w, out h);
                    if (dr == RcNative.RC_OK && rgba != IntPtr.Zero)
                    {
                        EnsureBgraPool(w, h);
                        if (!_freeBufs.TryPop(out bgra)) bgra = new byte[w * h * 4]; // 兜底，正常不会触发
                        Marshal.Copy(rgba, bgra, 0, w * h * 4);
                        _decCounter++;
                    }
                }
                if (bgra == null) return;
                if (IsDisposed || !_running) { _freeBufs.Push(bgra); return; }
                // 只保留最新一帧用于绘制：丢弃排队的旧帧，控制端始终看到最新画面。
                bool needPost;
                lock (_paintLock)
                {
                    // 旧的最新帧已被更新的帧取代，回收其缓冲（drop-to-latest）。
                    if (_latestBgra != null) _freeBufs.Push(_latestBgra);
                    _latestBgra = bgra; _latestW = w; _latestH = h;
                    needPost = !_paintPending;
                    _paintPending = true;
                }
                if (needPost)
                {
                    // GDI+ Bitmaps are not thread-safe: repaint on the UI thread.
                    try { BeginInvoke((MethodInvoker)PaintLatest); } catch { }
                }
            }
            finally { Marshal.FreeHGlobal(un); }
        }

        // 按当前分辨率建立 3 个固定 BGRA 缓冲（关键：永不每帧 new 5.76MB 进 LOH）。
        // 分辨率变化时清空旧池并把 _latestBgra 置空，避免把旧尺寸缓冲误用进新池。
        private void EnsureBgraPool(int w, int h)
        {
            if (_poolW == w && _poolH == h && _poolW != 0) return;
            lock (_paintLock)
            {
                _poolW = w; _poolH = h;
                int bytes = w * h * 4;
                _freeBufs.Clear();
                _freeBufs.Push(new byte[bytes]);
                _freeBufs.Push(new byte[bytes]);
                _freeBufs.Push(new byte[bytes]);
                _latestBgra = null;   // 分辨率切换：丢弃过期帧引用
                _paintPending = false;
            }
        }

        private void StartDecodeThread()
        {
            if (_decodeRunning) return;
            _decodeRunning = true;
            _decCts = new CancellationTokenSource();
            var token = _decCts.Token;
            _decodeThread = new Thread(() => DecodeLoop(token)) { IsBackground = true, Name = "DecodeWorker" };
            _decodeThread.Start();
        }

        private void DecodeLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _running)
            {
                if (_nalQueue.TryDequeue(out var nal)) DecodeOne(nal);
                else Thread.Sleep(1);
            }
        }

        private void StopDecodeThread()
        {
            _decodeRunning = false;
            try { _decCts?.Cancel(); } catch { }
            try { _decodeThread?.Join(1000); } catch { }
            _decodeThread = null;
            while (_nalQueue.TryDequeue(out _)) { } // 清空积压，避免下次连接复用旧帧
        }

        // Paint only the most recently decoded frame. Intermediate frames that
        // arrived while a paint was already queued are skipped (their buffers
        // are simply overwritten by the next decode), so display latency stays
        // at ~1 frame regardless of how far ahead the network/decoder got.
        private void PaintLatest()
        {
            byte[] bgra; int w, h;
            lock (_paintLock)
            {
                bgra = _latestBgra; w = _latestW; h = _latestH;
                _latestBgra = null;
                _paintPending = false;     // 允许下一帧再次投递绘制
            }
            if (bgra == null || !_running || IsDisposed) return;
            PaintBitmap(bgra, w, h);
        }

        private void PaintBitmap(byte[] bgra, int w, int h)
        {
            if (!_running || IsDisposed) return;   // skip if disconnected
            if (_bmp == null || _bmp.Width != w || _bmp.Height != h)
            {
                _bmp?.Dispose();
                _bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                _screen.Image = _bmp;
            }
            var bd = _bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(bgra, 0, bd.Scan0, bgra.Length);
            _bmp.UnlockBits(bd);
            _screen.Invalidate();
        }

        private static IntPtr MarshalToUnmanaged(byte[] src)
        {
            var p = Marshal.AllocHGlobal(src.Length);
            Marshal.Copy(src, 0, p, src.Length);
            return p;
        }

        // ---- coordinate mapping (Zoom letterbox aware) ---------------------
        // The PictureBox is in Zoom mode, so the image is centred with black
        // bars. Map client coordinates through the *displayed* image rect.
        private Rectangle ImageRect()
        {
            int cw = _screen.ClientSize.Width, ch = _screen.ClientSize.Height;
            if (_hostW <= 0 || _hostH <= 0 || cw <= 0 || ch <= 0)
                return new Rectangle(0, 0, cw, ch);
            double ir = (double)_hostW / _hostH;
            double cr = (double)cw / ch;
            int dw, dh;
            if (cr > ir) { dh = ch; dw = (int)Math.Round(ch * ir); }
            else         { dw = cw; dh = (int)Math.Round(cw / ir); }
            int ox = (cw - dw) / 2, oy = (ch - dh) / 2;
            return new Rectangle(ox, oy, dw, dh);
        }

        private bool MapToHost(int ex, int ey, out int hx, out int hy)
        {
            hx = hy = 0;
            var rc = ImageRect();
            if (rc.Width <= 0 || rc.Height <= 0) return false;
            double fx = (ex - rc.X) / (double)rc.Width;
            double fy = (ey - rc.Y) / (double)rc.Height;
            if (fx < 0 || fx > 1 || fy < 0 || fy > 1) return false; // over the letterbox
            hx = (int)(fx * (_hostW - 1));
            hy = (int)(fy * (_hostH - 1));
            return true;
        }

        // ---- Phase 5 会话内标注 --------------------------------------------
        // 标注用归一化坐标(0~1)存储，绘制时映射到 _screen 的显示画幅(ImageRect)，
        // 这样本地预览与控制端视频一一对应；发往被控端后对方按自身分辨率还原。
        private void NormToPixel(float fx, float fy, out int px, out int py)
        {
            var rc = ImageRect();
            px = rc.Width > 0 ? (int)(rc.X + fx * rc.Width) : (int)(fx * _screen.ClientSize.Width);
            py = rc.Height > 0 ? (int)(rc.Y + fy * rc.Height) : (int)(fy * _screen.ClientSize.Height);
        }
        private void PixelToNorm(int px, int py, out float fx, out float fy)
        {
            var rc = ImageRect();
            if (rc.Width > 0 && rc.Height > 0) { fx = (px - rc.X) / (float)rc.Width; fy = (py - rc.Y) / (float)rc.Height; }
            else { fx = px / (float)Math.Max(1, _screen.ClientSize.Width); fy = py / (float)Math.Max(1, _screen.ClientSize.Height); }
        }

        private void ToggleAnnoMode()
        {
            _annoMode = !_annoMode;
            if (_annoMode) { try { Cursor.Show(); } catch { } _screen.Cursor = Cursors.Cross; }
            else { _screen.Cursor = Cursors.Default; _annoDragging = false; _annoDraft = null; }
            if (_annoMode && (_sidePanel == null || !_sidePanel.Visible))
                try { _sidePanel.Visible = true; } catch { }
            if (_annoToggleBtn != null)
            {
                _annoToggleBtn.Text = _annoMode ? "退出标注模式" : "进入标注模式";
                _annoToggleBtn.BackColor = _annoMode ? Color.LightGreen : SystemColors.Control;
            }
            try { _screen.Invalidate(); } catch { }
        }

        private void AnnoMouseDown(MouseEventArgs e)
        {
            PixelToNorm(e.X, e.Y, out float fx, out float fy);
            if (_annoTools != null && _annoTools.SelectedIndex == 1) // 文字
            {
                var txt = PromptText("文字标注", "输入要显示在对方屏幕上的文字：");
                if (!string.IsNullOrEmpty(txt))
                {
                    var a = new Anno { Kind = AnnoKind.Text, X1 = fx, Y1 = fy, ColorArgb = _annoColor, Text = txt };
                    _annos.Add(a);
                    SendAnno(a);
                    try { _screen.Invalidate(); } catch { }
                }
            }
            else // 箭头（拖拽）
            {
                _annoDragging = true;
                _annoDraft = new Anno { Kind = AnnoKind.Arrow, X1 = fx, Y1 = fy, X2 = fx, Y2 = fy, ColorArgb = _annoColor };
            }
        }
        private void AnnoMouseMove(MouseEventArgs e)
        {
            if (!_annoDragging || _annoDraft == null) return;
            PixelToNorm(e.X, e.Y, out float fx, out float fy);
            _annoDraft.X2 = fx; _annoDraft.Y2 = fy;
            try { _screen.Invalidate(); } catch { }
        }
        private void AnnoMouseUp(MouseEventArgs e)
        {
            if (!_annoDragging || _annoDraft == null) return;
            PixelToNorm(e.X, e.Y, out float fx, out float fy);
            _annoDraft.X2 = fx; _annoDraft.Y2 = fy;
            if (Math.Abs(_annoDraft.X2 - _annoDraft.X1) > 0.004 || Math.Abs(_annoDraft.Y2 - _annoDraft.Y1) > 0.004)
            {
                _annos.Add(_annoDraft);
                SendAnno(_annoDraft);
            }
            _annoDraft = null;
            _annoDragging = false;
            try { _screen.Invalidate(); } catch { }
        }
        private void AnnoClear()
        {
            _annos.Clear();
            _annoDraft = null; _annoDragging = false;
            SendAnno(new Anno { Kind = AnnoKind.Clear });
            try { _screen.Invalidate(); } catch { }
        }

        private void SendAnno(Anno a)
        {
            if (!_running) return;
            try { SendToHost(MessageType.AnnoFrame, Codec.BuildAnno(a)); } catch { }
        }

        private void DrawAnnotations(Graphics g)
        {
            if (_annos.Count == 0 && _annoDraft == null) return;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            foreach (var a in _annos) DrawAnno(g, a);
            if (_annoDraft != null) DrawAnno(g, _annoDraft);
        }
        private void DrawAnno(Graphics g, Anno a)
        {
            var col = Color.FromArgb(a.ColorArgb);
            if (a.Kind == AnnoKind.Arrow)
            {
                NormToPixel(a.X1, a.Y1, out int x1, out int y1);
                NormToPixel(a.X2, a.Y2, out int x2, out int y2);
                using var pen = new Pen(col, 3f);
                g.DrawLine(pen, x1, y1, x2, y2);
                float ang = (float)Math.Atan2(y2 - y1, x2 - x1);
                float len = 16;
                var p1 = new PointF(x2 - len * (float)Math.Cos(ang - 0.4f), y2 - len * (float)Math.Sin(ang - 0.4f));
                var p2 = new PointF(x2 - len * (float)Math.Cos(ang + 0.4f), y2 - len * (float)Math.Sin(ang + 0.4f));
                using var brush = new SolidBrush(col);
                g.FillPolygon(brush, new PointF[] { new PointF(x2, y2), p1, p2 });
            }
            else if (a.Kind == AnnoKind.Text)
            {
                NormToPixel(a.X1, a.Y1, out int x, out int y);
                using var font = new Font("Segoe UI", 16f, FontStyle.Bold);
                var size = g.MeasureString(a.Text, font);
                using var bg = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
                g.FillRectangle(bg, x, y, size.Width + 8, size.Height + 4);
                using var brush = new SolidBrush(col);
                g.DrawString(a.Text, font, brush, x + 4, y + 2);
            }
        }

        private static string PromptText(string title, string prompt, string def = "")
        {
            using var f = new Form
            {
                Width = 380, Height = 150, Text = title,
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false,
            };
            var lbl = new Label { Left = 12, Top = 14, Text = prompt, AutoSize = true };
            var tb = new TextBox { Left = 12, Top = 40, Width = 344, Text = def };
            var ok = new Button { Text = "确定", Left = 196, Top = 84, DialogResult = DialogResult.OK, Width = 72 };
            var cancel = new Button { Text = "取消", Left = 276, Top = 84, DialogResult = DialogResult.Cancel, Width = 72 };
            f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
            f.AcceptButton = ok; f.CancelButton = cancel;
            return f.ShowDialog() == DialogResult.OK ? tb.Text : null;
        }

        // ---- input capture -------------------------------------------------
        private void AttachInput()
        {
            _screen.MouseMove += (s, e) =>
            {
                if (_annoMode) { AnnoMouseMove(e); return; }
                if (_viewOnly) return;
                _mousePos = e.Location;
                if (_cursorInside) _screen.Invalidate();
                if (!_running || _hostW == 0) return;
                if (MapToHost(e.X, e.Y, out int x, out int y))
                    SendToHost(MessageType.InputEvent, Codec.BuildInputMove(x, y));
            };
            _screen.MouseEnter += (s, e) =>
            {
                // 标注模式下保留系统光标（用于绘制），不隐藏、不抢焦点。
                if (_annoMode) { _cursorInside = false; return; }
                // In view-only we must NOT steal focus or hide the local cursor;
                // the operator should keep using their own machine normally.
                if (_viewOnly || !_running) return;
                _cursorInside = true;
                _screen.Focus(); Cursor.Hide(); _screen.Invalidate();
            };
            _screen.MouseLeave += (s, e) =>
            {
                if (_cursorInside && _running) { try { Cursor.Show(); } catch { } }
                _cursorInside = false;
                _screen.Invalidate();
            };
            _screen.MouseDown += (s, e) =>
            {
                if (_annoMode) { AnnoMouseDown(e); return; }
                if (_viewOnly || !_running) return;
                _screen.Focus();
                byte b = e.Button == MouseButtons.Right ? (byte)1 : e.Button == MouseButtons.Middle ? (byte)2 : (byte)0;
                SendToHost(MessageType.InputEvent, Codec.BuildInputButton(b, 1));
            };
            _screen.MouseUp += (s, e) =>
            {
                if (_annoMode) { AnnoMouseUp(e); return; }
                if (_viewOnly || !_running) return;
                byte b = e.Button == MouseButtons.Right ? (byte)1 : e.Button == MouseButtons.Middle ? (byte)2 : (byte)0;
                SendToHost(MessageType.InputEvent, Codec.BuildInputButton(b, 0));
            };
            _screen.MouseWheel += (s, e) =>
            {
                if (_annoMode) return;
                if (_viewOnly || !_running) return;
                SendToHost(MessageType.InputEvent, Codec.BuildInputWheel(e.Delta));
            };
            // Keyboard is handled by the application-wide IMessageFilter
            // (PreFilterMessage) so special keys are never lost to navigation.
        }

        // ---- keyboard: full capture via message filter ---------------------
        private void HookKeyFilter()
        {
            if (_filterHooked) return;
            Application.AddMessageFilter(this);
            _filterHooked = true;
        }

        private void UnhookKeyFilter()
        {
            if (!_filterHooked) return;
            Application.RemoveMessageFilter(this);
            _filterHooked = false;
        }

        // Should we grab keystrokes right now? Only while a session is live and
        // the remote screen holds focus — this keeps the connection textboxes
        // usable and lets other windows (chat, file dialogs) type normally.
        // View-only mode forbids any control input, so capture is off.
        private bool WantKeyCapture()
            => _running && !_viewOnly && !IsDisposed && Form.ActiveForm == this
               && UserSettings.Current.KeyboardMapping
               && (ActiveControl == _screen || FormBorderStyle == FormBorderStyle.None);

        // Apply the host-enforced "view-only" state. While on, the viewer can
        // watch the screen and chat, but cannot send input, switch displays, or
        // push files. The host drops those messages anyway; this only makes the
        // local UI honest so the operator's own keystrokes go to their machine.
        private void SetViewOnly(bool on)
        {
            _viewOnly = on;
            if (IsDisposed) return;
            try
            {
                _viewOnlyLabel.Visible = on;
                _fileBtn.Enabled = on ? false : _running;
                _termBtn.Enabled = on ? false : _running;   // 仅观看：禁止终端
                _specialBtn.Enabled = on ? false : _running;
                _monBtn.Enabled = on ? false : (_running && _hostMonitors.Count > 1);
                SetState(on ? "对方已开启仅观看：你只能观看，无法操作"
                            : "对方已关闭仅观看：已恢复完整控制",
                         on ? Color.DarkOrange : Color.Green);
                // Make sure a captured-but-now-forbidden key is released to the
                // local machine (e.g. if the user was mid-typing when toggled).
                if (on && _cursorInside) { try { Cursor.Show(); } catch { } _cursorInside = false; }
            }
            catch { }
        }

        bool IMessageFilter.PreFilterMessage(ref Message m)
        {
            const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101,
                      WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
            if (!WantKeyCapture()) return false;
            switch (m.Msg)
            {
                case WM_KEYDOWN:
                case WM_SYSKEYDOWN:
                    SendKey((uint)m.WParam.ToInt64(), 1);
                    return true;   // swallow: don't let WinForms act locally
                case WM_KEYUP:
                case WM_SYSKEYUP:
                    SendKey((uint)m.WParam.ToInt64(), 0);
                    return true;
            }
            return false;
        }

        private void SendKey(uint vk, byte down)
        {
            try { SendToHost(MessageType.InputEvent, Codec.BuildInputKey(vk, down)); }
            catch { }
        }

        // Send a Ctrl system command (see HostForm.ExecSystemCommand).
        private void SendCtrl(int cmd)
        {
            try { SendToHost(MessageType.Ctrl, Codec.BuildCtrl(cmd)); } catch { }
        }

        // ---- 供 QuickOpsForm（轻量会话）调用的公开入口 -------------------
        public void RequestReboot() => SendCtrl(3);
        public void RequestShutdown() => SendCtrl(4);
        public void TriggerFileSend() => ViewerFileMenu();
        public void CloseSession() => Disconnect();

        private bool _remoteBlackOn;
        private void ToggleRemoteBlack()
        {
            _remoteBlackOn = !_remoteBlackOn;
            SendCtrl(_remoteBlackOn ? 13 : 14);
            SetState(_remoteBlackOn ? "已请求对方隐私黑屏" : "已请求关闭对方黑屏", Color.Gray);
        }

        // Press a chord (down in order, up in reverse) — used by the special
        // keys menu for combos such as Alt+Tab, Win, Alt+F4.
        private void SendCombo(params uint[] vks)
        {
            if (_transport == null) return;
            for (int i = 0; i < vks.Length; i++) SendKey(vks[i], 1);
            System.Threading.Thread.Sleep(40);
            for (int i = vks.Length - 1; i >= 0; i--) SendKey(vks[i], 0);
        }

        // Virtual-key constants we need for combos.
        private const uint VK_CONTROL = 0x11, VK_MENU = 0x12, VK_SHIFT = 0x10,
                           VK_LWIN = 0x5B, VK_TAB = 0x09, VK_ESCAPE = 0x1B,
                           VK_DELETE = 0x2E, VK_F4 = 0x73, VK_SNAPSHOT = 0x2C,
                           VK_D = 0x44, VK_E = 0x45;

        private void ShowSpecialKeys()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Ctrl + Alt + Del", null, (s, e) =>
            {
                // A synthesised SendInput can't raise the secure attention
                // sequence, so ask the host to call SendSAS on its side.
                try { SendToHost(MessageType.Ctrl, Codec.BuildCtrl(10)); } catch { }
            });
            menu.Items.Add("任务管理器 (Ctrl+Shift+Esc)", null, (s, e) => SendCombo(VK_CONTROL, VK_SHIFT, VK_ESCAPE));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Alt + Tab", null, (s, e) => SendCombo(VK_MENU, VK_TAB));
            menu.Items.Add("Win (开始菜单)", null, (s, e) => SendCombo(VK_LWIN));
            menu.Items.Add("显示桌面 (Win+D)", null, (s, e) => SendCombo(VK_LWIN, VK_D));
            menu.Items.Add("文件资源管理器 (Win+E)", null, (s, e) => SendCombo(VK_LWIN, VK_E));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Alt + F4 (关闭窗口)", null, (s, e) => SendCombo(VK_MENU, VK_F4));
            menu.Items.Add("Esc", null, (s, e) => SendCombo(VK_ESCAPE));
            menu.Items.Add("截屏 (PrintScreen)", null, (s, e) => SendCombo(VK_SNAPSHOT));
            menu.Items.Add(new ToolStripSeparator());
            // Remote privacy / power (routed via Ctrl commands the host runs).
            string blackLabel = _remoteBlackOn ? "关闭对方隐私黑屏" : "开启对方隐私黑屏";
            menu.Items.Add(blackLabel, null, (s, e) => ToggleRemoteBlack());
            menu.Items.Add("关闭对方显示器", null, (s, e) => SendCtrl(12));
            menu.Items.Add("让对方睡眠(待机)", null, (s, e) =>
            {
                if (MessageBox.Show(this, "让被控端进入睡眠？会断开连接吗？", "远程待机",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    SendCtrl(11);
            });
            menu.Show(_specialBtn, new Point(0, _specialBtn.Height));
        }

        // Draw a crisp local pointer so cursor motion feels instant even when
        // the video stream lags.
        private static readonly Point[] ArrowShape =
        {
            new Point(0, 0), new Point(0, 17), new Point(4, 13), new Point(7, 19),
            new Point(10, 18), new Point(6, 12), new Point(12, 12)
        };

        private void DrawCursorOverlay(object sender, PaintEventArgs e)
        {
            // Phase 5：无论光标是否在内，都先把已存在的标注画在最上层。
            DrawAnnotations(e.Graphics);
            if (!_running || !_cursorInside || _hostW == 0) return;
            var pts = new Point[ArrowShape.Length];
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new Point(ArrowShape[i].X + _mousePos.X, ArrowShape[i].Y + _mousePos.Y);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(Color.White);
            using var pen = new Pen(Color.Black, 1.4f);
            e.Graphics.FillPolygon(fill, pts);
            e.Graphics.DrawPolygon(pen, pts);
        }

        // ---- timers --------------------------------------------------------
        private void OnTick()
        {
            // Refresh decode-fps once per second.
            if (DateTime.UtcNow - _decStamp >= TimeSpan.FromSeconds(1))
            {
                _decFps = _decCounter; _decCounter = 0; _decStamp = DateTime.UtcNow;
            }
            // Receive bandwidth over the last second (kbit/s).
            _bwKbps = _bytesRecv * 8.0 / 1000.0; _bytesRecv = 0;
            // Jitter: mean absolute successive difference of RTT over a window.
            if (_rttMs >= 0)
            {
                _rttHist.Enqueue(_rttMs);
                while (_rttHist.Count > 20) _rttHist.Dequeue();
                _jitterMs = ComputeJitter(_rttHist);
            }
            // Send a ping stamped with our clock so we can measure RTT. Routes
            // via P2P when available (so the measurement stays on the direct
            // path), else via the relay.
            if (_running && !_reconnecting)
            {
                // Phase 7A: 15s 无 Pong → 断开（被控端已死或网络中断）
                if ((DateTime.UtcNow - _lastPong).TotalSeconds > 15)
                {
                    _running = false;
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (!IsDisposed)
                        {
                            MessageBox.Show(this, "与被控端的连接已断开（心跳超时）。", "连接中断",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            SetIdleUI("连接超时", Color.Gray);
                        }
                    }));
                    return;
                }
                try { SendToHost(MessageType.Ping, BitConverter.GetBytes(_pingSw.ElapsedMilliseconds)); } catch { }
                // 把本机实测的链路质量（RTT/jitter/解码帧率/带宽）回传给被控端，
                // 让它的自适应控制器用真实延迟而非发送阻塞代理值，消除码率震荡率?
                if (_rttMs >= 0)
                    try { SendToHost(MessageType.LinkStat, Codec.BuildLinkStat(_rttMs, _jitterMs, _decFps, (int)_bwKbps)); } catch { }
            }
            // Feed the connection-quality panel if it is open.
            if (_statForm != null && !_statForm.IsDisposed && _running && !_reconnecting)
            {
                bool relay = !_p2pReady;   // 直连就绪则显绀? P2P，否则显示经中转
                _statForm.Push(_rttMs, _jitterMs, _decFps, _bwKbps,
                               _hostW > 0 ? $"{_hostW}x{_hostH}" : "--", relay);
            }
            // Compose the status line.
            if (_running && !_reconnecting && _hostW > 0)
            {
                string rtt = _rttMs < 0 ? "--" : _rttMs.ToString();
                string sec = _aead != null ? "🔒" : "🔓";
                string aud = _audioStarted ? " | 🔊" : "";
                string p2p = _p2pReady ? " | 🔗直连" : "";
                _status.Text = $"已连接 {sec} | {_hostW}x{_hostH} | RTT {rtt}ms | 解码 {_decFps}fps{aud}{p2p}";
                _status.ForeColor = Color.Green;
            }
            else
            {
                _status.Text = _stateText; _status.ForeColor = _stateColor;
            }

            // ---- Phase 1A 底部多列专业状态栏 ----
            UpdateStatusBar();
        }

        private void UpdateStatusBar()
        {
            if (_statusBar == null) return;
            if (_running && !_reconnecting && _hostW > 0)
            {
                var ts = _connStart == DateTime.MinValue ? TimeSpan.Zero : (DateTime.UtcNow - _connStart);
                string dur = ts.TotalHours >= 1
                    ? (int)ts.TotalHours + ":" + ts.Minutes.ToString("00") + ":" + ts.Seconds.ToString("00")
                    : (int)ts.TotalMinutes + ":" + ts.Seconds.ToString("00");
                string bw  = _bwKbps >= 1000 ? (_bwKbps / 1000.0).ToString("0.0") + " Mbps" : ((int)_bwKbps) + " kbps";
                string fb  = _lastFrameBytes <= 0 ? "--" : (_lastFrameBytes / 1024.0).ToString("0.0") + " KB";
                string rtt = _rttMs < 0 ? "--" : _rttMs + " ms";
                string enc = "H264";
                if (_p2pReady) enc += " · 🔗直连"; else if (_aead != null) enc += " · 🔒";
                if (_audioStarted) enc += " · 🔊";
                _statCell[0].Text = "FPS " + _decFps;
                _statCell[1].Text = "带宽 " + bw;
                _statCell[2].Text = "单帧 " + fb;
                _statCell[3].Text = "编码 " + enc;
                _statCell[4].Text = "RTT " + rtt;
                _statCell[5].Text = "分辨率 " + _hostW + "x" + _hostH;
                _statCell[6].Text = "时长 " + dur;
                for (int i = 0; i < 6; i++) _statCell[i].ForeColor = Color.FromArgb(0x2E, 0x7D, 0x32);
                _statCell[6].ForeColor = Color.FromArgb(0x55, 0x55, 0x55);
            }
            else
            {
                string[] idle = { "FPS --", "带宽 --", "单帧 --", "编码 --", "RTT --", "分辨率 --", _stateText };
                for (int i = 0; i < 7; i++)
                {
                    _statCell[i].Text = idle[i];
                    _statCell[i].ForeColor = i == 6 ? _stateColor : Color.FromArgb(0xAA, 0xAA, 0xAA);
                }
            }
        }

        private static int ComputeJitter(System.Collections.Generic.Queue<int> hist)
        {
            if (hist.Count < 2) return -1;
            var arr = hist.ToArray();
            long sum = 0; int n = 0;
            for (int i = 1; i < arr.Length; i++) { sum += Math.Abs(arr[i] - arr[i - 1]); n++; }
            return n == 0 ? 0 : (int)(sum / n);
        }

        private void ShowStats()
        {
            if (_statForm == null || _statForm.IsDisposed) _statForm = new StatsForm();
            try { _statForm.Show(); _statForm.BringToFront(); } catch { }
        }

        // ---- Wake-on-LAN ---------------------------------------------------
        private void ShowWakeDialog()
        {
            var dlg = new Form
            {
                Text = "远程唤醒 (WOL)",
                Width = 340, Height = 170,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false,
            };
            var lp = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
            lp.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            lp.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            lp.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

            var macLabel = new Label { Text = "MAC 地址:", AutoSize = true, Dock = DockStyle.Left };
            var macBox = new TextBox { Dock = DockStyle.Fill }; macLabel.Size = new Size(80, 23);
            var ipLabel = new Label { Text = "广播 IP:", AutoSize = true, Dock = DockStyle.Left };
            var ipBox = new TextBox { Text = "255.255.255.255", Dock = DockStyle.Fill }; ipLabel.Size = new Size(80, 23);

            var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var sendBtn = new Button { Text = "发送", Width = 70 };
            btnPanel.Controls.Add(sendBtn);

            var macRow = new Panel { Dock = DockStyle.Fill }; macRow.Controls.Add(macBox); macRow.Controls.Add(macLabel);
            var ipRow = new Panel { Dock = DockStyle.Fill }; ipRow.Controls.Add(ipBox); ipRow.Controls.Add(ipLabel);

            lp.Controls.Add(macRow, 0, 0);
            lp.Controls.Add(ipRow, 0, 1);
            lp.Controls.Add(btnPanel, 0, 2);
            dlg.Controls.Add(lp);

            sendBtn.Click += (s, e) =>
            {
                if (Common.SendWakeOnLan(macBox.Text, ipBox.Text))
                {
                    SetState("已发送 WOL 魔术包 → " + macBox.Text, Color.Green);
                    dlg.Close();
                }
                else SetState("WOL 发送失败（检查 MAC 地址格式如 AA:BB:CC:DD:EE:FF）", Color.Red);
            };
            dlg.ShowDialog(this);
        }

        // ---- drag-drop file sending ----------------------------------------
        protected override void OnDragEnter(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
            base.OnDragEnter(e);
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                    SendFilesToHost(files);
            }
            base.OnDragDrop(e);
        }

        private void PollClipboard()
        {
            if (!_running || !_clipboardChk.Checked) return;
            string cur = SafeGetClipboard();
            if (!string.IsNullOrEmpty(cur) && cur != _lastClipboard)
            {
                _lastClipboard = cur;
                try { SendToHost(MessageType.Clipboard, Codec.BuildClipboard(cur)); } catch { }
            }
            PollClipboardImage();
        }

        // Sends the local clipboard image as PNG when it changes (loop-guarded
        // by a cheap content hash so a received image is not echoed back).
        private void PollClipboardImage()
        {
            byte[] png = SafeGetClipboardImagePng();
            if (png == null || png.Length == 0 || png.Length > 8 * 1024 * 1024) return;
            int h = ComputeClipHash(png);
            if (h == _lastClipImgHash) return;
            _lastClipImgHash = h;
            try { SendToHost(MessageType.ClipImage, png); } catch { }
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
                img.Save(ms, ImageFormat.Png);
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

        // ---- session recording ---------------------------------------------
        private void ToggleRecording()
        {
            if (_recording) { StopRecording(); return; }
            if (_hostW <= 0 || _hostH <= 0)
            {
                SetState("尚未收到画面，无法开始录制", Color.DarkOrange);
                return;
            }
            using var dlg = new FolderBrowserDialog
            {
                Description = "选择录像保存目录",
                SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _recDir = dlg.SelectedPath;
            _recPart = 0;
            if (StartRecordingFile())
            {
                _recording = true;
                _recBtn.Text = "停止录制";
                SetState("录制中 → " + _recDir, Color.Red);
            }
            else SetState("录制启动失败", Color.Red);
        }

        private bool StartRecordingFile()
        {
            string name = "远程会话_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                          + (_recPart > 0 ? $"_part{_recPart + 1}" : "") + ".mp4";
            string path = System.IO.Path.Combine(_recDir, name);
            _recW = _hostW; _recH = _hostH;
            _recSw.Restart();
            int rc = RcNative.rc_record_start(path, _recW, _recH, 30,
                                              _recExtra ?? Array.Empty<byte>(),
                                              _recExtra?.Length ?? 0);
            return rc == 0;
        }

        // Close the current part and immediately begin the next one (used when
        // the stream resolution changes mid-recording).
        private void RecordRollover()
        {
            try { RcNative.rc_record_stop(); } catch { }
            _recPart++;
            if (!StartRecordingFile())
            {
                _recording = false;
                try { BeginInvoke((MethodInvoker)(() => _recBtn.Text = "录制")); } catch { }
                SetState("分辨率变化后录制重启失败，已停止录制", Color.Red);
            }
        }

        private void StopRecording()
        {
            _recording = false;
            try { RcNative.rc_record_stop(); } catch { }
            _recBtn.Text = "录制";
            SetState("录制已保存到 " + _recDir, Color.Green);
        }

        // ---- audio playback -------------------------------------------------
        private void ToggleAudio()
        {
            if (_audioWant)
            {
                _audioWant = false;
                StopAudioPlayback();
                _audioBtn.Text = "🔈声音";
                SetState("已关闭远程声音", Color.Gray);
            }
            else
            {
                _audioWant = true;
                _audioBtn.Text = "🔊声音开";
                if (_audioCfgSeen && !_audioStarted) StartAudioPlayback();
                else SetState("已开启声音，等待对方共享…", Color.Green);
            }
        }

        // Phase 2 远程文件浏览器：打开/聚焦文件管理器窗体。
        private void OpenFileBrowser()
        {
            if (!_running) { SetState("未连接，无法打开文件管理器", Color.DarkOrange); return; }
            if (_fileBrowser == null || _fileBrowser.IsDisposed)
                _fileBrowser = new FileBrowserForm((t, p) => SendToHost(t, p));
            if (_fileBrowser.Visible) { _fileBrowser.BringToFront(); return; }
            _fileBrowser.Show(this);
            _fileBrowser.Navigate("");   // 打开即列出盘符
        }

        private void StartAudioPlayback()
        {
            if (_audioStarted) return;
            if (RcNative.rc_audio_play_start() == RcNative.RC_OK)
            {
                _audioStarted = true;
                SetState("远程声音已连接", Color.Green);
            }
            else SetState("音频播放初始化失败", Color.DarkOrange);
        }

        private void StopAudioPlayback()
        {
            if (!_audioStarted) return;
            try { RcNative.rc_audio_play_stop(); } catch { }
            _audioStarted = false;
        }

        private static bool Sleep(int ms, CancellationToken token)
        {
            try { return token.WaitHandle.WaitOne(ms); } catch { return true; }
        }

        // Read the single RESULT frame the relay sends immediately after JOIN.
        // Returns true on accept; false (with a reject reason) on reject.
        private static bool RelayHandshake(Transport t, out string reject, out bool forceUpgrade)
        {
            reject = "";
            forceUpgrade = false;
            if (!t.TryReceive(out var type, out var payload))
                return false;
            if (type != MessageType.Result)
                return true; // shouldn't happen, but don't block the session
            Codec.ParseResult(payload, out int code, out string text);
            if (code == 0) return true;
            reject = text;
            if (code == 2) forceUpgrade = true;
            return false;
        }

        private void SetState(string text, Color color)
        {
            _stateText = text; _stateColor = color;
            if (IsDisposed) return;
            try { BeginInvoke((MethodInvoker)(() => { _status.Text = text; _status.ForeColor = color; })); }
            catch { }
        }

        // Return the UI to the pre-connection idle state: Connect enabled, every
        // session button disabled, and the periodic timers stopped. Without this,
        // a rejected join (e.g. wrong password) left the UI stuck — Connect stayed
        // grayed out and Disconnect did nothing because the session was already
        // dead, so the user could never re-enter the password.
        private void SetIdleUI(string status, Color color)
        {
            try { _pingTimer.Stop(); } catch { }
            try { _clipTimer.Stop(); } catch { }
            if (IsDisposed) return;
            try
            {
                BeginInvoke((MethodInvoker)(() =>
                {
                    _connectBtn.Enabled = true; _disconnectBtn.Enabled = false;
                    _chatBtn.Enabled = _fileBtn.Enabled = _fsBtn.Enabled = _fullBtn.Enabled = _recBtn.Enabled = _audioBtn.Enabled = _specialBtn.Enabled = _statBtn.Enabled = false;
                    _status.Text = status; _status.ForeColor = color;
                }));
            }
            catch { }
        }

        // Host sent an intentional Bye — stop for good (no reconnect).
        private void FinishFromRemote()
        {
            _running = false;
            StopDecodeThread();   // 停止独立解码线程
            StopAudioPlayback(); _audioCfgSeen = false;
            if (_recording) { _recording = false; try { RcNative.rc_record_stop(); } catch { } }
            if (IsDisposed) return;
            try
            {
                BeginInvoke((MethodInvoker)(() =>
                {
                    _pingTimer.Stop(); _clipTimer.Stop();
                    if (_cursorInside) { try { Cursor.Show(); } catch { } _cursorInside = false; }
                    RcNative.rc_decoder_free();
                    _bmp?.Dispose(); _bmp = null; _screen.Image = null;
                    _connectBtn.Enabled = true; _disconnectBtn.Enabled = false;
                    _specialBtn.Enabled = _audioBtn.Enabled = _fsBtn.Enabled = _statBtn.Enabled = false;
                    _status.Text = "对方已断开"; _status.ForeColor = Color.Gray;
                }));
            }
            catch { }
        }

        private void Disconnect()
        {
            if (!_running && _transport == null) return;
            _running = false;
            _connStart = DateTime.MinValue; // Phase 1A：重置时长
            _viewOnly = false; // drop any view-only state from the previous session
            try { if (!IsDisposed) _viewOnlyLabel.Visible = false; } catch { }
            StopAudioPlayback(); _audioCfgSeen = false;
            if (_recording) { _recording = false; try { RcNative.rc_record_stop(); } catch { } try { _recBtn.Text = "录制"; } catch { } }
            try { _pingTimer.Stop(); } catch { }
            try { _clipTimer.Stop(); } catch { }
            try { _cts?.Cancel(); } catch { }
            try { _transport?.Send(MessageType.Bye, Array.Empty<byte>()); } catch { }
            try { _transport?.Dispose(); } catch { }
            _transport = null;
            StopDecodeThread();   // 停止独立解码线程
            CloseP2P(); _p2pEverConnected = false; _rttMs = -1;
            lock (_decLock) RcNative.rc_decoder_free();
            _bmp?.Dispose(); _bmp = null;
            if (_cursorInside) { try { Cursor.Show(); } catch { } _cursorInside = false; }
            try { if (!IsDisposed) _screen.Image = null; } catch { }
            _connectBtn.Enabled = true; _disconnectBtn.Enabled = false;
            _specialBtn.Enabled = _audioBtn.Enabled = _statBtn.Enabled = false;
            try { if (_keyLog != null && !_keyLog.IsDisposed) _keyLog.Clear(); } catch { }  // Phase 1D：清空按键流
            try { if (_fileBrowser != null && !_fileBrowser.IsDisposed) { _fileBrowser.Close(); } } catch { }
            _fileBrowser = null;
            _status.Text = "已断开"; _status.ForeColor = Color.Gray;
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
            try { SendToHost(MessageType.Chat, Codec.BuildChat(text)); } catch { }
        }

        // ---- file transfer --------------------------------------------------
        private void ViewerFileMenu()
        {
            if (_viewOnly) { MessageBox.Show(this, "对方已开启仅观看模式，无法发送文件。", "文件传输", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            using var m = new Form { Text = "文件", Width = 260, Height = 140, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog };
            var send = new Button { Text = "发送文件给被控端", Dock = DockStyle.Top, Height = 36 };
            var recv = new Label { Text = "接收：被控端发来文件时会自动提示", Dock = DockStyle.Bottom, Height = 36 };
            send.Click += (s, e) => { m.Close(); SendFileToHost(); };
            m.Controls.Add(send); m.Controls.Add(recv);
            m.ShowDialog();
        }

        // Ensure the transfer window exists AND is visible before Add() (which
        // needs a live window handle).
        private void ShowFtForm()
        {
            if (_ftForm == null || _ftForm.IsDisposed) _ftForm = new FileTransferForm();
            try { if (!_ftForm.Visible) _ftForm.Show(); _ftForm.BringToFront(); } catch { }
        }

        private void NotifyFileSaved(string path)
        {
            try
            {
                var r = MessageBox.Show(this, "文件已保存到：\n" + path + "\n\n是否打开所在文件夹",
                    "文件接收完成", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (r == DialogResult.Yes)
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch { }
        }

        private void SendFileToHost()
        {
            using var d = new OpenFileDialog { Title = "选择要发送的文件（可多选）", Multiselect = true };
            if (d.ShowDialog() != DialogResult.OK) return;
            SendFilesToHost(d.FileNames);
        }

        // Send one or more files sequentially (used by the dialog AND drag-drop).
        // Each file waits for the host's accept before streaming, then the next
        // file starts — keeping the accept dialogs from overlapping.
        public void SendFilesToHost(string[] files)
        {
            if (!_running) { SetState("未连接，无法发送文件", Color.DarkOrange); return; }
            if (files == null || files.Length == 0) return;
            var list = new System.Collections.Generic.List<string>();
            foreach (var f in files) if (System.IO.File.Exists(f)) list.Add(f);
            if (list.Count == 0) { SetState("拖入的项目不是文件（暂不支持文件夹）", Color.DarkOrange); return; }
            Task.Run(() =>
            {
                int n = 0;
                foreach (var file in list)
                {
                    if (!_running) break;
                    n++;
                    SetState($"发送文件 {n}/{list.Count}: {System.IO.Path.GetFileName(file)}", Color.Green);
                    SendOneFileToHost(file);
                }
                SetState($"文件发送完成（{list.Count} 个）", Color.Green);
            });
        }

        // Blocking single-file send (call from a background thread).
        private void SendOneFileToHost(string file)
        {
            var t = _ft.BeginSend(file, -1);
            try { BeginInvoke((MethodInvoker)(() => { ShowFtForm(); _ftForm.Add(t); })); } catch { }
            try { SendToHost(MessageType.FOpen, Codec.BuildFOpen(t.Id, 0, t.Name, t.Size)); }
            catch (Exception ex) { t.Canceled = true; _ft.EndOutgoing(t); SetState("发送请求失败: " + ex.Message, Color.Red); return; }
            // Wait for the host to accept. The host must walk through TWO modal
            // dialogs (accept, then choose a save location), which easily takes
            // longer than a fixed short timeout. If we removed the transfer on a
            // timeout, the late FResp could no longer be matched and the sender
            // would be stuck at 0% forever -- this is exactly the reported bug.
            // So we wait until the host actually accepts, cancels, or the
            // connection drops. A generous safety timeout (3 min) cancels and
            // notifies the host so neither side hangs if FResp is truly lost.
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
                // Not accepted: either the host denied (t.Canceled) or never
                // responded in time. Mark as canceled so the window reports
                // failure, and notify the host (unless it already did).
                bool denied = t.Canceled;
                if (!denied)
                {
                    t.Canceled = true;
                    try { SendToHost(MessageType.FCancel, Codec.BuildId(t.Id)); } catch { }
                }
                _ft.EndOutgoing(t);
                SetState(denied ? "对方拒绝接收: " + t.Name : "对方长时间未响应，已取消: " + t.Name, Color.DarkOrange);
                return;
            }
            byte[] chunk;
            while ((chunk = _ft.SendNext(t, out int id)) != null)
            {
                if (_running && !t.Canceled) { try { SendToHost(MessageType.FData, Codec.BuildFData(id, chunk)); } catch { } }
                else break;
                if (Sleep(0, _cts.Token)) break;
            }
            if (!t.Canceled) { try { SendToHost(MessageType.FEnd, Codec.BuildId(t.Id)); } catch { } }
            _ft.EndOutgoing(t);
        }

        private void OnIncomingFile(int fid, string name, long size)
        {
            using var ask = new Form { Text = "收到文件", Width = 360, Height = 160, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog };
            var lb = new Label { Text = $"被控端想发送：\n{name}  ({size / 1024} KB)", Dock = DockStyle.Top, Height = 60 };
            var acc = new Button { Text = "接收", Dock = DockStyle.Left, Width = 80 };
            var den = new Button { Text = "拒绝", Dock = DockStyle.Right, Width = 80 };
            ask.Controls.Add(lb); ask.Controls.Add(acc); ask.Controls.Add(den);
            int choice = 0;
            acc.Click += (s, e) => { choice = 1; ask.Close(); };
            den.Click += (s, e) => { choice = 2; ask.Close(); };
            ask.ShowDialog();
            if (choice != 1) { try { SendToHost(MessageType.FCancel, Codec.BuildId(fid)); } catch { } return; }
            string dir = Common.PickSaveDir("选择保存目录") ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            // Adopt the sender's (host's) transfer id so FData/FEnd resolve here.
            var t = _ft.ReceiveOpen(fid, -1, 0, name, size, dir);
            ShowFtForm(); _ftForm.Add(t);
            _ft.Accept(t);
            try { SendToHost(MessageType.FResp, Codec.BuildFResp(t.Id, 1)); } catch { }
            SetState("正在接收文件，保存到本地: " + t.Path, Color.Green);
        }

        private void OnSendAccepted(int id, bool accept)
        {
            var t = _ft.Find(id);
            if (t == null) return;
            if (accept) t.Accepted = true;
            else { _ft.CancelOutgoing(t); SetState("被控端拒绝了文件", Color.DarkOrange); }
        }

        // ---- monitor switching --------------------------------------------
        private void PickMonitor()
        {
            if (_hostMonitors.Count <= 1) { SetState("对方只有一块显示器", Color.Gray); return; }
            using var m = new Form { Text = "切换显示器", Width = 240, Height = 60 + _hostMonitors.Count * 30, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog };
            var flp = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown };
            for (int i = 0; i < _hostMonitors.Count; i++)
            {
                int idx = i;
                var b = new Button { Text = _hostMonitors[i], Width = 200 };
                b.Click += (s, e) => { try { SendToHost(MessageType.Cmd, Codec.BuildCtrl(idx)); } catch { } m.Close(); };
                flp.Controls.Add(b);
            }
            m.Controls.Add(flp);
            m.ShowDialog();
        }

        private void ParseMonitorList(byte[] p)
        {
            string text = Codec.ParseMonitorList(p);
            var lines = text.Split('\n');
            _hostMonitors = new System.Collections.Generic.List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                var ln = lines[i].Trim();
                if (ln.Length == 0) continue;
                _hostMonitors.Add("显示器 " + ln);
            }
            BeginInvoke((MethodInvoker)(() =>
            {
                _monBtn.Enabled = _running && _hostMonitors.Count > 1;
            }));
        }

        // ---- fullscreen -----------------------------------------------------
        private void ToggleFullscreen()
        {
            if (FormBorderStyle == FormBorderStyle.None)
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                WindowState = FormWindowState.Normal;
                _fullBtn.Text = "全屏";
            }
            else
            {
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Maximized;
                _fullBtn.Text = "退出全屏";
            }
        }

        private sealed class BufferedPictureBox : PictureBox
        {
            public BufferedPictureBox()
            {
                DoubleBuffered = true;
                // PictureBox is not focusable by default; make it selectable so
                // it can hold keyboard focus while the operator controls the remote.
                SetStyle(ControlStyles.Selectable, true);
                TabStop = true;
            }
        }
    }
}
