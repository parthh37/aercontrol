using System.Buffers.Binary;
using System.Text;

namespace AerControl.Protocol;

public enum CborKind { Unsigned, Negative, Bytes, Text, Array, Map, Bool, Null, Undefined, Double, Tagged }

/// <summary>A decoded CBOR item (RFC 8949): enough for the aer's replies. Mirrors CBORValue (Swift).</summary>
public sealed class CborValue
{
    public CborKind Kind { get; private init; }
    public ulong UInt { get; private init; }
    public long Int { get; private init; }
    public byte[]? Bytes { get; private init; }
    public string? Text { get; private init; }
    public IReadOnlyList<CborValue>? Items { get; private init; }
    public IReadOnlyList<KeyValuePair<CborValue, CborValue>>? Pairs { get; private init; }
    public bool Bool { get; private init; }
    public double Double { get; private init; }
    public ulong Tag { get; private init; }

    public CborValue? this[string key] =>
        Kind == CborKind.Map ? Pairs!.FirstOrDefault(p => p.Key.Kind == CborKind.Text && p.Key.Text == key).Value : null;

    public int? AsInt => Kind switch
    {
        CborKind.Unsigned when UInt <= int.MaxValue => (int)UInt,
        CborKind.Negative when Int >= int.MinValue => (int)Int,
        _ => null,
    };

    public string? AsString => Kind == CborKind.Text ? Text : null;
    public IReadOnlyList<CborValue>? AsArray => Kind == CborKind.Array ? Items : null;

    internal static CborValue Of(CborKind k) => new() { Kind = k };
    internal static CborValue OfUInt(ulong v) => new() { Kind = CborKind.Unsigned, UInt = v };
    internal static CborValue OfNeg(long v) => new() { Kind = CborKind.Negative, Int = v };
    internal static CborValue OfBytes(byte[] b) => new() { Kind = CborKind.Bytes, Bytes = b };
    internal static CborValue OfText(string s) => new() { Kind = CborKind.Text, Text = s };
    internal static CborValue OfArray(List<CborValue> a) => new() { Kind = CborKind.Array, Items = a };
    internal static CborValue OfMap(List<KeyValuePair<CborValue, CborValue>> m) => new() { Kind = CborKind.Map, Pairs = m };
    internal static CborValue OfBool(bool b) => new() { Kind = CborKind.Bool, Bool = b };
    internal static CborValue OfDouble(double d) => new() { Kind = CborKind.Double, Double = d };
    internal static CborValue OfTag(ulong t, CborValue v) => new() { Kind = CborKind.Tagged, Tag = t, Items = [v] };
}

public sealed class CborException(string message) : Exception(message);

public ref struct CborReader
{
    private readonly ReadOnlySpan<byte> _b;
    private int _i;
    private int _depth;

    public CborReader(ReadOnlySpan<byte> bytes) { _b = bytes; _i = 0; _depth = 0; }

    /// <summary>Decodes one item; trailing bytes (BLE padding) are ignored.</summary>
    public static CborValue Decode(ReadOnlySpan<byte> bytes) => new CborReader(bytes).ReadItem();

    private byte Next() => _i < _b.Length ? _b[_i++] : throw new CborException("truncated");

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || _i + n > _b.Length) throw new CborException("truncated");
        var s = _b.Slice(_i, n);
        _i += n;
        return s;
    }

    private ulong Argument(byte info) => info switch
    {
        < 24 => info,
        24 => Next(),
        25 => BinaryPrimitives.ReadUInt16BigEndian(Take(2)),
        26 => BinaryPrimitives.ReadUInt32BigEndian(Take(4)),
        27 => BinaryPrimitives.ReadUInt64BigEndian(Take(8)),
        _ => throw new CborException($"additional info {info}"),
    };

    private bool AtBreak()
    {
        if (_i >= _b.Length) throw new CborException("truncated");
        if (_b[_i] != 0xFF) return false;
        _i++;
        return true;
    }

    private int Count(ulong n) => n <= (ulong)_b.Length ? (int)n : throw new CborException("truncated");

    public CborValue ReadItem()
    {
        if (++_depth > 32) throw new CborException("nesting too deep");
        try
        {
            byte initial = Next();
            int major = initial >> 5;
            byte info = (byte)(initial & 0x1F);

            if (info == 31)
            {
                switch (major)
                {
                    case 2 or 3:
                        var buf = new List<byte>();
                        while (!AtBreak())
                        {
                            var chunk = ReadItem();
                            buf.AddRange(chunk.Kind switch
                            {
                                CborKind.Bytes => chunk.Bytes!,
                                CborKind.Text => Encoding.UTF8.GetBytes(chunk.Text!),
                                _ => throw new CborException("bad string chunk"),
                            });
                        }
                        return major == 2 ? CborValue.OfBytes(buf.ToArray()) : CborValue.OfText(Encoding.UTF8.GetString(buf.ToArray()));
                    case 4:
                        var items = new List<CborValue>();
                        while (!AtBreak()) items.Add(ReadItem());
                        return CborValue.OfArray(items);
                    case 5:
                        var pairs = new List<KeyValuePair<CborValue, CborValue>>();
                        while (!AtBreak()) { var k = ReadItem(); pairs.Add(new(k, ReadItem())); }
                        return CborValue.OfMap(pairs);
                    default:
                        throw new CborException($"indefinite length for major type {major}");
                }
            }

            switch (major)
            {
                case 0: return CborValue.OfUInt(Argument(info));
                case 1:
                    var a = Argument(info);
                    if (a >= long.MaxValue) throw new CborException("negative overflow");
                    return CborValue.OfNeg(-1 - (long)a);
                case 2: return CborValue.OfBytes(Take(Count(Argument(info))).ToArray());
                case 3: return CborValue.OfText(Encoding.UTF8.GetString(Take(Count(Argument(info)))));
                case 4:
                {
                    int n = Count(Argument(info));
                    var list = new List<CborValue>(n);
                    for (int j = 0; j < n; j++) list.Add(ReadItem());
                    return CborValue.OfArray(list);
                }
                case 5:
                {
                    int n = Count(Argument(info));
                    var map = new List<KeyValuePair<CborValue, CborValue>>(n);
                    for (int j = 0; j < n; j++) { var k = ReadItem(); map.Add(new(k, ReadItem())); }
                    return CborValue.OfMap(map);
                }
                case 6:
                {
                    var tag = Argument(info);
                    return CborValue.OfTag(tag, ReadItem());
                }
                default:
                    return info switch
                    {
                        20 => CborValue.OfBool(false),
                        21 => CborValue.OfBool(true),
                        22 => CborValue.Of(CborKind.Null),
                        25 => CborValue.OfDouble((double)BinaryPrimitives.ReadHalfBigEndian(Take(2))),
                        26 => CborValue.OfDouble(BinaryPrimitives.ReadSingleBigEndian(Take(4))),
                        27 => CborValue.OfDouble(BinaryPrimitives.ReadDoubleBigEndian(Take(8))),
                        24 => SkipSimple(),
                        _ => CborValue.Of(CborKind.Undefined),
                    };
            }
        }
        finally
        {
            _depth--;
        }
    }

    private CborValue SkipSimple()
    {
        Next();
        return CborValue.Of(CborKind.Undefined);
    }
}

/// <summary>Message types from the vendor transcoder library. A reply's type is the request's + 50.</summary>
public enum AerMessageType : uint
{
    Provision = 101, SetTime = 102, Schedule = 103, SprayNow = 104, Refill = 105, StopSchedule = 106,
    FetchStatus = 107, RemoveSchedule = 108, RetrieveSchedule = 109, SetUniqueId = 110, GetUniqueId = 111,
    RestartSchedule = 112, TestLed = 113, GetTime = 114,
}

public static class AerMessageTypeExtensions
{
    public static int ResponseType(this AerMessageType t) => (int)t + 50;
}

/// <summary>Any reply on the RESPONSE characteristic: {mT, mN, ack, …}; ack 200 = OK.</summary>
public sealed record AerReply(int MessageType, int? MessageNumber, int? Acknowledgement, CborValue Body)
{
    public bool IsSuccess => Acknowledgement == 200;

    public static AerReply? Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var v = CborReader.Decode(bytes);
            return v["mT"]?.AsInt is int mT ? new AerReply(mT, v["mN"]?.AsInt, v["ack"]?.AsInt, v) : null;
        }
        catch (CborException)
        {
            return null;
        }
    }
}

/// <summary>Decoded fetch-status reply (mT 157). Mirrors DeviceStatusReport (Swift).</summary>
public sealed record DeviceStatusReport(
    int? MessageNumber, int? BatteryMillivolts, string? FirmwareVersion, int? CurrentStatus,
    IReadOnlyList<int> SprayNowCounts, IReadOnlyList<int> ScheduleCounts, IReadOnlyList<int> ButtonPressCounts)
{
    /// <summary>Sprays per refill (vendor app Constants.TOTAL_SPRAY).</summary>
    public const int RefillCapacity = 2200;
    public const int BatteryLowMillivolts = 2800;
    public const int BatteryCriticalMillivolts = 2700;

    public int TotalSprays => SprayNowCounts.Sum() + ScheduleCounts.Sum() + ButtonPressCounts.Sum();
    public int SpraysRemaining => Math.Max(0, RefillCapacity - TotalSprays);

    /// <summary>The vendor app's formula: round((2200 − min(total, 2200)) / 2200 × 100).</summary>
    public int RefillPercent =>
        (int)Math.Round((RefillCapacity - Math.Min(TotalSprays, RefillCapacity)) / (double)RefillCapacity * 100,
                        MidpointRounding.AwayFromZero);

    /// <summary>Rough estimate, linear from 2.4 V (empty) to 3.2 V (fresh pair of AAs).</summary>
    public int? BatteryPercentEstimate => BatteryMillivolts is int mv
        ? Math.Clamp((int)Math.Round((mv - 2400) / 800.0 * 100, MidpointRounding.AwayFromZero), 0, 100)
        : null;

    public static DeviceStatusReport? Parse(ReadOnlySpan<byte> bytes)
    {
        var reply = AerReply.Parse(bytes);
        if (reply is null || reply.MessageType != AerMessageType.FetchStatus.ResponseType()) return null;
        var v = reply.Body;
        int[] Ints(string key) => v["sC"]?[key]?.AsArray?.Select(x => x.AsInt).OfType<int>().ToArray() ?? [];
        return new DeviceStatusReport(
            reply.MessageNumber,
            v["bV"]?.AsInt,
            v["fV"]?.AsString ?? v["fV"]?.AsInt?.ToString(),
            v["cS"]?.AsInt,
            Ints("sn"), Ints("sc"), Ints("bn"));
    }
}

public static class AerCommands
{
    /// <summary>{mT:107, mN}: reply mT 157 arrives on the RESPONSE characteristic.</summary>
    public static byte[] FetchStatus(uint messageNumber)
    {
        var w = new CborWriter();
        w.BeginIndefiniteMap();
        w.WriteText("mT"); w.WriteUInt((ulong)AerMessageType.FetchStatus);
        w.WriteText("mN"); w.WriteUInt(messageNumber);
        w.EndIndefinite();
        return w.ToArray();
    }

    /// <summary>{mT:105, mN, rR:true}: a new refill went in; resets the spray counters.</summary>
    public static byte[] Refill(uint messageNumber, bool reset = true)
    {
        var w = new CborWriter();
        w.BeginIndefiniteMap();
        w.WriteText("mT"); w.WriteUInt((ulong)AerMessageType.Refill);
        w.WriteText("mN"); w.WriteUInt(messageNumber);
        w.WriteText("rR"); w.WriteBool(reset);
        w.EndIndefinite();
        return w.ToArray();
    }
}
