// LargeNumber.cs - zzrat-style large number counter for keep-alive Ping/Pong.
// Increments a byte array as a big-endian unsigned integer.
// Used to generate unique tokens that must be echoed back by the peer.
using System;

namespace RemoteControl
{
    public static class LargeNumber
    {
        /// <summary>Increment the byte array in place (big-endian, wraps at overflow).</summary>
        public static void Increment(byte[] num)
        {
            if (num == null || num.Length == 0) return;
            for (int i = num.Length - 1; i >= 0; i--)
            {
                if (++num[i] != 0) break;
            }
        }

        /// <summary>Create a counter with the given byte size, initialized to zero.</summary>
        public static byte[] Create(int byteCount = 8)
        {
            return new byte[byteCount];
        }

        /// <summary>Convert counter to hex string for logging.</summary>
        public static string ToHex(byte[] num)
        {
            if (num == null) return "";
            return BitConverter.ToString(num).Replace("-", "");
        }
    }
}
