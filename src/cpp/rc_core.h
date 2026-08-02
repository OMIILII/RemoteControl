// rc_core.h - Public C API for the remote-control native core.
//
// Design goals (why this is NOT "send one screenshot per frame"):
//   1. Capture is GPU-based via DXGI Desktop Duplication. AcquireNextFrame()
//      only returns when the desktop actually changed, and we can ask for
//      dirty rectangles. We never GDI-BitBlt the whole screen on a timer.
//   2. Frames are encoded into a continuous H.264 video stream (x264 /
//      libavcodec) and pushed over a long-lived TCP connection. This is the
//      same "streaming" model the big vendors use, not request/response.
//   3. Input events are sent back as small binary messages on the same link.
//
// The C# GUI (WinForms) P/Invokes these functions; the relay only
// relays bytes between the two peers.

#ifndef RC_CORE_H
#define RC_CORE_H

#include <stdint.h>

#ifdef _WIN32
  #ifdef RC_CORE_EXPORTS
    #define RC_API __declspec(dllexport)
  #else
    #define RC_API __declspec(dllimport)
  #endif
#else
  #define RC_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

// Return codes
#define RC_OK        0
#define RC_NO_FRAME  1   // capture timed out / decoder had no output yet
#define RC_ERR      -1

// ---- Screen capture (DXGI Desktop Duplication) -------------------------
// display_index: 0 = primary monitor. Returns RC_OK / RC_ERR.
RC_API int  rc_capture_init(int display_index);
RC_API void rc_capture_free(void);
// Fills *out_rgba with a BGRA buffer (owned by the library, do NOT free).
// Returns RC_OK, RC_NO_FRAME (nothing changed) or RC_ERR.
RC_API int  rc_capture_frame(uint8_t** out_rgba, int* out_w, int* out_h, uint64_t* out_pts);
// Reports the captured monitor's rectangle in virtual-desktop coordinates
// (left/top are non-zero for secondary monitors). Returns RC_OK / RC_ERR.
RC_API int  rc_capture_get_bounds(int* left, int* top, int* width, int* height);
// Switch the shared monitor without tearing down the whole core.
RC_API int  rc_capture_reinit(int display_index);

// ---- H.264 encoder (libavcodec / x264) ---------------------------------
// Allocates encoder for w x h at fps with a target bitrate (kbps).
// *out_extra / *out_extra_size receive the SPS/PPS header (av_malloc'd,
// free with rc_free) that the decoder needs before the first frame.
RC_API int  rc_encoder_init(int w, int h, int fps, int bitrate_kbps,
                            uint8_t** out_extra, int* out_extra_size);
// Hot parameter change (resolution / bitrate) — re-opens the encoder and
// returns the new SPS/PPS so the peer can re-init its decoder.
RC_API int  rc_encoder_set(int w, int h, int fps, int bitrate_kbps,
                           uint8_t** out_extra, int* out_extra_size);
RC_API void rc_encoder_free(void);
// Encodes one BGRA frame. *out_nal receives av_malloc'd encoded bytes
// (free with rc_free). *out_key is 1 for IDR frames.
RC_API int  rc_encoder_encode(const uint8_t* rgba, int w, int h,
                              uint8_t** out_nal, int* out_nal_size, int* out_key);
// Name of the encoder actually chosen (e.g. "h264_nvenc", "libx264"), or ""
// if the encoder is not initialised. Buffer owned by the library.
RC_API const char* rc_encoder_name(void);

// ---- H.264 decoder (libavcodec) ----------------------------------------
// extra/extra_size = SPS/PPS header from the encoder side (may be NULL).
RC_API int  rc_decoder_init(const uint8_t* extra, int extra_size);
RC_API void rc_decoder_free(void);
// Decodes one NAL. *out_rgba is a BGRA buffer owned by the library.
// Returns RC_OK, RC_NO_FRAME or RC_ERR.
RC_API int  rc_decoder_decode(const uint8_t* nal, int nal_size,
                              uint8_t** out_rgba, int* out_w, int* out_h);

// ---- Input injection (host side) ---------------------------------------
// Sets the shared monitor rectangle (virtual-desktop coords) so mouse moves
// map onto the correct monitor. Call after rc_capture_get_bounds.
RC_API void rc_input_set_bounds(int left, int top, int width, int height);
RC_API void rc_input_mouse_move(int x, int y);          // monitor-relative px
RC_API void rc_input_mouse_button(int button, int down);// 0=L 1=R 2=M
RC_API void rc_input_wheel(int delta);                  // wheel delta
RC_API void rc_input_key(uint32_t vk, int down);        // Windows VK_*, down=1/0
RC_API int  rc_input_send_cad(void);                    // Ctrl+Alt+Del (SendSAS)

// ---- Memory ------------------------------------------------------------
RC_API void rc_free(uint8_t* p);

// ---- Host-side system control & helpers -------------------------------
RC_API int  rc_system_lock(void);     // lock the workstation
RC_API int  rc_system_logoff(void);   // log off the current user
RC_API int  rc_system_reboot(void);   // reboot (needs SE_SHUTDOWN privilege)
RC_API int  rc_system_shutdown(void); // shutdown / power off
RC_API int  rc_system_sleep(void);    // suspend (S3 sleep)
RC_API int  rc_system_monitor_off(void); // turn local display(s) off
RC_API int  rc_monitor_count(void);   // number of available displays

// ---- Session recording (viewer side, MP4 remux of the live stream) ----
// Muxes received H.264 NALs into an MP4 without re-encoding.
RC_API int  rc_record_start(const char* path, int w, int h, int fps,
                            const unsigned char* extradata, int extralen);
RC_API int  rc_record_write(const unsigned char* nal, int len,
                            long long pts_ms, int key);
RC_API int  rc_record_stop(void);
RC_API int  rc_record_active(void);

// ---- Audio streaming (WASAPI loopback capture + Opus) -----------------
// Host: capture whatever plays on the default speakers, Opus-encode 20 ms
// frames. Fixed format: 48 kHz stereo. Servicing runs on an internal thread.
RC_API int  rc_audio_cap_start(void);
// Pops one encoded Opus packet into *out_opus (malloc'd, free with rc_afree).
// Returns RC_OK, RC_NO_FRAME (queue empty) or RC_ERR.
RC_API int  rc_audio_cap_read(uint8_t** out_opus, int* out_size);
RC_API void rc_audio_cap_stop(void);
// Viewer: decode Opus + render to the local default speakers.
RC_API int  rc_audio_play_start(void);
RC_API int  rc_audio_play_write(const uint8_t* opus, int size);
RC_API void rc_audio_play_stop(void);
// Frees a buffer returned by rc_audio_cap_read (uses plain malloc/free).
RC_API void rc_afree(uint8_t* p);

// ---- Memory Module (Phase 9: zzrat-inspired MemoryModulePP) -----------
// Load a PE DLL from a raw byte buffer into memory without writing to disk.
RC_API void* rc_memload_library(const uint8_t* data, int size);
RC_API void* rc_memload_getproc(void* mod, const char* name);
RC_API void  rc_memload_free(void* mod);

#ifdef __cplusplus
}
#endif
#endif // RC_CORE_H
