// encoder.cpp - H.264 encoding with hardware acceleration + software fallback.
//
// Why this exists / how it differs from a naive encoder:
//   We try GPU encoders first (NVIDIA NVENC -> Intel QSV -> AMD AMF ->
//   Windows Media Foundation) and only fall back to software x264 if none
//   open. Every candidate is configured for ULTRA-LOW LATENCY: no B-frames,
//   short GOP, CBR-ish rate control, and vendor "low delay / ultra low
//   latency" tuning. This keeps CPU usage low on machines with a GPU while
//   still working everywhere.
//
// BGRA frames in -> Annex-B/AVCC NAL packets out, streamed to the viewer.

#include "rc_core.h"
#include <new>
#include <cstring>  // memcpy, strncpy
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/opt.h>
#include <libswscale/swscale.h>
}

namespace {

struct EncCtx {
    AVCodecContext* c    = nullptr;
    AVFrame*        frame= nullptr;
    SwsContext*     sws  = nullptr;   // BGRA(enc dims) -> YUV(enc dims)
    SwsContext*     sws_in = nullptr; // BGRA(src dims) -> BGRA(enc dims) for dynamic res
    uint8_t*        scaled = nullptr; // temp BGRA buffer at enc dims
    int             scaled_cap = 0;
    int             src_w = 0, src_h = 0; // last seen source dims (for sws_in rebuild)
    int64_t         pts  = 0;
    int             w = 0, h = 0;
    AVPixelFormat   pix  = AV_PIX_FMT_YUV420P;
    char            name[64] = {0};   // human-readable chosen encoder name
};

EncCtx e;

// Per-encoder low-latency tuning. Returns 0 on success (context configured).
void apply_common(AVCodecContext* c, int w, int h, int fps, int bitrate_kbps,
                  AVPixelFormat pix) {
    c->width       = w;
    c->height      = h;
    c->pix_fmt     = pix;
    c->time_base   = AVRational{ 1, fps > 0 ? fps : 30 };
    c->framerate   = AVRational{ fps > 0 ? fps : 30, 1 };
    c->bit_rate    = (int64_t)bitrate_kbps * 1000;
    c->rc_max_rate = (int64_t)bitrate_kbps * 1000;
    // 极低延迟：VBV 缓冲只留 ~3 帧（原来注释写"low latency"其实是 1 秒缓冲，
    // 那是高延迟——场景一切换编码器就把大帧憋在缓冲里，画面滞后近 1 秒）。
    // 交互式远控要"所见即所得"，所以缓冲压到几帧，关键帧直接发出去。
    int frameBits = (fps > 0 ? bitrate_kbps * 1000 / fps : bitrate_kbps * 1000 / 30);
    int vbv = frameBits * 3;
    if (vbv < 32000) vbv = 32000;           // 极小码率给个下限，免得 rate control 失稳
    c->rc_buffer_size = vbv;
    c->rc_initial_buffer_occupancy = vbv / 2;
    // 底层是 TCP（中继、P2P 都是可靠有序、零丢包），根本不需要用周期性大 IDR
    // 来做"丢包恢复"——那样每 ~1s 就整屏刷新一次（整屏重绘 + 那一帧最糊）。
    // 改用 intra-refresh（滚动关键帧）：I 块平摊到 GOP 内的每一帧，逐步刷新全部
    // 宏块，既消除了周期性整屏重绘，又保留错误自愈能力；GOP 拉到 ~8s 仅作为
    // 刷新周期与兜底。新控制端入会仍由 RebuildEncoder 强制打一个真 IDR 来同步。
    c->gop_size    = 250;   // ~8s 刷新周期（intra-refresh 平摊 I 块，不炸整屏）
    c->max_b_frames= 0;     // no B-frames -> minimal latency
    c->thread_count= 1;
    c->flags      |= AV_CODEC_FLAG_GLOBAL_HEADER; // SPS/PPS -> extradata
}

// Configure vendor-specific private options for lowest latency.
void apply_priv(AVCodecContext* c, const char* enc_name) {
    void* p = c->priv_data;
    if (!p) return;
    if (!strcmp(enc_name, "h264_nvenc")) {
        av_opt_set(p, "preset", "p1", 0);          // fastest
        av_opt_set(p, "tune",   "ull", 0);         // ultra low latency
        av_opt_set(p, "rc",     "cbr", 0);
        av_opt_set_int(p, "delay", 0, 0);
        av_opt_set_int(p, "zerolatency", 1, 0);
        av_opt_set_int(p, "b_ref_mode", 0, 0);
        // TCP 零丢包：不需要每秒一个大 IDR。GOP 拉到 250；intra-refresh 平摊 I 块
        // （不支持则静默忽略），消除周期性整屏重绘。
        av_opt_set_int(p, "gop", 250, 0);
        av_opt_set_int(p, "no-scenecut", 1, 0);
        av_opt_set_int(p, "intra-refresh", 1, 0);
    } else if (!strcmp(enc_name, "h264_qsv")) {
        av_opt_set(p, "preset", "veryfast", 0);
        av_opt_set_int(p, "async_depth", 1, 0);
        av_opt_set_int(p, "low_delay_brc", 1, 0);
        av_opt_set_int(p, "gop", 250, 0);
    } else if (!strcmp(enc_name, "h264_amf")) {
        av_opt_set(p, "usage",   "ultralowlatency", 0);
        av_opt_set(p, "quality", "speed", 0);
        av_opt_set_int(p, "gops_per_idr", 1, 0);
        av_opt_set(p, "rc",      "cbr", 0);
        av_opt_set_int(p, "gop", 250, 0);
    } else if (!strcmp(enc_name, "h264_mf")) {
        av_opt_set_int(p, "hw_encoding", 1, 0);
    } else if (!strcmp(enc_name, "libx264")) {
        av_opt_set(p, "preset", "ultrafast", 0);
        av_opt_set(p, "tune",   "zerolatency", 0);
        // 滚动关键帧（intra-refresh）：不输出周期性大 IDR，I 块平摊到 GOP 内每帧，
        // 彻底消除"每几秒整屏重绘一次 + 那一帧最糊"的现象；TCP 零丢包下无需用
        // 大 IDR 做丢包恢复。keyint/min-keyint=250 仅定义刷新周期（~8s 滚完一轮）。
        av_opt_set_int(p, "keyint", 250, 0);
        av_opt_set_int(p, "min-keyint", 250, 0);
        av_opt_set_int(p, "scenecut", 0, 0);
        av_opt_set_int(p, "intra-refresh", 1, 0);
        // 稍微收紧 rate control，减小 CBR 模式的瞬时溢出。
        av_opt_set(p, "rc-lookahead", "0", 0);
    }
}

// Try to build+open one encoder by name. On success fills e.* and returns true.
bool try_open(const char* enc_name, int w, int h, int fps, int bitrate_kbps,
              AVPixelFormat pix) {
    const AVCodec* codec = avcodec_find_encoder_by_name(enc_name);
    if (!codec) return false;

    AVCodecContext* c = avcodec_alloc_context3(codec);
    if (!c) return false;

    apply_common(c, w, h, fps, bitrate_kbps, pix);
    apply_priv(c, enc_name);

    if (avcodec_open2(c, codec, nullptr) < 0) {
        avcodec_free_context(&c);
        return false;
    }

    e.c   = c;
    e.pix = pix;
    strncpy(e.name, enc_name, sizeof(e.name) - 1);
    return true;
}

} // namespace

extern "C" {

// Shared open routine used by both rc_encoder_init (first open) and
// rc_encoder_set (hot parameter change). Frees any prior per-instance
// resources, picks an encoder, allocates the frame + BGRA->YUV scaler at
// (w,h), and returns the new SPS/PPS in *out_extra.
static int enc_open(int w, int h, int fps, int bitrate_kbps,
                    uint8_t** out_extra, int* out_extra_size) {
    if (e.frame)  { av_frame_free(&e.frame); e.frame = nullptr; }
    if (e.sws)    { sws_freeContext(e.sws); e.sws = nullptr; }
    if (e.sws_in) { sws_freeContext(e.sws_in); e.sws_in = nullptr; }
    if (e.scaled) { av_free(e.scaled); e.scaled = nullptr; e.scaled_cap = 0; }
    if (e.c)      { avcodec_free_context(&e.c); e.c = nullptr; }
    e.src_w = e.src_h = 0;

    // Hardware encoders prefer NV12; x264 uses YUV420P.
    struct Cand { const char* name; AVPixelFormat pix; };
    const Cand cands[] = {
        { "h264_nvenc", AV_PIX_FMT_NV12    },  // NVIDIA
        { "h264_qsv",   AV_PIX_FMT_NV12    },  // Intel Quick Sync
        { "h264_amf",   AV_PIX_FMT_NV12    },  // AMD
        { "h264_mf",    AV_PIX_FMT_NV12    },  // Windows Media Foundation
        { "libx264",    AV_PIX_FMT_YUV420P },  // software fallback
    };

    bool ok = false;
    for (const auto& cand : cands) {
        if (try_open(cand.name, w, h, fps, bitrate_kbps, cand.pix)) { ok = true; break; }
    }
    if (!ok) {
        // Last resort: whatever the default H.264 encoder is.
        const AVCodec* codec = avcodec_find_encoder(AV_CODEC_ID_H264);
        if (!codec) return RC_ERR;
        e.c = avcodec_alloc_context3(codec);
        if (!e.c) return RC_ERR;
        apply_common(e.c, w, h, fps, bitrate_kbps, AV_PIX_FMT_YUV420P);
        av_opt_set(e.c->priv_data, "preset", "ultrafast", 0);
        av_opt_set(e.c->priv_data, "tune",   "zerolatency", 0);
        if (avcodec_open2(e.c, codec, nullptr) < 0) { rc_encoder_free(); return RC_ERR; }
        e.pix = AV_PIX_FMT_YUV420P;
        strncpy(e.name, codec->name, sizeof(e.name) - 1);
    }

    e.frame = av_frame_alloc();
    if (!e.frame) { rc_encoder_free(); return RC_ERR; }
    e.frame->format = e.pix;
    e.frame->width  = w;
    e.frame->height = h;
    if (av_frame_get_buffer(e.frame, 0) < 0) { rc_encoder_free(); return RC_ERR; }

    e.sws = sws_getContext(w, h, AV_PIX_FMT_BGRA,
                           w, h, e.pix,
                           SWS_BILINEAR, nullptr, nullptr, nullptr);
    if (!e.sws) { rc_encoder_free(); return RC_ERR; }

    e.w = w; e.h = h; e.pts = 0;

    if (e.c->extradata && e.c->extradata_size > 0) {
        *out_extra = (uint8_t*)av_malloc(e.c->extradata_size);
        memcpy(*out_extra, e.c->extradata, e.c->extradata_size);
        *out_extra_size = e.c->extradata_size;
    } else {
        *out_extra = nullptr;
        *out_extra_size = 0;
    }
    return RC_OK;
}

int rc_encoder_init(int w, int h, int fps, int bitrate_kbps,
                    uint8_t** out_extra, int* out_extra_size) {
    if (e.c) rc_encoder_free();
    return enc_open(w, h, fps, bitrate_kbps, out_extra, out_extra_size);
}

// Hot parameter change without tearing down the whole core: re-open the
// encoder at a new resolution / bitrate and return the new SPS/PPS so the
// viewer can re-init its decoder. Used by the adaptive bitrate / dynamic
// resolution controller.
int rc_encoder_set(int w, int h, int fps, int bitrate_kbps,
                   uint8_t** out_extra, int* out_extra_size) {
    return enc_open(w, h, fps, bitrate_kbps, out_extra, out_extra_size);
}

void rc_encoder_free(void) {
    if (e.frame)  { av_frame_free(&e.frame); e.frame = nullptr; }
    if (e.sws)    { sws_freeContext(e.sws); e.sws = nullptr; }
    if (e.sws_in) { sws_freeContext(e.sws_in); e.sws_in = nullptr; }
    if (e.scaled) { av_free(e.scaled); e.scaled = nullptr; e.scaled_cap = 0; }
    if (e.c)      { avcodec_free_context(&e.c); e.c = nullptr; }
    e.src_w = e.src_h = 0;
    e.pts = 0;
    e.name[0] = 0;
}

// Returns the active encoder name (e.g. "h264_nvenc", "libx264") or "" if none.
const char* rc_encoder_name(void) {
    return e.c ? e.name : "";
}

int rc_encoder_encode(const uint8_t* rgba, int w, int h,
                      uint8_t** out_nal, int* out_nal_size, int* out_key) {
    if (!e.c || !e.frame) return RC_ERR;

    if (av_frame_make_writable(e.frame) < 0) return RC_ERR;

    // The capture may run at native resolution while the encoder target is
    // smaller (dynamic resolution scaling). Scale the source down to the
    // encoder size first; otherwise feed it straight through.
    const uint8_t* src = rgba;
    int sw = w, sh = h;
    if (w != e.w || h != e.h) {
        if (e.sws_in == nullptr || e.src_w != w || e.src_h != h) {
            if (e.sws_in) sws_freeContext(e.sws_in);
            e.sws_in = sws_getContext(w, h, AV_PIX_FMT_BGRA,
                                      e.w, e.h, AV_PIX_FMT_BGRA,
                                      SWS_BILINEAR, nullptr, nullptr, nullptr);
            e.src_w = w; e.src_h = h;
            int need = e.w * e.h * 4;
            if (e.scaled == nullptr || e.scaled_cap < need) {
                if (e.scaled) av_free(e.scaled);
                e.scaled = (uint8_t*)av_malloc(need);
                e.scaled_cap = need;
            }
        }
        if (e.sws_in && e.scaled) {
            const uint8_t* in_s[1]  = { rgba };
            const int      in_st[1]  = { w * 4 };
            int            out_st[1] = { e.w * 4 };
            sws_scale(e.sws_in, in_s, in_st, 0, h, &e.scaled, out_st);
            src = e.scaled; sw = e.w; sh = e.h;
        }
    }

    const uint8_t* conv_s[1]  = { src };
    const int      conv_st[1] = { sw * 4 };
    sws_scale(e.sws, conv_s, conv_st, 0, sh,
              e.frame->data, e.frame->linesize);

    e.frame->pts = e.pts++;
    if (avcodec_send_frame(e.c, e.frame) < 0) return RC_ERR;

    AVPacket* pkt = av_packet_alloc();
    int ret = avcodec_receive_packet(e.c, pkt);
    if (ret < 0) { av_packet_free(&pkt); *out_nal_size = 0; return RC_NO_FRAME; }

    *out_nal = (uint8_t*)av_malloc(pkt->size);
    memcpy(*out_nal, pkt->data, pkt->size);
    *out_nal_size = pkt->size;
    *out_key = (pkt->flags & AV_PKT_FLAG_KEY) ? 1 : 0;
    av_packet_free(&pkt);
    return RC_OK;
}

} // extern "C"
