// FileTransfer.cs - Chunked, resumable-ish bidirectional file transfer.
//
// The UI/forms own the network pipe (they know how to route a frame to the
// peer or a specific viewer). This class owns the file I/O, progress, and
// cancellation bookkeeping. A transfer id namespaces the two directions.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace RemoteControl
{
    public sealed class FileTransfer
    {
        public enum Direction { Outgoing, Incoming }

        public sealed class Transfer
        {
            public int Id;
            public Direction Dir;
            public string Path;
            public string Name;
            public long Size;
            public long Done;
            public int ViewerId = -1;   // for incoming: who sent it
            public bool Accepted;
            public bool Canceled;
            public FileStream Stream;
            public Action<Transfer> Progress;
            public Action<Transfer, bool /*ok*/> DoneHandler;
        }

        private readonly object _lock = new object();
        private readonly Dictionary<int, Transfer> _transfers = new Dictionary<int, Transfer>();
        private int _seq = new Random().Next(1, 100000);

        public const int ChunkSize = 32 * 1024;

        private int NewId() { lock (_lock) { _seq = (_seq % 1000000) + 1; return unchecked(Environment.TickCount & 0x7fffffff) % 100000 + _seq; } }

        // ---- outgoing (this side is the sender) --------------------------
        public Transfer BeginSend(string filePath, int viewerId = -1)
        {
            var fi = new FileInfo(filePath);
            var t = new Transfer
            {
                Id = NewId(),
                Dir = Direction.Outgoing,
                Path = filePath,
                Name = fi.Name,
                Size = fi.Length,
                Done = 0,
                ViewerId = viewerId,
                Accepted = false,
                Canceled = false,
                Stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize),
            };
            lock (_lock) _transfers[t.Id] = t;
            return t;
        }

        // Returns the next chunk to send, or null when finished/canceled.
        public byte[] SendNext(Transfer t, out int id)
        {
            id = t.Id;
            if (t.Canceled) return null;
            byte[] buf = new byte[ChunkSize];
            int n = t.Stream.Read(buf, 0, buf.Length);
            if (n <= 0)
            {
                t.Stream.Dispose();
                return null;
            }
            if (n < buf.Length) { var last = new byte[n]; Array.Copy(buf, last, n); buf = last; }
            t.Done += n;
            // Let the SENDER's own transfer window show live progress. (The
            // receiver side already fires Progress from ReceiveData -- the
            // sender side needs this so its percentage moves off 0%.)
            t.Progress?.Invoke(t);
            return buf;
        }

        public void EndOutgoing(Transfer t)
        {
            // The sender side finishes here. Report completion (success unless
            // the transfer was canceled) so the sender's window can flip to
            // "done" instead of staying stuck on "transferring".
            try { t.DoneHandler?.Invoke(t, !t.Canceled); } catch { }
            try { t.Stream?.Dispose(); } catch { }
            lock (_lock) _transfers.Remove(t.Id);
        }

        // ---- incoming (this side is the receiver) -------------------------
        // Called when a peer requests to send a file. dir=0 => sender wants the
        // host, dir=1 => viewer. Returns the Transfer (Accepted=false yet).
        // `id` MUST be the sender-provided transfer id (from FOpen) so that
        // subsequent FData/FEnd keyed by that id resolve on this side.
        public Transfer ReceiveOpen(int id, int viewerId, int dir, string name, long size, string saveDir)
        {
            Directory.CreateDirectory(saveDir);
            var t = new Transfer
            {
                Id = id,
                Dir = Direction.Incoming,
                Name = name,
                Path = Path.Combine(saveDir, MakeUnique(saveDir, name)),
                Size = size,
                Done = 0,
                ViewerId = viewerId,
                Accepted = false,
            };
            lock (_lock) _transfers[t.Id] = t;
            return t;
        }

        private static string MakeUnique(string dir, string name)
        {
            string basep = Path.Combine(dir, name);
            if (!File.Exists(basep)) return name;
            string ext = Path.GetExtension(name);
            string stem = string.IsNullOrEmpty(ext) ? name : name.Substring(0, name.Length - ext.Length);
            int i = 1;
            while (File.Exists(Path.Combine(dir, $"{stem} ({i}){ext}"))) i++;
            return $"{stem} ({i}){ext}";
        }

        public bool ReceiveData(int id, byte[] chunk)
        {
            Transfer t;
            lock (_lock) _transfers.TryGetValue(id, out t);
            if (t == null || t.Canceled || !t.Accepted) return false;
            if (t.Stream == null)
                t.Stream = new FileStream(t.Path, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize);
            t.Stream.Write(chunk, 0, chunk.Length);
            t.Done += chunk.Length;
            t.Progress?.Invoke(t);
            return true;
        }

        // Mark an incoming transfer accepted. t.Path must already be the final
        // destination (set by ReceiveOpen or overridden by the caller). We only
        // ensure the parent directory exists -- we do NOT recompute the path,
        // which would discard a user-chosen filename.
        public void Accept(Transfer t)
        {
            t.Accepted = true;
            try
            {
                var d = Path.GetDirectoryName(t.Path);
                if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
            }
            catch { }
        }

        public void ReceiveEnd(int id)
        {
            Transfer t;
            lock (_lock) { if (!_transfers.TryGetValue(id, out t)) return; _transfers.Remove(id); }
            try { t.Stream?.Dispose(); } catch { }
            t.DoneHandler?.Invoke(t, true);
        }

        public void ReceiveCancel(int id)
        {
            Transfer t;
            lock (_lock) { if (!_transfers.TryGetValue(id, out t)) return; _transfers.Remove(id); }
            // Mark canceled so a sender still waiting on this transfer's Accepted
            // flag (outgoing side) stops waiting immediately on a remote deny.
            t.Canceled = true;
            try { t.Stream?.Dispose(); } catch { }
            if (t.Accepted) { try { if (File.Exists(t.Path)) File.Delete(t.Path); } catch { } }
            t.DoneHandler?.Invoke(t, false);
        }

        // Whether a transfer id is still tracked (removed on end/cancel).
        public bool IsTracked(int id)
        {
            lock (_lock) return _transfers.ContainsKey(id);
        }

        public void CancelOutgoing(Transfer t)
        {
            t.Canceled = true;
            try { t.Stream?.Dispose(); } catch { }
            lock (_lock) _transfers.Remove(t.Id);
        }

        public Transfer Find(int id)
        {
            lock (_lock) return _transfers.TryGetValue(id, out var t) ? t : null;
        }
    }
}
