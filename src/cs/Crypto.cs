// Crypto.cs - End-to-end AES-256-GCM for the payloads that travel between
// host and viewer. The relay only ever sees ciphertext for content
// frames; it still routes by the plaintext message TYPE and the relay
// envelope ids, so it stays a dumb, zero-knowledge relay.
//
// Key model: key = PBKDF2-HMAC-SHA256(password, salt="rc-e2e-v1|"+room).
// A room password therefore doubles as the E2E secret. No password => no
// key => frames are sent in the clear (UI shows this clearly). The relay is
// given only hex(sha256(password)) for pairing/auth, which does not reveal
// the PBKDF2 key for a decent password.
using System;
using System.Security.Cryptography;
using System.Text;

namespace RemoteControl
{
    public sealed class Aead
    {
        private const int NonceLen = 12;   // AES-GCM standard nonce
        private const int TagLen   = 16;   // 128-bit auth tag
        private readonly byte[] _key;      // 32 bytes (AES-256)

        public Aead(string password, string room)
        {
            var salt = Encoding.UTF8.GetBytes("rc-e2e-v1|" + (room ?? ""));
            _key = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password ?? ""),
                salt, 100_000, HashAlgorithmName.SHA256, 32);
        }

        /// <summary>Returns an Aead if a password is set, otherwise null (=> plaintext).</summary>
        public static Aead FromPassword(string password, string room)
            => string.IsNullOrEmpty(password) ? null : new Aead(password, room);

        /// <summary>plain -> [nonce(12)][tag(16)][ciphertext].</summary>
        public byte[] Encrypt(byte[] plain)
        {
            plain ??= Array.Empty<byte>();
            var nonce = new byte[NonceLen];
            RandomNumberGenerator.Fill(nonce);
            var cipher = new byte[plain.Length];
            var tag = new byte[TagLen];
            using var gcm = new AesGcm(_key, TagLen);
            gcm.Encrypt(nonce, plain, cipher, tag);

            var outb = new byte[NonceLen + TagLen + cipher.Length];
            Buffer.BlockCopy(nonce, 0, outb, 0, NonceLen);
            Buffer.BlockCopy(tag, 0, outb, NonceLen, TagLen);
            Buffer.BlockCopy(cipher, 0, outb, NonceLen + TagLen, cipher.Length);
            return outb;
        }

        /// <summary>Reverse of Encrypt. Returns null if authentication fails
        /// (wrong password / tampered / not actually encrypted).</summary>
        public byte[] Decrypt(byte[] data)
        {
            if (data == null || data.Length < NonceLen + TagLen) return null;
            try
            {
                var nonce = new byte[NonceLen];
                var tag = new byte[TagLen];
                int clen = data.Length - NonceLen - TagLen;
                var cipher = new byte[clen];
                Buffer.BlockCopy(data, 0, nonce, 0, NonceLen);
                Buffer.BlockCopy(data, NonceLen, tag, 0, TagLen);
                Buffer.BlockCopy(data, NonceLen + TagLen, cipher, 0, clen);
                var plain = new byte[clen];
                using var gcm = new AesGcm(_key, TagLen);
                gcm.Decrypt(nonce, cipher, tag, plain);
                return plain;
            }
            catch { return null; }
        }
    }
}
