using System.Globalization;
using System.Text.Json.Serialization;

namespace AerControl.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LinkStatus { BluetoothOff, Unsupported, NoDevice, Disconnected, Connecting, Connected }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SprayTrigger { Button, Tray, Widget, Schedule, Hotkey, Cli }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WriteMode { Automatic, WithResponse, WithoutResponse }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScanFilter { ServiceOrName, Service, Name, All }

public enum AdapterState { Unknown, On, Off, Unsupported }

public static class LinkStatusExtensions
{
    public static string Label(this LinkStatus s) => s switch
    {
        LinkStatus.BluetoothOff => "Bluetooth off",
        LinkStatus.Unsupported => "No BLE adapter",
        LinkStatus.NoDevice => "No device",
        LinkStatus.Disconnected => "Idle",
        LinkStatus.Connecting => "Connecting…",
        LinkStatus.Connected => "Connected",
        _ => s.ToString(),
    };
}

public sealed record SprayRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset Date { get; init; } = DateTimeOffset.Now;
    public bool Success { get; init; }
    public SprayTrigger Trigger { get; init; }
    public uint? MessageNumber { get; init; }
    public string? WriteMode { get; init; }
    public string? Error { get; init; }
    /// <summary>Hex of any notification the device sent right after the write.</summary>
    public string? Reply { get; init; }
}

/// <summary>Battery and refill level from the device's fetch-status reply.</summary>
public sealed record DeviceStatus
{
    /// <summary>Estimated from voltage; the device reports millivolts only.</summary>
    public int? BatteryPercent { get; init; }
    public int? BatteryMillivolts { get; init; }
    public int? SpraysRemaining { get; init; }
    public int? RefillCapacity { get; init; }
    public int? RefillPercent { get; init; }
    public int? TotalSprays { get; init; }
    public string? FirmwareVersion { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    public static DeviceStatus From(Protocol.DeviceStatusReport r) => new()
    {
        BatteryPercent = r.BatteryPercentEstimate,
        BatteryMillivolts = r.BatteryMillivolts,
        SpraysRemaining = r.SpraysRemaining,
        RefillCapacity = Protocol.DeviceStatusReport.RefillCapacity,
        RefillPercent = r.RefillPercent,
        TotalSprays = r.TotalSprays,
        FirmwareVersion = r.FirmwareVersion,
    };

    /// <summary>Below 2.8 V the vendor app warns; below 2.7 V it shows a danger icon.</summary>
    /// <summary>
    /// More sprays counted than a can holds: almost always a new can that was never reset
    /// (the device zeroes its counter only on a refill-reset command).
    /// </summary>
    public bool RefillNeedsReset => TotalSprays is int t && RefillCapacity is int c && t > c;

    public bool BatteryLow => BatteryMillivolts is int mv && mv < Protocol.DeviceStatusReport.BatteryLowMillivolts;
    public string? BatteryVolts => BatteryMillivolts is int mv ? $"{mv / 1000.0:0.00} V" : null;
    public string BatteryText => BatteryVolts is string v ? (BatteryPercent is int p ? $"~{p}% · {v}" : v) : "—";
    public string RefillText => RefillPercent is int p && SpraysRemaining is int r ? $"{p}% · {r} left" : "—";
}

public sealed record DiscoveredDevice(ulong Address, string Name, short Rssi, bool AdvertisesService, DateTimeOffset LastSeen)
{
    public string AddressText => BluetoothAddress.Format(Address);
}

/// <summary>A raw advertisement as reported by the platform scanner (name may be empty).</summary>
public readonly record struct Advertisement(ulong Address, string? LocalName, short Rssi, bool AdvertisesService);

public sealed record WriteOutcome(string Mode, byte[]? Reply);

public enum BleErrorKind
{
    BluetoothOff, Unsupported, NoDeviceSelected, DeviceNotFound, Unreachable, Timeout,
    ServiceNotFound, CharacteristicNotFound, WriteFailed, Disconnected,
}

public sealed class BleException(BleErrorKind kind, string? detail = null) : Exception(Describe(kind, detail))
{
    public BleErrorKind Kind { get; } = kind;

    private static string Describe(BleErrorKind kind, string? detail) => kind switch
    {
        BleErrorKind.BluetoothOff => "Bluetooth is turned off.",
        BleErrorKind.Unsupported => "No Bluetooth LE adapter found.",
        BleErrorKind.NoDeviceSelected => "No device selected. Scan and pick your aer first.",
        BleErrorKind.DeviceNotFound => "Device not found. Is it in range and powered?",
        BleErrorKind.Unreachable => "Device unreachable. The aer accepts one connection at a time: is the phone app " +
                                    "or another computer connected to it?",
        BleErrorKind.Timeout => $"Timed out during {detail ?? "operation"}. Is the phone app or another computer connected to the aer?",
        BleErrorKind.ServiceNotFound => "The aer GATT service wasn't found on this device. Close the phone app " +
                                        "(the device allows one connection) and try again. If you paired it in " +
                                        "Windows Bluetooth settings, remove it there: AerControl doesn't need pairing.",
        BleErrorKind.CharacteristicNotFound => "The spray characteristic wasn't found on this device.",
        BleErrorKind.WriteFailed => $"Write failed: {detail}",
        BleErrorKind.Disconnected => $"Disconnected{(detail is null ? "" : $": {detail}")}.",
        _ => kind.ToString(),
    };
}

public static class BluetoothAddress
{
    public static string Format(ulong address) =>
        string.Join(":", Enumerable.Range(0, 6).Reverse().Select(i => ((address >> (i * 8)) & 0xFF).ToString("X2")));

    /// <summary>Accepts <c>AA:BB:CC:DD:EE:FF</c>, <c>AA-BB-…</c> or bare hex.</summary>
    public static bool TryParse(string text, out ulong address)
    {
        var hex = text.Replace(":", "").Replace("-", "").Trim();
        return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address)
               && hex.Length is > 0 and <= 12;
    }
}

public static class ScanMatcher
{
    /// <summary>The device advertises "Smart Matic" and omits the service UUID.</summary>
    public const string DefaultNameFilter = "aer, Smart Matic";

    public static bool Passes(ScanFilter filter, string nameFilter, string? name, bool advertisesService)
    {
        var terms = nameFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool nameMatch = !string.IsNullOrEmpty(name)
                         && terms.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase));
        return filter switch
        {
            ScanFilter.All => true,
            ScanFilter.Service => advertisesService,
            ScanFilter.Name => nameMatch,
            _ => advertisesService || nameMatch,
        };
    }
}
