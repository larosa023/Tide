using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace Tide.Core;

/// <summary>
/// Starts Tide at sign-in through Task Scheduler. A Run-key entry would trigger a UAC prompt on every
/// boot because Tide needs admin rights; a scheduled task with "highest privileges" starts silently.
/// </summary>
public static class StartupManager
{
    private const string TaskName = "Tide Network Monitor";

    public static bool IsEnabled()
    {
        var (code, _) = Run($"/Query /TN \"{TaskName}\"");
        return code == 0;
    }

    public static bool SetEnabled(bool enabled, out string? error)
    {
        error = null;
        if (!enabled)
        {
            var (c, o) = Run($"/Delete /TN \"{TaskName}\" /F");
            if (c != 0 && IsEnabled()) { error = o; return false; }
            return true;
        }

        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
        var user = WindowsIdentity.GetCurrent().Name;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts Tide (network monitor) when you sign in.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>--autostart</Arguments>
                  <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exe))}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        var file = Path.Combine(Path.GetTempPath(), "tide-task.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            var (code, output) = Run($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
            if (code != 0) { error = output.Trim(); return false; }
            return true;
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    private static (int Code, string Output) Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
