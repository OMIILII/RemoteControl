// FileBrowserForm.cs - Phase 2 远程文件浏览器（控制端侧）。
//
// 浏览被控端(host)文件系统：目录树 + 文件列表 + 传输队列。
// 所有被控端操作通过构造时传入的 send 回调发往 HostForm.DispatchFromHost 的
// FS 消息分支；被控端回包由 ViewerForm 转发到这里 Handle()。
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RemoteControl
{
    public sealed class FileBrowserForm : Form
    {
        private readonly Action<MessageType, byte[]> _send;

        private TreeView _tree;
        private ListView _list;
        private TextBox _pathBox;
        private ListView _queue;
        private Label _hint;

        private string _curPath = "";                 // 当前浏览目录（""=盘符根）
        private readonly Dictionary<string, TreeNode> _pendingTree = new();  // path -> 等待展开的节点
        private readonly Dictionary<int, FsJob> _jobs = new();
        private int _jobSeq = 1;
        private readonly object _jobLock = new object();

        private sealed class FsJob
        {
            public int Id;
            public string Name = "";
            public string RemotePath = "";
            public string LocalPath = "";
            public long Total;
            public long Done;
            public bool IsUpload;
            public bool Active;
            public bool Canceled = false;
            public FileStream Stream;
            public ListViewItem Item;
            // Phase 7F: 速度追踪（借鉴 zzrat offset 思路）
            public DateTime Started = DateTime.UtcNow;
            public DateTime LastSample = DateTime.UtcNow;
            public long LastDone;
            public double SpeedBps;  // bytes/s，每 1s 更新一次
        }

        public FileBrowserForm(Action<MessageType, byte[]> send)
        {
            _send = send;
            InitUI();
        }

        private void InitUI()
        {
            Text = "远程文件管理器";
            Width = 920; Height = 640;
            StartPosition = FormStartPosition.CenterParent;

            var tool = new ToolStrip
            {
                Dock = DockStyle.Top,
                GripStyle = ToolStripGripStyle.Hidden,
            };
            var up = new ToolStripButton("⬆ 上级") { Enabled = false };
            var refresh = new ToolStripButton("⟳ 刷新");
            var mkdir = new ToolStripButton("📂 新建文件夹");
            var upload = new ToolStripButton("⬆ 上传");
            var download = new ToolStripButton("⬇ 下载");
            var del = new ToolStripButton("🗑 删除");
            var rename = new ToolStripButton("✎ 重命名");
            tool.Items.AddRange(new ToolStripItem[] { up, refresh, mkdir, upload, download, del, rename });

            _pathBox = new TextBox { Dock = DockStyle.Top, ReadOnly = true, Text = "此电脑" };

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 240,
            };

            _tree = new TreeView
            {
                Dock = DockStyle.Fill,
                ShowPlusMinus = true,
                ShowLines = true,
                LabelEdit = false,
            };
            var root = new TreeNode("此电脑") { Tag = null, ImageKey = "root" };
            _tree.Nodes.Add(root);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                Columns =
                {
                    new ColumnHeader { Text = "名称", Width = 240 },
                    new ColumnHeader { Text = "大小", Width = 100 },
                    new ColumnHeader { Text = "修改时间", Width = 150 },
                    new ColumnHeader { Text = "类型", Width = 80 },
                },
            };

            split.Panel1.Controls.Add(_tree);
            split.Panel2.Controls.Add(_list);

            // 底部传输队列
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 160 };
            var qlbl = new Label { Text = "传输队列", Dock = DockStyle.Top, Height = 20 };
            _queue = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                Columns =
                {
                    new ColumnHeader { Text = "名称", Width = 240 },
                    new ColumnHeader { Text = "方向", Width = 60 },
                    new ColumnHeader { Text = "进度", Width = 200 },
                    new ColumnHeader { Text = "状态", Width = 120 },
                },
            };
            bottom.Controls.Add(_queue);
            bottom.Controls.Add(qlbl);

            _hint = new Label { Dock = DockStyle.Bottom, Height = 20, Text = "双击文件夹进入；双击文件下载；右键可下载/删除/重命名。" };

            Controls.Add(bottom);
            Controls.Add(split);
            Controls.Add(_pathBox);
            Controls.Add(tool);
            Controls.Add(_hint);

            // ---- 事件 ----
            up.Click += (s, e) => NavigateUp();
            refresh.Click += (s, e) => Navigate(_curPath);
            mkdir.Click += (s, e) => DoMkdir();
            upload.Click += (s, e) => DoUpload();
            download.Click += (s, e) => DownloadSelected();
            del.Click += (s, e) => DeleteSelected();
            rename.Click += (s, e) => RenameSelected();

            _tree.BeforeExpand += (s, e) =>
            {
                var node = e.Node;
                if (node == root) { if (node.Nodes.Count == 0) RequestTree(node, ""); return; }
                string p = node.Tag as string;
                if (!string.IsNullOrEmpty(p) && node.Nodes.Count == 0) RequestTree(node, p);
            };
            _tree.AfterSelect += (s, e) =>
            {
                var node = e.Node;
                string p = node.Tag as string;
                if (node == root) Navigate("");
                else if (!string.IsNullOrEmpty(p)) Navigate(p);
            };

            _list.DoubleClick += (s, e) =>
            {
                var it = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0] : null;
                if (it == null) return;
                bool isDir = (bool)it.Tag;
                if (isDir) Navigate(Path.Combine(_curPath == "" ? "" : _curPath, it.Text));
                else DownloadFile(it.Text);
            };
            _list.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    var hit = _list.HitTest(e.X, e.Y);
                    if (hit.Item != null) { _list.SelectedIndices.Clear(); hit.Item.Selected = true; ShowListMenu(hit.Item); }
                }
            };

            root.Expand();
        }

        // ---- 导航 ----
        public void Navigate(string path)
        {
            _curPath = path ?? "";
            _pathBox.Text = string.IsNullOrEmpty(_curPath) ? "此电脑" : _curPath;
            _list.Items.Clear();
            _list.Items.Add(new ListViewItem("（加载中…）") { Tag = false });
            _send(MessageType.FsList, Codec.BuildPath(_curPath));
        }

        private void NavigateUp()
        {
            if (string.IsNullOrEmpty(_curPath)) return;
            var di = Path.GetDirectoryName(_curPath);
            Navigate(di ?? "");   // 盘符根的上一级 = 盘符根本身（GetDirectoryName("C:\")==null -> 回根）
        }

        private void RequestTree(TreeNode node, string path)
        {
            _pendingTree[path ?? "\0"] = node;
            if (node.Nodes.Count == 0) node.Nodes.Add(new TreeNode("（加载中…）") { Tag = "__loading" });
            _send(MessageType.FsList, Codec.BuildPath(path));
        }

        // ---- 被控端回包 ----
        public void HandleFs(MessageType t, byte[] p)
        {
            if (InvokeRequired) { BeginInvoke((MethodInvoker)(() => HandleFs(t, p))); return; }
            switch (t)
            {
                case MessageType.FsListResp:
                    OnList(p); break;
                case MessageType.FsGetReady:
                    OnGetReady(p); break;
                case MessageType.FsChunk:
                    OnChunk(p); break;
                case MessageType.FsGetEnd:
                    OnGetEnd(p); break;
                case MessageType.FsGetErr:
                    OnGetErr(p); break;
                case MessageType.FsPutReady:
                    OnPutReady(p); break;
                case MessageType.FsPutAck:
                    OnPutAck(p); break;
                case MessageType.FsDeleteResp:
                    OnSimpleResp(p, "删除"); break;
                case MessageType.FsRenameResp:
                    OnSimpleResp(p, "重命名"); break;
                case MessageType.FsMkdirResp:
                    OnSimpleResp(p, "新建文件夹"); break;
            }
        }

        private void OnList(byte[] p)
        {
            Codec.ParseFsListResp(p, out string path, out int err, out var items);
            if (err != 0)
            {
                string msg = err switch { 1 => "路径不存在", 2 => "无权限访问", 3 => "读取异常", _ => "错误码 " + err };
                _hint.Text = "列目录失败：" + msg;
                if (_curPath == path) { _list.Items.Clear(); _list.Items.Add(new ListViewItem("（无法访问：" + msg + "）") { Tag = false }); }
                return;
            }
            // 填充文件列表（若响应的是当前目录）
            if (_curPath == path)
            {
                _list.Items.Clear();
                foreach (var e in items)
                    _list.Items.Add(MakeItem(e));
            }
            // 填充目录树（若这是某节点等待的展开结果）
            string key = string.IsNullOrEmpty(path) ? "\0" : path;
            if (_pendingTree.TryGetValue(key, out var node))
            {
                _pendingTree.Remove(key);
                node.Nodes.Clear();
                foreach (var e in items)
                {
                    if (!e.IsDir) continue;
                    var child = new TreeNode(e.Name) { Tag = path == "" ? e.Name : Path.Combine(path, e.Name) };
                    child.Nodes.Add(new TreeNode("（加载中…）") { Tag = "__loading" });
                    node.Nodes.Add(child);
                }
            }
        }

        private ListViewItem MakeItem(FsEntry e)
        {
            var it = new ListViewItem(e.Name) { Tag = e.IsDir };
            it.SubItems.Add(e.IsDir ? "" : FormatSize(e.Size));
            it.SubItems.Add(e.IsDir ? "" : FormatTime(e.Mtime));
            it.SubItems.Add(e.IsDir ? "文件夹" : (string.IsNullOrEmpty(Path.GetExtension(e.Name)) ? "文件" : Path.GetExtension(e.Name).ToUpper() + " 文件"));
            return it;
        }

        // ---- 下载 ----
        private void DownloadSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            foreach (ListViewItem it in _list.SelectedItems)
                if (!(bool)it.Tag) DownloadFile(it.Text);
        }

        private void DownloadFile(string name)
        {
            if (string.IsNullOrEmpty(_curPath)) { MessageBox.Show(this, "请先进入一个盘符/文件夹再下载。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using var dlg = new FolderBrowserDialog { Description = "选择本机保存位置" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string remote = Path.Combine(_curPath, name);
            string local = Path.Combine(dlg.SelectedPath, name);
            // Phase 7G: 检查本地是否已有部分文件（断点续传）
            long resumeOff = 0;
            try { if (File.Exists(local)) resumeOff = new FileInfo(local).Length; } catch { }
            int id = NewId();
            var job = new FsJob
            {
                Id = id,
                Name = name,
                RemotePath = remote,
                LocalPath = local,
                IsUpload = false,
                Done = resumeOff,
                Item = AddQueueItem(name, "下载", resumeOff, 0, "请求中" + (resumeOff > 0 ? " (续传)" : "")),
            };
            lock (_jobLock) _jobs[id] = job;
            _send(MessageType.FsGet, Codec.BuildFsGet(id, remote, resumeOff));
        }

        private void OnGetReady(byte[] p)
        {
            Codec.ParseFsGetReady(p, out int id, out int code, out long size, out string name);
            FsJob job; lock (_jobLock) { if (!_jobs.TryGetValue(id, out job)) return; }
            if (code != 0) { job.Item.SubItems[3].Text = "被拒绝：" + name; job.Item.SubItems[2].Text = ""; return; }
            try
            {
                job.Total = size;
                // Phase 7G: 已有部分文件时用追加模式续传
                bool resume = job.Done > 0 && job.Done < size;
                var mode = resume ? FileMode.Append : FileMode.Create;
                job.Stream = new FileStream(job.LocalPath, mode, FileAccess.Write, FileShare.None, 1 << 18, FileOptions.SequentialScan);
                if (resume) job.Done = job.Stream.Length;  // 确保和实际长度对齐
                job.Active = true;
                job.Item.SubItems[3].Text = resume ? "续传中" : "传输中";
                UpdateProgress(job);
            }
            catch (Exception ex) { job.Item.SubItems[3].Text = "本地写入失败：" + ex.Message; }
        }

        private void OnChunk(byte[] p)
        {
            Codec.ParseFData(p, out int id, out var chunk);
            FsJob job; lock (_jobLock) { if (!_jobs.TryGetValue(id, out job) || !job.Active) return; }
            try { job.Stream.Write(chunk, 0, chunk.Length); job.Done += chunk.Length; UpdateProgress(job); }
            catch (Exception ex) { job.Item.SubItems[3].Text = "写入失败：" + ex.Message; }
        }

        private void OnGetEnd(byte[] p)
        {
            int id = Codec.ParseId(p);
            FsJob job; lock (_jobLock) { if (!_jobs.TryGetValue(id, out job)) return; }
            try { job.Stream?.Dispose(); } catch { }
            job.Item.SubItems[3].Text = "完成";
            job.Item.SubItems[2].Text = "100%";
        }

        private void OnGetErr(byte[] p)
        {
            Codec.ParseFsGetErr(p, out int id, out int code, out string msg);
            FsJob job; lock (_jobLock) { if (!_jobs.TryGetValue(id, out job)) return; }
            try { job.Stream?.Dispose(); } catch { }
            job.Item.SubItems[3].Text = "失败：" + msg;
        }

        // ---- 上传 ----
        private void DoUpload()
        {
            if (string.IsNullOrEmpty(_curPath)) { MessageBox.Show(this, "请先进入一个盘符/文件夹再上传。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using var dlg = new OpenFileDialog { Multiselect = true, Title = "选择要上传的文件" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            foreach (var f in dlg.FileNames) StartUpload(f);
        }

        private void StartUpload(string localPath)
        {
            string name = Path.GetFileName(localPath);
            string remote = Path.Combine(_curPath, name);
            int id = NewId();
            var job = new FsJob
            {
                Id = id,
                Name = name,
                RemotePath = remote,
                LocalPath = localPath,
                IsUpload = true,
                Total = new FileInfo(localPath).Length,
                Item = AddQueueItem(name, "上传", 0, 0, "请求中"),
            };
            lock (_jobLock) _jobs[id] = job;
            _send(MessageType.FsPut, Codec.BuildFsPut(id, remote, job.Total));
        }

        private void OnPutReady(byte[] p)
        {
            Codec.ParseFsPutReady(p, out int id, out int code, out string msg);
            FsJob job; lock (_jobLock) { if (!_jobs.TryGetValue(id, out job)) return; }
            if (code != 0) { job.Item.SubItems[3].Text = "被拒绝：" + msg; return; }
            job.Active = true;
            job.Item.SubItems[3].Text = "传输中";
            UpdateProgress(job);
            // 后台读取本地文件并分块发送
            _ = Task.Run(() => UploadPump(job));
        }

        private void UploadPump(FsJob job)
        {
            try
            {
                using var fs = new FileStream(job.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 18, FileOptions.SequentialScan);
                job.Stream = fs;
                var buf = new byte[1 << 18];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    if (job.Canceled) break;
                    var chunk = new byte[n];
                    Array.Copy(buf, chunk, n);
                    _send(MessageType.FsChunk, Codec.BuildFData(job.Id, chunk));
                    job.Done += n;
                    BeginInvoke((MethodInvoker)(() => UpdateProgress(job)));
                }
                if (!job.Canceled) _send(MessageType.FsPutEnd, Codec.BuildId(job.Id));
            }
            catch (Exception ex)
            {
                BeginInvoke((MethodInvoker)(() => { job.Item.SubItems[3].Text = "失败：" + ex.Message; }));
            }
        }

        private void OnPutAck(byte[] p)
        {
            Codec.ParseFsPutAck(p, out int id, out int code, out string msg);
            FsJob job; lock (_jobLock) { if (!_jobs.TryGetValue(id, out job)) return; }
            job.Item.SubItems[3].Text = code == 0 ? "完成" : ("失败：" + msg);
            job.Item.SubItems[2].Text = code == 0 ? "100%" : job.Item.SubItems[2].Text;
        }

        // ---- 删除 / 重命名 / 新建文件夹 ----
        private void DeleteSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            if (MessageBox.Show(this, "确认删除选中的 " + _list.SelectedItems.Count + " 项？此操作在被控端执行且不可恢复。", "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            foreach (ListViewItem it in _list.SelectedItems)
            {
                string p = Path.Combine(_curPath == "" ? "" : _curPath, it.Text);
                _send(MessageType.FsDelete, Codec.BuildPath(p));
            }
            RefreshSoon();
        }

        private void RenameSelected()
        {
            if (_list.SelectedItems.Count != 1) return;
            var it = _list.SelectedItems[0];
            string oldName = it.Text;
            string input = PromptInput("重命名", "输入新名称：", oldName);
            if (string.IsNullOrWhiteSpace(input) || input == oldName) return;
            string oldP = Path.Combine(_curPath == "" ? "" : _curPath, oldName);
            string newP = Path.Combine(_curPath == "" ? "" : _curPath, input);
            _send(MessageType.FsRename, Codec.BuildFsRename(oldP, newP));
            RefreshSoon();
        }

        private void DoMkdir()
        {
            if (string.IsNullOrEmpty(_curPath)) { MessageBox.Show(this, "请先进入一个盘符/文件夹。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            string name = PromptInput("新建文件夹", "输入文件夹名称：", "新建文件夹");
            if (string.IsNullOrWhiteSpace(name)) return;
            string p = Path.Combine(_curPath, name);
            _send(MessageType.FsMkdir, Codec.BuildPath(p));
            RefreshSoon();
        }

        private void OnSimpleResp(byte[] p, string op)
        {
            Codec.ParseResult(p, out int code, out string msg);
            if (code == 0) _hint.Text = op + "成功。";
            else _hint.Text = op + "失败：" + msg;
            if (code == 0) RefreshSoon();
        }

        // ---- 杂项 ----
        private int NewId() { lock (_jobLock) return _jobSeq++; }

        private ListViewItem AddQueueItem(string name, string dir, long done, long total, string status)
        {
            var it = new ListViewItem(name) { Tag = null };
            it.SubItems.Add(dir);
            it.SubItems.Add(FormatProgress(done, total));
            it.SubItems.Add(status);
            _queue.Items.Add(it);
            return it;
        }

        private void UpdateProgress(FsJob job)
        {
            if (job.Item == null) return;
            // Phase 7F: 每 ~1s 采样一次速度
            var now = DateTime.UtcNow;
            double elapsed = (now - job.LastSample).TotalSeconds;
            if (elapsed >= 0.9)
            {
                long delta = job.Done - job.LastDone;
                job.SpeedBps = delta / Math.Max(elapsed, 0.01);
                job.LastSample = now;
                job.LastDone = job.Done;
            }
            job.Item.SubItems[2].Text = FormatProgress(job.Done, job.Total, job.SpeedBps);
        }

        private void RefreshSoon()
        {
            // 给被控端一点时间落盘，再刷新当前目录与树
            var t = new Timer { Interval = 600 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); Navigate(_curPath); };
            t.Start();
        }

        private void ShowListMenu(ListViewItem it)
        {
            bool isDir = (bool)it.Tag;
            var m = new ContextMenuStrip();
            if (!isDir) m.Items.Add("下载", null, (s, e) => DownloadFile(it.Text));
            m.Items.Add("删除", null, (s, e) => { _list.SelectedIndices.Clear(); it.Selected = true; DeleteSelected(); });
            m.Items.Add("重命名", null, (s, e) => { _list.SelectedIndices.Clear(); it.Selected = true; RenameSelected(); });
            m.Show(Cursor.Position);
        }

        private string PromptInput(string title, string prompt, string def)
        {
            var box = new Form { Text = title, Width = 360, Height = 150, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
            var lbl = new Label { Text = prompt, Left = 16, Top = 16, AutoSize = true };
            var tb = new TextBox { Left = 16, Top = 44, Width = 312, Text = def };
            var ok = new Button { Text = "确定", Left = 168, Top = 84, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", Left = 252, Top = 84, DialogResult = DialogResult.Cancel };
            box.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
            box.AcceptButton = ok; box.CancelButton = cancel;
            return box.ShowDialog(this) == DialogResult.OK ? tb.Text.Trim() : "";
        }

        private static string FormatSize(long b)
        {
            if (b < 1024) return b + " B";
            double v = b;
            string[] u = { "KB", "MB", "GB", "TB" };
            int i = -1;
            do { v /= 1024; i++; } while (v >= 1024 && i < u.Length - 1);
            return v.ToString("0.##") + " " + u[i];
        }
        private static string FormatProgress(long done, long total, double speedBps = 0)
        {
            if (total <= 0) return "—";
            int pct = (int)(done * 100 / total);
            string s = pct + "%  (" + FormatSize(done) + "/" + FormatSize(total) + ")";
            // Phase 7F: 速度 + 预估剩余时间
            if (speedBps > 0 && done < total)
            {
                long remaining = total - done;
                int etaSec = (int)(remaining / speedBps);
                string speedStr = speedBps >= 1_000_000 ? (speedBps / 1_000_000).ToString("F1") + " MB/s"
                    : speedBps >= 1_000 ? (speedBps / 1_000).ToString("F0") + " KB/s"
                    : ((int)speedBps) + " B/s";
                string etaStr = etaSec < 60 ? etaSec + "s" : (etaSec / 60) + "m" + (etaSec % 60) + "s";
                return s + "  " + speedStr + "  ETA " + etaStr;
            }
            return s;
        }
        private static string FormatTime(long ft)
        {
            try { return DateTime.FromFileTimeUtc(ft).ToLocalTime().ToString("yyyy-MM-dd HH:mm"); }
            catch { return ""; }
        }
    }
}
