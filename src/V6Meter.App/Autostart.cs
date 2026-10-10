using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using V6Meter.Core.Startup;

namespace V6Meter.App;

/// <summary>
/// The "start at logon" switch, backed by a Task Scheduler task (Schedule.Service COM, built into Windows;
/// unlike schtasks.exe its XML is not squeezed through the console code page, so non-ASCII paths survive).
/// Reading works without admin rights; registering or deleting the task does not, so a non-elevated
/// process asks for UAC and lets an elevated copy of itself (<see cref="RunHelper"/>) do the change.
/// </summary>
internal static class Autostart
{
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int ErrorCancelled = 1223;

    public static string CurrentUserSid
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.User!.Value;
        }
    }

    private static string ExePath => Environment.ProcessPath!;

    public static AutostartState GetState()
    {
        try
        {
            return AutostartTask.Evaluate(QueryXml(CurrentUserSid), ExePath, File.Exists);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return AutostartState.Off;
        }
    }

    /// <summary>
    /// Turns autostart on (pointing at this exe) or off. Returns null on success, "" if the user cancelled
    /// the UAC prompt, otherwise an error message.
    /// </summary>
    public static async Task<string?> SetAsync(bool on)
    {
        string sid = CurrentUserSid;
        try
        {
            Apply(on, sid, ExePath);
            return null;
        }
        catch (Exception ex) when (IsAccessDenied(ex) && !TrayApp.IsElevated)
        {
            // Fall through to the elevated helper.
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        Process? helper;
        try
        {
            // The SID is passed along: with over-the-shoulder UAC the elevated copy runs as a different account.
            helper = Process.Start(new ProcessStartInfo(ExePath, $"--autostart {(on ? "on" : "off")} --sid {sid}")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return "";
        }
        if (helper == null)
            return "无法启动设置程序。";
        using (helper)
        {
            await helper.WaitForExitAsync();
            return helper.ExitCode == 0 ? null : $"设置开机自启失败（错误码 0x{helper.ExitCode:X8}）。";
        }
    }

    /// <summary>
    /// Elevated helper entry: <c>--autostart on|off --sid S-1-...</c>. Returns the process exit code.
    /// <c>--autostart status</c> returns the <see cref="AutostartState"/> instead (no elevation needed).
    /// </summary>
    public static int RunHelper(string[] args)
    {
        int i = Array.IndexOf(args, "--autostart");
        if (i >= 0 && i + 1 < args.Length && args[i + 1] == "status") // diagnostics: the state as exit code
            return (int)GetState();
        int s = Array.IndexOf(args, "--sid");
        if (i < 0 || i + 1 >= args.Length || s < 0 || s + 1 >= args.Length)
            return 87; // ERROR_INVALID_PARAMETER
        try
        {
            Apply(args[i + 1] == "on", args[s + 1], ExePath);
            return 0;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return ex.HResult != 0 ? ex.HResult : 1;
        }
    }

    // A denied RegisterTask was verified to throw UnauthorizedAccessException; the HRESULT check also covers
    // a COMException carrying E_ACCESSDENIED.
    private static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070005);

    private static void Apply(bool on, string sid, string exe)
    {
        dynamic root = Connect().GetFolder("\\");
        string name = AutostartTask.TaskName(sid);
        if (on)
            root.RegisterTask(name, AutostartTask.BuildXml(exe, sid), TaskCreateOrUpdate, sid, null, TaskLogonInteractiveToken, null);
        else if (QueryXml(sid) != null)
            root.DeleteTask(name, 0);
    }

    private static string? QueryXml(string sid)
    {
        dynamic root = Connect().GetFolder("\\");
        try
        {
            return (string)root.GetTask(AutostartTask.TaskName(sid)).Xml;
        }
        catch (Exception ex) when (ex is FileNotFoundException || ex.HResult == unchecked((int)0x80070002))
        {
            return null;
        }
    }

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new COMException("Task Scheduler is not available.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }
}
