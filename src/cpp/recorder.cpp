// recorder.cpp - session recording: mux the incoming H.264 elementary
// stream straight into an MP4 file (no re-encode). The viewer feeds every
// received NAL here while recording is on.
//
// ffmpeg's mov/mp4 muxer accepts Annex-B packets and converts them to
// length-prefixed (avcC) internally, so we can pass the wire format as-is.
#include "rc_core.h"

extern "C" {
#include <libavformat/avformat.h>
#include <libavutil/avutil.h>
}

#include <cstring>
#include <mutex>

namespace {
    AVFormatContext* g_fmt = nullptr;
    AVStream* g_stream = nullptr;
    std::mutex g_mtx;
    int g_fps = 30;
    bool g_have_key = false;   // have we seen the first IDR yet?
    long long g_pts0 = -1;     // pts (ms) of the first written frame -> rebased to 0
    long long g_last_dts = -1; // last written dts (in stream time_base units)
}

extern "C" {

// Start recording to `path` (UTF-8). extradata = SPS/PPS from VideoConfig
// (may be empty when the stream is Annex-B in-band). Returns 0 on success.
RC_API int rc_record_start(const char* path, int w, int h, int fps,
                           const unsigned char* extradata, int extralen)
{
    std::lock_guard<std::mutex> lk(g_mtx);
    if (g_fmt) return -1; // already recording

    g_fps = fps > 0 ? fps : 30;
    g_have_key = false;
    g_pts0 = -1;
    g_last_dts = -1;

    if (avformat_alloc_output_context2(&g_fmt, nullptr, "mp4", path) < 0 || !g_fmt)
        return -2;

    g_stream = avformat_new_stream(g_fmt, nullptr);
    if (!g_stream) { avformat_free_context(g_fmt); g_fmt = nullptr; return -3; }

    AVCodecParameters* par = g_stream->codecpar;
    par->codec_type = AVMEDIA_TYPE_VIDEO;
    par->codec_id   = AV_CODEC_ID_H264;
    par->width      = w;
    par->height     = h;
    if (extradata && extralen > 0) {
        par->extradata = (uint8_t*)av_mallocz(extralen + AV_INPUT_BUFFER_PADDING_SIZE);
        memcpy(par->extradata, extradata, extralen);
        par->extradata_size = extralen;
    }
    g_stream->time_base = AVRational{ 1, 1000 }; // we timestamp in ms

    if (!(g_fmt->oformat->flags & AVFMT_NOFILE)) {
        if (avio_open(&g_fmt->pb, path, AVIO_FLAG_WRITE) < 0) {
            avformat_free_context(g_fmt); g_fmt = nullptr; g_stream = nullptr;
            return -4;
        }
    }
    // faststart: relocate the moov atom to the front on close so that plain
    // players (Windows Media Player, browsers, phones) can open the file even
    // when they don't scan to the end.
    AVDictionary* opt = nullptr;
    av_dict_set(&opt, "movflags", "faststart", 0);
    int hr = avformat_write_header(g_fmt, &opt);
    av_dict_free(&opt);
    if (hr < 0) {
        if (g_fmt->pb) avio_closep(&g_fmt->pb);
        avformat_free_context(g_fmt); g_fmt = nullptr; g_stream = nullptr;
        return -5;
    }
    return 0;
}

// Append one encoded access unit. pts_ms is milliseconds since recording
// start; key != 0 marks IDR frames. Returns 0 on success.
RC_API int rc_record_write(const unsigned char* nal, int len,
                           long long pts_ms, int key)
{
    std::lock_guard<std::mutex> lk(g_mtx);
    if (!g_fmt || !g_stream || !nal || len <= 0) return -1;

    // A valid MP4 must begin at a keyframe (IDR): decoders/players that don't
    // tolerate a mid-GOP start otherwise show black/green or refuse the file.
    // Drop everything until the first IDR arrives.
    if (!g_have_key) {
        if (!key) return 0;   // silently skip pre-roll P-frames
        g_have_key = true;
        g_pts0 = pts_ms;      // rebase timeline so the first sample is pts=0
    }

    // Rebase to a zero-based, non-negative timeline.
    long long rel = pts_ms - g_pts0;
    if (rel < 0) rel = 0;

    AVPacket* pkt = av_packet_alloc();
    if (!pkt) return -2;
    if (av_new_packet(pkt, len) < 0) { av_packet_free(&pkt); return -3; }
    memcpy(pkt->data, nal, len);
    pkt->stream_index = g_stream->index;
    // Our stream has no B-frames (zerolatency), so dts == pts.
    long long ts = av_rescale_q(rel, AVRational{1,1000}, g_stream->time_base);
    // Force strictly increasing dts; the mp4 muxer rejects non-monotonic dts.
    if (ts <= g_last_dts) ts = g_last_dts + 1;
    g_last_dts = ts;
    pkt->pts = ts;
    pkt->dts = ts;
    if (key) pkt->flags |= AV_PKT_FLAG_KEY;

    int rc = av_interleaved_write_frame(g_fmt, pkt);
    av_packet_free(&pkt);
    return rc < 0 ? -4 : 0;
}

// Finish and close the file. Safe to call when not recording.
RC_API int rc_record_stop(void)
{
    std::lock_guard<std::mutex> lk(g_mtx);
    if (!g_fmt) return 0;
    av_write_trailer(g_fmt);
    if (g_fmt->pb) avio_closep(&g_fmt->pb);
    avformat_free_context(g_fmt);
    g_fmt = nullptr; g_stream = nullptr;
    g_have_key = false; g_pts0 = -1; g_last_dts = -1;
    return 0;
}

// 1 while a recording session is open.
RC_API int rc_record_active(void)
{
    std::lock_guard<std::mutex> lk(g_mtx);
    return g_fmt ? 1 : 0;
}

} // extern "C"
