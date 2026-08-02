// capture.cpp - GPU screen capture via DXGI Desktop Duplication API.
// Captures only when the desktop changes; never polls the whole screen.

#include "rc_core.h"
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <new>
#include <cstring>  // memcpy

// Implementation entry points (C linkage) so the public wrappers and
// system.cpp's rc_capture_reinit can call them.
extern "C" {
    int  rc_capture_init_impl(int display_index);
    void rc_capture_free_impl(void);
}

namespace {

struct CapCtx {
    ID3D11Device*           device    = nullptr;
    ID3D11DeviceContext*    ctx       = nullptr;
    IDXGIOutputDuplication* dup       = nullptr;
    ID3D11Texture2D*        staging   = nullptr;
    int                     width     = 0;
    int                     height    = 0;
    int                     left      = 0;   // monitor origin on the virtual desktop
    int                     top       = 0;
    BYTE*                   buf       = nullptr;
};

CapCtx g;

} // namespace

extern "C" {

int rc_capture_init(int display_index) {
    return rc_capture_init_impl(display_index);
}

int rc_capture_init_impl(int display_index) {
    if (g.device) rc_capture_free_impl();

    D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1,
                                   D3D_FEATURE_LEVEL_10_0, D3D_FEATURE_LEVEL_9_3 };
    D3D_FEATURE_LEVEL got = D3D_FEATURE_LEVEL_9_1;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                                   0, levels, ARRAYSIZE(levels), D3D11_SDK_VERSION,
                                   &g.device, &got, &g.ctx);
    if (FAILED(hr)) return RC_ERR;

    IDXGIDevice* dxgiDev = nullptr;
    hr = g.device->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgiDev);
    if (FAILED(hr)) return RC_ERR;

    IDXGIAdapter* adapter = nullptr;
    hr = dxgiDev->GetAdapter(&adapter);
    dxgiDev->Release();
    if (FAILED(hr)) return RC_ERR;

    IDXGIOutput* output = nullptr;
    hr = adapter->EnumOutputs(display_index, &output);
    adapter->Release();
    if (FAILED(hr)) return RC_ERR;

    DXGI_OUTPUT_DESC desc;
    output->GetDesc(&desc);
    g.width  = desc.DesktopCoordinates.right  - desc.DesktopCoordinates.left;
    g.height = desc.DesktopCoordinates.bottom - desc.DesktopCoordinates.top;
    g.left   = desc.DesktopCoordinates.left;   // offset within the virtual desktop
    g.top    = desc.DesktopCoordinates.top;    // (non-zero for secondary monitors)

    IDXGIOutput1* output1 = nullptr;
    hr = output->QueryInterface(__uuidof(IDXGIOutput1), (void**)&output1);
    output->Release();
    if (FAILED(hr)) return RC_ERR;

    hr = output1->DuplicateOutput(g.device, &g.dup);
    output1->Release();
    if (FAILED(hr)) return RC_ERR;

    D3D11_TEXTURE2D_DESC td = {};
    td.Width          = g.width;
    td.Height         = g.height;
    td.MipLevels      = 1;
    td.ArraySize      = 1;
    td.Format         = DXGI_FORMAT_B8G8R8A8_UNORM;
    td.SampleDesc     = { 1, 0 };
    td.Usage          = D3D11_USAGE_STAGING;
    td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    td.BindFlags      = 0;
    td.MiscFlags      = 0;
    hr = g.device->CreateTexture2D(&td, nullptr, &g.staging);
    if (FAILED(hr)) { rc_capture_free(); return RC_ERR; }

    g.buf = new (std::nothrow) BYTE[size_t(g.width) * g.height * 4];
    if (!g.buf) { rc_capture_free(); return RC_ERR; }
    return RC_OK;
}

void rc_capture_free(void) {
    rc_capture_free_impl();
}

void rc_capture_free_impl(void) {
    if (g.dup)     { g.dup->Release();     g.dup = nullptr; }
    if (g.staging) { g.staging->Release(); g.staging = nullptr; }
    if (g.ctx)     { g.ctx->Release();     g.ctx = nullptr; }
    if (g.device)  { g.device->Release();  g.device = nullptr; }
    delete[] g.buf; g.buf = nullptr;
    g.width = g.height = 0;
    g.left = g.top = 0;
}

// Report the captured monitor's rectangle in virtual-desktop coordinates.
// The host feeds this to rc_input_set_bounds so injected mouse coordinates
// land on the correct monitor (secondary monitors have a non-zero origin).
int rc_capture_get_bounds(int* left, int* top, int* width, int* height) {
    if (!g.device) return RC_ERR;
    if (left)   *left   = g.left;
    if (top)    *top    = g.top;
    if (width)  *width  = g.width;
    if (height) *height = g.height;
    return RC_OK;
}

int rc_capture_frame(uint8_t** out_rgba, int* out_w, int* out_h, uint64_t* out_pts) {
    if (!g.dup) return RC_ERR;

    IDXGIResource* res = nullptr;
    DXGI_OUTDUPL_FRAME_INFO info = {};
    // Wait up to ~33ms for the next desktop update. If nothing changed we
    // simply return RC_NO_FRAME instead of sending a duplicate image.
    HRESULT hr = g.dup->AcquireNextFrame(33, &info, &res);
    if (hr == DXGI_ERROR_WAIT_TIMEOUT) { if (res) res->Release(); return RC_NO_FRAME; }
    if (FAILED(hr)) return RC_ERR;

    ID3D11Texture2D* tex = nullptr;
    hr = res->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&tex);
    res->Release();
    if (FAILED(hr)) { g.dup->ReleaseFrame(); return RC_ERR; }

    g.ctx->CopyResource(g.staging, tex);
    tex->Release();

    D3D11_MAPPED_SUBRESOURCE map = {};
    hr = g.ctx->Map(g.staging, 0, D3D11_MAP_READ, 0, &map);
    if (FAILED(hr)) { g.dup->ReleaseFrame(); return RC_ERR; }

    const int rowBytes = g.width * 4;
    const BYTE* src = (const BYTE*)map.pData;
    BYTE* dst = g.buf;
    for (int y = 0; y < g.height; ++y)
        memcpy(dst + size_t(y) * rowBytes, src + size_t(y) * map.RowPitch, rowBytes);
    g.ctx->Unmap(g.staging, 0);

    *out_rgba = g.buf;
    *out_w    = g.width;
    *out_h    = g.height;
    *out_pts  = (uint64_t)(info.LastPresentTime.QuadPart);
    g.dup->ReleaseFrame();

    // Phase 7C: Draw mouse cursor on top of the captured frame (zzrat-style).
    {
        CURSORINFO ci = { sizeof(CURSORINFO) };
        if (GetCursorInfo(&ci) && (ci.flags & CURSOR_SHOWING))
        {
            ICONINFO ii;
            if (GetIconInfo(ci.hCursor, &ii))
            {
                int cx = ci.ptScreenPos.x - g.left;
                int cy = ci.ptScreenPos.y - g.top;
                // Create a DIB section backed by a temp buffer, copy frame rgba into it,
                // draw the cursor icon, then copy back into g.buf.
                BITMAPINFO bmi = {};
                bmi.bmiHeader.biSize        = sizeof(BITMAPINFOHEADER);
                bmi.bmiHeader.biWidth       = g.width;
                bmi.bmiHeader.biHeight      = -g.height;  // top-down
                bmi.bmiHeader.biPlanes      = 1;
                bmi.bmiHeader.biBitCount    = 32;
                bmi.bmiHeader.biCompression = BI_RGB;
                void* dibBits = nullptr;
                HDC hdcScreen = GetDC(nullptr);
                HDC hdcMem = CreateCompatibleDC(hdcScreen);
                HBITMAP hBmp = CreateDIBSection(hdcMem, &bmi, DIB_RGB_COLORS, &dibBits, nullptr, 0);
                if (hBmp && dibBits)
                {
                    size_t frameBytes = size_t(g.width) * g.height * 4;
                    memcpy(dibBits, g.buf, frameBytes);
                    HBITMAP oldBmp = (HBITMAP)SelectObject(hdcMem, hBmp);
                    DrawIconEx(hdcMem, cx - ii.xHotspot, cy - ii.yHotspot,
                               ci.hCursor, 0, 0, 0, nullptr, DI_NORMAL);
                    memcpy(g.buf, dibBits, frameBytes);
                    SelectObject(hdcMem, oldBmp);
                }
                if (hBmp) DeleteObject(hBmp);
                if (hdcMem) DeleteDC(hdcMem);
                if (hdcScreen) ReleaseDC(nullptr, hdcScreen);
                if (ii.hbmMask)  DeleteObject(ii.hbmMask);
                if (ii.hbmColor) DeleteObject(ii.hbmColor);
            }
        }
    }

    return RC_OK;
}

} // extern "C"
