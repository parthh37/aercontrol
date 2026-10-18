using AerControl.Core;
using AerControl.Protocol;

namespace AerControl.Tests;

internal sealed class FakeLink : IBleLink
{
    public bool IsConnected { get; private set; }
    public int Connects, Disconnects;
    public List<byte[]> Writes = new();
    public Exception? ConnectError;
    /// <summary>This many connects fail as Unreachable first, like Windows while another host holds the aer.</summary>
    public int BusyConnects;
    public bool HangOnConnect;
    public byte[]? AutoReply;
    /// <summary>Sent one notification at a time after each write (stray packets, MTU-sized chunks).</summary>
    public List<byte[]>? AutoNotifications;
    public int ConcurrentWrites, MaxConcurrentWrites;
    public DateTime ConnectedAtUtc;
    public List<DateTime> WriteTimesUtc = new();
    public string Diagnostics => "fake link";
    /// <summary>ATT MTU before / after the exchange, which lands <see cref="MtuExchangeAfter"/> after connecting.</summary>
    public int MtuBefore = 23, MtuAfter = 247;
    public TimeSpan MtuExchangeAfter = TimeSpan.Zero;
    public int Mtu => DateTime.UtcNow - ConnectedAtUtc >= MtuExchangeAfter ? MtuAfter : MtuBefore;
    public int MtuAtWrite;

    public async Task<int> WaitForMtuExchangeAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Mtu <= 23 && DateTime.UtcNow < deadline) await Task.Delay(10, ct);
        return Mtu;
    }
    public event EventHandler<string?>? ConnectionLost;
    public event EventHandler<byte[]>? NotificationReceived;

    public async Task ConnectAsync(ulong address, CancellationToken ct)
    {
        Connects++;
        if (HangOnConnect) await Task.Delay(Timeout.Infinite, ct);
        if (Connects <= BusyConnects) throw new BleException(BleErrorKind.Unreachable);
        if (ConnectError is not null) throw ConnectError;
        IsConnected = true;
        ConnectedAtUtc = DateTime.UtcNow;
    }

    public async Task<string> WriteAsync(byte[] data, WriteMode mode, CancellationToken ct)
    {
        MtuAtWrite = Mtu;
        var now = Interlocked.Increment(ref ConcurrentWrites);
        MaxConcurrentWrites = Math.Max(MaxConcurrentWrites, now);
        await Task.Delay(20, ct);
        lock (Writes) { Writes.Add(data); WriteTimesUtc.Add(DateTime.UtcNow); }
        Interlocked.Decrement(ref ConcurrentWrites);
        if (AutoReply is not null) NotificationReceived?.Invoke(this, AutoReply);
        if (AutoNotifications is not null)
            _ = Task.Run(async () =>
            {
                foreach (var n in AutoNotifications)
                {
                    await Task.Delay(5);
                    NotificationReceived?.Invoke(this, n);
                }
            });
        return mode == WriteMode.WithResponse ? "with-response" : "without-response";
    }

    public Task DisconnectAsync() { Disconnects++; IsConnected = false; return Task.CompletedTask; }
    public Task<IReadOnlyList<string>> DescribeGattAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["service x"]);
    public void Drop() { IsConnected = false; ConnectionLost?.Invoke(this, "test drop"); }
    public void Dispose() { }
}

internal sealed class FakeRadio(AdapterState state = AdapterState.On) : IBluetoothRadio
{
    public AdapterState State { get; set; } = state;
    public Task<AdapterState> GetStateAsync() => Task.FromResult(State);
    public event EventHandler<AdapterState>? StateChanged { add { } remove { } }
}

public class BleSessionTests
{
    private static readonly FileLogger Log = new(Path.Combine(Path.GetTempPath(), "aer-tests", "test.log"));

    private static (BleSession, FakeLink, FakeRadio) Make(ulong? device = 0xAABBCCDDEEFF)
    {
        var link = new FakeLink();
        var radio = new FakeRadio();
        var s = new BleSession(link, radio, Log)
        {
            ReplyWindow = TimeSpan.FromMilliseconds(50),
            ExpectedReplyTimeout = TimeSpan.FromMilliseconds(500),
            IdleTimeout = TimeSpan.Zero,
            ReconnectBaseDelay = TimeSpan.FromMilliseconds(10),
            BusyRetryDelay = TimeSpan.FromMilliseconds(10),
            ConnectTimeout = TimeSpan.FromMilliseconds(500),
            PostConnectSettle = TimeSpan.Zero,
            MtuExchangeTimeout = TimeSpan.FromMilliseconds(300),
        };
        s.SelectDeviceAsync(device).GetAwaiter().GetResult();
        return (s, link, radio);
    }

    [Fact]
    public async Task ConnectsOnDemandAndWrites()
    {
        var (s, link, _) = Make();
        var outcome = await s.SendAsync(new SprayPayload().Encode());
        Assert.Equal(1, link.Connects);
        Assert.Single(link.Writes);
        Assert.Equal("without-response", outcome.Mode);
        Assert.Null(outcome.Reply);
        Assert.Equal(LinkStatus.Connected, s.Status);
    }

    [Fact]
    public async Task ReusesConnectionForSecondWrite()
    {
        var (s, link, _) = Make();
        await s.SendAsync([1]);
        await s.SendAsync([2]);
        Assert.Equal(1, link.Connects);
        Assert.Equal(2, link.Writes.Count);
    }

    [Fact]
    public async Task SerializesConcurrentSends()
    {
        var (s, link, _) = Make();
        await Task.WhenAll(Enumerable.Range(0, 5).Select(i => s.SendAsync([(byte)i])));
        Assert.Equal(5, link.Writes.Count);
        Assert.Equal(1, link.MaxConcurrentWrites);
    }

    [Fact]
    public async Task CapturesNotificationAsReply()
    {
        var (s, link, _) = Make();
        link.AutoReply = [0xA1, 0x01];
        var outcome = await s.SendAsync([1]);
        Assert.Equal(new byte[] { 0xA1, 0x01 }, outcome.Reply);
    }

    /// <summary>Fetch-status reply captured from the real device (protocol/test-vectors.json), 105 bytes.</summary>
    private static readonly byte[] StatusReply = Convert.FromHexString(
        "bf626d54189d626d4e18a06361636b18c86262561a000009ca62665668332e302e362d54316263531885627343a362736e84190041" +
        "1900001900001900416273638319091a19010719000062626e8319001119005819000462735666313031313130ff");

    private static List<byte[]> Chunk(byte[] data, int size) => data.Chunk(size).ToList();

    [Fact]
    public async Task ReassemblesReplySplitAcrossMtuSizedNotifications()
    {
        var (s, link, _) = Make();
        link.AutoNotifications = Chunk(StatusReply, 20); // default ATT MTU 23 → 20-byte payloads
        var outcome = await s.SendAsync(AerCommands.FetchStatus(160), AerMessageType.FetchStatus.ResponseType());
        Assert.Equal(StatusReply, outcome.Reply);
        Assert.Equal(2506, DeviceStatusReport.Parse(outcome.Reply)!.BatteryMillivolts);
    }

    [Fact]
    public async Task SkipsUnrelatedNotificationBeforeExpectedReply()
    {
        var (s, link, _) = Make();
        var sprayAck = Convert.FromHexString("bf626d54189a626d4e18a06361636b18c8ff"); // {mT:154, mN:160, ack:200}
        link.AutoNotifications = [sprayAck, [0x00, 0x01], .. Chunk(StatusReply, 64)];
        var outcome = await s.SendAsync(AerCommands.FetchStatus(160), AerMessageType.FetchStatus.ResponseType());
        Assert.Equal(StatusReply, outcome.Reply);
    }

    [Fact]
    public async Task ExpectedReplyWaitsPastTheSettleWindow()
    {
        var (s, link, _) = Make(); // ReplyWindow 50 ms, ExpectedReplyTimeout 500 ms
        link.AutoNotifications = [.. Enumerable.Repeat(new byte[] { 0x00 }, 20), StatusReply]; // ~105 ms of noise first
        var outcome = await s.SendAsync(AerCommands.FetchStatus(160), AerMessageType.FetchStatus.ResponseType());
        Assert.Equal(StatusReply, outcome.Reply);
    }

    [Fact]
    public void AssemblerIgnoresATruncatedReply()
    {
        Assert.Null(ReplyCollector.TryAssemble([StatusReply[..40]], 157));
        Assert.Null(ReplyCollector.TryAssemble([StatusReply], 154));
        Assert.Equal(StatusReply, ReplyCollector.TryAssemble(Chunk(StatusReply, 20), 157));
    }

    [Fact]
    public async Task ReplyWindowActsAsSettleDelay()
    {
        var (s, _, _) = Make();
        s.ReplyWindow = TimeSpan.FromMilliseconds(200);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await s.SendAsync([1]);
        Assert.True(sw.ElapsedMilliseconds >= 180, $"returned after {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task NoDeviceSelectedThrows()
    {
        var (s, _, _) = Make(device: null);
        var ex = await Assert.ThrowsAsync<BleException>(() => s.SendAsync([1]));
        Assert.Equal(BleErrorKind.NoDeviceSelected, ex.Kind);
        Assert.Equal(LinkStatus.NoDevice, s.Status);
    }

    [Fact]
    public async Task BluetoothOffThrows()
    {
        var (s, _, radio) = Make();
        radio.State = AdapterState.Off;
        var ex = await Assert.ThrowsAsync<BleException>(() => s.SendAsync([1]));
        Assert.Equal(BleErrorKind.BluetoothOff, ex.Kind);
        Assert.Equal(LinkStatus.BluetoothOff, s.Status);
    }

    [Fact]
    public async Task ConnectTimeoutIsReported()
    {
        var (s, link, _) = Make();
        link.HangOnConnect = true;
        s.ConnectTimeout = TimeSpan.FromMilliseconds(100);
        var ex = await Assert.ThrowsAsync<BleException>(() => s.SendAsync([1]));
        Assert.Equal(BleErrorKind.Timeout, ex.Kind);
        Assert.Equal(LinkStatus.Disconnected, s.Status);
    }

    [Fact]
    public async Task IdleDisconnectFreesTheDevice()
    {
        var (s, link, _) = Make();
        s.IdleTimeout = TimeSpan.FromMilliseconds(150);
        await s.SendAsync([1]);
        Assert.True(link.IsConnected);
        await WaitUntil(() => !link.IsConnected, TimeSpan.FromSeconds(2));
        Assert.Equal(1, link.Disconnects);
        Assert.Equal(LinkStatus.Disconnected, s.Status);
    }

    [Fact]
    public async Task ActivityPostponesIdleDisconnect()
    {
        var (s, link, _) = Make();
        s.IdleTimeout = TimeSpan.FromMilliseconds(300);
        await s.SendAsync([1]);
        await Task.Delay(150);
        await s.SendAsync([2]);
        await Task.Delay(200);
        Assert.True(link.IsConnected); // 350 ms since first send, but only ~200 since last
    }

    [Fact]
    public async Task ReconnectsAfterUnexpectedDropWhileInUse()
    {
        var (s, link, _) = Make();
        s.IdleTimeout = TimeSpan.Zero; // stay-connected mode: always reconnect
        await s.SendAsync([1]);
        link.Drop();
        await WaitUntil(() => link.Connects == 2 && link.IsConnected, TimeSpan.FromSeconds(2));
        Assert.Equal(LinkStatus.Connected, s.Status);
    }

    [Fact]
    public async Task DoesNotReconnectWhenAutoReconnectOff()
    {
        var (s, link, _) = Make();
        s.AutoReconnect = false;
        await s.SendAsync([1]);
        link.Drop();
        await Task.Delay(200);
        Assert.Equal(1, link.Connects);
        Assert.Equal(LinkStatus.Disconnected, s.Status);
    }

    [Fact]
    public async Task WaitsForDeviceHeldByAnotherHost()
    {
        var (s, link, _) = Make();
        link.BusyConnects = 3;
        await s.SendAsync([1]);
        Assert.Equal(4, link.Connects);
        Assert.Single(link.Writes);
        Assert.Equal(LinkStatus.Connected, s.Status);
    }

    [Fact]
    public async Task ReportsUnreachableWhenDeviceStaysBusy()
    {
        var (s, link, _) = Make();
        link.ConnectError = new BleException(BleErrorKind.Unreachable);
        s.ConnectTimeout = TimeSpan.FromMilliseconds(200);
        var ex = await Assert.ThrowsAsync<BleException>(() => s.SendAsync([1]));
        Assert.Equal(BleErrorKind.Unreachable, ex.Kind);
        Assert.True(link.Connects > 1);
        Assert.Equal(LinkStatus.Disconnected, s.Status);
    }

    [Fact]
    public async Task DoesNotRetryNonBusyErrors()
    {
        var (s, link, _) = Make();
        link.ConnectError = new BleException(BleErrorKind.CharacteristicNotFound);
        await Assert.ThrowsAsync<BleException>(() => s.SendAsync([1]));
        Assert.Equal(1, link.Connects);
    }

    [Fact]
    public async Task FailedReconnectsDoNotKeepGrabbingTheDevice()
    {
        var (s, link, _) = Make();
        s.IdleTimeout = TimeSpan.FromMilliseconds(200);
        s.ConnectTimeout = TimeSpan.FromMilliseconds(100);
        await s.SendAsync([1]);
        link.ConnectError = new BleException(BleErrorKind.Unreachable);
        link.Drop();
        await Task.Delay(1500);
        var settled = link.Connects;
        await Task.Delay(800);
        Assert.Equal(settled, link.Connects); // gave up once the in-use window passed
    }

    [Fact]
    public async Task SprayControllerIncrementsMessageNumberAndRecordsHistory()
    {
        var (s, link, _) = Make();
        var dir = Path.Combine(Path.GetTempPath(), $"aer-{Guid.NewGuid()}");
        var c = new SprayController(s, Log, Path.Combine(dir, "settings.json"), Path.Combine(dir, "history.json"))
        {
            RefreshStatusAfterSpray = false,
        };
        await c.UpdateSettingsAsync(x => { x.DeviceAddress = 0xAABBCCDDEEFF; x.IdleDisconnectSeconds = 0; });

        var r1 = await c.SprayAsync(SprayTrigger.Button);
        var r2 = await c.SprayAsync(SprayTrigger.Hotkey);
        Assert.True(r1.Success && r2.Success);
        Assert.Equal(154u, r1.MessageNumber);
        Assert.Equal(155u, r2.MessageNumber);
        Assert.Equal(Hex.ToHex(new SprayPayload(MessageNumber: 155).Encode()), Hex.ToHex(link.Writes[1]));
        Assert.Equal(SprayTrigger.Hotkey, c.LastSpray!.Trigger);
        Assert.Equal(2, c.History.Count);
        Assert.Equal(156u, c.Settings.MessageNumber);
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task FailedSprayDoesNotAdvanceMessageNumber()
    {
        var (s, link, _) = Make();
        link.ConnectError = new BleException(BleErrorKind.Unreachable);
        var dir = Path.Combine(Path.GetTempPath(), $"aer-{Guid.NewGuid()}");
        var c = new SprayController(s, Log, Path.Combine(dir, "settings.json"), Path.Combine(dir, "history.json"));
        await c.UpdateSettingsAsync(x => x.DeviceAddress = 0xAABBCCDDEEFF);
        var r = await c.SprayAsync(SprayTrigger.Button);
        Assert.False(r.Success);
        Assert.Contains("unreachable", r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(154u, c.Settings.MessageNumber);
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task FirstWriteWaitsForFreshLinkToSettle()
    {
        var (s, link, _) = Make();
        s.PostConnectSettle = TimeSpan.FromMilliseconds(300);
        await s.SendAsync(new SprayPayload().Encode());
        Assert.True(link.WriteTimesUtc[0] - link.ConnectedAtUtc >= TimeSpan.FromMilliseconds(280),
                    $"wrote {(link.WriteTimesUtc[0] - link.ConnectedAtUtc).TotalMilliseconds:0} ms after connecting");
    }

    [Fact]
    public async Task NoSettleOnceTheLinkHasBeenUpLongEnough()
    {
        var (s, link, _) = Make();
        s.PostConnectSettle = TimeSpan.FromMilliseconds(300);
        await s.ConnectAsync();
        await Task.Delay(350);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await s.SendAsync([1]);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(250), $"send took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(1, link.Connects);
    }

    [Fact]
    public async Task ReconnectsAndSettlesAgainIfTheLinkDropsWhileSettling()
    {
        var (s, link, _) = Make();
        s.AutoReconnect = false;
        s.PostConnectSettle = TimeSpan.FromMilliseconds(300);
        var send = s.SendAsync([1]);
        await WaitUntil(() => link.Connects == 1 && link.IsConnected, TimeSpan.FromSeconds(1));
        link.Drop();
        await send;
        Assert.Equal(2, link.Connects);
        Assert.Single(link.Writes);
        Assert.True(link.WriteTimesUtc[0] - link.ConnectedAtUtc >= TimeSpan.FromMilliseconds(280));
    }

    [Fact]
    public async Task StatusFetchWaitsForALateMtuExchange()
    {
        var (s, link, _) = Make();
        link.MtuExchangeAfter = TimeSpan.FromMilliseconds(150);
        s.MtuExchangeTimeout = TimeSpan.FromSeconds(2);
        await s.SendAsync(AerCommands.FetchStatus(160), AerMessageType.FetchStatus.ResponseType());
        Assert.Equal(247, link.MtuAtWrite);
    }

    [Fact]
    public async Task SprayDoesNotWaitForTheMtuExchange()
    {
        var (s, link, _) = Make();
        link.MtuExchangeAfter = TimeSpan.FromSeconds(5);
        s.MtuExchangeTimeout = TimeSpan.FromSeconds(5);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await s.SendAsync(new SprayPayload().Encode());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"spray took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(23, link.MtuAtWrite);
    }

    [Fact]
    public async Task StatusFetchStillWritesWhenTheMtuNeverGrows()
    {
        var (s, link, _) = Make();
        link.MtuExchangeAfter = TimeSpan.MaxValue;
        s.MtuExchangeTimeout = TimeSpan.FromMilliseconds(100);
        await s.SendAsync(AerCommands.FetchStatus(160), AerMessageType.FetchStatus.ResponseType());
        Assert.Single(link.Writes);
        Assert.Equal(23, link.MtuAtWrite);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), "condition not met in time");
    }
}
