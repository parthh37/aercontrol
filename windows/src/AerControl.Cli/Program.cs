using AerControl.Ble.Windows;
using AerControl.Core;
using AerControl.Protocol;

// aerctl: Phase 1 command-line spray test (Windows). Mirrors macos/Sources/aerctl.

const string Usage = """
usage: aerctl <command> [options]

commands:
  scan [--seconds N] [--all]       list nearby devices (default 10s, aer/service filter)
  spray [--device ADDR] [--mn N] [--no-increment] [--mode auto|with|without]
        [--count N] [--interval SECONDS] [--raw HEX]
        [--settle SECONDS]         wait after connecting before writing (default 2)
                                   connect and send the spray payload
  inspect [--device ADDR]          connect and list all GATT services/characteristics
  status [--device ADDR]           battery, refill level, firmware (fetch status)
  refill-reset [--device ADDR] --yes
                                   tell the device a new refill went in (resets counters)
  remember <ADDR>                  set the default device (AA:BB:CC:DD:EE:FF)
  forget                           clear the default device
  payload [--mn N]                 print the payload hex without sending
  log                              print the log file path

State lives in %LOCALAPPDATA%\AerControl\cli\. Set AER_VERBOSE=1 to echo the log.
The device allows one BLE connection at a time; close the phone app first.
""";

var argv = args.ToList();
var command = argv.FirstOrDefault() ?? "help";
var options = new Dictionary<string, string>();
var flags = new HashSet<string>();
string[] valued = ["seconds", "device", "mn", "mode", "count", "interval", "raw", "settle"];
var positional = new List<string>();
for (int i = 1; i < argv.Count; i++)
{
    if (argv[i].StartsWith("--"))
    {
        var key = argv[i][2..];
        if (valued.Contains(key) && i + 1 < argv.Count) options[key] = argv[++i];
        else flags.Add(key);
    }
    else positional.Add(argv[i]);
}

AppPaths.DataDirectory = Path.Combine(AppPaths.DataDirectory, "cli");
var log = FileLogger.Shared;
log.EchoToConsole = Environment.GetEnvironmentVariable("AER_VERBOSE") is not null;

static int Fail(string message)
{
    Console.Error.WriteLine($"error: {message}");
    return 1;
}

SprayController NewController(out ScanSession scan)
{
    var (session, s) = WinRtBle.Create(log);
    scan = s;
    return new SprayController(session, log, AppPaths.SettingsFile, AppPaths.HistoryFile);
}

async Task<int?> ApplyDeviceOption(SprayController c)
{
    if (!options.TryGetValue("device", out var d)) return null;
    if (!BluetoothAddress.TryParse(d, out var addr)) return Fail("--device must be a BLE address like AA:BB:CC:DD:EE:FF");
    await c.UpdateSettingsAsync(s => s.DeviceAddress = addr);
    return null;
}

switch (command)
{
    case "payload":
    {
        uint mn = options.TryGetValue("mn", out var s) && uint.TryParse(s, out var v) ? v : SprayPayload.CapturedMessageNumber;
        var bytes = new SprayPayload(MessageNumber: mn).Encode();
        Console.WriteLine($"{string.Join(" ", bytes.Select(b => b.ToString("x2")))} ({bytes.Length} bytes)");
        return 0;
    }

    case "log":
        Console.WriteLine(log.FilePath);
        return 0;

    case "remember":
    {
        if (positional.Count == 0 || !BluetoothAddress.TryParse(positional[0], out var addr))
            return Fail("remember needs a BLE address like AA:BB:CC:DD:EE:FF");
        var c = NewController(out _);
        await c.UpdateSettingsAsync(s => s.DeviceAddress = addr);
        Console.WriteLine($"default device set to {BluetoothAddress.Format(addr)}");
        return 0;
    }

    case "forget":
    {
        var c = NewController(out _);
        await c.UpdateSettingsAsync(s => { s.DeviceAddress = null; s.DeviceName = null; });
        Console.WriteLine("default device cleared");
        return 0;
    }

    case "scan":
    {
        NewController(out var scan);
        if (flags.Contains("all")) scan.Filter = ScanFilter.All;
        double seconds = options.TryGetValue("seconds", out var s) && double.TryParse(s, out var v) ? v : 10;
        Console.WriteLine($"scanning {seconds:0}s (filter: {scan.Filter})…");
        scan.Start(TimeSpan.FromSeconds(seconds));
        await Task.Delay(TimeSpan.FromSeconds(seconds + 0.3));
        scan.Stop();
        var devices = scan.Devices;
        if (devices.Count == 0) Console.WriteLine("no matching devices. Try --all, and make sure the phone app is disconnected.");
        foreach (var d in devices)
            Console.WriteLine($"{d.Rssi,4} dBm  {(d.AdvertisesService ? "svc✓" : "    ")}  {d.AddressText}  {d.Name}");
        return 0;
    }

    case "inspect":
    {
        var c = NewController(out _);
        if (await ApplyDeviceOption(c) is int code) return code;
        await c.InitializeAsync();
        try
        {
            foreach (var line in await c.Session.InspectAsync()) Console.WriteLine(line);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            await c.Session.DisconnectAsync("cli done");
        }
        return 0;
    }

    case "status":
    case "refill-reset":
    {
        var c = NewController(out _);
        if (await ApplyDeviceOption(c) is int code) return code;
        if (c.Settings.DeviceAddress is null) return Fail("no device. Use --device or `aerctl remember`");
        await c.UpdateSettingsAsync(s => s.IdleDisconnectSeconds = 0);
        await c.InitializeAsync();
        try
        {
            if (command == "refill-reset")
            {
                if (!flags.Contains("yes")) return Fail("this resets the device's spray counter; re-run with --yes after inserting a new refill");
                await c.ResetRefillAsync();
                Console.WriteLine("refill reset");
            }
            else
            {
                await c.RefreshDeviceStatusAsync();
            }
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            await c.Session.DisconnectAsync("cli done");
        }
        if (c.DeviceStatus is not DeviceStatus st) return Fail("no status reply from the device");
        Console.WriteLine($"battery:  {st.BatteryVolts}  (~{st.BatteryPercent}%, estimated){(st.BatteryLow ? "  LOW" : "")}");
        Console.WriteLine($"refill:   {st.RefillPercent}%  ({st.SpraysRemaining} of {st.RefillCapacity} left, {st.TotalSprays} sprays since reset)");
        Console.WriteLine($"firmware: {st.FirmwareVersion}");
        return 0;
    }

    case "spray":
    {
        var c = NewController(out _);
        if (await ApplyDeviceOption(c) is int code) return code;
        byte[]? raw = null;
        if (options.TryGetValue("raw", out var rawHex) && !Hex.TryParse(rawHex, out raw))
            return Fail("--raw must be hex, e.g. bf626d54…ff");
        uint? mn = null;
        if (options.TryGetValue("mn", out var mnText))
        {
            if (!uint.TryParse(mnText, out var parsed)) return Fail("--mn must be a non-negative integer");
            mn = parsed;
        }
        var mode = (options.GetValueOrDefault("mode") ?? "auto") switch
        {
            "auto" => (WriteMode?)WriteMode.Automatic,
            "with" => WriteMode.WithResponse,
            "without" => WriteMode.WithoutResponse,
            _ => null,
        };
        if (mode is null) return Fail("--mode must be auto, with, or without");

        await c.UpdateSettingsAsync(s =>
        {
            if (mn is uint m) s.MessageNumber = m;
            s.WriteMode = mode.Value;
            // mN auto-increments by default as a safeguard (see protocol/README.md field notes).
            s.AutoIncrementMessageNumber = !flags.Contains("no-increment");
            s.IdleDisconnectSeconds = 0; // the CLI disconnects explicitly
        });
        if (c.Settings.DeviceAddress is null)
            return Fail("no device. Run `aerctl scan`, then `aerctl remember <ADDR>` or pass --device");
        await c.InitializeAsync();
        if (options.TryGetValue("settle", out var settleText))
        {
            if (!double.TryParse(settleText, System.Globalization.CultureInfo.InvariantCulture, out var settle) || settle < 0)
                return Fail("--settle must be a number of seconds");
            c.Session.PostConnectSettle = TimeSpan.FromSeconds(settle);
        }

        int count = options.TryGetValue("count", out var cs) && int.TryParse(cs, out var n) ? Math.Max(1, n) : 1;
        double interval = options.TryGetValue("interval", out var iv) && double.TryParse(iv, out var ivs) ? ivs : 3;
        int failures = 0;
        for (int i = 1; i <= count; i++)
        {
            var r = await c.SprayAsync(SprayTrigger.Cli, raw);
            if (r.Success)
            {
                Console.WriteLine($"[{i}/{count}] sent (mN={r.MessageNumber}, {r.WriteMode})");
                Console.WriteLine(r.Reply is null ? $"    no reply within {c.Session.ReplyWindow.TotalSeconds}s" : $"    reply: {r.Reply}");
            }
            else
            {
                failures++;
                Console.WriteLine($"[{i}/{count}] FAILED: {r.Error}");
            }
            if (i < count) await Task.Delay(TimeSpan.FromSeconds(interval));
        }
        await c.Session.DisconnectAsync("cli done");
        return failures == 0 ? 0 : 2;
    }

    default:
        Console.WriteLine(Usage);
        return command is "help" or "--help" or "-h" ? 0 : 1;
}
