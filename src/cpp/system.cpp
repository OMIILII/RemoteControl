// system.cpp - host-side system control and monitor switching.
// rc_system_* wrap the documented Windows APIs (LockWorkStation, ExitWindowsEx,
// InitiateSystemShutdownEx). rc_capture_reinit switches the shared monitor
// without tearing down the whole core (the C# side reopens the encoder after).

#include "rc_core.h"
#include <windows.h>

// SetSuspendState lives in powrprof (linked via CMake). Declare it directly so
// we don't need the whole <powrprof.h> which pulls in extra dependencies.
extern "C" BOOLEAN WINAPI SetSuspendState(BOOLEAN bHibernate, BOOLEAN bForce, BOOLEAN bWakeupEventsDisabled);

// capture.cpp keeps its DXGI context in an anonymous namespace, so to let
// system.cpp switch the shared monitor we re-declare the two helpers here as
// thin wrappers that mirror capture.cpp's public API (C linkage, matching
// the definitions in capture.cpp).
extern "C" {
    int  rc_capture_init_impl(int display_index);
    void rc_capture_free_impl(void);
}

namespace {

// Whether a privilege is held/enabled (used to decide if reboot/shutdown is
// even possible from this process token).
bool EnablePrivilege(const wchar_t* name)
{
    HANDLE h = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &h))
        return false;
    LUID luid;
    if (!LookupPrivilegeValueW(nullptr, name, &luid)) { CloseHandle(h); return false; }
    TOKEN_PRIVILEGES tp = {};
    tp.PrivilegeCount = 1;
    tp.Privileges[0].Luid = luid;
    tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
    bool ok = AdjustTokenPrivileges(h, FALSE, &tp, 0, nullptr, nullptr) && GetLastError() == ERROR_SUCCESS;
    CloseHandle(h);
    return ok;
}

} // namespace

extern "C" {

int rc_system_lock(void) {
    LockWorkStation();
    return RC_OK;
}

int rc_system_logoff(void) {
    // EWX_LOGOFF, force so it doesn't hang on a blocked app.
    if (!ExitWindowsEx(EWX_LOGOFF | EWX_FORCEIFHUNG, 0)) return RC_ERR;
    return RC_OK;
}

int rc_system_reboot(void) {
    EnablePrivilege(L"SeShutdownPrivilege");
    if (!ExitWindowsEx(EWX_REBOOT | EWX_FORCEIFHUNG,
                       SHTDN_REASON_MAJOR_APPLICATION | SHTDN_REASON_MINOR_OTHER)) return RC_ERR;
    return RC_OK;
}

int rc_system_shutdown(void) {
    EnablePrivilege(L"SeShutdownPrivilege");
    if (!ExitWindowsEx(EWX_SHUTDOWN | EWX_POWEROFF | EWX_FORCEIFHUNG,
                       SHTDN_REASON_MAJOR_APPLICATION | SHTDN_REASON_MINOR_OTHER)) return RC_ERR;
    return RC_OK;
}

// Put the machine to sleep (suspend, not hibernate). Forced so a hung app
// can't block it. Needs no special privilege for S3 sleep.
int rc_system_sleep(void) {
    if (!SetSuspendState(FALSE, TRUE, FALSE)) return RC_ERR;
    return RC_OK;
}

// Turn the local monitor(s) off (privacy / power). The display wakes again on
// the next mouse move or key press. SendMessageTimeout avoids blocking if a
// top-level window is not pumping messages. lParam 2 = power off.
int rc_system_monitor_off(void) {
    DWORD_PTR res = 0;
    LRESULT r = SendMessageTimeoutW(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER,
                                    (LPARAM)2, SMTO_ABORTIFHUNG, 1000, &res);
    return r != 0 ? RC_OK : RC_ERR;
}

// Number of available displays (for the viewer's monitor picker).
int rc_monitor_count(void) {
    return GetSystemMetrics(SM_CMONITORS);
}

// Re-initialise the capture on a different monitor. Returns RC_OK / RC_ERR.
int rc_capture_reinit(int display_index) {
    rc_capture_free_impl();
    return rc_capture_init_impl(display_index);
}

} // extern "C"
