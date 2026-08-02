// NativeMem.cs - MemoryModulePP P/Invoke wrapper.
// Load a native DLL from a byte[] buffer without writing to disk.
// Uses rc_core's rc_memload_* exports (Phase 9 zzrat-inspired).
using System;
using System.Runtime.InteropServices;

namespace RemoteControl
{
    public static class NativeMem
    {
        [DllImport("rc_core.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr rc_memload_library(byte[] data, int size);

        [DllImport("rc_core.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr rc_memload_getproc(IntPtr mod, string name);

        [DllImport("rc_core.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void rc_memload_free(IntPtr mod);

        /// <summary>Load a native DLL from a byte buffer. Returns a module handle, or IntPtr.Zero on failure.</summary>
        public static IntPtr Load(byte[] data)
        {
            if (data == null || data.Length == 0) return IntPtr.Zero;
            return rc_memload_library(data, data.Length);
        }

        /// <summary>Get a function pointer from a memory-loaded module.</summary>
        public static IntPtr GetProc(IntPtr mod, string name)
        {
            if (mod == IntPtr.Zero || string.IsNullOrEmpty(name)) return IntPtr.Zero;
            return rc_memload_getproc(mod, name);
        }

        /// <summary>Free a memory-loaded module.</summary>
        public static void Free(IntPtr mod)
        {
            if (mod != IntPtr.Zero) rc_memload_free(mod);
        }
    }
}
