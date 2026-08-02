// Stun.cs - STUN-based public address discovery for P2P TCP hole punching.
//
// When both peers sit behind NAT/frp, the relay server only sees the tunnel
// entry address (not the peer's real public address), so it cannot hand out a
// usable PeerAddr. Each peer instead discovers its OWN public (ip, port) via
// STUN and reports it to the relay (T_PUBCAND); the relay forwards it to the
// other peer, which tries a direct TCP connection (hole punch).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace RemoteControl
{
    public static class StunProbe
    {
        // Public STUN servers (UDP). Multiple for resilience.
        private static readonly string[] Servers = {
            "stun.l.google.com:19302",
            "stun1.l.google.com:19302",
            "stun.qq.com:3478",
        };
        private static List<(string ip, int port)> _cache;
        private static readonly object _lock = new object();

        /// <summary>Discover this machine's public (ip, port) candidates via
        /// STUN. Blocks up to timeoutMs total. Cached after first success.</summary>
        public static List<(string ip, int port)> GetCandidates(int timeoutMs = 3000)
        {
            lock (_lock)
            {
                if (_cache != null) return new List<(string ip, int port)>(_cache);
            }
            var result = new List<(string ip, int port)>();
            int per = Math.Max(800, timeoutMs / Servers.Length);
            foreach (var s in Servers)
            {
                var parts = s.Split(':');
                if (parts.Length != 2) continue;
                if (!int.TryParse(parts[1], out int port)) continue;
                var c = ProbeOne(parts[0], port, per);
                if (c.HasValue && !Contains(result, c.Value)) result.Add(c.Value);
            }
            lock (_lock) { _cache = result; }
            return result;
        }

        /// <summary>Probe STUN candidates (blocking up to 3s) and report them to
        /// the relay as a T_PUBCAND frame (plaintext, so the relay can read it).</summary>
        public static void SendPubCand(Transport t)
        {
            var cands = GetCandidates(3000);
            if (cands == null || cands.Count == 0) return;
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(cands.Count);
            foreach (var c in cands)
            {
                var ib = Encoding.UTF8.GetBytes(c.ip ?? "");
                bw.Write(ib.Length);
                bw.Write(ib);
                bw.Write(c.port);
            }
            t.Send(MessageType.PubCand, ms.ToArray());
        }

        private static bool Contains(List<(string ip, int port)> list, (string ip, int port) c)
            => list.Exists(x => x.ip == c.ip && x.port == c.port);

        private static (string ip, int port)? ProbeOne(string host, int port, int timeoutMs)
        {
            try
            {
                var addrs = Dns.GetHostAddresses(host);
                if (addrs.Length == 0) return null;
                using var udp = new UdpClient(addrs[0].AddressFamily);
                udp.Client.ReceiveTimeout = timeoutMs;
                udp.Client.SendTimeout = timeoutMs;
                byte[] txid = new byte[12];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(txid);
                byte[] req = new byte[20];
                req[0] = 0x00; req[1] = 0x01;          // Binding Request
                req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42; // Magic Cookie
                Array.Copy(txid, 0, req, 8, 12);
                var remote = new IPEndPoint(addrs[0], port);
                udp.Send(req, req.Length, remote);
                IPEndPoint? ep = null;
                byte[] resp = udp.Receive(ref ep);
                return ParseStun(resp, txid);
            }
            catch { return null; }
        }

        private static (string ip, int port)? ParseStun(byte[] resp, byte[] txid)
        {
            if (resp == null || resp.Length < 20) return null;
            int type = (resp[0] << 8) | resp[1];
            if (type != 0x0101) return null;            // Binding Success Response
            for (int i = 0; i < 12; i++) if (resp[8 + i] != txid[i]) return null;
            int msgLen = (resp[2] << 8) | resp[3];
            int off = 20;
            byte[] magic = { 0x21, 0x12, 0xA4, 0x42 };
            while (off + 4 <= resp.Length && off + 4 <= 20 + msgLen)
            {
                int attrType = (resp[off] << 8) | resp[off + 1];
                int attrLen = (resp[off + 2] << 8) | resp[off + 3];
                int pad = ((attrLen + 3) / 4) * 4;
                if (attrType == 0x0020)                 // XOR-MAPPED-ADDRESS
                {
                    if (off + 4 + attrLen > resp.Length) break;
                    byte[] val = new byte[attrLen];
                    Array.Copy(resp, off + 4, val, 0, attrLen);
                    if (val.Length < 8) break;
                    byte family = val[1];
                    if (family == 0x01)                // IPv4
                    {
                        ushort xport = (ushort)((val[2] << 8) | val[3]);
                        ushort rport = (ushort)(xport ^ 0x2112);
                        byte[] xaddr = { val[4], val[5], val[6], val[7] };
                        byte[] addr = new byte[4];
                        for (int i = 0; i < 4; i++) addr[i] = (byte)(xaddr[i] ^ magic[i]);
                        return (new IPAddress(addr).ToString(), rport);
                    }
                }
                off += 4 + pad;
            }
            return null;
        }
    }
}
