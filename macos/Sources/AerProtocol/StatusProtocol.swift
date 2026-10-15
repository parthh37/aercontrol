import Foundation

/// Battery / refill status reported by the device.
public struct DeviceStatusFields: Equatable, Sendable {
    public var batteryPercent: Int?
    public var spraysRemaining: Int?
    public var refillCapacity: Int?

    public init(batteryPercent: Int? = nil, spraysRemaining: Int? = nil, refillCapacity: Int? = nil) {
        self.batteryPercent = batteryPercent
        self.spraysRemaining = spraysRemaining
        self.refillCapacity = refillCapacity
    }
}

/// Status query. Not reverse-engineered yet: the device answers reads with 00 00 and sends
/// nothing unsolicited, so the official app must send a query command. Until it's decoded
/// from com.godrejcp.aermatic, `requestPayload` is nil and the UI shows "—".
public enum AerStatusProtocol {
    public static let requestPayload: [UInt8]? = nil

    public static func parse(_ bytes: [UInt8]) -> DeviceStatusFields? { nil }
}
