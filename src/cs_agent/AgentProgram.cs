// AgentProgram.cs - Phase 12: headless host-only agent.
// Native DLLs are bundled via Content+IncludeAllContentForSelfExtract.
// .NET runtime auto-extracts them to temp on first run.
// Phase 13: Windows Service support (+ --install / --uninstall / --run-as-service)
using System;
using System.Linq;
using System.ServiceProcess;
using System.Windows.Forms;

namespace RemoteControl
{
    internal static class AgentProgram
    {
        internal static string CloudToken = "";
        internal static string ServerOverride = "";
        internal static int AgentPort = 25498;

        [STAThread]
        static void Main(string[] rawArgs)
        {
            var args = rawArgs ?? Array.Empty<string>();

            // ---- 命令行开关处理 ----
            if (args.Any(a => a.Equals("--install", StringComparison.OrdinalIgnoreCase)))
            {
                AgentService.Install();
                return;
            }
            if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                AgentService.Uninstall();
                return;
            }

            try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.High; } catch { }

            var opts = AppOptions.Parse(Array.Empty<string>());
            opts.Role = Role.Host;
            opts.Hide = true;

            try
            {
                var json = Common.ReadOverlayConfig(Environment.ProcessPath ?? "");
                if (!string.IsNullOrEmpty(json))
                {
                    var cfg = System.Text.Json.Nodes.JsonNode.Parse(json);
                    if (cfg is System.Text.Json.Nodes.JsonObject obj)
                    {
                        if (obj.TryGetPropertyValue("server", out var s)) ServerOverride = s?.ToString() ?? "";
                        if (obj.TryGetPropertyValue("port", out var pr) && int.TryParse(pr?.ToString(), out int p)) AgentPort = p;
                        if (obj.TryGetPropertyValue("room", out var rm)) opts.Room = rm?.ToString() ?? "";
                        if (obj.TryGetPropertyValue("pw", out var pw)) opts.Password = pw?.ToString() ?? "";
                        if (obj.TryGetPropertyValue("token", out var tk)) CloudToken = tk?.ToString() ?? "";
                        if (obj.TryGetPropertyValue("api", out var ap) && !string.IsNullOrEmpty(ap?.ToString()))
                            DeviceCloud.ApiBase = ap.ToString();
                    }
                }
            }
            catch { }

            // 以 Windows 服务方式运行
            if (args.Any(a => a.Equals("--run-as-service", StringComparison.OrdinalIgnoreCase)))
            {
                using var svc = new AgentService();
                ServiceBase.Run(svc);
                return;
            }

            var host = new AgentHost(opts);
            host.Run();
        }
    }
}
