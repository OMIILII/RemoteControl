// decoder.cpp - H.264 decoding (libavcodec) -> BGRA for the GUI to paint.

#include "rc_core.h"
#include <new>      // std::nothrow
#include <cstring>  // memcpy
extern "C" {
#include <libavcodec/avcodec.h>
#include <libswscale/swscale.h>
}

namespace {

struct DecCtx {
    AVCodecContext* c     = nullptr;
    AVFrame*        frame = nullptr;
    SwsContext*     sws   = nullptr;
    uint8_t*        out   = nullptr;
    int             w = 0, h = 0;
};

DecCtx d;

} // namespace

extern "C" {

int rc_decoder_init(const uint8_t* extra, int extra_size) {
    if (d.c) rc_decoder_free();

    const AVCodec* codec = avcodec_find_decoder(AV_CODEC_ID_H264);
    if (!codec) return RC_ERR;

    d.c = avcodec_alloc_context3(codec);
    if (!d.c) return RC_ERR;

    if (extra && extra_size > 0) {
        d.c->extradata = (uint8_t*)av_malloc(extra_size);
        memcpy(d.c->extradata, extra, extra_size);
        d.c->extradata_size = extra_size;
    }

    // 放开解码并行度：thread_count=0 让 ffmpeg 按 CPU 核心数自动决定线程数，
    // 并同时开启帧级(FF_THREAD_FRAME)与切片级(FF_THREAD_SLICE)多线程，使单帧
    // H.264 软解在多个核心上并行。帧级多线程会带来约 1 帧的流水线延迟，但已被
    // ViewerForm 的 drop-to-latest 渲染（只画最新帧）与主机 intra-refresh 完全
    // 掩盖，零可感知损失；却能把 1600x900 这类高分辨率软解的吞吐显著拉高。
    d.c->thread_count = 0;
    d.c->thread_type  = FF_THREAD_FRAME | FF_THREAD_SLICE;
    d.c->flags       |= AV_CODEC_FLAG_LOW_DELAY;

    if (avcodec_open2(d.c, codec, nullptr) < 0) { rc_decoder_free(); return RC_ERR; }

    d.frame = av_frame_alloc();
    if (!d.frame) { rc_decoder_free(); return RC_ERR; }
    d.sws = nullptr; d.out = nullptr; d.w = d.h = 0;
    return RC_OK;
}

void rc_decoder_free(void) {
    if (d.frame) { av_frame_free(&d.frame); }
    if (d.sws)   { sws_freeContext(d.sws); d.sws = nullptr; }
    if (d.c)     { avcodec_free_context(&d.c); }
    delete[] d.out; d.out = nullptr;
    d.w = d.h = 0;
}

int rc_decoder_decode(const uint8_t* nal, int nal_size,
                      uint8_t** out_rgba, int* out_w, int* out_h) {
    if (!d.c || !d.frame) return RC_ERR;

    AVPacket* pkt = av_packet_alloc();
    if (!pkt) return RC_ERR;
    av_new_packet(pkt, nal_size);
    memcpy(pkt->data, nal, nal_size);

    int send = avcodec_send_packet(d.c, pkt);
    av_packet_free(&pkt);
    if (send < 0) return RC_ERR;

    int ret = avcodec_receive_frame(d.c, d.frame);
    if (ret < 0) { *out_rgba = nullptr; return RC_NO_FRAME; }

    const int W = d.frame->width, H = d.frame->height;
    if (!d.sws || d.w != W || d.h != H) {
        if (d.sws) sws_freeContext(d.sws);
        d.sws = sws_getContext(W, H, AV_PIX_FMT_YUV420P,
                               W, H, AV_PIX_FMT_BGRA,
                               SWS_BILINEAR, nullptr, nullptr, nullptr);
        if (!d.sws) return RC_ERR;
        delete[] d.out;
        d.out = new (std::nothrow) uint8_t[size_t(W) * H * 4];
        if (!d.out) return RC_ERR;
        d.w = W; d.h = H;
    }

    uint8_t* out[1]       = { d.out };
    const int out_stride[1] = { W * 4 };
    sws_scale(d.sws, (const uint8_t* const*)d.frame->data, d.frame->linesize,
              0, H, out, out_stride);

    *out_rgba = d.out;
    *out_w    = W;
    *out_h    = H;
    return RC_OK;
}

} // extern "C"
