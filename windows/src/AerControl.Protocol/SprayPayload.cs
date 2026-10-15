using System.Buffers.Binary;
using System.Text;

namespace AerControl.Protocol;

public static class AerGatt
{
    public const string ServiceUuid = "6E400000-B5A3-F393-E0A9-E50E24DCCA9E";
    public const string CharacteristicUuid = "6E400003-B5A3-F393-E0A9-E50E24DCCA9E";
    public static readonly Guid Service = Guid.Parse(ServiceUuid);
    public static readonly Guid Characteristic = Guid.Parse(CharacteristicUuid);
}

/// <summary>
/// Minimal CBOR writer: unsigned ints, text strings and indefinite-length maps, always
/// using the shortest integer form (RFC 8949 preferred serialization).
/// </summary>
public sealed class CborWriter
{
    private readonly List<byte> _bytes = new();

    public void BeginIndefiniteMap() => _bytes.Add(0xBF);
    public void EndIndefinite() => _bytes.Add(0xFF);
    public void WriteUInt(ulong value) => WriteHead(0, value);

    public void WriteText(string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        WriteHead(3, (ulong)utf8.Length);
        _bytes.AddRange(utf8);
    }

    public byte[] ToArray() => _bytes.ToArray();

    private void WriteHead(byte major, ulong arg)
    {
        byte m = (byte)(major << 5);
        Span<byte> buf = stackalloc byte[8];
        switch (arg)
        {
            case < 24:
                _bytes.Add((byte)(m | (byte)arg));
                break;
            case <= 0xFF:
                _bytes.Add((byte)(m | 24));
                _bytes.Add((byte)arg);
                break;
            case <= 0xFFFF:
                _bytes.Add((byte)(m | 25));
                BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)arg);
                _bytes.AddRange(buf[..2].ToArray());
                break;
            case <= 0xFFFF_FFFF:
                _bytes.Add((byte)(m | 26));
                BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)arg);
                _bytes.AddRange(buf[..4].ToArray());
                break;
            default:
                _bytes.Add((byte)(m | 27));
                BinaryPrimitives.WriteUInt64BigEndian(buf, arg);
                _bytes.AddRange(buf.ToArray());
                break;
        }
    }
}

/// <summary>
/// The <c>{mT, mN, rI}</c> spray command as an indefinite-length CBOR map, keys in the
/// device's fixed order. See /protocol/README.md.
/// </summary>
public readonly record struct SprayPayload(uint MessageType = SprayPayload.DefaultMessageType,
                                           uint MessageNumber = SprayPayload.CapturedMessageNumber,
                                           uint RequestIndex = 0)
{
    public const uint DefaultMessageType = 104;
    public const uint CapturedMessageNumber = 154;

    public SprayPayload() : this(DefaultMessageType, CapturedMessageNumber, 0) { }

    public byte[] Encode()
    {
        var w = new CborWriter();
        w.BeginIndefiniteMap();
        w.WriteText("mT"); w.WriteUInt(MessageType);
        w.WriteText("mN"); w.WriteUInt(MessageNumber);
        w.WriteText("rI"); w.WriteUInt(RequestIndex);
        w.EndIndefinite();
        return w.ToArray();
    }
}

public static class MessageCounter
{
    /// <summary>
    /// Next <c>mN</c> when auto-increment is on. Wraps within 0–255 so the payload stays at
    /// most 16 bytes (a 1-byte rolling counter is the most likely firmware behaviour).
    /// Keep this identical to MessageCounter.next(after:) in the macOS client.
    /// </summary>
    public static uint Next(uint current) => (current + 1) & 0xFF;
}

public static class Hex
{
    public static string ToHex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public static bool TryParse(string text, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        var clean = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (clean.Length % 2 != 0) return false;
        try
        {
            bytes = Convert.FromHexString(clean);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
