using AerControl.Core;
using AerControl.Protocol;

namespace AerControl.Tests;

internal sealed class FakeLink : IBleLink
{
    public bool IsConnected { get; private set; }
    public int Connects, Disconnects;
    public List<byte[]> Writes = new();
    public Exception? ConnectError;
    public bool HangOnConnect;
    public byte[]? AutoReply;
    public int ConcurrentWrites, MaxConcurrentWrites;
    public event EventHandler<string?>? ConnectionLost;
    public event EventHandler<byte[]>? NotificationReceived;

    public async Task ConnectAsync(ulong address, CancellationToken ct)
    {
        Connects++;
        if (HangOnConnect) await Task.Delay(Timeout.Infinite, ct);
        if (ConnectError is not null) throw ConnectError;
        IsConnected = true;
    }

    public async Task<string> WriteAsync(byte[] data, WriteMode mode, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref ConcurrentWrites);
        MaxConcurrentWrites = Math.Max(MaxConcurrentWrites, now);
        await Task.Delay(20, ct);
        lock (Writes) Writes.Add(data);
        Interlocked.Decrement(ref ConcurrentWrites);
        if (AutoReply is not null) NotificationReceived?.Invoke(this, AutoReply);
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
            IdleTimeout = TimeSpan.Zero,
            ReconnectBaseDelay = TimeSpan.FromMilliseconds(10),
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

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), "condition not met in time");
    }
}
