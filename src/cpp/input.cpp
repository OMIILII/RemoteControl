// input.cpp - Inject mouse/keyboard input on the controlled machine.
// Called by the host-side GUI after it receives INPUT_EVENT messages.

#include "rc_core.h"
#include <windows.h>

namespace {
// Rectangle of the monitor currently being shared, in virtual-desktop
// coordinates. Set by the host via rc_input_set_bounds. When width==0 we
// fall back to the primary-monitor mapping.
int g_left = 0, g_top = 0, g_w = 0, g_h = 0;
}

extern "C" {

// The host calls this after selecting a monitor (values from
// rc_capture_get_bounds) so that input maps onto that exact monitor.
void rc_input_set_bounds(int left, int top, int width, int height) {
    g_left = left; g_top = top; g_w = width; g_h = height;
}

void rc_input_mouse_move(int x, int y) {
    // Map viewer-space coordinates [0..w]x[0..h] onto the shared monitor's
    // slice of the whole virtual desktop. MOUSEEVENTF_VIRTUALDESK makes the
    // 0..65535 range span all monitors, so a secondary monitor works too.
    if (g_w > 0 && g_h > 0) {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (vw <= 0 || vh <= 0) return;
        double abs_x = (double)(g_left + x) - vx;
        double abs_y = (double)(g_top  + y) - vy;
        INPUT in = {};
        in.type       = INPUT_MOUSE;
        in.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        in.mi.dx      = (LONG)(abs_x * 65535.0 / (vw - 1));
        in.mi.dy      = (LONG)(abs_y * 65535.0 / (vh - 1));
        SendInput(1, &in, sizeof(in));
        return;
    }

    // Fallback: primary monitor only.
    int sx = GetSystemMetrics(SM_CXSCREEN);
    int sy = GetSystemMetrics(SM_CYSCREEN);
    if (sx <= 0 || sy <= 0) return;
    INPUT in = {};
    in.type        = INPUT_MOUSE;
    in.mi.dwFlags  = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
    in.mi.dx       = (LONG)((double)x * 65535.0 / sx);
    in.mi.dy       = (LONG)((double)y * 65535.0 / sy);
    SendInput(1, &in, sizeof(in));
}

void rc_input_mouse_button(int button, int down) {
    INPUT in = {};
    in.type = INPUT_MOUSE;
    switch (button) {
        case 0: in.mi.dwFlags = down ? MOUSEEVENTF_LEFTDOWN  : MOUSEEVENTF_LEFTUP;   break;
        case 1: in.mi.dwFlags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;  break;
        case 2: in.mi.dwFlags = down ? MOUSEEVENTF_MIDDLEDOWN: MOUSEEVENTF_MIDDLEUP; break;
        default: return;
    }
    SendInput(1, &in, sizeof(in));
}

void rc_input_wheel(int delta) {
    INPUT in = {};
    in.type          = INPUT_MOUSE;
    in.mi.dwFlags    = MOUSEEVENTF_WHEEL;
    in.mi.mouseData  = (DWORD)delta;
    SendInput(1, &in, sizeof(in));
}

// Some virtual keys live on the "extended" part of the keyboard and must be
// tagged with KEYEVENTF_EXTENDEDKEY or Windows injects the wrong physical key
// (e.g. the arrow cluster vs. the numpad, right-hand Ctrl/Alt, Insert/Delete).
static bool is_extended_vk(uint32_t vk) {
    switch (vk) {
        case VK_RCONTROL: case VK_RMENU:
        case VK_INSERT:   case VK_DELETE:
        case VK_HOME:     case VK_END:
        case VK_PRIOR:    case VK_NEXT:      // Page Up / Page Down
        case VK_LEFT:     case VK_UP:
        case VK_RIGHT:    case VK_DOWN:
        case VK_NUMLOCK:  case VK_DIVIDE:
        case VK_SNAPSHOT:                    // Print Screen
        case VK_LWIN:     case VK_RWIN:      case VK_APPS:
            return true;
        default:
            return false;
    }
}

void rc_input_key(uint32_t vk, int down) {
    INPUT in = {};
    in.type       = INPUT_KEYBOARD;
    in.ki.wVk     = (WORD)vk;
    // Provide the hardware scan code too; some apps/games read scan codes.
    in.ki.wScan   = (WORD)MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
    in.ki.dwFlags = (down ? 0 : KEYEVENTF_KEYUP);
    if (is_extended_vk(vk)) in.ki.dwFlags |= KEYEVENTF_EXTENDEDKEY;
    SendInput(1, &in, sizeof(in));
}

// Send the Secure Attention Sequence (Ctrl+Alt+Del). A normal SendInput cannot
// synthesise it; SendSAS (sas.dll) can, provided the host process has the right
// to (interactive user, or the "SoftwareSASGeneration" policy allows apps).
// Loaded dynamically so the DLL is optional at build time. Returns 1 on success.
int rc_input_send_cad(void) {
    typedef VOID (WINAPI *SendSAS_t)(BOOL);
    HMODULE h = LoadLibraryW(L"sas.dll");
    if (!h) return 0;
    SendSAS_t fn = (SendSAS_t)GetProcAddress(h, "SendSAS");
    int ok = 0;
    if (fn) { fn(FALSE); ok = 1; }   // FALSE = as the current desktop user
    FreeLibrary(h);
    return ok;
}

} // extern "C"
