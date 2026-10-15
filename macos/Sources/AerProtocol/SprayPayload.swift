import Foundation

public enum AerGATT {
    public static let serviceUUIDString = "6E400000-B5A3-F393-E0A9-E50E24DCCA9E"
    public static let characteristicUUIDString = "6E400003-B5A3-F393-E0A9-E50E24DCCA9E"
}

/// `{mT, mN, rI}` spray command, encoded as an indefinite-length CBOR map with keys
/// in the device's fixed order. See /protocol/README.md.
public struct SprayPayload: Equatable, Sendable {
    public static let defaultMessageType: UInt32 = 104
    public static let capturedMessageNumber: UInt32 = 154

    public var messageType: UInt32
    public var messageNumber: UInt32
    public var requestIndex: UInt32

    public init(messageType: UInt32 = SprayPayload.defaultMessageType,
                messageNumber: UInt32 = SprayPayload.capturedMessageNumber,
                requestIndex: UInt32 = 0) {
        self.messageType = messageType
        self.messageNumber = messageNumber
        self.requestIndex = requestIndex
    }

    public func encoded() -> [UInt8] {
        var w = CBORWriter()
        w.beginIndefiniteMap()
        w.text("mT"); w.unsigned(UInt64(messageType))
        w.text("mN"); w.unsigned(UInt64(messageNumber))
        w.text("rI"); w.unsigned(UInt64(requestIndex))
        w.endIndefinite()
        return w.bytes
    }

    public var data: Data { Data(encoded()) }
}

public enum MessageCounter {
    /// Next `mN` when auto-increment is on. Wraps within 0…255 so the payload stays at
    /// most 16 bytes, the same length as the captured packet (a 1-byte rolling counter is
    /// the most likely firmware behaviour).
    public static func next(after current: UInt32) -> UInt32 {
        (current + 1) & 0xFF
    }
}

public extension Collection where Element == UInt8 {
    var hexString: String { map { String(format: "%02x", $0) }.joined() }
}

public extension Array where Element == UInt8 {
    init?(hex: String) {
        let chars = [Character](hex.filter { !$0.isWhitespace })
        guard chars.count.isMultiple(of: 2) else { return nil }
        var out: [UInt8] = []
        out.reserveCapacity(chars.count / 2)
        for i in stride(from: 0, to: chars.count, by: 2) {
            guard let b = UInt8(String([chars[i], chars[i + 1]]), radix: 16) else { return nil }
            out.append(b)
        }
        self = out
    }
}
