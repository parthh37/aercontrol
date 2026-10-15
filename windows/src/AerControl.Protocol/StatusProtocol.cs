namespace AerControl.Protocol;

/// <summary>Battery / refill status reported by the device.</summary>
public sealed record DeviceStatusFields(int? BatteryPercent, int? SpraysRemaining, int? RefillCapacity);

/// <summary>
/// Status query. Not reverse-engineered yet: the device answers reads with 00 00 and sends
/// nothing unsolicited, so the official app must send a query command. Until that's decoded
/// from com.godrejcp.aermatic, <see cref="RequestPayload"/> is null and the UI shows "—".
/// Mirrors AerStatusProtocol in the macOS client.
/// </summary>
public static class AerStatusProtocol
{
    public static byte[]? RequestPayload => null;
    public static DeviceStatusFields? Parse(ReadOnlySpan<byte> bytes) => null;
}
