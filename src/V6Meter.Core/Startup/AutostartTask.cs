using System.Xml.Linq;

namespace V6Meter.Core.Startup;

public enum AutostartState
{
    /// <summary>No task for this user.</summary>
    Off,
    /// <summary>The task starts this exe.</summary>
    On,
    /// <summary>The task starts an exe that no longer exists (the user moved this one): safe to repoint.</summary>
    Moved,
    /// <summary>The task starts a different copy that still exists; left alone.</summary>
    OtherCopy,
}

/// <summary>
/// Task Scheduler definition for "start at logon with highest privileges". The Run key cannot elevate
/// silently, a task with RunLevel HighestAvailable can: admin accounts get admin mode, standard accounts
/// get user mode. Registering such a task needs elevation (verified: denied from a medium-integrity token),
/// reading it does not.
/// </summary>
public static class AutostartTask
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>One task per Windows account, keyed by SID so renames and shared PCs do not collide.</summary>
    public static string TaskName(string userSid) => $"v6meter-autostart-{userSid}";

    /// <summary>Lets Explorer bring the taskbar up first.</summary>
    public static readonly TimeSpan LogonDelay = TimeSpan.FromSeconds(10);

    public static string BuildXml(string exePath, string userSid)
    {
        var doc = new XDocument(
            new XElement(Ns + "Task", new XAttribute("version", "1.2"),
                new XElement(Ns + "RegistrationInfo",
                    new XElement(Ns + "Author", "v6meter"),
                    new XElement(Ns + "Description", "登录时启动 v6meter（校园网 IPv4/IPv6 流量监控）。可在 v6meter 的托盘菜单中关闭。")),
                new XElement(Ns + "Triggers",
                    new XElement(Ns + "LogonTrigger",
                        new XElement(Ns + "Enabled", "true"),
                        new XElement(Ns + "UserId", userSid),
                        new XElement(Ns + "Delay", $"PT{(int)LogonDelay.TotalSeconds}S"))),
                new XElement(Ns + "Principals",
                    new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(Ns + "UserId", userSid),
                        new XElement(Ns + "LogonType", "InteractiveToken"),
                        new XElement(Ns + "RunLevel", "HighestAvailable"))),
                new XElement(Ns + "Settings",
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                    // The default (7) is below-normal CPU and I/O priority; 4 is normal, so the ETW consumer keeps up.
                    new XElement(Ns + "Priority", "4")),
                new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(Ns + "Exec",
                        new XElement(Ns + "Command", Quote(exePath)),
                        new XElement(Ns + "WorkingDirectory", Path.GetDirectoryName(exePath) ?? "")))));
        return doc.ToString();
    }

    /// <summary>The exe a registered task starts, or null if the XML has no Exec action.</summary>
    public static string? ParseCommand(string taskXml)
    {
        try
        {
            var cmd = XDocument.Parse(taskXml).Descendants(Ns + "Command").FirstOrDefault()?.Value.Trim();
            return string.IsNullOrEmpty(cmd) ? null : Environment.ExpandEnvironmentVariables(cmd.Trim('"'));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <param name="registeredXml">The task's XML, or null if there is no task.</param>
    public static AutostartState Evaluate(string? registeredXml, string currentExe, Func<string, bool> fileExists)
    {
        if (registeredXml == null)
            return AutostartState.Off;
        string? cmd = ParseCommand(registeredXml);
        if (cmd == null)
            return AutostartState.Moved;
        if (SamePath(cmd, currentExe))
            return AutostartState.On;
        return fileExists(cmd) ? AutostartState.OtherCopy : AutostartState.Moved;
    }

    private static string Quote(string path) => $"\"{path}\"";

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
