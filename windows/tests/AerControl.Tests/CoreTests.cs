using AerControl.Core;

namespace AerControl.Tests;

public class CoreTests
{
    [Fact]
    public void SettingsToleratesMissingKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aer-{Guid.NewGuid()}.json");
        File.WriteAllText(path, """{ "NameFilter": "godrej", "IdleDisconnectSeconds": 0 }""");
        var s = new JsonFileStore<AerSettings>(path).Load();
        Assert.Equal("godrej", s.NameFilter);
        Assert.Equal(0, s.IdleDisconnectSeconds);
        Assert.Equal(154u, s.MessageNumber);
        Assert.True(s.AutoIncrementMessageNumber);
        Assert.Equal(WriteMode.Automatic, s.WriteMode);
        Assert.Null(s.DeviceAddress);
        File.Delete(path);
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aer-{Guid.NewGuid()}.json");
        var store = new JsonFileStore<AerSettings>(path);
        var s = new AerSettings { DeviceAddress = 0xD884_00A8_FDE1, WriteMode = WriteMode.WithoutResponse };
        s.Schedule.Enabled = true;
        s.Schedule.Window = new ScheduleWindow(22 * 60, 6 * 60);
        store.Save(s);
        var loaded = store.Load();
        Assert.Equal(s.DeviceAddress, loaded.DeviceAddress);
        Assert.Equal(WriteMode.WithoutResponse, loaded.WriteMode);
        Assert.True(loaded.Schedule.Enabled);
        Assert.Equal(new ScheduleWindow(22 * 60, 6 * 60), loaded.Schedule.Window);
        File.Delete(path);
    }

    [Fact]
    public void CorruptSettingsFallBackToDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aer-{Guid.NewGuid()}.json");
        File.WriteAllText(path, "{ not json");
        Assert.Equal(20, new JsonFileStore<AerSettings>(path).Load().IdleDisconnectSeconds);
        File.Delete(path);
    }

    [Theory]
    [InlineData("D8:84:00:A8:FD:E1", 0xD88400A8FDE1UL)]
    [InlineData("d8-84-00-a8-fd-e1", 0xD88400A8FDE1UL)]
    [InlineData("D88400A8FDE1", 0xD88400A8FDE1UL)]
    public void ParsesBluetoothAddresses(string text, ulong expected)
    {
        Assert.True(BluetoothAddress.TryParse(text, out var a));
        Assert.Equal(expected, a);
        Assert.Equal("D8:84:00:A8:FD:E1", BluetoothAddress.Format(a));
    }

    [Fact]
    public void RejectsBadAddresses()
    {
        Assert.False(BluetoothAddress.TryParse("not-an-address", out _));
        Assert.False(BluetoothAddress.TryParse("D8:84:00:A8:FD:E1:00", out _));
    }

    [Theory]
    [InlineData(ScanFilter.ServiceOrName, "Smart Matic", false, true)]
    [InlineData(ScanFilter.ServiceOrName, "smart matic", false, true)]
    [InlineData(ScanFilter.ServiceOrName, "Parth's iPhone", false, false)]
    [InlineData(ScanFilter.ServiceOrName, null, true, true)]
    [InlineData(ScanFilter.Service, "Smart Matic", false, false)]
    [InlineData(ScanFilter.Name, "aer-1234", false, true)]
    [InlineData(ScanFilter.All, null, false, true)]
    public void ScanFilterMatching(ScanFilter filter, string? name, bool hasService, bool expected) =>
        Assert.Equal(expected, ScanMatcher.Passes(filter, ScanMatcher.DefaultNameFilter, name, hasService));

    [Theory]
    [InlineData("v1.2.0", "1.1.9", true)]
    [InlineData("2.0.0", "1.99.99", true)]
    [InlineData("1.0.1", "1.0.0.0", true)]
    [InlineData("1.0.0", "1.0.0.0", false)]
    [InlineData("1.2", "1.2.0", false)]
    [InlineData("v1.0.0", "1.0.1", false)]
    public void ComparesVersions(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateChecker.IsNewer(candidate, current));

    [Fact]
    public void ParsesGitHubRelease()
    {
        var r = UpdateChecker.Parse("""
            {"tag_name":"v1.3.0","html_url":"https://github.com/parthh37/aercontrol/releases/tag/v1.3.0","body":"n",
             "assets":[{"name":"AerControl-x64.appinstaller","browser_download_url":"https://example.com/a.appinstaller"},
                       {"name":"AerControl-windows-ARM64.zip","browser_download_url":"https://example.com/arm.zip"}]}
            """);
        Assert.Equal("1.3.0", r.Version);
        Assert.Equal("https://example.com/a.appinstaller", r.WindowsDownloadUrl("x64").ToString());
        Assert.Equal("https://example.com/arm.zip", r.WindowsDownloadUrl("ARM64").ToString());
    }

    private static DateTime At(int h, int m) => new(2026, 10, 15, h, m, 0, DateTimeKind.Local);

    [Fact]
    public void DaytimeWindow()
    {
        var w = new ScheduleWindow(9 * 60, 21 * 60);
        Assert.True(w.Contains(At(9, 0)));
        Assert.True(w.Contains(At(12, 30)));
        Assert.False(w.Contains(At(8, 59)));
        Assert.False(w.Contains(At(23, 0)));
    }

    [Fact]
    public void EvaluatorRespectsInterval()
    {
        var sched = new SpraySchedule { Enabled = true, IntervalMinutes = 30 };
        var e = new ScheduleEvaluator(sched);
        Assert.True(e.IsDue(At(10, 0), null));
        Assert.False(e.IsDue(At(10, 20), At(10, 0)));
        Assert.True(e.IsDue(At(10, 30), At(10, 0)));
        sched.Enabled = false;
        Assert.False(e.IsDue(At(10, 0), null));
    }
}
