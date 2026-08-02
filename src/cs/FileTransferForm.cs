// FileTransferForm.cs - shared UI listing active transfers with progress
// bars and cancel buttons. The host/viewer form does the actual networking:
// this window only reflects state and lets the user cancel.
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace RemoteControl
{
    public sealed class FileTransferForm : Form
    {
        private readonly ListView _list;
        private readonly Dictionary<int, ListViewItem> _items = new Dictionary<int, ListViewItem>();
        private readonly object _sync = new object();

        public FileTransferForm()
        {
            Text = "文件传输";
            Width = 520; Height = 320; StartPosition = FormStartPosition.CenterParent;
            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                Columns =
                {
                    new ColumnHeader { Text = "名称", Width = 160 },
                    new ColumnHeader { Text = "方向", Width = 70 },
                    new ColumnHeader { Text = "进度", Width = 120 },
                    new ColumnHeader { Text = "状态", Width = 120 },
                },
            };
            Controls.Add(_list);
        }

        public void Add(FileTransfer.Transfer t)
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke((MethodInvoker)(() =>
                {
                    var it = new ListViewItem(new[] {
                        t.Name,
                        t.Dir == FileTransfer.Direction.Outgoing ? "发送" : "接收",
                        "0%",
                        t.Accepted ? "传输中" : (t.Canceled ? "已取消" : "等待接受"),
                    }) { Tag = t.Id };
                    _list.Items.Add(it);
                    lock (_sync) _items[t.Id] = it;
                    t.Progress = OnProgress;
                    t.DoneHandler += OnDone;
                }));
            }
            catch { }
        }

        private void OnProgress(FileTransfer.Transfer t)
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke((MethodInvoker)(() =>
                {
                    if (_items.TryGetValue(t.Id, out var it))
                    {
                        int pct = t.Size > 0 ? (int)(t.Done * 100 / t.Size) : 0;
                        it.SubItems[2].Text = pct + "%";
                        it.SubItems[3].Text = "传输中";
                    }
                }));
            }
            catch { }
        }

        private void OnDone(FileTransfer.Transfer t, bool ok)
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke((MethodInvoker)(() =>
                {
                    if (_items.TryGetValue(t.Id, out var it))
                    {
                        int pct = t.Size > 0 ? (int)(t.Done * 100 / t.Size) : 100;
                        it.SubItems[2].Text = pct + "%";
                        it.SubItems[3].Text = ok ? "完成" : (t.Canceled ? "已取消" : "已拒绝");
                    }
                }));
            }
            catch { }
        }
    }
}
