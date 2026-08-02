// RcNative.cs - P/Invoke bindings into rc_core.dll (the C++ core).
using System;
using System.Runtime.InteropServices;

namespace RemoteControl
{
    internal static class RcNative
    {
        private const string Dll = "rc_core.dll";

        public const int RC_OK = 0;
        public const int RC_NO_FRAME = 1;
        public const int RC_ERR = -1;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_capture_init(int display_index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_capture_free();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_capture_frame(out IntPtr out_rgba, out int out_w, out int out_h, out ulong out_pts);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_capture_get_bounds(out int left, out int top, out int width, out int height);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_capture_reinit(int display_index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_system_lock();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_system_logoff();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_system_reboot();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_system_shutdown();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_system_sleep();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_system_monitor_off();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_monitor_count();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_encoder_init(int w, int h, int fps, int bitrate_kbps, out IntPtr out_extra, out int out_extra_size);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_encoder_set(int w, int h, int fps, int bitrate_kbps, out IntPtr out_extra, out int out_extra_size);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_encoder_free();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_encoder_encode(IntPtr rgba, int w, int h, out IntPtr out_nal, out int out_nal_size, out int out_key);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_decoder_init(IntPtr extra, int extra_size);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_decoder_free();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_decoder_decode(IntPtr nal, int nal_size, out IntPtr out_rgba, out int out_w, out int out_h);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_input_set_bounds(int left, int top, int width, int height);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_input_mouse_move(int x, int y);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_input_mouse_button(int button, int down);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_input_wheel(int delta);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_input_key(uint vk, int down);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_input_send_cad();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_free(IntPtr p);

        // ---- session recording (MP4 remux, no re-encode) -----------------
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_record_start([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                                                 int w, int h, int fps, byte[] extradata, int extralen);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_record_write(byte[] nal, int len, long pts_ms, int key);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_record_stop();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_record_active();

        // ---- encoder name (hardware vs software) -------------------------
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr rc_encoder_name();
        public static string EncoderName()
        {
            try { var p = rc_encoder_name(); return p == IntPtr.Zero ? "" : (Marshal.PtrToStringAnsi(p) ?? ""); }
            catch { return ""; }
        }

        // ---- audio streaming (WASAPI loopback capture + Opus) ------------
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_audio_cap_start();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_audio_cap_read(out IntPtr out_opus, out int out_size);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_audio_cap_stop();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_audio_play_start();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_audio_play_write(byte[] opus, int size);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_audio_play_stop();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_afree(IntPtr p);
    }
}
