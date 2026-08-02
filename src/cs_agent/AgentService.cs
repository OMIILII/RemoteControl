// AgentService.cs - Phase 13: Watchdog service that spawns Agent in user session (Session 1+)
// Architecture:
//   Service (SYSTEM, Session 0) -> WTSQueryUserToken -> CreateProcessAsUser(Agent, Session N)
//   Agent (User, Session N)    -> Desktop Duplication -> capture works normally
//   Service                     -> monitors Agent process, restarts on crash
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

namespace RemoteControl
{
    public class AgentService : ServiceBase
    {
        private CancellationTokenSource? _cts;
        private Process? _agentProc;

        public AgentService()
        {
            ServiceName = "RemoteControlAgent";
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            new Thread(() => WatchdogLoop(token))
            { IsBackground = true, Name = "AgentWatchdog" }.Start();
        }

        protected override void OnStop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _agentProc?.Kill(); } catch { }
            try { _agentProc?.WaitForExit(5000); } catch { }
            try { _agentProc?.Dispose(); } catch { }
        }

        protected override void OnShutdown() => OnStop();

        /// <summary>
        /// Main watchdog: finds the active console session, launches Agent inside it,
        /// restarts if it dies.
        /// </summary>
        private void WatchdogLoop(CancellationToken token)
        {
            var selfPath = Environment.ProcessPath ?? "";
            while (!token.IsCancellationRequested)
            {
                try
                {
                    uint sessionId = GetActiveConsoleSession();
                    if (sessionId == 0xFFFFFFFF)
                    {
                        EventLog.WriteEntry("RemoteControlAgent",
                            "No active user session found, retrying in 10s...",
                            EventLogEntryType.Information, 100);
                        token.WaitHandle.WaitOne(10000);
                        continue;
                    }

                    EventLog.WriteEntry("RemoteControlAgent",
                        $"Launching Agent in session {sessionId}...",
                        EventLogEntryType.Information, 101);

                    // Step 1: launch Agent in user session with interactive desktop
                    if (!LaunchInSession(selfPath, sessionId, out _agentProc, out string err))
                    {
                        EventLog.WriteEntry("RemoteControlAgent",
                            $"Failed to launch agent: {err}. Retrying in 10s...",
                            EventLogEntryType.Warning, 102);
                        token.WaitHandle.WaitOne(10000);
                        continue;
                    }

                    EventLog.WriteEntry("RemoteControlAgent",
                        $"Agent started (PID {_agentProc.Id}) in session {sessionId}",
                        EventLogEntryType.Information, 103);

                    // Step 2: wait for process to exit, then restart
                    try { _agentProc.WaitForExit(); }
                    catch (Win32Exception) { }

                    int code = -1;
                    try { code = _agentProc.ExitCode; } catch { }
                    EventLog.WriteEntry("RemoteControlAgent",
                        $"Agent exited with code {code}, restarting in 3s...",
                        EventLogEntryType.Warning, 104);

                    try { _agentProc.Dispose(); } catch { }
                    _agentProc = null;

                    token.WaitHandle.WaitOne(3000);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    try
                    {
                        EventLog.WriteEntry("RemoteControlAgent",
                            $"Watchdog error: {ex.Message}", EventLogEntryType.Error, 500);
                    }
                    catch { }
                    token.WaitHandle.WaitOne(10000);
                }
            }
        }

        // ---- Native interop for session-aware process launch ----

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool CreateProcessAsUser(
            IntPtr hToken,
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint NORMAL_PRIORITY_CLASS = 0x20;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x400;

        private static uint GetActiveConsoleSession()
        {
            return WTSGetActiveConsoleSessionId();
        }

        /// <summary>Launch a process in the specified user session.</summary>
        private static bool LaunchInSession(string exePath, uint sessionId,
            out Process? proc, out string error)
        {
            proc = null; error = "";

            // Get user token for session
            if (!WTSQueryUserToken(sessionId, out IntPtr hToken))
            {
                error = $"WTSQueryUserToken failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                return false;
            }

            try
            {
                // Create environment block for this user
                IntPtr lpEnv = IntPtr.Zero;
                CreateEnvironmentBlock(out lpEnv, hToken, false);

                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(si);
                si.lpDesktop = IntPtr.Zero; // use default desktop
                si.dwFlags = 0;

                string cmdLine = $"\"{exePath}\" --watchdog-child";

                if (!CreateProcessAsUser(hToken, null, cmdLine,
                    IntPtr.Zero, IntPtr.Zero, false,
                    NORMAL_PRIORITY_CLASS | CREATE_UNICODE_ENVIRONMENT,
                    lpEnv, null, ref si, out var pi))
                {
                    error = $"CreateProcessAsUser failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                    if (lpEnv != IntPtr.Zero) DestroyEnvironmentBlock(lpEnv);
                    return false;
                }

                if (lpEnv != IntPtr.Zero) DestroyEnvironmentBlock(lpEnv);

                CloseHandle(pi.hThread);
                proc = Process.GetProcessById((int)pi.dwProcessId);
                CloseHandle(pi.hProcess);
                return true;
            }
            finally
            {
                CloseHandle(hToken);
            }
        }

        // ---- Install / Uninstall via sc.exe ----

        public static void Install()
        {
            var path = Environment.ProcessPath ?? "";
            // sc.exe create 需要 binPath= 后跟引号内的完整路径和参数
            var args = string.Format(
                "create RemoteControlAgent binPath= \"\\\"{0}\\\" --run-as-service\" start= auto DisplayName= \"RemoteControl Agent\"",
                path);
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                var p = Process.Start(psi);
                p?.WaitForExit(8000);
                if (p?.ExitCode == 0)
                    AgentHost.Log("Service installed successfully");
                else
                    AgentHost.Log($"sc.exe create returned code {p?.ExitCode}");
            }
            catch (Exception ex)
            {
                AgentHost.Log("Install failed (admin required): " + ex.Message);
            }
        }

        public static void Uninstall()
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "stop RemoteControlAgent & sc.exe delete RemoteControlAgent",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                var p = Process.Start(psi);
                p?.WaitForExit(8000);
                AgentHost.Log("Service uninstalled");
            }
            catch (Exception ex)
            {
                AgentHost.Log("Uninstall failed (admin required): " + ex.Message);
            }
        }
    }
}
