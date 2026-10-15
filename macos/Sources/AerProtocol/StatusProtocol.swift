import Foundation

/// Message types, from the vendor app's transcoder (com.cwdin.cwdtranscoderlibrary).
/// A reply's type is the request's type + 50.
public enum AerMessageType: UInt32, Sendable {
    case provision = 101, setTime = 102, schedule = 103, sprayNow = 104, refill = 105
    case stopSchedule = 106, fetchStatus = 107, removeSchedule = 108, retrieveSchedule = 109
    case setUniqueID = 110, getUniqueID = 111, restartSchedule = 112, testLED = 113, getTime = 114

    public var responseType: UInt32 { rawValue + 50 }
}

/// Any reply on the RESPONSE characteristic: `{mT, mN, ack, …}`. `ack == 200` means OK.
public struct AerReply: Equatable, Sendable {
    public var messageType: Int
    public var messageNumber: Int?
    public var acknowledgement: Int?
    public var body: CBORValue

    public var isSuccess: Bool { acknowledgement == 200 }

    public static func parse(_ bytes: [UInt8]) -> AerReply? {
        guard let v = try? CBORReader.decode(bytes), let mT = v["mT"]?.intValue else { return nil }
        return AerReply(messageType: mT, messageNumber: v["mN"]?.intValue,
                        acknowledgement: v["ack"]?.intValue, body: v)
    }
}

/// Decoded fetch-status reply (mT 157).
///
/// ```
/// {_ "mT":157, "mN":160, "ack":200, "bV":2506, "fV":"3.0.6-T1", "cS":133,
///    "sC":{"sn":[65,0,0,65], "sc":[2330,263,0], "bn":[17,88,4]}, "sV":"101110"}
/// ```
public struct DeviceStatusReport: Equatable, Sendable {
    /// A full refill lasts this many sprays (vendor app `Constants.TOTAL_SPRAY`).
    public static let refillCapacity = 2200
    /// The vendor app shows a "low battery" warning from 2.70 V to 2.799 V…
    public static let batteryLowMillivolts = 2800
    /// …and a "danger" battery icon below 2.7 V.
    public static let batteryCriticalMillivolts = 2700

    public var messageNumber: Int?
    public var batteryMillivolts: Int?
    public var firmwareVersion: String?
    public var currentStatus: Int?
    /// Spray-now counts per interval slot (0/10/20/40 min).
    public var sprayNowCounts: [Int]
    /// Scheduled-spray counts per interval (10/20/40 min).
    public var scheduleCounts: [Int]
    /// Device button presses per interval (10/20/40 min).
    public var buttonPressCounts: [Int]

    /// Sprays since the last refill reset; the sum of every counter, as in the vendor app.
    public var totalSprays: Int { (sprayNowCounts + scheduleCounts + buttonPressCounts).reduce(0, +) }

    public var spraysRemaining: Int { max(0, Self.refillCapacity - totalSprays) }

    /// The vendor app's formula: round((2200 − min(total, 2200)) / 2200 × 100).
    public var refillPercent: Int {
        let used = min(totalSprays, Self.refillCapacity)
        return Int((Double(Self.refillCapacity - used) / Double(Self.refillCapacity) * 100).rounded())
    }

    /// Rough percentage. The vendor app shows no percentage, only OK (≥ 2.7 V) or danger.
    /// Linear from 2.4 V (empty) to 3.2 V (fresh pair of alkaline AAs).
    public var batteryPercentEstimate: Int? {
        guard let mv = batteryMillivolts else { return nil }
        return min(100, max(0, Int((Double(mv - 2400) / 800 * 100).rounded())))
    }

    public static func parse(_ bytes: [UInt8]) -> DeviceStatusReport? {
        guard let reply = AerReply.parse(bytes),
              reply.messageType == Int(AerMessageType.fetchStatus.responseType) else { return nil }
        let v = reply.body
        func ints(_ key: String) -> [Int] { v["sC"]?[key]?.arrayValue?.compactMap(\.intValue) ?? [] }
        return DeviceStatusReport(
            messageNumber: reply.messageNumber,
            batteryMillivolts: v["bV"]?.intValue,
            firmwareVersion: v["fV"]?.stringValue ?? v["fV"]?.intValue.map(String.init),
            currentStatus: v["cS"]?.intValue,
            sprayNowCounts: ints("sn"),
            scheduleCounts: ints("sc"),
            buttonPressCounts: ints("bn"))
    }
}

public enum AerCommands {
    /// `{mT:107, mN}` → reply mT 157 on the RESPONSE characteristic.
    public static func fetchStatus(messageNumber: UInt32) -> [UInt8] {
        var w = CBORWriter()
        w.beginIndefiniteMap()
        w.text("mT"); w.unsigned(UInt64(AerMessageType.fetchStatus.rawValue))
        w.text("mN"); w.unsigned(UInt64(messageNumber))
        w.endIndefinite()
        return w.bytes
    }

    /// `{mT:105, mN, rR:true}`: tells the device a new refill was inserted, which resets
    /// the spray counters (the vendor app's "Reset level").
    public static func refill(messageNumber: UInt32, reset: Bool = true) -> [UInt8] {
        var w = CBORWriter()
        w.beginIndefiniteMap()
        w.text("mT"); w.unsigned(UInt64(AerMessageType.refill.rawValue))
        w.text("mN"); w.unsigned(UInt64(messageNumber))
        w.text("rR"); w.bool(reset)
        w.endIndefinite()
        return w.bytes
    }
}
