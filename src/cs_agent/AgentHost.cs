// AgentHost.cs - Phase 12: headless host agent using HostForm internals.
// Hides all windows, auto-accepts permissions. Logs to AppData.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace RemoteControl
{
    internal sealed class AgentHost : IDisposable
    {
        private readonly AppOptions _opts;
        private Transport? _transport;
        private bool _running;
        private CancellationTokenSource? _cts;
        private readonly object _encLock = new();
        private int _encW, _encH;
        private byte[]? _encExtra;
        private bool _encInit;
        private readonly Dictionary<int, DateTime> _viewers = new();
        private bool _black;
        private bool _viewOnly;

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint es);
        private const uint ES_CONT = 0x80000000, ES_SYS = 0x00000001;

        public AgentHost(AppOptions opts) { _opts = opts; }

        public void Run()
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            try { SetThreadExecutionState(ES_CONT | ES_SYS); } catch { }
            try { RcNative.rc_input_set_bounds(0, 0, Screen.PrimaryScreen?.Bounds.Width ?? 1920, Screen.PrimaryScreen?.Bounds.Height ?? 1080); } catch { }
            RegisterAutostart();
            _running = true;
            // Phase 12 debug: log overlay status
            try
            {
                var path = Environment.ProcessPath ?? "";
                var json = Common.ReadOverlayConfig(path);
                Log($"Overlay: path={path}, json_len={(json?.Length ?? 0)}, room={_opts.Room ?? "null"}, token={AgentProgram.CloudToken ?? "null"}");
            }
            catch (Exception ex) { Log("Overlay read error: " + ex.Message); }
            int backoff = 800;

            while (_running && !token.IsCancellationRequested)
            {
                try
                {
                    Log($"Connecting {(string.IsNullOrEmpty(AgentProgram.ServerOverride) ? _opts.Server : AgentProgram.ServerOverride)}:{AgentProgram.AgentPort}");
                    var t = Transport.Connect(
                        string.IsNullOrEmpty(AgentProgram.ServerOverride) ? (_opts.Server ?? "127.0.0.1") : AgentProgram.ServerOverride,
                        AgentProgram.AgentPort);
                    var tok = AgentProgram.CloudToken;
                    if (!string.IsNullOrEmpty(tok))
                    {
                        Log("Sending v2 JOIN with AGENT:1");
                        var si = GatherSystemInfo();
                        t.SendJoinV2(tok, "host", version: "1.0.0", isAgent: true,
                            computerName: Environment.MachineName, lanIp: Common.GetLanIP(),
                            osVer: si.osVer, cpuInfo: si.cpuInfo, memInfo: si.memInfo);
                    }
                    else if (!string.IsNullOrEmpty(_opts.Room))
                    {
                        t.SendJoin(_opts.Room, "host", Common.HashPassword(_opts.Password));
                    }
                    else { Log("No token/room"); break; }

                    if (!t.TryReceive(out var ht, out var hp) || ht != MessageType.Result)
                    { t.Dispose(); backoff = Bump(backoff, token); continue; }
                    Codec.ParseResult(hp, out int code, out string text);
                    if (code != 0) { t.Dispose(); Log($"Rejected: {text}"); if (code == 2) { _running = false; break; } backoff = Bump(backoff, token); continue; }

                    _transport = t; backoff = 800;
                    Log("Connected"); SyncHeader();
                    StartCapture(token);
                    // Phase 12: send empty keepalive every 30s to prevent idle disconnect
                    var kaThread = new Thread(() => {
                        while (_running && !token.IsCancellationRequested && _transport != null) {
                            try { Thread.Sleep(30000); _transport?.Send(MessageType.KeepAlive, Array.Empty<byte>()); } catch { break; }
                        }
                    }) { IsBackground = true, Name = "KA" };
                    kaThread.Start();
                    RecvLoop(token);
                    Log("RecvLoop exit");
                    try { _transport?.Dispose(); } catch { } _transport = null;
                    if (!_running || token.IsCancellationRequested) break;
                    Log("Reconnecting..."); Sleep(token, 1000);
                }
                catch (Exception ex) { Log($"Error: {ex.Message}"); backoff = Bump(backoff, token); }
            }
            try { RcNative.rc_capture_free(); } catch { }
            try { RcNative.rc_encoder_free(); } catch { }
            Log("Stopped");
        }

        // ---- Capture ----
        private Thread? _capThread;
        private void StartCapture(CancellationToken token)
        {
            _capThread = new Thread(() =>
            {
                int fps = 20; var sw = new Stopwatch();
                while (_running && !token.IsCancellationRequested && _transport != null)
                {
                    sw.Restart();
                    try
                    {
                        int r = RcNative.rc_capture_frame(out var rgba, out int w, out int h, out _);
                        if (r == 0 && rgba != IntPtr.Zero && w > 0 && h > 0)
                        {
                            lock (_encLock)
                            {
                                if (!_encInit || _encW != w || _encH != h)
                                {
                                    if (_encInit) { try { RcNative.rc_encoder_free(); } catch { } _encInit = false; }
                                    int er = RcNative.rc_encoder_init(w, h, fps, 2000, out var ex, out var es);
                                    if (er == 0 && es > 0) { _encExtra = new byte[es]; Marshal.Copy(ex, _encExtra, 0, es); _encInit = true; _encW = w; _encH = h; }
                                    if (_transport != null && _encExtra != null) try { _transport.Send(MessageType.VideoConfig, Codec.BuildVideoConfig(w, h, fps, _encExtra)); } catch { }
                                }
                                if (_encInit)
                                {
                                    int er = RcNative.rc_encoder_encode(rgba, w, h, out var nal, out int ns, out int key);
                                    if (er == 0 && nal != IntPtr.Zero && ns > 0)
                                    {
                                        var buf = new byte[ns]; Marshal.Copy(nal, buf, 0, ns);
                                        RcNative.rc_free(nal);
                                        try { _transport?.Send(MessageType.VideoFrame, Codec.BuildVideoFrame((byte)key, buf)); } catch { }
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                    int el = (int)sw.ElapsedMilliseconds;
                    if (el < 1000 / fps) Thread.Sleep(Math.Max(1, 1000 / fps - el));
                }
            }) { IsBackground = true, Name = "AgentCapture" };
            _capThread.Start();
        }

        private void SyncHeader()
        {
            lock (_encLock) { if (_encInit && _encExtra != null && _transport != null) try { _transport.Send(MessageType.VideoConfig, Codec.BuildVideoConfig(_encW, _encH, 20, _encExtra)); } catch { } }
        }

        // ---- Recv ----
        private void RecvLoop(CancellationToken token)
        {
            while (_running && !token.IsCancellationRequested && _transport != null)
            {
                if (!_transport.TryReceive(out var type, out var payload)) return;
                switch (type)
                {
                    case MessageType.VJoin:
                        int id = Codec.ParseViewerId(payload);
                        if (id > 0) { _viewers[id] = DateTime.UtcNow; Log($"Viewer {id} joined ({_viewers.Count})"); SyncHeader(); }
                        break;
                    case MessageType.VLeave:
                        id = Codec.ParseViewerId(payload);
                        _viewers.Remove(id); Log($"Viewer {id} left ({_viewers.Count})");
                        break;
                    case MessageType.FromViewer:
                        if (Codec.ParseFromViewer(payload, out int vid, out var it, out var inner))
                            HandleMsg(vid, it, inner);
                        break;
                    case MessageType.Ctrl:
                        DoCtrl(payload); break;
                    case MessageType.KeepAlive:
                        if (payload is { Length: > 0 }) try { _transport.Send(MessageType.KeepAlive, payload); } catch { }
                        break;
                    case MessageType.NoVideo:
                        _viewOnly = true; Log("View-only mode"); break;
                }
            }
        }

        private void HandleMsg(int vid, MessageType t, byte[] p)
        {
            try
            {
                switch (t)
                {
                    case MessageType.InputEvent: ApplyInput(p); break;
                    case MessageType.Ctrl: DoCtrl(p); break;
                    case MessageType.Clipboard:
                        var txt = System.Text.Encoding.UTF8.GetString(p);
                        if (!string.IsNullOrEmpty(txt)) try { Clipboard.SetText(txt); } catch { }
                        break;
                }
            }
            catch { }
        }

        // ---- Input ----
        private void ApplyInput(byte[] p)
        {
            if (p == null || p.Length < 2) return;
            var kind = (InputKind)p[0];
            using var ms = new MemoryStream(p, 1, p.Length - 1);
            using var br = new BinaryReader(ms);
            try
            {
                switch (kind)
                {
                    case InputKind.Move:
                        if (ms.Length < 8) return;
                        RcNative.rc_input_mouse_move(br.ReadInt32(), br.ReadInt32());
                        break;
                    case InputKind.Button:
                        if (ms.Length < 2) return;
                        RcNative.rc_input_mouse_button(br.ReadByte(), br.ReadByte());
                        break;
                    case InputKind.Wheel:
                        if (ms.Length < 4) return;
                        RcNative.rc_input_wheel(br.ReadInt32());
                        break;
                    case InputKind.Key:
                        if (ms.Length < 5) return;
                        RcNative.rc_input_key(br.ReadUInt32(), br.ReadByte());
                        break;
                }
            }
            catch { }
        }

        private void DoCtrl(byte[] p)
        {
            if (p == null || p.Length < 2) return;
            int act = p[0];
            try
            {
                switch (act)
                {
                    case 0: RcNative.rc_system_lock(); break;
                    case 2: RcNative.rc_system_logoff(); break;
                    case 3: RcNative.rc_system_reboot(); break;
                    case 4: RcNative.rc_system_shutdown(); break;
                    case 11: RcNative.rc_system_sleep(); break;
                    case 12: RcNative.rc_system_monitor_off(); break;
                }
            }
            catch { }
        }

        // ---- Helpers ----
        private static string? _ExtractOverlay(string key)
        {
            try
            {
                var json = Common.ReadOverlayConfig(Environment.ProcessPath ?? "");
                if (string.IsNullOrEmpty(json)) return null;
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty(key, out var v) ? v.GetString() : null;
            }
            catch { return null; }
        }
        private int Bump(int backoff, CancellationToken token) { if (!Sleep(token, backoff)) backoff = Math.Min(backoff * 2, 8000); return backoff; }
        private static bool Sleep(CancellationToken t, int ms) { try { return t.WaitHandle.WaitOne(ms); } catch { return true; } }
        private static void RegisterAutostart()
        {
            try
            {
                var exePath = Environment.ProcessPath ?? "";
                if (string.IsNullOrEmpty(exePath)) return;
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true)
                    ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run");
                k.SetValue("RemoteControlAgent", $"\"{exePath}\"");
            }
            catch { }
        }
        internal static void Log(string m)
        {
            try { var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteControl"); Directory.CreateDirectory(d); File.AppendAllText(Path.Combine(d, "agent.log"), $"{DateTime.Now:HH:mm:ss} {m}\n"); }
            catch { }
        }
        public void Dispose() { _running = false; try { _cts?.Cancel(); } catch { } try { _transport?.Dispose(); } catch { } }

        // ---- 系统信息采集（借鉴 Iceberg 上线回传系统摘要）----
        internal struct SysInfo { public string osVer, cpuInfo, memInfo; }

        private static SysInfo GatherSystemInfo()
        {
            var s = new SysInfo();
            try { s.osVer = Environment.OSVersion.VersionString; } catch { s.osVer = "Unknown"; }
            try { s.osVer = RuntimeInformation.OSDescription; } catch { }
            try
            {
                using var se = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (se != null)
                    s.cpuInfo = (se.GetValue("ProcessorNameString") as string ?? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "").Trim();
            }
            catch { s.cpuInfo = Environment.ProcessorCount + " CPU cores"; }
            try
            {
                // 用 P/Invoke 获取物理内存总量（不依赖 WMI/NuGet）
                long total = 0;
                try
                {
                    var mi = new MEMORYSTATUSEX();
                    mi.dwLength = (uint)Marshal.SizeOf(mi);
                    if (GlobalMemoryStatusEx(ref mi))
                        total = (long)mi.ullTotalPhys;
                }
                catch { }
                if (total <= 0) total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                s.memInfo = FormatSize(total);
            }
            catch { s.memInfo = "? GB"; }
            return s;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 40) return $"{(double)bytes / (1L << 40):F1} TB";
            if (bytes >= 1L << 30) return $"{(double)bytes / (1L << 30):F1} GB";
            if (bytes >= 1L << 20) return $"{(double)bytes / (1L << 20):F1} MB";
            return $"{bytes / 1024} KB";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }
}
