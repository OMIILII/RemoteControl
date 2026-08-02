// Stubs.cs - empty type stubs for UI classes referenced by HostForm but unused in agent mode.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace RemoteControl
{
    internal class FileTransferForm : Form { }
    internal class BlackScreenForm : Form { }

    internal class AnnotationOverlay : UserControl
    {
        public void Add(string text) { }
    }

    internal class ChatForm : Form
    {
        public ChatForm(Action<string, bool> onSend) { }
        public void Append(string text) { }
    }

    // Account data stub
    internal class CloudConfig
    {
        public string Token = "";
        public string DeviceToken = "";
        public string Username = "";
    }

    // Version check stub
    internal static class UpgradeCheck
    {
        public static string CurrentVersion() => "1.0.0";
    }

    internal static class CjkFontHolder
    {
        public const string FontName = "Microsoft YaHei";
        public static readonly Font Font = new Font(FontName, 9f);
    }
}