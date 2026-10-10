using System.Xml.Linq;
using V6Meter.Core.Startup;

namespace V6Meter.Core.Tests;

public class AutostartTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static string Value(string xml, string element) => XDocument.Parse(xml).Descendants(Ns + element).Single().Value;

    [Fact]
    public void XmlStartsAtLogonWithHighestPrivileges()
    {
        string xml = AutostartTask.BuildXml(@"D:\工具\v6meter\v6meter.exe", Sid);
        Assert.Equal("HighestAvailable", Value(xml, "RunLevel"));
        Assert.Equal("InteractiveToken", Value(xml, "LogonType"));
        Assert.Equal(Sid, XDocument.Parse(xml).Descendants(Ns + "LogonTrigger").Single().Element(Ns + "UserId")!.Value);
        Assert.Equal("PT10S", Value(xml, "Delay"));
        Assert.Equal("PT0S", Value(xml, "ExecutionTimeLimit"));
        Assert.Equal("false", Value(xml, "DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value(xml, "StopIfGoingOnBatteries"));
        Assert.Equal("IgnoreNew", Value(xml, "MultipleInstancesPolicy"));
        Assert.Equal("4", Value(xml, "Priority"));
        Assert.Equal(@"D:\工具\v6meter", Value(xml, "WorkingDirectory"));
    }

    [Theory]
    [InlineData(@"C:\Users\张三\Downloads\v6meter.exe")]
    [InlineData(@"C:\Program Files\R&D <tools>\v6meter.exe")] // XML special characters
    public void CommandRoundTrips(string exe)
    {
        string xml = AutostartTask.BuildXml(exe, Sid);
        Assert.Equal($"\"{exe}\"", Value(xml, "Command")); // quoted for spaces
        Assert.Equal(exe, AutostartTask.ParseCommand(xml));
        Assert.Equal(AutostartState.On, AutostartTask.Evaluate(xml, exe, _ => true));
    }

    [Fact]
    public void ParsesUnquotedAndEnvironmentCommands()
    {
        string xml = $"""<Task xmlns="{Ns}"><Actions><Exec><Command>%SystemRoot%\x.exe</Command></Exec></Actions></Task>""";
        Assert.Equal(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\x.exe"), AutostartTask.ParseCommand(xml));
    }

    [Fact]
    public void States()
    {
        string xml = AutostartTask.BuildXml(@"C:\old\v6meter.exe", Sid);
        Assert.Equal(AutostartState.Off, AutostartTask.Evaluate(null, @"C:\new\v6meter.exe", _ => true));
        Assert.Equal(AutostartState.On, AutostartTask.Evaluate(xml, @"c:\OLD\v6meter.exe", _ => true)); // case-insensitive
        Assert.Equal(AutostartState.Moved, AutostartTask.Evaluate(xml, @"C:\new\v6meter.exe", _ => false));
        Assert.Equal(AutostartState.OtherCopy, AutostartTask.Evaluate(xml, @"C:\new\v6meter.exe", _ => true));
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\" />")]
    public void UnusableTaskCountsAsMoved(string xml) =>
        Assert.Equal(AutostartState.Moved, AutostartTask.Evaluate(xml, @"C:\v6meter.exe", _ => true));

    [Fact]
    public void TaskNameIsPerUser() =>
        Assert.NotEqual(AutostartTask.TaskName(Sid), AutostartTask.TaskName("S-1-5-21-1-2-3-1002"));
}
