using System.Runtime.Versioning;
using System.ServiceProcess;

namespace OpenHW.App.Settings;

/// <summary>
/// The OpenHW Helper Windows service, from Settings → Driver Management: its state, Start and Stop. No elevation:
/// <c>openhw-helper --install</c> gives the "OpenHW Users" group and the installing account start/stop/query rights
/// on the service. Stopping it gives every fan OpenHW holds back to its BMC curve (the service does that as it stops);
/// the BMC sensors, fan control and power limits are gone until it runs again.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsHelperService
{
    public const string Name = "OpenHWHelper";

    /// <summary>"Running", "Stopped", "Starting"…, or "Not installed".</summary>
    public static string State()
    {
        try
        {
            using var service = new ServiceController(Name);
            return service.Status switch
            {
                ServiceControllerStatus.Running => "Running",
                ServiceControllerStatus.Stopped => "Stopped",
                ServiceControllerStatus.StartPending => "Starting",
                ServiceControllerStatus.StopPending => "Stopping",
                var other => other.ToString(),
            };
        }
        catch (InvalidOperationException) { return "Not installed"; }
    }

    public static bool Running => State() == "Running";

    /// <summary>Starts or stops the service and waits for it; returns an error message, or null when it worked.</summary>
    public static string? Set(bool run, TimeSpan? wait = null)
    {
        try
        {
            using var service = new ServiceController(Name);
            var target = run ? ServiceControllerStatus.Running : ServiceControllerStatus.Stopped;
            if (service.Status == target) return null;
            if (run) service.Start(); else service.Stop();
            service.WaitForStatus(target, wait ?? TimeSpan.FromSeconds(30));
            return null;
        }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 5 })
        {
            return "Access denied: reinstall the service (packaging\\windows\\install.ps1) so OpenHW Users may start and stop it.";
        }
        catch (InvalidOperationException ex) { return ex.InnerException?.Message ?? ex.Message; }
        catch (System.ServiceProcess.TimeoutException) { return $"The service didn't {(run ? "start" : "stop")} within 30 s."; }
    }
}
