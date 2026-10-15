using System.Text.Json;
using AerControl.Protocol;

namespace AerControl.Tests;

public class ProtocolTests
{
    private sealed record Vector(string name, uint mT, uint mN, uint rI, string hex);
    private sealed record VectorFile(string service_uuid, string characteristic_uuid, List<Vector> vectors);

    /// <summary>Walks up from the test binary to find /protocol/test-vectors.json (shared with macOS).</summary>
    private static VectorFile LoadVectors()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "protocol", "test-vectors.json");
            if (File.Exists(path)) return JsonSerializer.Deserialize<VectorFile>(File.ReadAllText(path))!;
        }
        throw new FileNotFoundException("protocol/test-vectors.json not found above " + AppContext.BaseDirectory);
    }

    public static IEnumerable<object[]> Vectors() => LoadVectors().vectors.Select(v => new object[] { v.name, v.mT, v.mN, v.rI, v.hex });

    [Fact]
    public void CapturedPayloadMatchesExactly()
    {
        byte[] expected = [0xbf, 0x62, 0x6d, 0x54, 0x18, 0x68, 0x62, 0x6d, 0x4e, 0x18, 0x9a, 0x62, 0x72, 0x49, 0x00, 0xff];
        Assert.Equal(expected, new SprayPayload().Encode());
        Assert.Equal(16, new SprayPayload().Encode().Length);
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void SharedVector(string name, uint mT, uint mN, uint rI, string hex)
    {
        var bytes = new SprayPayload(mT, mN, rI).Encode();
        Assert.True(hex == Hex.ToHex(bytes), $"{name}: expected {hex}, got {Hex.ToHex(bytes)}");
    }

    [Fact]
    public void UuidsMatchSpec()
    {
        var f = LoadVectors();
        Assert.Equal(AerGatt.ServiceUuid, f.service_uuid);
        Assert.Equal(AerGatt.CharacteristicUuid, f.characteristic_uuid);
    }

    [Fact]
    public void MessageCounterWrapsWithinOneByte()
    {
        Assert.Equal(155u, MessageCounter.Next(154));
        Assert.Equal(0u, MessageCounter.Next(255));
        for (uint n = 0; n <= 255; n++)
            Assert.True(new SprayPayload(MessageNumber: MessageCounter.Next(n)).Encode().Length <= 16);
    }

    [Fact]
    public void HexRoundTrip()
    {
        Assert.True(Hex.TryParse("bf626d541868626d4e189a62724900ff", out var bytes));
        Assert.Equal(new SprayPayload().Encode(), bytes);
        Assert.False(Hex.TryParse("abc", out _));
        Assert.False(Hex.TryParse("zz", out _));
    }
}
