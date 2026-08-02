// audio.cpp - System audio streaming (host loopback capture + viewer playback).
//
// Host side  : WASAPI *loopback* capture of the default render endpoint
//              (i.e. whatever is playing on the speakers) -> resample to
//              48 kHz stereo float -> Opus encode (20 ms frames).
// Viewer side: Opus decode -> resample to the local render mix format ->
//              WASAPI shared-mode render.
//
// Both directions run their own servicing thread so WASAPI is fed/drained on
// time; the C# layer just pushes/pops Opus packets. This is real audio
// streaming, matching the video path, not periodic PCM dumps.

#include "rc_core.h"

#include <initguid.h>          // make DEFINE_GUID actually emit the GUIDs
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>

#include <thread>
#include <mutex>
#include <atomic>
#include <deque>
#include <vector>
#include <cstring>

extern "C" {
#include <libavutil/opt.h>
#include <libavutil/channel_layout.h>
#include <libswresample/swresample.h>
#include <opus/opus.h>
}

namespace {

constexpr int SR    = 48000;   // Opus internal rate
constexpr int CH    = 2;       // stereo
constexpr int FRAME = 960;     // 20 ms @ 48 kHz (samples per channel)

// Detect the AVSampleFormat that a WAVEFORMATEX describes. We deliberately
// avoid WAVEFORMATEXTENSIBLE / KSDATAFORMAT_SUBTYPE_* (which would pull in
// ksmedia.h) — on modern Windows the default render mix format is IEEE float,
// so we treat anything non-PCM as float. The exact format only affects the
// resampler's input type, and float is always correct here.
AVSampleFormat wfx_fmt(const WAVEFORMATEX* w) {
    if (w->wFormatTag == 1) // WAVE_FORMAT_PCM
        return w->wBitsPerSample <= 16 ? AV_SAMPLE_FMT_S16 : AV_SAMPLE_FMT_S32;
    return AV_SAMPLE_FMT_FLT; // WAVE_FORMAT_IEEE_FLOAT / EXTENSIBLE / anything else
}

// ---------------- Capture (host) ---------------------------------------
struct CapCtx {
    std::thread             th;
    std::atomic<bool>       run{false};
    std::mutex              mtx;
    std::deque<std::vector<uint8_t>> q;   // encoded Opus packets
};
CapCtx cap;

void cap_thread() {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    IMMDeviceEnumerator* enum_ = nullptr;
    IMMDevice*           dev   = nullptr;
    IAudioClient*        ac    = nullptr;
    IAudioCaptureClient* cc    = nullptr;
    WAVEFORMATEX*        wfx   = nullptr;
    SwrContext*          swr   = nullptr;
    OpusEncoder*         enc   = nullptr;

    auto cleanup = [&]() {
        if (ac) ac->Stop();
        if (enc) opus_encoder_destroy(enc);
        if (swr) swr_free(&swr);
        if (wfx) CoTaskMemFree(wfx);
        if (cc)  cc->Release();
        if (ac)  ac->Release();
        if (dev) dev->Release();
        if (enum_) enum_->Release();
        CoUninitialize();
    };

    if (FAILED(CoCreateInstance(CLSID_MMDeviceEnumerator, nullptr, CLSCTX_ALL,
            IID_IMMDeviceEnumerator, (void**)&enum_))) { cleanup(); return; }
    if (FAILED(enum_->GetDefaultAudioEndpoint(eRender, eConsole, &dev))) { cleanup(); return; }
    if (FAILED(dev->Activate(IID_IAudioClient, CLSCTX_ALL, nullptr, (void**)&ac))) { cleanup(); return; }
    if (FAILED(ac->GetMixFormat(&wfx))) { cleanup(); return; }

    REFERENCE_TIME dur = 2000000; // 200 ms buffer
    if (FAILED(ac->Initialize(AUDCLNT_SHAREMODE_SHARED,
            AUDCLNT_STREAMFLAGS_LOOPBACK, dur, 0, wfx, nullptr))) { cleanup(); return; }
    if (FAILED(ac->GetService(IID_IAudioCaptureClient, (void**)&cc))) { cleanup(); return; }

    // Resampler: device mix format -> 48 kHz stereo float (interleaved).
    AVChannelLayout in_ch, out_ch;
    av_channel_layout_default(&in_ch, wfx->nChannels);
    av_channel_layout_default(&out_ch, CH);
    if (swr_alloc_set_opts2(&swr, &out_ch, AV_SAMPLE_FMT_FLT, SR,
            &in_ch, wfx_fmt(wfx), wfx->nSamplesPerSec, 0, nullptr) < 0 ||
        swr_init(swr) < 0) { cleanup(); return; }

    int err = 0;
    enc = opus_encoder_create(SR, CH, OPUS_APPLICATION_RESTRICTED_LOWDELAY, &err);
    if (!enc || err != OPUS_OK) { cleanup(); return; }
    opus_encoder_ctl(enc, OPUS_SET_BITRATE(96000));
    opus_encoder_ctl(enc, OPUS_SET_SIGNAL(OPUS_SIGNAL_MUSIC));

    ac->Start();

    std::vector<float>   fifo;      // interleaved 48k stereo float
    std::vector<float>   outbuf(SR * CH); // resample scratch (1s max)
    unsigned char        pkt[4000];

    while (cap.run.load()) {
        UINT32 avail = 0;
        cc->GetNextPacketSize(&avail);
        if (avail == 0) { Sleep(5); }
        while (avail != 0) {
            BYTE* data = nullptr; UINT32 frames = 0; DWORD flags = 0;
            if (FAILED(cc->GetBuffer(&data, &frames, &flags, nullptr, nullptr))) break;
            const uint8_t* in[1] = { (flags & AUDCLNT_BUFFERFLAGS_SILENT) ? nullptr : data };
            int cap_out = (int)outbuf.size() / CH;
            uint8_t* out[1] = { (uint8_t*)outbuf.data() };
            int got = swr_convert(swr, out, cap_out, in, frames);
            if (got > 0) fifo.insert(fifo.end(), outbuf.data(), outbuf.data() + (size_t)got * CH);
            cc->ReleaseBuffer(frames);
            cc->GetNextPacketSize(&avail);
        }
        // Encode as many full 20 ms frames as we have.
        while ((int)fifo.size() >= FRAME * CH) {
            int n = opus_encode_float(enc, fifo.data(), FRAME, pkt, sizeof(pkt));
            fifo.erase(fifo.begin(), fifo.begin() + FRAME * CH);
            if (n > 1) {
                std::lock_guard<std::mutex> lk(cap.mtx);
                if (cap.q.size() < 64) cap.q.emplace_back(pkt, pkt + n);
            }
        }
    }

    av_channel_layout_uninit(&in_ch);
    av_channel_layout_uninit(&out_ch);
    cleanup();
}

// ---------------- Playback (viewer) ------------------------------------
struct PlayCtx {
    std::thread          th;
    std::atomic<bool>    run{false};
    std::mutex           mtx;
    std::vector<uint8_t> fifo;         // PCM bytes in *render* format
    OpusDecoder*         dec = nullptr;
    SwrContext*          swr = nullptr;
    int                  block = 4;    // render nBlockAlign
};
PlayCtx play;

void play_thread() {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    IMMDeviceEnumerator* enum_ = nullptr;
    IMMDevice*           dev   = nullptr;
    IAudioClient*        ac    = nullptr;
    IAudioRenderClient*  rc    = nullptr;
    WAVEFORMATEX*        wfx   = nullptr;

    auto cleanup = [&]() {
        if (ac) ac->Stop();
        if (wfx) CoTaskMemFree(wfx);
        if (rc)  rc->Release();
        if (ac)  ac->Release();
        if (dev) dev->Release();
        if (enum_) enum_->Release();
        CoUninitialize();
    };

    if (FAILED(CoCreateInstance(CLSID_MMDeviceEnumerator, nullptr, CLSCTX_ALL,
            IID_IMMDeviceEnumerator, (void**)&enum_))) { cleanup(); return; }
    if (FAILED(enum_->GetDefaultAudioEndpoint(eRender, eConsole, &dev))) { cleanup(); return; }
    if (FAILED(dev->Activate(IID_IAudioClient, CLSCTX_ALL, nullptr, (void**)&ac))) { cleanup(); return; }
    if (FAILED(ac->GetMixFormat(&wfx))) { cleanup(); return; }

    REFERENCE_TIME dur = 2000000; // 200 ms
    if (FAILED(ac->Initialize(AUDCLNT_SHAREMODE_SHARED, 0, dur, 0, wfx, nullptr))) { cleanup(); return; }

    UINT32 bufFrames = 0;
    if (FAILED(ac->GetBufferSize(&bufFrames))) { cleanup(); return; }
    if (FAILED(ac->GetService(IID_IAudioRenderClient, (void**)&rc))) { cleanup(); return; }

    // Resampler: 48 kHz stereo float -> render mix format.
    AVChannelLayout in_ch, out_ch;
    av_channel_layout_default(&in_ch, CH);
    av_channel_layout_default(&out_ch, wfx->nChannels);
    SwrContext* swr = nullptr;
    if (swr_alloc_set_opts2(&swr, &out_ch, wfx_fmt(wfx), wfx->nSamplesPerSec,
            &in_ch, AV_SAMPLE_FMT_FLT, SR, 0, nullptr) < 0 ||
        swr_init(swr) < 0) { cleanup(); return; }

    {
        std::lock_guard<std::mutex> lk(play.mtx);
        play.swr   = swr;
        play.block = wfx->nBlockAlign;
    }

    ac->Start();

    while (play.run.load()) {
        UINT32 padding = 0;
        if (FAILED(ac->GetCurrentPadding(&padding))) break;
        UINT32 canWrite = bufFrames - padding;
        if (canWrite == 0) { Sleep(5); continue; }

        BYTE* buf = nullptr;
        if (FAILED(rc->GetBuffer(canWrite, &buf))) { Sleep(5); continue; }

        int block = 4;
        {
            std::lock_guard<std::mutex> lk(play.mtx);
            block = play.block > 0 ? play.block : 4;
            size_t want = (size_t)canWrite * block;
            size_t have = play.fifo.size();
            size_t take = have < want ? have : want;
            if (take) { memcpy(buf, play.fifo.data(), take);
                        play.fifo.erase(play.fifo.begin(), play.fifo.begin() + take); }
            if (take < want) memset(buf + take, 0, want - take); // pad silence
        }
        DWORD flags = 0; // we always wrote a full buffer (padded)
        rc->ReleaseBuffer(canWrite, flags);
        Sleep(5);
    }

    {
        std::lock_guard<std::mutex> lk(play.mtx);
        play.swr = nullptr;
    }
    swr_free(&swr);
    av_channel_layout_uninit(&in_ch);
    av_channel_layout_uninit(&out_ch);
    cleanup();
}

} // namespace

extern "C" {

// ---- Host capture ------------------------------------------------------
int rc_audio_cap_start(void) {
    if (cap.run.load()) return RC_OK;
    cap.run.store(true);
    try { cap.th = std::thread(cap_thread); }
    catch (...) { cap.run.store(false); return RC_ERR; }
    return RC_OK;
}

int rc_audio_cap_read(uint8_t** out_opus, int* out_size) {
    std::lock_guard<std::mutex> lk(cap.mtx);
    if (cap.q.empty()) { *out_size = 0; return RC_NO_FRAME; }
    auto& front = cap.q.front();
    *out_opus = (uint8_t*)malloc(front.size());
    if (!*out_opus) { *out_size = 0; return RC_ERR; }
    memcpy(*out_opus, front.data(), front.size());
    *out_size = (int)front.size();
    cap.q.pop_front();
    return RC_OK;
}

void rc_audio_cap_stop(void) {
    if (!cap.run.load()) return;
    cap.run.store(false);
    if (cap.th.joinable()) cap.th.join();
    std::lock_guard<std::mutex> lk(cap.mtx);
    cap.q.clear();
}

// ---- Viewer playback ---------------------------------------------------
int rc_audio_play_start(void) {
    if (play.run.load()) return RC_OK;
    int err = 0;
    play.dec = opus_decoder_create(SR, CH, &err);
    if (!play.dec || err != OPUS_OK) { if (play.dec) opus_decoder_destroy(play.dec); play.dec = nullptr; return RC_ERR; }
    play.run.store(true);
    try { play.th = std::thread(play_thread); }
    catch (...) { play.run.store(false); opus_decoder_destroy(play.dec); play.dec = nullptr; return RC_ERR; }
    return RC_OK;
}

int rc_audio_play_write(const uint8_t* opus, int size) {
    if (!play.run.load() || !play.dec) return RC_ERR;
    float pcm[FRAME * CH];
    int got = opus_decode_float(play.dec, opus, size, pcm, FRAME, 0);
    if (got <= 0) return RC_ERR;

    std::lock_guard<std::mutex> lk(play.mtx);
    if (!play.swr) return RC_OK; // render not ready yet; drop
    // Convert 48k stereo float -> render format, append to fifo.
    int max_out = got * 4 + 256;
    std::vector<uint8_t> tmp((size_t)max_out * (play.block > 0 ? play.block : 8));
    const uint8_t* in[1] = { (const uint8_t*)pcm };
    uint8_t* out[1] = { tmp.data() };
    int conv = swr_convert(play.swr, out, max_out, in, got);
    if (conv > 0) {
        size_t bytes = (size_t)conv * (play.block > 0 ? play.block : 8);
        if (play.fifo.size() < (size_t)SR * 4 * 8) // cap ~ a few seconds
            play.fifo.insert(play.fifo.end(), tmp.data(), tmp.data() + bytes);
    }
    return RC_OK;
}

void rc_audio_play_stop(void) {
    if (!play.run.load()) return;
    play.run.store(false);
    if (play.th.joinable()) play.th.join();
    std::lock_guard<std::mutex> lk(play.mtx);
    if (play.dec) { opus_decoder_destroy(play.dec); play.dec = nullptr; }
    play.fifo.clear();
}

// rc_audio_cap_read buffers come from malloc(), so free them with plain free().
void rc_afree(uint8_t* p) { if (p) free(p); }

} // extern "C"
