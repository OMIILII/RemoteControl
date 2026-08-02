// TerminalForm.cs - 远程终端（控制端）。
//
// 连接对端（被控端）隐藏运行的 Shell，发送命令并回显输出。被控端完全看不到
// 窗口与过程。功能（实用型）：多行输出、↑↓命令历史、Ctrl+C 中断、清屏、
// 复制/粘贴、可调字体与窗口大小、显示当前路径。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace RemoteControl
{
    public sealed class TerminalForm : Form
    {
        private readonly Action<MessageType, byte[]> _send;   // 发往被控端
        private readonly Action _onClose;                      // 关闭时通知会话

        private TextBox _out;
        private TextBox _in;
        private Label _path;
        private Label _outPlaceholder;
        private ComboBox _shell;
        private readonly List<string> _history = new();
        private int _histIdx = -1;
        private string _cwd = "";

        public TerminalForm(Action<MessageType, byte[]> send, Action onClose)
        {
            _send = send;
            _onClose = onClose;
            InitUI();
        }

        private void InitUI()
        {
            Text = "远程终端";
            Width = 720; Height = 460;
            MinimumSize = new Size(420, 220);  // 防止被拖太小后 _out 被挤没
            Font = new Font(CjkFontHolder.FontName, 10f);

            _path = new Label { Dock = DockStyle.Top, Height = 22, Text = "路径: (连接中…)", ForeColor = Color.DarkBlue };
            // 终端工具条：用 FlatStyle.System 走 OS 原生按钮渲染，GDI 不会用 CJK 字体度量把文字
            // 挤到底部裁切（之前用默认 FlatStyle.Standard 时"清屏/字体/断开"被切底）。
            // 高度 32 给 10f CJK 文字 + 上下边距留余地。
            var bar = new Panel { Dock = DockStyle.Top, Height = 32 };
            _shell = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Font = new Font(CjkFontHolder.FontName, 9.5f) };
            _shell.Items.Add("命令提示符 (cmd)"); _shell.Items.Add("PowerShell"); _shell.SelectedIndex = 0;
            var btnClear = new Button { Text = "清屏", Width = 70, FlatStyle = FlatStyle.System, Font = new Font(CjkFontHolder.FontName, 9.5f) };
            var btnFont  = new Button { Text = "字体", Width = 70, FlatStyle = FlatStyle.System, Font = new Font(CjkFontHolder.FontName, 9.5f) };
            var btnClose = new Button { Text = "断开", Width = 70, FlatStyle = FlatStyle.System, Font = new Font(CjkFontHolder.FontName, 9.5f) };
            bar.Controls.Add(_shell); _shell.Location = new Point(4, 5);
            bar.Controls.Add(btnClear); btnClear.Location = new Point(162, 4);
            bar.Controls.Add(btnFont);  btnFont.Location  = new Point(240, 4);
            bar.Controls.Add(btnClose); btnClose.Location = new Point(318, 4);

            _out = new TextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true, Multiline = true,
                ScrollBars = ScrollBars.Both, BackColor = Color.Black, ForeColor = Color.LightGray,
                Font = new Font("Consolas", 11f), WordWrap = false,
            };
            // 允许从输出区 Ctrl+C 复制选中文本
            _out.KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.C) _out.Copy(); };
            // 第一行内容进来后立刻把占位 Label 藏掉（OnOutput 里统一处理）。
            _out.TextChanged += (s, e) =>
            {
                if (_out.TextLength > 0 && _outPlaceholder != null && _outPlaceholder.Visible)
                    _outPlaceholder.Visible = false;
                if (_out.TextLength == 0 && _outPlaceholder != null && !_outPlaceholder.Visible)
                    _outPlaceholder.Visible = true;
            };

            _in = new TextBox { Dock = DockStyle.Bottom, Height = 26, Font = new Font("Consolas", 11f) };
            _in.KeyDown += OnInputKey;

            // 占位 Label：cmd 启动到第一帧输出之间，避免 _out 一片纯黑让用户误以为"卡死"。
            // 用 Dock=None + 绝对位置贴到 _out 客户区中上（不是最顶，避免和 _path 撞）。
            // 由 TextChanged 自动隐藏/恢复。
            _outPlaceholder = new Label
            {
                Text = "（等待命令输出…）",
                ForeColor = Color.DimGray,
                BackColor = Color.Transparent,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            _out.Controls.Add(_outPlaceholder);
            // Form 首次显示后再定位到 _out 中上（_out 还没尺寸）
            Shown += (s, e) => CenterPlaceholder();

            // 顺序：非 Fill 控件先 Add，_out(Fill) 最后 Add，否则 _out 会按
            // "全部可用空间"先占满整个客户区，把底部的 _in 输入框整个挤掉覆盖掉。
            Controls.Add(_path);
            Controls.Add(bar);
            Controls.Add(_in);
            Controls.Add(_out);
            // 显式把 _out 拉到最上层，覆盖 _in 之后确保 _out 永远在 _in 之下被绘，
            // 同时防止 _in 的 z-order 因某些宿主形态被推到上面盖住 _out 顶部。
            _out.BringToFront();

            btnClear.Click += (s, e) => { _out.Clear(); SendLine("cls"); };
            btnFont.Click += (s, e) =>
            {
                using var d = new FontDialog { Font = _out.Font };
                if (d.ShowDialog(this) == DialogResult.OK) { _out.Font = d.Font; _in.Font = d.Font; }
            };
            btnClose.Click += (s, e) => Close();
        }

        // 把占位 Label 放到 _out 客户区的中上部（不挤到 _path 的位置）。
        private void CenterPlaceholder()
        {
            if (_outPlaceholder == null || _out == null) return;
            int phW = _outPlaceholder.Width;
            int phH = _outPlaceholder.Height;
            int x = Math.Max(4, (_out.ClientSize.Width - phW) / 2);
            // 距 _out 顶 1 行高，看起来像"等待中"的提示而不是顶在最上。
            int y = Math.Max(2, (_out.ClientSize.Height - phH) / 2 - 6);
            _outPlaceholder.Location = new Point(x, y);
            _outPlaceholder.BringToFront();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // 打开远端隐藏 Shell
            byte shell = _shell.SelectedIndex == 1 ? (byte)1 : (byte)0;
            _send(MessageType.TerminalOpen, Codec.BuildTerminalOpen(Cols, Rows, shell));
            _shell.Enabled = false;
            // 打开后立即把焦点放到输入框，用户可直接敲命令
            try { ActiveControl = _in; _in.Focus(); } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            try { _send(MessageType.TerminalClose, Codec.BuildTerminalClose(0)); } catch { }
            _onClose?.Invoke();
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            try { _send(MessageType.TerminalResize, Codec.BuildTerminalResize(Cols, Rows)); } catch { }
        }

        // 拖动/缩放时，把 _out 内的占位 Label 重新居中。
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            CenterPlaceholder();
        }

        private int Cols
        {
            get
            {
                using var g = _out.CreateGraphics();
                var w = (int)g.MeasureString("M", _out.Font).Width;
                return Math.Max(20, _out.ClientSize.Width / Math.Max(1, w));
            }
        }
        private int Rows
        {
            get
            {
                using var g = _out.CreateGraphics();
                int h = (int)g.MeasureString("M", _out.Font).Height;
                return Math.Max(6, _out.ClientSize.Height / Math.Max(1, h));
            }
        }

        private void OnInputKey(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                string line = _in.Text;
                if (!string.IsNullOrEmpty(line)) { _history.Add(line); if (_history.Count > 200) _history.RemoveAt(0); }
                _histIdx = _history.Count;
                // 本地只放一个 "> " 提示符（不带 line、不带 \n）：远端 cmd 会自己回显
                // 整条命令 + 错误信息 + 新提示符，避免本地/远端双回显。
                AppendLocal("> ");
                SendLine(line);
                _in.Clear();
            }
            else if (e.KeyCode == Keys.Up)
            {
                e.Handled = true;
                if (_history.Count > 0) { _histIdx = Math.Max(0, _histIdx - 1); _in.Text = _history[_histIdx]; _in.Select(_in.Text.Length, 0); }
            }
            else if (e.KeyCode == Keys.Down)
            {
                e.Handled = true;
                if (_history.Count > 0)
                {
                    _histIdx = Math.Min(_history.Count, _histIdx + 1);
                    _in.Text = _histIdx < _history.Count ? _history[_histIdx] : "";
                    _in.Select(_in.Text.Length, 0);
                }
            }
            else if (e.Control && e.KeyCode == Keys.C)
            {
                // 向远端发送 SIGINT（0x03），中断当前命令行程序
                e.Handled = true;
                _send(MessageType.TerminalData, new byte[] { 3 });
                AppendLocal("^C\n");
            }
            else if (e.KeyCode == Keys.V && e.Control)
            {
                // 粘贴：发送剪贴板文本
                if (Clipboard.ContainsText())
                {
                    var txt = Clipboard.GetText();
                    _in.AppendText(txt);
                }
            }
        }

        private void SendLine(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            _send(MessageType.TerminalData, bytes);
        }

        // 被控端回传的输出（已解密）
        public void OnOutput(byte[] data, int stream)
        {
            if (data == null || data.Length == 0) return;
            string text;
            try { text = Encoding.UTF8.GetString(data); }
            catch { text = Encoding.Default.GetString(data); }
            BeginInvoke((MethodInvoker)(() =>
            {
                AppendLocal(text);
                // 解析提示符里的当前路径：形如 C:\Users\foo>
                int idx = text.LastIndexOf(">");
                if (idx > 0)
                {
                    int s = text.LastIndexOfAny(new[] { '\n', '\r' }, idx);
                    string line = text.Substring(s + 1, idx - s - 1).Trim();
                    if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^[A-Za-z]:\\"))
                    {
                        _cwd = line.TrimEnd('>');
                        _path.Text = "路径: " + _cwd;
                    }
                }
            }));
        }

        public void OnClosed(int code)
        {
            BeginInvoke((MethodInvoker)(() =>
            {
                _in.Enabled = false;
                string reason = code == 1 ? "（对方拒绝）" : code == 2 ? "（启动失败）" : "（已结束）";
                AppendLocal("\n--- 终端已关闭 " + reason + " ---\n");
                Text = "远程终端 " + reason;
            }));
        }

        private void AppendLocal(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            // 标准化换行：远端 cmd 在 chcp 65001 (UTF-8) 后，部分回显只发 \n 不带 \r，
            // TextBox.AppendText 收到单独 \n 偶发表现为"不换行/挤行"。
            // 把任意 \r\n/\r/\n 统一成 \r\n 后保证多行模式正常换行。
            var s = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            _out.AppendText(s);
            _out.SelectionStart = _out.TextLength;
            _out.ScrollToCaret();
        }
    }
}
