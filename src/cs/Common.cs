// Common.cs - shared helpers used by both the host and viewer GUIs.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace RemoteControl
{
    public static class Common
    {
        // ---- room password -------------------------------------------------
        // The password is never sent in cleartext: the relay hands out a
        // per-room challenge and the client proves knowledge of the password by
        // sending sha256(password + challenge) (host) or sha256(password +
        // challenge) for viewers — both equal the stored hash when correct.
        public static string HashPassword(string password)
        {
            using var sha = SHA256.Create();
            var b = sha.ComputeHash(Encoding.UTF8.GetBytes(password ?? ""));
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        // ---- saved connections (favorites) -------------------------------
        public sealed class Favorite
        {
            public string Name { get; set; }
            public string Server { get; set; }
            public int Port { get; set; }
            public string Room { get; set; }
            public string Password { get; set; }
        }

        private static string FavPath()
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "RemoteControl", "favorites.json");

        public static Favorite[] LoadFavorites()
        {
            try
            {
                var p = FavPath();
                if (!File.Exists(p)) return Array.Empty<Favorite>();
                var arr = JsonSerializer.Deserialize<Favorite[]>(File.ReadAllText(p));
                return arr ?? Array.Empty<Favorite>();
            }
            catch { return Array.Empty<Favorite>(); }
        }

        public static void SaveFavorites(Favorite[] list)
        {
            var dir = Path.GetDirectoryName(FavPath());
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(FavPath(), JsonSerializer.Serialize(list,
                new JsonSerializerOptions { WriteIndented = true }));
        }

        // A reusable "save file" dialog that appends an extension if missing.
        public static string AutoSaveDialog(string title, string initialDir, string suggestedName, string filter)
        {
            using var d = new SaveFileDialog
            {
                Title = title,
                InitialDirectory = initialDir,
                FileName = suggestedName,
                Filter = filter,
            };
            return d.ShowDialog() == DialogResult.OK ? d.FileName : null;
        }

        public static string PickSaveDir(string title)
        {
            using var d = new FolderBrowserDialog { Description = title };
            return d.ShowDialog() == DialogResult.OK ? d.SelectedPath : null;
        }

        // ---- launch at login (HKCU Run key) ------------------------------
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AutostartName = "RemoteControlHost";

        /// <summary>
        /// 被控端"开机自动建房间"的持久化配置。存到
        /// %LocalAppData%\RemoteControl\host_profile.json（口令在此，不进注册表）。
        /// 开机自启的 Run 键只写 "--host --autostart --hide"，房间细节从本文件读，
        /// 避免把口令明文塞进注册表 Run 项。
        /// </summary>
        public sealed class HostProfile
        {
            public string Server = "";
            public int Port = 0;
            public string Room = "";
            public string Password = "";
            public int Fps = 0;
            public int Quality = 1;     // 0 流畅 / 1 均衡 / 2 清晰
            public int Monitor = 0;
            public bool ViewOnly = false;
            public bool NoAdapt = false;
            public bool NoComp = false;
            public bool NoP2P = false;
            public bool NoClip = false;
            public bool Audio = false;
            public bool Retry = false;       // 建房间失败自动每 30 秒重试
        }

        private static string ProfileDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteControl");
        public static string HostProfilePath => Path.Combine(ProfileDir, "host_profile.json");

        public static void SaveHostProfile(HostProfile p)
        {
            try
            {
                Directory.CreateDirectory(ProfileDir);
                var json = System.Text.Json.JsonSerializer.Serialize(p,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(HostProfilePath, json, System.Text.Encoding.UTF8);
            }
            catch { }
        }

        public static HostProfile LoadHostProfile()
        {
            try
            {
                if (!File.Exists(HostProfilePath)) return null;
                var json = File.ReadAllText(HostProfilePath, System.Text.Encoding.UTF8);
                var p = System.Text.Json.JsonSerializer.Deserialize<HostProfile>(json);
                return p ?? null;
            }
            catch { return null; }
        }

        public static bool IsAutostartEnabled()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false);
                return k?.GetValue(AutostartName) != null;
            }
            catch { return false; }
        }

        // Phase 7E: Binary Overlay — zzrat 风格：PE 最后一个 section 之后追加的配置。
        public static string ReadOverlayConfig(string exePath)
        {
            try
            {
                if (!File.Exists(exePath)) return null;
                using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                long fileSize = fs.Length;
                if (fileSize < 256) return null;

                // 读 DOS 头
                var dosBuf = new byte[64];
                fs.Seek(0, SeekOrigin.Begin);
                if (fs.Read(dosBuf, 0, 64) < 64) return null;
                ushort magic = BitConverter.ToUInt16(dosBuf, 0);
                if (magic != 0x5A4D) return null; // MZ
                int peOff = BitConverter.ToInt32(dosBuf, 60); // e_lfanew

                // 读 PE 签名 + NT 头
                var peBuf = new byte[4 + 20]; // Signature + FileHeader
                fs.Seek(peOff, SeekOrigin.Begin);
                if (fs.Read(peBuf, 0, peBuf.Length) < peBuf.Length) return null;
                if (BitConverter.ToUInt32(peBuf, 0) != 0x00004550) return null; // PE\0\0
                ushort sections = BitConverter.ToUInt16(peBuf, 6); // NumberOfSections

                // 跳过 OptionalHeader 到 Section Headers
                ushort optSize = BitConverter.ToUInt16(peBuf, 20); // SizeOfOptionalHeader
                fs.Seek(peOff + 4 + 20 + optSize, SeekOrigin.Begin);

                // 找最后一个 section
                long lastSectionEnd = 0;
                var secBuf = new byte[40];
                for (int i = 0; i < sections; i++)
                {
                    if (fs.Read(secBuf, 0, 40) < 40) break;
                    uint rawOff = BitConverter.ToUInt32(secBuf, 20);  // PointerToRawData
                    uint rawSize = BitConverter.ToUInt32(secBuf, 16);  // SizeOfRawData
                    long end = rawOff + rawSize;
                    if (end > lastSectionEnd) lastSectionEnd = end;
                }

                if (lastSectionEnd <= 0 || lastSectionEnd >= fileSize) return null;

                // Phase 12: for single-file EXEs, the .NET bundle sits between
                // the PE sections and our appended overlay. Instead of reading
                // from lastSectionEnd (which includes the bundle), read the
                // LAST 4096 bytes and find the JSON from the trailing end.
                int tail = Math.Min(4096, (int)(fileSize - lastSectionEnd));
                fs.Seek(-tail, SeekOrigin.End);
                var data = new byte[tail];
                if (fs.Read(data, 0, tail) != tail) return null;

                string chunk = Encoding.UTF8.GetString(data);
                int opening = chunk.LastIndexOf('{');
                int closing = chunk.LastIndexOf('}');
                if (opening < 0 || closing < opening) return null;
                return chunk.Substring(opening, closing - opening + 1);
            }
            catch { return null; }
        }

        /// <summary>把 jsonText 写入 srcExe 最后一个 PE section 之后，输出到 dstExe。</summary>
        public static bool WriteOverlayConfig(string srcExe, string dstExe, string jsonText)
        {
            try
            {
                if (!File.Exists(srcExe) || string.IsNullOrEmpty(jsonText)) return false;
                byte[] overlay = Encoding.UTF8.GetBytes(jsonText);
                File.Copy(srcExe, dstExe, true);
                using var fs = new FileStream(dstExe, FileMode.Append, FileAccess.Write, FileShare.None);
                fs.Write(overlay, 0, overlay.Length);
                return true;
            }
            catch { return false; }
        }

        /// <summary>获取本机首选局域网 IPv4 地址（zzrat 风格）。</summary>
        public static string GetLanIP()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var ip in host.AddressList)
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return ip.ToString();
            }
            catch { }
            return "";
        }

        public static void SetAutostart(bool enable, string extraArgs = "")
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true)
                              ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
                if (enable)
                {
                    string exe = Application.ExecutablePath;
                    string cmd = "\"" + exe + "\"";
                    if (!string.IsNullOrWhiteSpace(extraArgs)) cmd += " " + extraArgs;
                    k.SetValue(AutostartName, cmd);
                }
                else k.DeleteValue(AutostartName, false);
            }
            catch { }
        }

        // ---- Wake-on-LAN --------------------------------------------------
        // Sends the magic packet (6x0xFF + 16x MAC) as a UDP broadcast so a
        // sleeping/powered-off machine on the same LAN wakes up.
        public static bool SendWakeOnLan(string mac, string broadcast = "255.255.255.255", int port = 9)
        {
            try
            {
                byte[] macBytes = ParseMac(mac);
                if (macBytes == null) return false;
                var packet = new byte[6 + 16 * 6];
                for (int i = 0; i < 6; i++) packet[i] = 0xFF;
                for (int i = 0; i < 16; i++) Array.Copy(macBytes, 0, packet, 6 + i * 6, 6);
                using var udp = new System.Net.Sockets.UdpClient { EnableBroadcast = true };
                var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(broadcast), port);
                udp.Send(packet, packet.Length, ep);
                return true;
            }
            catch { return false; }
        }

        private static byte[] ParseMac(string mac)
        {
            if (string.IsNullOrWhiteSpace(mac)) return null;
            string clean = mac.Replace(":", "").Replace("-", "").Replace(" ", "").Trim();
            if (clean.Length != 12) return null;
            var b = new byte[6];
            for (int i = 0; i < 6; i++)
            {
                if (!byte.TryParse(clean.Substring(i * 2, 2),
                        System.Globalization.NumberStyles.HexNumber, null, out b[i]))
                    return null;
            }
            return b;
        }

        /// <summary>
        /// 是否显示"高级设置"（中继服务器 / 端口 输入框）。
        /// 默认隐藏，仅当启动命令行带 --adv 或 --advanced 时显示。
        /// 例：RemoteControl.exe --adv
        /// </summary>
        public static bool IsAdvancedUi()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                foreach (var a in args)
                {
                    if (string.Equals(a, "--adv", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(a, "--advanced", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }
    }
}
