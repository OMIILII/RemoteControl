// Protocol.cs - Binary framing shared by host and viewer.
//
// Every application message is:  [byte type][int32 length LE][payload]
// The signaling relay is a transparent TCP channel and only parses
// the very first text line ("JOIN <room> <role>\n") to pair the peers.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace RemoteControl
{
    public enum MessageType : byte
    {
        Hello = 1,
        VideoConfig = 2,
        VideoFrame = 3,
        InputEvent = 4,
        Bye = 5,
        Ping = 6,
        Clipboard = 7,

        // Relay-level control (host <-> relay only). The relay wraps each
        // viewer message in FromViewer(id) so the host knows who sent it,
        // and the host can reply to / kick a specific viewer.
        VJoin = 32,       // payload: int32 viewer id       (relay -> host)
        VLeave = 33,      // payload: int32 viewer id       (relay -> host)
        FromViewer = 34,  // payload: int32 id + inner frame (relay -> host)
        ToViewer = 35,    // payload: int32 id + inner frame (host -> relay)
        Kick = 36,        // payload: int32 viewer id       (host -> relay)
        Result = 40,      // payload: int32 code + text     (relay -> peer, accept/reject)

        // Session features (relayed between peers like any other message).
        Chat = 50,        // payload: text                     (broadcast)
        Ctrl = 51,        // payload: int32 command            (viewer -> host)
        Cmd = 52,        // payload: int32 display index      (viewer -> host)
        ClipImage = 53,   // payload: PNG bytes                (bidirectional clipboard image)
        AudioConfig = 54, // payload: int32 sampleRate + int32 channels (host -> viewers)
        AudioFrame = 55,  // payload: Opus packet bytes        (host -> viewers)
        FOpen = 60,       // payload: int32 dir + int32 nameLen + name + int64 size  (request)
        FData = 61,       // payload: int32 id + int32 len + bytes (chunk)
        FEnd = 62,        // payload: int32 id                 (success)
        FCancel = 63,     // payload: int32 id                 (abort)
        FResp = 64,       // payload: int32 id + int32 accept   (accept/deny a request)
        MonitorList = 65,  // payload: int32 len + text          (host -> viewers)
        ViewOnly = 66,    // payload: byte(0/1) on/off           (host -> viewers)

        // P2P / TCP-hole-punch signaling (relayed in the clear; the relay
        // forwards a peer's *public* (ip, port) so the two endpoints can try a
        // direct TCP connection and stop bouncing through the relay).
        PeerAddr = 71,    // payload: PeerAddr (relay -> peer)    NOT encrypted
        LinkStat = 73,    // payload: rtt/jitter/decFps/bw        (viewer -> host)

        // Device-cloud "host confirm" mode (relay <-> host, plaintext, NOT encrypted).
        CtrlReq = 80,     // payload: utf8 "reqId|requesterName"  (relay -> host)
        CtrlAck = 81,     // payload: utf8 reqId                  (host -> relay, allow)
        CtrlNak = 82,     // payload: utf8 reqId                  (host -> relay, deny)

        // Server-pushed notice (relay <-> peer, plaintext, NOT encrypted).
        // Used to tell outdated/legacy clients their version is too old.
        // Old clients ignore the unknown frame type, so this stays backward
        // compatible. payload: utf8 text.
        Notice = 83,

        // Server->client relay-level keepalive (plaintext, NOT encrypted,
        // empty payload). The relay sends it every PING_INTERVAL seconds so
        // idle TCP links don't get silently dropped by tunnel/NAT firewalls
        // (which would otherwise cause repeated "连接断开，重连中…" loops).
        // Clients ignore this frame type; it carries no data and needs no reply.
        KeepAlive = 90,

        // Server->client relay-level "admin message" (plaintext, NOT encrypted,
        // utf-8 text payload). The backend management sends chat into a room
        // attributed to 【管理员】. It must be a separate plaintext frame because
        // normal chat is end-to-end encrypted and the server cannot forge its
        // ciphertext. Old clients ignore this frame type; new clients render it
        // in the chat window as from 【管理员】.
        AdminMsg = 84,

        // Client-reported public (ip, port) candidates discovered via STUN, used
        // for P2P TCP hole punching when both peers sit behind NAT/frp. Relayed
        // in the clear (NOT encrypted) so the relay can read and forward them.
        PubCand = 85,

        // Remote terminal (hidden shell on the controlled machine; the controlled
        // person never sees it). Carried as inner frames inside FromViewer/ToViewer
        // like any other session feature, so E2E encryption + relay forwarding are
        // reused for free.
        TerminalOpen  = 86,  // [int32 cols][int32 rows][byte shell(0=cmd,1=pwsh)]  (viewer -> host)
        TerminalData  = 87,  // raw stdin bytes                                            (viewer -> host)
        TerminalOut   = 88,  // [int32 stream(0=out,1=err)][bytes]                         (host -> viewer)
        TerminalClose = 89,  // [int32 code]                                                (either -> other)
        TerminalResize = 91, // [int32 cols][int32 rows]                                    (viewer -> host)
        NoVideo = 93,        // 轻量会话：控制端声明不需要视频（被控端可跳过编码/发送）  (viewer -> host)

        // Phase 1B 实时画质协商：viewer 把想要的缩放/帧率/码率档位发给 host，
        // host 据此重建编码器（payload [byte resScale][byte fps][byte quality][byte rsv]）。
        ViewerPref = 94,
        // Phase 1D 键盘监视：host 通过底层键盘钩子捕获本机按键并广播给所有 viewer，
        // viewer 在右侧面板实时显示（payload [int32 vk][byte down]）。
        KeyEvent   = 95,

        // Phase 2 远程文件浏览器：viewer 浏览/管理 host 文件系统。
        // 与聊天发文件（FOpen/FData/...）完全独立，使用独立消息类型与传输 id 空间，
        // 避免与"房主↔控制方互发文件"功能互相干扰。
        FsList      = 100,  // [int32 pathLen][path]                       (v -> h) 列目录（空=盘符根）
        FsListResp  = 101,  // [int32 pathLen][path][int32 err][int32 n][条目] (h -> v)
        FsGet       = 102,  // [int32 pathLen][path]                       (v -> h) 请求下载
        FsGetReady  = 103,  // [int32 id][int32 code][int64 size][int32 nameLen][name] (h -> v)
        FsChunk     = 104,  // [int32 id][int32 len][bytes]                (双向) 复用 FData 帧格式
        FsGetEnd    = 105,  // [int32 id]                                  (h -> v) 下载完成
        FsGetErr    = 106,  // [int32 id][int32 code][int32 msgLen][msg]   (h -> v) 下载/上传错误
        FsCancel    = 107,  // [int32 id]                                  (双向) 取消传输
        FsPut       = 108,  // [int32 pathLen][path][int64 size]           (v -> h) 请求上传
        FsPutReady  = 109,  // [int32 id][int32 code][int32 msgLen][msg]   (h -> v) 上传接受
        FsPutEnd    = 110,  // [int32 id]                                  (v -> h) 上传结束
        FsDelete    = 111,  // [int32 pathLen][path]                       (v -> h) 删除
        FsDeleteResp= 112,  // [int32 code][int32 msgLen][msg]             (h -> v)
        FsRename    = 113,  // [int32 oldLen][old][int32 newLen][new]      (v -> h) 重命名
        FsRenameResp= 114,  // [int32 code][int32 msgLen][msg]             (h -> v)
        FsMkdir     = 115,  // [int32 pathLen][path]                       (v -> h) 新建文件夹
        FsMkdirResp = 116,  // [int32 code][int32 msgLen][msg]             (h -> v)
        FsPutAck    = 117,  // [int32 id][int32 code][int32 msgLen][msg]   (h -> v) 上传落盘确认

        // Phase 3 设备卡片缩略图墙：控制端（房主 MainForm）对每个在线设备建立
        // 一条轻量后台连接，定时请求一张本机屏幕快照；被控端用 PNG（绕开单例 H264
        // 编码器）回传，控制端在设备卡片上渲染。帧极小、频率低（~2-3s 一张）。
        ThumbReq   = 120,  // [int32 maxW]                                 (v -> h) 请求快照（期望最大宽度）
        ThumbFrame  = 121,  // [int32 w][int32 h][int32 pngLen][png]        (h -> v) 快照（PNG 字节）

        // Phase 4 动作编排 + 批量/定时执行：控制端把"一个动作"推给被控端执行，
        // 被控端执行后回传结果（含 stdout / 退出码 / 状态文本）。动作连接是轻量无窗体
        // 会话（同 ThumbnailClient 思路，发 NoVideo 声明不抓屏），命令字小、频率极低。
        ActRun    = 130,  // [int32 actionId][byte kind][byte silent][int32 payloadLen][payload]  (v -> h)
        ActResult = 131,  // [int32 actionId][int32 code][int32 outLen][out utf8]                  (h -> v)

        // Phase 5 会话内标注（箭头/文字，对方可见）：控制端在对方屏幕上画标注，
        // 被控端用全屏透明覆盖层显示（绕开视频管线，独立通道）。坐标用归一化(0~1)，
        // 跨不同分辨率自动对齐。kind: 0=箭头 1=文字 2=清除全部。
        AnnoFrame = 140,  // [byte kind][...]   (v -> h)
        // Phase 7A Keep-Alive：已存在 Ping=6 做 RTT 测量；Pong 是纯响应，无 payload。
        Pong     = 141,  // (v <-> h) 响应 Ping
    }

    /// <summary>Phase 5 会话内标注的单条标注。</summary>
    public enum AnnoKind : byte
    {
        Arrow = 0,
        Text  = 1,
        Clear = 2,
    }

    /// <summary>Phase 5 标注数据模型（归一化坐标 0~1）。</summary>
    public class Anno
    {
        public AnnoKind Kind = AnnoKind.Arrow;
        public float X1, Y1, X2, Y2;     // 箭头两端 / 文字锚点（文字只用 X1,Y1）
        public int ColorArgb = unchecked((int)0xFFFF0000); // 默认红色
        public string Text = "";
    }

    /// <summary>Phase 2 文件浏览器：单条目录条目。</summary>
    public class FsEntry
    {
        public bool IsDir;
        public long Size;    // 字节；目录为 0
        public long Mtime;   // Windows FILETIME（100ns ticks），目录为 0
        public string Name = "";
    }

    public enum InputKind : byte
    {
        Move = 0,
        Button = 1,
        Wheel = 2,
        Key = 3,
    }

    /// <summary>Length-prefixed, little-endian binary channel over a TcpClient.</summary>
    public sealed class Transport : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly object _writeLock = new object();
        private Aead _aead;   // null => no encryption (plaintext)

        /// <summary>Master switch for the transparent zlib payload compression
        /// layer. Each compressed message carries a 1-byte flag (0x00 = raw,
        /// 0x01 = zlib) so the receiver can decompress per-message; this makes
        /// the feature self-describing and safe even if the two peers disagree
        /// on the setting. Relay-envelope types are never compressed.</summary>
        public static bool CompressionEnabled = true;
        private const int CompressThreshold = 48; // don't bother below this

        public Transport(TcpClient client)
        {
            _client = client;
            _client.NoDelay = true;
            _stream = _client.GetStream();
        }

        /// <summary>Attach (or clear) the E2E key. Must be set before sending
        /// content frames so both sides agree on encryption.</summary>
        public void SetCrypto(Aead aead) => _aead = aead;
        public bool Encrypted => _aead != null;
        /// <summary>Manually encrypt a payload (used for relay-wrapped inner
        /// frames the auto path can't see). Assumes Encrypted == true.</summary>
        public byte[] EncryptPayload(byte[] p) => _aead.Encrypt(p);
        /// <summary>Manually decrypt; returns null on auth failure.</summary>
        public byte[] DecryptPayload(byte[] p) => _aead.Decrypt(p);

        public static Transport Connect(string host, int port)
        {
            var c = new TcpClient();
            c.Connect(host, port);
            return new Transport(c);
        }

        /// <summary>Send the JOIN line that the relay server reads to pair peers.
        /// hash is hex(sha256(password)) — the host sets it; viewers must match.</summary>
        public void SendJoin(string room, string role, string hash = "", string version = "", string displayName = "")
        {
            // The relay parses "JOIN <room> <role> <hash> [version] [name]" and
            // text.split() collapses whitespace, so a blank room would be misread
            // as the role and rejected. Fall back to a non-empty sentinel so an
            // empty room box still pairs (host + viewer both resolve to the same
            // one). The optional version token is appended for the server to flag
            // outdated clients; old servers simply ignore the extra token. The
            // optional displayName is base64 and appended last so the relay can
            // surface the viewer's identity to the host (assist room member list).
            var r = string.IsNullOrWhiteSpace(room) ? "default" : room.Trim();
            var ver = string.IsNullOrWhiteSpace(version) ? "" : version.Trim();
            var sb = new System.Text.StringBuilder();
            sb.Append($"JOIN {r} {role} {hash} {ver}");
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(displayName));
                sb.Append(" NAME:").Append(b64);
            }
            sb.Append('\n');
            var line = Encoding.ASCII.GetBytes(sb.ToString());
            lock (_writeLock)
            {
                _stream.Write(line, 0, line.Length);
            }
        }

        /// <summary>Device-cloud (v2) join. The relay authenticates the device
        /// token against its DB and derives the room itself:
        ///   host  : "JOIN v2 &lt;device_token&gt; host [version]"
        ///   viewer: "JOIN v2 &lt;device_token&gt; viewer &lt;target_device_id&gt; [version]"
        /// The optional version token lets the server notify outdated clients.
        /// Old servers ignore the extra token.</summary>
        public void SendJoinV2(string deviceToken, string role, string target = "", string version = "",
                                 string displayName = "", string computerName = "", string lanIp = "", bool isAgent = false,
                                 string osVer = "", string cpuInfo = "", string memInfo = "")
        {
            var body = string.IsNullOrEmpty(target)
                ? $"JOIN v2 {deviceToken} {role}"
                : $"JOIN v2 {deviceToken} {role} {target}";
            var ver = string.IsNullOrWhiteSpace(version) ? "" : version.Trim();
            body += " " + ver;
            if (!string.IsNullOrWhiteSpace(displayName))
                body += " NAME:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(displayName));
            if (!string.IsNullOrWhiteSpace(computerName))
                body += " HOST:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(computerName));
            if (!string.IsNullOrWhiteSpace(lanIp))
                body += " LAN:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(lanIp));
            if (isAgent)
                body += " AGENT:1";
            if (!string.IsNullOrWhiteSpace(osVer))
                body += " OS:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(osVer));
            if (!string.IsNullOrWhiteSpace(cpuInfo))
                body += " CPU:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(cpuInfo));
            if (!string.IsNullOrWhiteSpace(memInfo))
                body += " MEM:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(memInfo));
            body += "\n";
            var b = Encoding.ASCII.GetBytes(body);
            lock (_writeLock)
            {
                _stream.Write(b, 0, b.Length);
            }
        }

        public void Send(MessageType type, byte[] payload)
        {
            if (payload == null) payload = Array.Empty<byte>();
            // Compress content payloads (flag-prefixed) BEFORE encryption so we
            // compress plaintext. Relay envelopes / handshake frames are skipped.
            if (CompressionEnabled && Codec.ShouldEncrypt(type) && payload.Length > 0)
                payload = WrapCompressed(payload);
            // Auto-encrypt content payloads sent directly (broadcast / viewer->relay).
            if (_aead != null && Codec.ShouldEncrypt(type) && payload.Length > 0)
                payload = _aead.Encrypt(payload);
            var len = payload.Length;
            lock (_writeLock)
            {
                _stream.WriteByte((byte)type);
                var lb = BitConverter.GetBytes(len);
                _stream.Write(lb, 0, 4);
                if (len > 0) _stream.Write(payload, 0, len);
                _stream.Flush();
            }
        }

        /// <summary>Blocking read of one message. Returns false on disconnect.</summary>
        public bool TryReceive(out MessageType type, out byte[] payload)
        {
            type = 0; payload = Array.Empty<byte>();
            try
            {
                int t = _stream.ReadByte();
                if (t < 0) return false;
                type = (MessageType)t;

                var lenBuf = new byte[4];
                if (!ReadExact(lenBuf, 4)) return false;
                int len = BitConverter.ToInt32(lenBuf, 0);
                if (len < 0 || len > 64 * 1024 * 1024) return false;

                if (len > 0)
                {
                    var buf = new byte[len];
                    if (!ReadExact(buf, len)) return false;
                    payload = buf;
                }
                // Auto-decrypt content payloads received directly. On auth
                // failure (wrong password / tamper) drop the payload so the
                // parser sees nothing rather than garbage.
                if (_aead != null && Codec.ShouldEncrypt(type) && payload.Length > 0)
                    payload = _aead.Decrypt(payload) ?? Array.Empty<byte>();
                // Transparently decompress (strip the 1-byte flag) for content
                // types. The flag is self-describing, so a mismatch in the
                // CompressionEnabled setting across peers is harmless.
                if (CompressionEnabled && Codec.ShouldEncrypt(type) && payload.Length > 0)
                    payload = UnwrapCompressed(payload);
                return true;
            }
            catch { return false; }
        }

        private bool ReadExact(byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = _stream.Read(buf, off, count - off);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        public void Dispose()
        {
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
        }

        // ---- zlib payload compression (flag-prefixed) ---------------------
        // Format of a compressed content payload: [byte flag][data]
        //   flag 0x00 = raw (data as-is)      flag 0x01 = zlib(data)
        // The flag lives INSIDE the payload (after encryption), so the relay,
        // which only sees [type][len][payload], stays completely unaware.

        /// <summary>Prepend a compression flag and, if it helps, zlib-compress.
        /// Returns [0x00][data] when compression is not beneficial, so the
        /// receiver can always just strip the flag.</summary>
        public static byte[] WrapCompressed(byte[] data)
        {
            var c = Compress(data);
            if (c == null)
            {
                var raw = new byte[data.Length + 1];
                raw[0] = 0;
                Buffer.BlockCopy(data, 0, raw, 1, data.Length);
                return raw;
            }
            var outp = new byte[c.Length + 1];
            outp[0] = 1;
            Buffer.BlockCopy(c, 0, outp, 1, c.Length);
            return outp;
        }

        /// <summary>Reverse of WrapCompressed: strip the flag and decompress
        /// when flagged. Used both on the receive path and for relayed inner
        /// frames (host side) after manual decryption.</summary>
        public static byte[] UnwrapCompressed(byte[] p)
        {
            if (p == null || p.Length == 0) return p;
            byte f = p[0];
            if (f == 1)
            {
                var d = Decompress(p, 1, p.Length - 1);
                return d ?? p;   // fall back to raw if decompression fails
            }
            if (f == 0)
            {
                var d = new byte[p.Length - 1];
                Buffer.BlockCopy(p, 1, d, 0, p.Length - 1);
                return d;
            }
            return p;
        }

        private static byte[] Compress(byte[] data)
        {
            if (data.Length < CompressThreshold) return null;
            try
            {
                using var ms = new MemoryStream();
                using (var z = new System.IO.Compression.ZLibStream(
                    ms, System.IO.Compression.CompressionLevel.Optimal, true))
                    z.Write(data, 0, data.Length);
                var outp = ms.ToArray();
                // Only keep it if it actually shrank; otherwise leave raw.
                return outp.Length < data.Length ? outp : null;
            }
            catch { return null; }
        }

        private static byte[] Decompress(byte[] data, int off, int len)
        {
            try
            {
                using var ms = new MemoryStream(data, off, len);
                using var z = new System.IO.Compression.ZLibStream(
                    ms, System.IO.Compression.CompressionMode.Decompress);
                using var outp = new MemoryStream();
                z.CopyTo(outp);
                return outp.ToArray();
            }
            catch { return null; }
        }
    }

    // ---- payload (de)serializers ----------------------------------------
    public static class Codec
    {
        /// <summary>Whether a message TYPE carries a peer-to-peer content
        /// payload that should be end-to-end encrypted. Relay envelope and
        /// handshake frames (VJoin/FromViewer/ToViewer/Result/Ping/Hello/Bye)
        /// stay in the clear so the relay can keep routing.</summary>
        public static bool ShouldEncrypt(MessageType t)
        {
            switch (t)
            {
                case MessageType.VideoConfig:
                case MessageType.VideoFrame:
                case MessageType.InputEvent:
                case MessageType.Clipboard:
                case MessageType.ClipImage:
                case MessageType.AudioConfig:
                case MessageType.AudioFrame:
                case MessageType.Chat:
                case MessageType.Ctrl:
                case MessageType.Cmd:
                case MessageType.TerminalOpen:
                case MessageType.TerminalData:
                case MessageType.TerminalOut:
                case MessageType.TerminalClose:
                case MessageType.TerminalResize:
                case MessageType.FOpen:
                case MessageType.FData:
                case MessageType.FEnd:
                case MessageType.FCancel:
                case MessageType.FResp:
            case MessageType.MonitorList:
            case MessageType.ViewOnly:
            case MessageType.LinkStat:
            case MessageType.ViewerPref:
            case MessageType.KeyEvent:
            case MessageType.FsList:
            case MessageType.FsListResp:
            case MessageType.FsGet:
            case MessageType.FsGetReady:
            case MessageType.FsChunk:
            case MessageType.FsGetEnd:
            case MessageType.FsGetErr:
            case MessageType.FsCancel:
            case MessageType.FsPut:
            case MessageType.FsPutReady:
            case MessageType.FsPutEnd:
            case MessageType.FsDelete:
            case MessageType.FsDeleteResp:
            case MessageType.FsRename:
            case MessageType.FsRenameResp:
            case MessageType.FsMkdir:
            case MessageType.FsMkdirResp:
            case MessageType.FsPutAck:
            case MessageType.ThumbReq:
            case MessageType.ThumbFrame:
            case MessageType.ActRun:
            case MessageType.ActResult:
            case MessageType.AnnoFrame:
            case MessageType.Ping:
            case MessageType.Pong:
                    return true;
                default:
                    return false;
            }
        }

        // ---- audio -------------------------------------------------------
        public static byte[] BuildAudioConfig(int sampleRate, int channels)
        {
            var buf = new byte[8];
            BitConverter.GetBytes(sampleRate).CopyTo(buf, 0);
            BitConverter.GetBytes(channels).CopyTo(buf, 4);
            return buf;
        }
        public static void ParseAudioConfig(byte[] p, out int sampleRate, out int channels)
        {
            sampleRate = (p != null && p.Length >= 4) ? BitConverter.ToInt32(p, 0) : 48000;
            channels   = (p != null && p.Length >= 8) ? BitConverter.ToInt32(p, 4) : 2;
        }
        // AudioFrame payload is the raw Opus packet (no extra framing needed).
        public static byte[] BuildAudioFrame(byte[] opus) => opus ?? Array.Empty<byte>();
        public static byte[] ParseAudioFrame(byte[] p) => p ?? Array.Empty<byte>();

        public static byte[] BuildVideoConfig(int w, int h, int fps, byte[] extra)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(w); bw.Write(h); bw.Write(fps);
            int es = extra?.Length ?? 0;
            bw.Write(es);
            if (es > 0) bw.Write(extra, 0, es);
            return ms.ToArray();
        }

        public static void ParseVideoConfig(byte[] p, out int w, out int h, out int fps, out byte[] extra)
        {
            using var ms = new MemoryStream(p);
            using var br = new BinaryReader(ms);
            w = br.ReadInt32(); h = br.ReadInt32(); fps = br.ReadInt32();
            int es = br.ReadInt32();
            extra = es > 0 ? br.ReadBytes(es) : Array.Empty<byte>();
        }

        public static byte[] BuildVideoFrame(byte key, byte[] nal)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(key);
            bw.Write(nal.Length);
            bw.Write(nal, 0, nal.Length);
            return ms.ToArray();
        }

        public static void ParseVideoFrame(byte[] p, out byte key, out byte[] nal)
        {
            using var ms = new MemoryStream(p);
            using var br = new BinaryReader(ms);
            key = br.ReadByte();
            int n = br.ReadInt32();
            nal = br.ReadBytes(n);
        }

        public static byte[] BuildInputMove(int x, int y)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)InputKind.Move); bw.Write(x); bw.Write(y);
            return ms.ToArray();
        }

        public static byte[] BuildInputButton(byte button, byte down)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)InputKind.Button); bw.Write(button); bw.Write(down);
            return ms.ToArray();
        }

        public static byte[] BuildInputWheel(int delta)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)InputKind.Wheel); bw.Write(delta);
            return ms.ToArray();
        }

        public static byte[] BuildInputKey(uint vk, byte down)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)InputKind.Key); bw.Write(vk); bw.Write(down);
            return ms.ToArray();
        }

        public static byte[] BuildClipboard(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text ?? "");
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(bytes.Length);
            bw.Write(bytes);
            return ms.ToArray();
        }

        public static string ParseClipboard(byte[] p)
        {
            if (p == null || p.Length < 4) return "";
            using var ms = new MemoryStream(p);
            using var br = new BinaryReader(ms);
            int n = br.ReadInt32();
            if (n <= 0 || n > 16 * 1024 * 1024) return "";
            return Encoding.UTF8.GetString(br.ReadBytes(n));
        }

        // ---- relay envelope helpers (multi-viewer) -----------------------

        /// <summary>int32 viewer id payload (VJoin / VLeave / Kick).</summary>
        public static byte[] BuildViewerId(int viewerId) => BitConverter.GetBytes(viewerId);

        public static int ParseViewerId(byte[] p) =>
            (p != null && p.Length >= 4) ? BitConverter.ToInt32(p, 0) : -1;

        /// <summary>从 VJoin 帧解析 viewer 显示名。
        /// 帧布局: [int32 vid][int32 nameLen][utf8 name]。name 可能为空（旧服务端不携带）。</summary>
        public static string ParseViewerName(byte[] p)
        {
            if (p == null || p.Length < 8) return "";
            int off = 4; // 跳过 vid
            int nlen = BitConverter.ToInt32(p, off);
            off += 4;
            if (nlen < 0 || nlen > 256 || off + nlen > p.Length) return "";
            return Encoding.UTF8.GetString(p, off, nlen);
        }

        /// <summary>ToViewer payload: [int32 id][inner frame: type+len+payload].</summary>
        public static byte[] BuildToViewer(int viewerId, MessageType t, byte[] inner)
        {
            inner ??= Array.Empty<byte>();
            var buf = new byte[4 + 5 + inner.Length];
            BitConverter.GetBytes(viewerId).CopyTo(buf, 0);
            buf[4] = (byte)t;
            BitConverter.GetBytes(inner.Length).CopyTo(buf, 5);
            inner.CopyTo(buf, 9);
            return buf;
        }

        /// <summary>Unwrap a FromViewer envelope. Returns false if malformed.</summary>
        public static bool ParseFromViewer(byte[] p, out int viewerId, out MessageType t, out byte[] inner)
        {
            viewerId = -1; t = 0; inner = Array.Empty<byte>();
            if (p == null || p.Length < 9) return false;
            viewerId = BitConverter.ToInt32(p, 0);
            t = (MessageType)p[4];
            int len = BitConverter.ToInt32(p, 5);
            if (len < 0 || len > 64 * 1024 * 1024 || 9 + len > p.Length) return false;
            if (len > 0)
            {
                inner = new byte[len];
                Array.Copy(p, 9, inner, 0, len);
            }
            return true;
        }

        // ---- room password (relay accept/reject) -------------------------
        public static byte[] BuildResult(int code, string text)
        {
            var t = Encoding.UTF8.GetBytes(text ?? "");
            var buf = new byte[4 + t.Length];
            BitConverter.GetBytes(code).CopyTo(buf, 0);
            t.CopyTo(buf, 4);
            return buf;
        }
        public static void ParseResult(byte[] p, out int code, out string text)
        {
            code = (p != null && p.Length >= 4) ? BitConverter.ToInt32(p, 0) : -1;
            text = (p != null && p.Length > 4) ? Encoding.UTF8.GetString(p, 4, p.Length - 4) : "";
        }

        // ---- chat / commands ---------------------------------------------
        public static byte[] BuildChat(string text)
        {
            var t = Encoding.UTF8.GetBytes(text ?? "");
            var buf = new byte[4 + t.Length];
            BitConverter.GetBytes(t.Length).CopyTo(buf, 0);
            t.CopyTo(buf, 4);
            return buf;
        }
        public static string ParseChat(byte[] p)
        {
            if (p == null || p.Length < 4) return "";
            int n = BitConverter.ToInt32(p, 0);
            if (n <= 0 || n > 16 * 1024 * 1024 || 4 + n > p.Length) return "";
            return Encoding.UTF8.GetString(p, 4, n);
        }

        public static byte[] BuildCtrl(int cmd) => BitConverter.GetBytes(cmd);
        public static int ParseCtrl(byte[] p) => (p != null && p.Length >= 4) ? BitConverter.ToInt32(p, 0) : -1;

        // ---- remote terminal ---------------------------------------------
        // TerminalOpen: [int32 cols][int32 rows][byte shell]
        public static byte[] BuildTerminalOpen(int cols, int rows, byte shell)
        {
            var buf = new byte[9];
            BitConverter.GetBytes(cols).CopyTo(buf, 0);
            BitConverter.GetBytes(rows).CopyTo(buf, 4);
            buf[8] = shell;
            return buf;
        }
        public static void ParseTerminalOpen(byte[] p, out int cols, out int rows, out byte shell)
        {
            cols = 80; rows = 24; shell = 0;
            if (p == null || p.Length < 9) return;
            cols = BitConverter.ToInt32(p, 0);
            rows = BitConverter.ToInt32(p, 4);
            shell = p[8];
        }
        // TerminalData: raw stdin bytes (no extra framing).
        public static byte[] BuildTerminalData(byte[] data) => data ?? Array.Empty<byte>();
        public static byte[] ParseTerminalData(byte[] p) => p ?? Array.Empty<byte>();
        // TerminalOut: [int32 stream(0=stdout,1=stderr)][bytes]
        public static byte[] BuildTerminalOut(int stream, byte[] data)
        {
            data ??= Array.Empty<byte>();
            var buf = new byte[4 + data.Length];
            BitConverter.GetBytes(stream).CopyTo(buf, 0);
            data.CopyTo(buf, 4);
            return buf;
        }
        public static void ParseTerminalOut(byte[] p, out int stream, out byte[] data)
        {
            stream = 0; data = Array.Empty<byte>();
            if (p == null || p.Length < 4) return;
            stream = BitConverter.ToInt32(p, 0);
            data = new byte[p.Length - 4];
            Array.Copy(p, 4, data, 0, data.Length);
        }
        // TerminalClose: [int32 code]
        public static byte[] BuildTerminalClose(int code) => BitConverter.GetBytes(code);
        public static int ParseTerminalClose(byte[] p) => (p != null && p.Length >= 4) ? BitConverter.ToInt32(p, 0) : 0;
        // TerminalResize: [int32 cols][int32 rows]
        public static byte[] BuildTerminalResize(int cols, int rows)
        {
            var buf = new byte[8];
            BitConverter.GetBytes(cols).CopyTo(buf, 0);
            BitConverter.GetBytes(rows).CopyTo(buf, 4);
            return buf;
        }
        public static void ParseTerminalResize(byte[] p, out int cols, out int rows)
        {
            cols = 80; rows = 24;
            if (p == null || p.Length < 8) return;
            cols = BitConverter.ToInt32(p, 0);
            rows = BitConverter.ToInt32(p, 4);
        }

        // ---- file transfer -----------------------------------------------
        // FOpen: [int32 id][int32 dir(0=toHost,1=toViewer)][int32 nameLen][name][int64 size]
        // The sender's transfer id is carried so BOTH ends key the transfer by
        // the same id -> FData/FEnd/FResp all match up.
        public static byte[] BuildFOpen(int id, int dir, string name, long size)
        {
            var nb = Encoding.UTF8.GetBytes(name ?? "file");
            // layout: id(4) + dir(4) + nameLen(4) + name(nb) + size(8) = 20 + nb
            var buf = new byte[20 + nb.Length];
            BitConverter.GetBytes(id).CopyTo(buf, 0);
            BitConverter.GetBytes(dir).CopyTo(buf, 4);
            BitConverter.GetBytes(nb.Length).CopyTo(buf, 8);
            nb.CopyTo(buf, 12);
            BitConverter.GetBytes(size).CopyTo(buf, 12 + nb.Length);
            return buf;
        }
        public static void ParseFOpen(byte[] p, out int id, out int dir, out string name, out long size)
        {
            id  = BitConverter.ToInt32(p, 0);
            dir = BitConverter.ToInt32(p, 4);
            int nl = BitConverter.ToInt32(p, 8);
            name = nl > 0 && 16 + nl <= p.Length ? Encoding.UTF8.GetString(p, 12, nl) : "file";
            size = BitConverter.ToInt64(p, 12 + (nl > 0 ? nl : 0));
        }
        // FData: [int32 id][int32 len][bytes]
        public static byte[] BuildFData(int id, byte[] chunk)
        {
            chunk ??= Array.Empty<byte>();
            var buf = new byte[8 + chunk.Length];
            BitConverter.GetBytes(id).CopyTo(buf, 0);
            BitConverter.GetBytes(chunk.Length).CopyTo(buf, 4);
            chunk.CopyTo(buf, 8);
            return buf;
        }
        public static void ParseFData(byte[] p, out int id, out byte[] chunk)
        {
            id = BitConverter.ToInt32(p, 0);
            int len = BitConverter.ToInt32(p, 4);
            if (len < 0 || len > 16 * 1024 * 1024 || 8 + len > p.Length) { chunk = Array.Empty<byte>(); return; }
            chunk = new byte[len];
            Array.Copy(p, 8, chunk, 0, len);
        }
        public static byte[] BuildId(int id) => BitConverter.GetBytes(id);
        public static int ParseId(byte[] p) => (p != null && p.Length >= 4) ? BitConverter.ToInt32(p, 0) : -1;
        // MonitorList: utf-8 text "0:WxH\n1:WxH\n..." broadcast to viewers.
        public static byte[] BuildMonitorList(string text)
        {
            var b = Encoding.UTF8.GetBytes(text ?? "");
            var buf = new byte[4 + b.Length];
            BitConverter.GetBytes(b.Length).CopyTo(buf, 0);
            b.CopyTo(buf, 4);
            return buf;
        }
        public static string ParseMonitorList(byte[] p)
        {
            if (p == null || p.Length < 4) return "";
            int n = BitConverter.ToInt32(p, 0);
            if (n <= 0 || 4 + n > p.Length) return "";
            return Encoding.UTF8.GetString(p, 4, n);
        }
        // ViewOnly: single byte, 1 = view-only on (viewer may watch + chat but
        // cannot operate the host), 0 = full control restored.
        public static byte[] BuildViewOnly(bool on) => new byte[] { (byte)(on ? 1 : 0) };
        public static bool ParseViewOnly(byte[] p) => p != null && p.Length > 0 && p[0] != 0;
        // FResp: [int32 id][int32 accept(1/0)]
        public static byte[] BuildFResp(int id, int accept)
        {
            var buf = new byte[8];
            BitConverter.GetBytes(id).CopyTo(buf, 0);
            BitConverter.GetBytes(accept).CopyTo(buf, 4);
            return buf;
        }
        public static void ParseFResp(byte[] p, out int id, out int accept)
        {
            id = BitConverter.ToInt32(p, 0); accept = BitConverter.ToInt32(p, 4);
        }

        // ---- P2P / TCP hole punch ----------------------------------------
        // PeerAddr: relay tells each peer the OTHER peer's public (ip, port)
        // so they can attempt a direct TCP connection. layout:
        //   [byte role(0=host,1=viewer)][int32 vid][int32 ipLen][ip utf8][int32 port]
        // Sent in the clear (the relay can't encrypt it), so it must NOT be in
        // the ShouldEncrypt whitelist.
        public static byte[] BuildPeerAddr(int role, int vid, string ip, int port,
                                           List<(string ip, int port)> candidates = null)
        {
            var ib = Encoding.UTF8.GetBytes(ip ?? "");
            var cands = candidates ?? new List<(string ip, int port)>();
            var cb = new List<byte[]>();
            int candBytes = 0;
            foreach (var c in cands)
            {
                var cib = Encoding.UTF8.GetBytes(c.ip ?? "");
                var b = new byte[4 + cib.Length + 4];
                BitConverter.GetBytes(cib.Length).CopyTo(b, 0);
                cib.CopyTo(b, 4);
                BitConverter.GetBytes(c.port).CopyTo(b, 4 + cib.Length);
                cb.Add(b);
                candBytes += b.Length;
            }
            // layout: [byte role][int32 vid][int32 ipLen][ip][int32 port]
            //         [int32 nCand][ per cand: int32 ipLen ][ip][int32 port] ]
            var buf = new byte[1 + 4 + 4 + ib.Length + 4 + 4 + candBytes];
            buf[0] = (byte)role;
            BitConverter.GetBytes(vid).CopyTo(buf, 1);
            BitConverter.GetBytes(ib.Length).CopyTo(buf, 5);
            ib.CopyTo(buf, 9);
            BitConverter.GetBytes(port).CopyTo(buf, 9 + ib.Length);
            int off = 9 + ib.Length + 4;
            BitConverter.GetBytes(cands.Count).CopyTo(buf, off);
            off += 4;
            foreach (var b in cb) { b.CopyTo(buf, off); off += b.Length; }
            return buf;
        }
        public static void ParsePeerAddr(byte[] p, out int role, out int vid, out string ip, out int port,
                                         out List<(string ip, int port)> candidates)
        {
            role = 0; vid = 0; ip = ""; port = 0;
            candidates = new List<(string ip, int port)>();
            if (p == null || p.Length < 13) return;
            role = p[0];
            vid = BitConverter.ToInt32(p, 1);
            int il = BitConverter.ToInt32(p, 5);
            if (il < 0 || 9 + il + 4 > p.Length) return;
            ip = Encoding.UTF8.GetString(p, 9, il);
            port = BitConverter.ToInt32(p, 9 + il);
            int off = 9 + il + 4;
            if (off + 4 > p.Length) return;
            int n = BitConverter.ToInt32(p, off); off += 4;
            for (int i = 0; i < n; i++)
            {
                if (off + 4 > p.Length) break;
                int cl = BitConverter.ToInt32(p, off); off += 4;
                if (cl < 0 || off + cl + 4 > p.Length) break;
                string cip = Encoding.UTF8.GetString(p, off, cl); off += cl;
                int cport = BitConverter.ToInt32(p, off); off += 4;
                candidates.Add((cip, cport));
            }
        }

        // LinkStat: the viewer's machine measures the real link quality it
        // experiences (RTT/jitter/decode-fps/receive-bandwidth) and feeds it
        // back to the host. The host's adaptive controller uses this *actual*
        // downlink RTT instead of guessing from its own send-stall proxy, which
        // removes the bitrate oscillation that causes latency spikes.
        // layout: [int32 rtt][int32 jitter][int32 decFps][int32 bwKbps]
        public static byte[] BuildLinkStat(int rtt, int jitter, int decFps, int bwKbps)
        {
            var buf = new byte[16];
            BitConverter.GetBytes(rtt).CopyTo(buf, 0);
            BitConverter.GetBytes(jitter).CopyTo(buf, 4);
            BitConverter.GetBytes(decFps).CopyTo(buf, 8);
            BitConverter.GetBytes(bwKbps).CopyTo(buf, 12);
            return buf;
        }
        public static void ParseLinkStat(byte[] p, out int rtt, out int jitter, out int decFps, out int bwKbps)
        {
            rtt = jitter = decFps = bwKbps = 0;
            if (p == null || p.Length < 16) return;
            rtt = BitConverter.ToInt32(p, 0);
            jitter = BitConverter.ToInt32(p, 4);
            decFps = BitConverter.ToInt32(p, 8);
            bwKbps = BitConverter.ToInt32(p, 12);
        }

        // ---- Phase 1B: viewer 实时画质协商 -------------------------------
        // ViewerPref: [byte resScale(100/75/50)][byte fps(5-30)][byte quality(1..5)][byte rsv]
        // 发送 viewer 想要的"缩放%/目标帧率/码率档位"给 host；host 在收到后重建
        // 编码器（见 HostForm.DispatchFromViewer 的 ViewerPref 分支）。
        public static byte[] BuildViewerPref(byte resScale, byte fps, byte quality)
        {
            return new byte[] { resScale, fps, quality, 0 };
        }
        public static void ParseViewerPref(byte[] p, out byte resScale, out byte fps, out byte quality)
        {
            resScale = 100; fps = 30; quality = 3;
            if (p == null || p.Length < 3) return;
            resScale = p[0] == 0 ? (byte)100 : p[0];
            fps = p[1] == 0 ? (byte)30 : p[1];
            quality = p[2] == 0 ? (byte)3 : p[2];
        }

        // ---- Phase 1D: 键盘事件（host -> viewer）------------------------
        // KeyEvent: [int32 vk][byte down(0/1)]。viewer 收到后转字符串显示。
        public static byte[] BuildKeyEvent(int vk, byte down)
        {
            var buf = new byte[5];
            BitConverter.GetBytes(vk).CopyTo(buf, 0);
            buf[4] = down;
            return buf;
        }
        public static void ParseKeyEvent(byte[] p, out int vk, out byte down)
        {
            vk = 0; down = 0;
            if (p == null || p.Length < 5) return;
            vk = BitConverter.ToInt32(p, 0);
            down = p[4];
        }

        // ---- Phase 2: 远程文件浏览器 -------------------------------------
        // 通用结果帧 BuildResult/ParseResult 已在上方（聊天发文件功能）定义，直接复用。
        // 带 id 的结果帧 [int32 id][int32 code][int32 msgLen][msg]
        public static byte[] BuildResultId(int id, int code, string msg)
        {
            var mb = Encoding.UTF8.GetBytes(msg ?? "");
            var buf = new byte[12 + mb.Length];
            BitConverter.GetBytes(id).CopyTo(buf, 0);
            BitConverter.GetBytes(code).CopyTo(buf, 4);
            BitConverter.GetBytes(mb.Length).CopyTo(buf, 8);
            mb.CopyTo(buf, 12);
            return buf;
        }
        public static void ParseResultId(byte[] p, out int id, out int code, out string msg)
        {
            id = -1; code = -1; msg = "";
            if (p == null || p.Length < 12) return;
            id = BitConverter.ToInt32(p, 0);
            code = BitConverter.ToInt32(p, 4);
            int n = BitConverter.ToInt32(p, 8);
            if (n < 0 || 12 + n > p.Length) return;
            msg = Encoding.UTF8.GetString(p, 12, n);
        }

        // FsList / FsDelete / FsMkdir: [int32 pathLen][utf8 path]
        public static byte[] BuildPath(string path)
        {
            var pb = Encoding.UTF8.GetBytes(path ?? "");
            var buf = new byte[4 + pb.Length];
            BitConverter.GetBytes(pb.Length).CopyTo(buf, 0);
            pb.CopyTo(buf, 4);
            return buf;
        }
        public static string ParsePath(byte[] p)
        {
            if (p == null || p.Length < 4) return "";
            int n = BitConverter.ToInt32(p, 0);
            if (n <= 0 || 4 + n > p.Length) return "";
            return Encoding.UTF8.GetString(p, 4, n);
        }

        // FsGet: [int32 id][int32 pathLen][path][int64 offset]  (id 由 viewer 生成，host 原样回显)
        // offset 为续传起始位置（0=从头开始），向后兼容旧格式不含 offset 的请求。
        public static byte[] BuildFsGet(int id, string path, long offset = 0)
        {
            var pb = Encoding.UTF8.GetBytes(path ?? "");
            var buf = new byte[16 + pb.Length];
            BitConverter.GetBytes(id).CopyTo(buf, 0);
            BitConverter.GetBytes(pb.Length).CopyTo(buf, 4);
            pb.CopyTo(buf, 8);
            BitConverter.GetBytes(offset).CopyTo(buf, 8 + pb.Length);
            return buf;
        }
        public static void ParseFsGet(byte[] p, out int id, out string path)
        {
            id = -1; path = "";
            if (p == null || p.Length < 8) return;
            id = BitConverter.ToInt32(p, 0);
            int n = BitConverter.ToInt32(p, 4);
            if (n <= 0 || 8 + n > p.Length) return;
            path = Encoding.UTF8.GetString(p, 8, n);
        }
        // Phase 7G: 解析续传 offset（向后兼容：旧请求无 offset 返回 0）
        public static long ParseFsGetOffset(byte[] p)
        {
            if (p == null || p.Length < 8) return 0;
            int n = BitConverter.ToInt32(p, 4);
            if (n <= 0 || 8 + n + 8 > p.Length) return 0;
            return BitConverter.ToInt64(p, 8 + n);
        }

        // FsListResp: [int32 pathLen][path][int32 err][int32 n][ 条目* ]
        //   条目: [byte isDir][int64 size][int64 mtime][int32 nameLen][name]
        public static byte[] BuildFsListResp(string path, int err, List<FsEntry> items)
        {
            var pb = Encoding.UTF8.GetBytes(path ?? "");
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(pb.Length); bw.Write(pb);
            bw.Write(err);
            bw.Write(items?.Count ?? 0);
            if (items != null)
                foreach (var e in items)
                {
                    bw.Write((byte)(e.IsDir ? 1 : 0));
                    bw.Write(e.Size);
                    bw.Write(e.Mtime);
                    var nb = Encoding.UTF8.GetBytes(e.Name ?? "");
                    bw.Write(nb.Length); bw.Write(nb);
                }
            return ms.ToArray();
        }
        public static void ParseFsListResp(byte[] p, out string path, out int err, out List<FsEntry> items)
        {
            path = ""; err = -1; items = new List<FsEntry>();
            if (p == null || p.Length < 12) return;
            using var ms = new MemoryStream(p);
            using var br = new BinaryReader(ms);
            int pl = br.ReadInt32(); path = pl > 0 ? Encoding.UTF8.GetString(br.ReadBytes(pl)) : "";
            err = br.ReadInt32();
            int n = br.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                if (ms.Position + 17 > ms.Length) break;
                var e = new FsEntry();
                e.IsDir = br.ReadByte() == 1;
                e.Size = br.ReadInt64();
                e.Mtime = br.ReadInt64();
                int nl = br.ReadInt32();
                if (nl < 0 || ms.Position + nl > ms.Length) break;
                e.Name = Encoding.UTF8.GetString(br.ReadBytes(nl));
                items.Add(e);
            }
        }

        // FsGetReady: [int32 id][int32 code][int64 size][int32 nameLen][name]
        public static byte[] BuildFsGetReady(int id, int code, long size, string name)
        {
            var nb = Encoding.UTF8.GetBytes(name ?? "file");
            var buf = new byte[20 + nb.Length];
            BitConverter.GetBytes(id).CopyTo(buf, 0);
            BitConverter.GetBytes(code).CopyTo(buf, 4);
            BitConverter.GetBytes(size).CopyTo(buf, 8);
            BitConverter.GetBytes(nb.Length).CopyTo(buf, 16);
            nb.CopyTo(buf, 20);
            return buf;
        }
        public static void ParseFsGetReady(byte[] p, out int id, out int code, out long size, out string name)
        {
            id = -1; code = -1; size = 0; name = "file";
            if (p == null || p.Length < 20) return;
            id = BitConverter.ToInt32(p, 0);
            code = BitConverter.ToInt32(p, 4);
            size = BitConverter.ToInt64(p, 8);
            int nl = BitConverter.ToInt32(p, 16);
            if (nl > 0 && 20 + nl <= p.Length) name = Encoding.UTF8.GetString(p, 20, nl);
        }

        // FsPut: [int32 id][int32 pathLen][path][int64 size][int64 offset]  (Phase 7H: offset 用于续传)
        public static byte[] BuildFsPut(int id, string path, long size, long offset = 0)
        {
            var pb = Encoding.UTF8.GetBytes(path ?? "");
            var buf = new byte[24 + pb.Length];
            BitConverter.GetBytes(id).CopyTo(buf, 0);
            BitConverter.GetBytes(pb.Length).CopyTo(buf, 4);
            pb.CopyTo(buf, 8);
            BitConverter.GetBytes(size).CopyTo(buf, 8 + pb.Length);
            BitConverter.GetBytes(offset).CopyTo(buf, 16 + pb.Length);
            return buf;
        }
        public static void ParseFsPut(byte[] p, out int id, out string path, out long size)
        {
            id = -1; path = ""; size = 0;
            if (p == null || p.Length < 16) return;
            id = BitConverter.ToInt32(p, 0);
            int pl = BitConverter.ToInt32(p, 4);
            if (pl <= 0 || 8 + pl + 8 > p.Length) return;
            path = Encoding.UTF8.GetString(p, 8, pl);
            size = BitConverter.ToInt64(p, 8 + pl);
        }
        public static long ParseFsPutOffset(byte[] p)
        {
            if (p == null || p.Length < 8) return 0;
            int pl = BitConverter.ToInt32(p, 4);
            if (pl <= 0 || 8 + pl + 16 > p.Length) return 0;
            return BitConverter.ToInt64(p, 16 + pl);
        }

        // FsRename: [int32 oldLen][old][int32 newLen][new]
        public static byte[] BuildFsRename(string oldPath, string newPath)
        {
            var ob = Encoding.UTF8.GetBytes(oldPath ?? "");
            var nb = Encoding.UTF8.GetBytes(newPath ?? "");
            var buf = new byte[8 + ob.Length + nb.Length];
            BitConverter.GetBytes(ob.Length).CopyTo(buf, 0);
            ob.CopyTo(buf, 4);
            BitConverter.GetBytes(nb.Length).CopyTo(buf, 4 + ob.Length);
            nb.CopyTo(buf, 8 + ob.Length);
            return buf;
        }
        public static void ParseFsRename(byte[] p, out string oldPath, out string newPath)
        {
            oldPath = ""; newPath = "";
            if (p == null || p.Length < 8) return;
            int ol = BitConverter.ToInt32(p, 0);
            if (ol <= 0 || 4 + ol + 4 > p.Length) return;
            oldPath = Encoding.UTF8.GetString(p, 4, ol);
            int off = 4 + ol;
            int nl = BitConverter.ToInt32(p, off); off += 4;
            if (nl <= 0 || off + nl > p.Length) return;
            newPath = Encoding.UTF8.GetString(p, off, nl);
        }

        // FsGetErr / FsPutReady / FsPutAck 共用 [int32 id][int32 code][int32 msgLen][msg]
        public static byte[] BuildFsGetErr(int id, int code, string msg) => BuildResultId(id, code, msg);
        public static void ParseFsGetErr(byte[] p, out int id, out int code, out string msg) => ParseResultId(p, out id, out code, out msg);
        public static byte[] BuildFsPutReady(int id, int code, string msg) => BuildResultId(id, code, msg);
        public static void ParseFsPutReady(byte[] p, out int id, out int code, out string msg) => ParseResultId(p, out id, out code, out msg);
        public static byte[] BuildFsPutAck(int id, int code, string msg) => BuildResultId(id, code, msg);
        public static void ParseFsPutAck(byte[] p, out int id, out int code, out string msg) => ParseResultId(p, out id, out code, out msg);

        // FsDeleteResp / FsRenameResp / FsMkdirResp 共用 [int32 code][int32 msgLen][msg]
        public static byte[] BuildFsDeleteResp(int code, string msg) => BuildResult(code, msg);
        public static void ParseFsDeleteResp(byte[] p, out int code, out string msg) => ParseResult(p, out code, out msg);
        public static byte[] BuildFsRenameResp(int code, string msg) => BuildResult(code, msg);
        public static void ParseFsRenameResp(byte[] p, out int code, out string msg) => ParseResult(p, out code, out msg);
        public static byte[] BuildFsMkdirResp(int code, string msg) => BuildResult(code, msg);
        public static void ParseFsMkdirResp(byte[] p, out int code, out string msg) => ParseResult(p, out code, out msg);

        // ---- Phase 3: 设备缩略图（PNG 快照，绕开单例 H264 编码器）-------------
        // ThumbReq: [int32 maxW] —— 期望最大宽度（被控端固定 240，可忽略）。
        public static byte[] BuildThumbReq(int maxW)
        {
            var buf = new byte[4];
            BitConverter.GetBytes(maxW).CopyTo(buf, 0);
            return buf;
        }
        public static void ParseThumbReq(byte[] p, out int maxW)
        {
            maxW = 240;
            if (p == null || p.Length < 4) return;
            maxW = BitConverter.ToInt32(p, 0);
        }
        // ThumbFrame: [int32 w][int32 h][int32 pngLen][png bytes]
        public static byte[] BuildThumbFrame(int w, int h, byte[] png)
        {
            var buf = new byte[12 + (png?.Length ?? 0)];
            BitConverter.GetBytes(w).CopyTo(buf, 0);
            BitConverter.GetBytes(h).CopyTo(buf, 4);
            int n = png?.Length ?? 0;
            BitConverter.GetBytes(n).CopyTo(buf, 8);
            if (n > 0) png.CopyTo(buf, 12);
            return buf;
        }
        public static void ParseThumbFrame(byte[] p, out int w, out int h, out byte[] png)
        {
            w = 0; h = 0; png = Array.Empty<byte>();
            if (p == null || p.Length < 12) return;
            w = BitConverter.ToInt32(p, 0);
            h = BitConverter.ToInt32(p, 4);
            int n = BitConverter.ToInt32(p, 8);
            if (n <= 0 || 12 + n > p.Length) return;
            png = new byte[n];
            Array.Copy(p, 12, png, 0, n);
        }

        // ---- Phase 4 动作编排 -----------------------------------------------
        // ActRun: [int32 actionId][byte kind][byte silent][int32 payloadLen][payload]
        //   kind   : 1=Exec 2=Launch 3=Keys 4=Lock 5=Message 6=Reboot 7=Shutdown
        //   silent : 1=跳过主机确认（危险动作如重启/关机直接执行；用于自动化）
        //   payload: utf-8，按 kind 解释（Exec=命令；Launch="路径\t参数"；
        //            Keys=要键入的文本；Message="标题\t正文"；其余为空）
        public static byte[] BuildActRun(int actionId, byte kind, byte silent, byte[] payload)
        {
            payload ??= Array.Empty<byte>();
            var buf = new byte[10 + payload.Length];
            BitConverter.GetBytes(actionId).CopyTo(buf, 0);
            buf[4] = kind;
            buf[5] = silent;
            BitConverter.GetBytes(payload.Length).CopyTo(buf, 6);
            if (payload.Length > 0) payload.CopyTo(buf, 10);
            return buf;
        }
        public static void ParseActRun(byte[] p, out int actionId, out byte kind, out byte silent, out byte[] payload)
        {
            actionId = -1; kind = 0; silent = 0; payload = Array.Empty<byte>();
            if (p == null || p.Length < 10) return;
            actionId = BitConverter.ToInt32(p, 0);
            kind = p[4];
            silent = p[5];
            int n = BitConverter.ToInt32(p, 6);
            if (n < 0 || 10 + n > p.Length) return;
            payload = new byte[n];
            Array.Copy(p, 10, payload, 0, n);
        }

        // ActResult: [int32 actionId][int32 code][int32 outLen][out utf-8]
        //   code  : 0=成功；1=执行异常；2=主机拒绝；3=未知动作类型；-1=连接/超时（控制端填）
        //   out   : 成功时的 stdout / 状态文本 / 错误信息
        public static byte[] BuildActResult(int actionId, int code, string outText)
        {
            var ob = Encoding.UTF8.GetBytes(outText ?? "");
            var buf = new byte[12 + ob.Length];
            BitConverter.GetBytes(actionId).CopyTo(buf, 0);
            BitConverter.GetBytes(code).CopyTo(buf, 4);
            BitConverter.GetBytes(ob.Length).CopyTo(buf, 8);
            ob.CopyTo(buf, 12);
            return buf;
        }
        public static void ParseActResult(byte[] p, out int actionId, out int code, out string outText)
        {
            actionId = -1; code = -1; outText = "";
            if (p == null || p.Length < 12) return;
            actionId = BitConverter.ToInt32(p, 0);
            code = BitConverter.ToInt32(p, 4);
            int n = BitConverter.ToInt32(p, 8);
            if (n < 0 || 12 + n > p.Length) return;
            outText = Encoding.UTF8.GetString(p, 12, n);
        }

        // ---- Phase 5 会话内标注 --------------------------------------------
        // 单帧布局（按 kind 区分）：
        //   kind=Arrow : [byte 0][float x1][float y1][float x2][float y2][int colorArgb]
        //   kind=Text  : [byte 1][float x][float y][int colorArgb][int textLen][utf8 text]
        //   kind=Clear : [byte 2]
        // 坐标均为归一化(0~1)，跨分辨率对齐。
        public static byte[] BuildAnno(Anno a)
        {
            a ??= new Anno();
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)a.Kind);
            if (a.Kind == AnnoKind.Arrow)
            {
                bw.Write(a.X1); bw.Write(a.Y1); bw.Write(a.X2); bw.Write(a.Y2);
                bw.Write(a.ColorArgb);
            }
            else if (a.Kind == AnnoKind.Text)
            {
                bw.Write(a.X1); bw.Write(a.Y1);
                bw.Write(a.ColorArgb);
                var tb = Encoding.UTF8.GetBytes(a.Text ?? "");
                bw.Write(tb.Length); bw.Write(tb);
            }
            // Clear：仅 kind 字节
            return ms.ToArray();
        }
        public static void ParseAnno(byte[] p, out Anno a)
        {
            a = new Anno();
            if (p == null || p.Length < 1) return;
            using var ms = new MemoryStream(p);
            using var br = new BinaryReader(ms);
            a.Kind = (AnnoKind)br.ReadByte();
            if (a.Kind == AnnoKind.Arrow && p.Length >= 21)
            {
                a.X1 = br.ReadSingle(); a.Y1 = br.ReadSingle();
                a.X2 = br.ReadSingle(); a.Y2 = br.ReadSingle();
                a.ColorArgb = br.ReadInt32();
            }
            else if (a.Kind == AnnoKind.Text && p.Length >= 13)
            {
                a.X1 = br.ReadSingle(); a.Y1 = br.ReadSingle();
                a.ColorArgb = br.ReadInt32();
                int n = br.ReadInt32();
                if (n > 0 && n <= 4096 && ms.Position + n <= ms.Length)
                    a.Text = Encoding.UTF8.GetString(br.ReadBytes(n));
            }
        }
    }
}
