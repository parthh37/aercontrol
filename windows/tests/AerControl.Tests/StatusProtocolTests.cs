using System.Text.Json;
using AerControl.Core;
using AerControl.Protocol;

namespace AerControl.Tests;

public class StatusProtocolTests
{
    private sealed record Command(string name, uint mT, uint mN, bool? rR, string hex);
    private sealed record Expect(int mT, int mN, int ack, int batteryMillivolts, string firmware, int currentStatus,
                                 int[] sprayNow, int[] schedule, int[] buttonPress, int totalSprays,
                                 int spraysRemaining, int refillPercent, int batteryPercentEstimate);
    private sealed record Reply(string name, string hex, Expect expect);
    private sealed record Vectors(List<Command> commands, List<Reply> responses);

    private static Vectors Load()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "protocol", "test-vectors.json");
            if (File.Exists(path)) return JsonSerializer.Deserialize<Vectors>(File.ReadAllText(path))!;
        }
        throw new FileNotFoundException("protocol/test-vectors.json");
    }

    [Fact]
    public void CommandsMatchVectors()
    {
        foreach (var c in Load().commands)
        {
            var bytes = c.mT switch
            {
                107 => AerCommands.FetchStatus(c.mN),
                105 => AerCommands.Refill(c.mN, c.rR ?? true),
                _ => throw new InvalidOperationException(c.name),
            };
            Assert.True(c.hex == Hex.ToHex(bytes), $"{c.name}: {Hex.ToHex(bytes)}");
        }
    }

    [Fact]
    public void DecodesCapturedStatusReply()
    {
        foreach (var r in Load().responses)
        {
            Assert.True(Hex.TryParse(r.hex, out var bytes));
            var reply = AerReply.Parse(bytes)!;
            Assert.Equal(r.expect.mT, reply.MessageType);
            Assert.Equal(r.expect.mN, reply.MessageNumber);
            Assert.Equal(r.expect.ack, reply.Acknowledgement);
            Assert.True(reply.IsSuccess);

            var s = DeviceStatusReport.Parse(bytes)!;
            Assert.Equal(r.expect.batteryMillivolts, s.BatteryMillivolts);
            Assert.Equal(r.expect.firmware, s.FirmwareVersion);
            Assert.Equal(r.expect.currentStatus, s.CurrentStatus);
            Assert.Equal(r.expect.sprayNow, s.SprayNowCounts);
            Assert.Equal(r.expect.schedule, s.ScheduleCounts);
            Assert.Equal(r.expect.buttonPress, s.ButtonPressCounts);
            Assert.Equal(r.expect.totalSprays, s.TotalSprays);
            Assert.Equal(r.expect.spraysRemaining, s.SpraysRemaining);
            Assert.Equal(r.expect.refillPercent, s.RefillPercent);
            Assert.Equal(r.expect.batteryPercentEstimate, s.BatteryPercentEstimate);

            var status = DeviceStatus.From(s);
            Assert.True(status.BatteryLow);
            Assert.True(status.BatteryCritical);
            Assert.Equal("2.51 V", status.BatteryVolts);
            Assert.Equal("0% · 0 left", status.RefillText);
        }
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1100, 50)]
    [InlineData(2189, 1)]
    [InlineData(2200, 0)]
    [InlineData(5000, 0)]
    public void RefillPercentMatchesVendorFormula(int total, int percent) =>
        Assert.Equal(percent, new DeviceStatusReport(null, null, null, null, [total], [], []).RefillPercent);

    [Theory]
    [InlineData(2506, true, true)]   // the reading behind the 2026-10-18 misfires
    [InlineData(2699, true, true)]
    [InlineData(2700, true, false)]  // the vendor app's danger level is "below 2.7 V"
    [InlineData(2799, true, false)]
    [InlineData(2800, false, false)]
    [InlineData(null, false, false)] // no reading yet
    public void BatteryThresholdsMatchVendorApp(int? millivolts, bool low, bool critical)
    {
        var s = new DeviceStatus { BatteryMillivolts = millivolts };
        Assert.Equal(low, s.BatteryLow);
        Assert.Equal(critical, s.BatteryCritical);
        Assert.Equal(critical, s.BatteryWarning is not null);
    }

    [Fact]
    public void BatteryWarningNamesTheVoltage() =>
        Assert.Equal("Batteries low (2.50 V). Replace them: sprays may not fire.",
                     new DeviceStatus { BatteryMillivolts = 2500 }.BatteryWarning);

    [Fact]
    public void StatusParserRejectsOtherReplies()
    {
        Assert.True(Hex.TryParse("bf626d54189a626d4e18a06361636b18c8ff", out var ack)); // {mT:154, mN:160, ack:200}
        Assert.Equal(154, AerReply.Parse(ack)!.MessageType);
        Assert.Null(DeviceStatusReport.Parse(ack));
        Assert.Null(AerReply.Parse([0x00, 0x00]));
        Assert.Null(AerReply.Parse([]));
    }

    [Fact]
    public void CborReaderHandlesDefiniteAndIndefiniteForms()
    {
        byte[] definite = [0xa1, 0x61, 0x61, 0x85, 0x01, 0x21, 0x61, 0x78, 0xf5, 0xf6];
        byte[] indefinite = [0xbf, 0x61, 0x61, 0x9f, 0x01, 0x21, 0x61, 0x78, 0xf5, 0xf6, 0xff, 0xff];
        foreach (var b in new[] { definite, indefinite })
        {
            var a = CborReader.Decode(b)["a"]!.AsArray!;
            Assert.Equal(1, a[0].AsInt);
            Assert.Equal(-2, a[1].AsInt);
            Assert.Equal("x", a[2].AsString);
            Assert.True(a[3].Bool);
            Assert.Equal(CborKind.Null, a[4].Kind);
        }
        Assert.Throws<CborException>(() => CborReader.Decode([0xbf, 0x61]));
    }

    [Theory]
    [InlineData("bf626d54189a626d4e18a06361636b18c8ff", true, "without-response, confirmed")]
    [InlineData("bf626d54189a626d4e18a06361636b1864ff", false, "without-response")]
    [InlineData(null, true, "without-response")]
    public void SprayAckInterpretation(string? replyHex, bool success, string mode)
    {
        byte[]? reply = replyHex is null ? null : Convert.FromHexString(replyHex);
        var r = SprayController.RecordFor(new WriteOutcome("without-response", reply), SprayTrigger.Button, 160);
        Assert.Equal(success, r.Success);
        Assert.Equal(mode, r.WriteMode);
        if (!success) Assert.Contains("ack 100", r.Error);
    }
}
