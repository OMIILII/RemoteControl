// Stubs.cs - 开源版移除功能的占位类。保编译，无实际功能。
// 如需启用对应功能，请自行还原原文件。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RemoteControl
{
    // --- 云账号相关 ---
    internal class AccountData
    {
        public string Token = "";
        public string DeviceToken = "";
        public string AccountKey = "";
        public string AccountKeyEnc = ""; // DPAPI 保护的密钥
        public string UserId = "";
        public int DeviceId;
        public string Username = "";
    }

    internal class AccountStore
    {
        public static AccountData? Load() => null;
        public static void Save(AccountData a) { }
        public static void Clear() { }
        public static string Unprotect(string enc) => "";
    }

    // --- 云会话参数 ---
    internal class CloudSession
    {
        public string TargetToken = "";
        public int TargetDeviceId;
        public string TargetName = "";
        public string DeviceToken = "";
        public string AccountKey = "";
        public string SessionId = "";
        public string Username = "";
        public string Server = "127.0.0.1";
        public int Port = 25498;
    }

    // --- CloudConfig（云连接常量） ---
    internal static class CloudConfig
    {
        public const string TcpHost = "127.0.0.1";
        public const int TcpPort = 25498;
    }

    // --- 版本检测 ---
    internal static class UpgradeCheck
    {
        public static string CurrentVersion() => "1.0.0";
        public static Task CheckAndPromptAsync() => Task.CompletedTask;
    }

    // --- 设备云 API ---
    internal static class DeviceCloud
    {
        public static string ApiBase = "http://127.0.0.1:21363";
        public static Task<T> GetDevicesAsync<T>(string token, int id) => Task.FromResult(default(T)!);
        public static Task OfflineAsync(string token, string deviceToken) => Task.CompletedTask;
        public static Task HeartbeatAsync(string token, string deviceToken) => Task.CompletedTask;
    }

    // --- 缩略图客户端（云设备预览） ---
    internal class ThumbnailClient : IDisposable
    {
        public ThumbnailClient(int devId, string token, string deviceToken,
            Action<int, Image> onFrame, string server, int port) { }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    // --- 实时监控（版本+公告轮询） ---
    internal static class RealTimeMonitor
    {
        public static void Start() { }
        public static void Stop() { }
    }

    // --- 非核心 UI 功能占位 ---
    internal class ChatForm : Form
    {
        public ChatForm() { }
        public ChatForm(HostForm hf) { }
        public ChatForm(Action<string> cb) { }
        public ChatForm(Action<List<string>> cb) { }
        public ChatForm(Action<string, string[]> cb) { }
        public void Append(string text) { }
        public void Append(List<string> texts) { }
        public void Append(string[] texts) { }
    }
    internal class BlackScreenForm : Form { public BlackScreenForm() { } public BlackScreenForm(HostForm hf, Transport t) { } }
    internal class AnnotationOverlay : Form
    {
        public AnnotationOverlay() { }
        public AnnotationOverlay(ViewerForm vf) { }
        public AnnotationOverlay(HostForm hf) { }
        public void Add(object? frame) { }
    }
    internal class StatsForm : Form
    {
        public StatsForm() { }
        public StatsForm(ViewerForm vf, Transport t) { }
        public StatsForm(ViewerForm vf) { }
        public StatsForm(HostForm hf) { }
        public void Push(object? stats) { }
        public void Push(int v1, int v2, double v3, double v4, string v5, bool v6) { }
        public void Push(int v1, int v2, int v3, int v4, int v5, long v6) { }
    }
    internal class QuickOpsForm : Form
    {
        public QuickOpsForm(DeviceInfo dev, CloudSession s, AccountData? a = null) { }
        public QuickOpsForm(ViewerForm vf, string s) { }
    }

    // DeviceInfo 的简化定义（原在 DeviceCloud.cs 中）
    internal class DeviceInfo
    {
        public int id { get; set; }
        public string? name { get; set; }
        public bool online { get; set; }
        public long last_seen { get; set; }
        public bool is_agent { get; set; }
    }
}
