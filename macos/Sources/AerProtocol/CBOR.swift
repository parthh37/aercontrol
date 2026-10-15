import Foundation

/// Minimal CBOR writer covering only what the aer payload needs: unsigned ints, short
/// text strings, and indefinite-length maps. Integers always use the shortest form
/// (RFC 8949 §4.2.1, preferred serialization).
public struct CBORWriter {
    public private(set) var bytes: [UInt8] = []

    public init() {}

    public mutating func beginIndefiniteMap() { bytes.append(0xBF) }
    public mutating func endIndefinite() { bytes.append(0xFF) }

    public mutating func unsigned(_ value: UInt64) {
        writeHead(major: 0, argument: value)
    }

    public mutating func bool(_ value: Bool) { bytes.append(value ? 0xF5 : 0xF4) }

    public mutating func text(_ string: String) {
        let utf8 = Array(string.utf8)
        writeHead(major: 3, argument: UInt64(utf8.count))
        bytes.append(contentsOf: utf8)
    }

    private mutating func writeHead(major: UInt8, argument: UInt64) {
        let m = major << 5
        switch argument {
        case 0..<24:
            bytes.append(m | UInt8(argument))
        case 24...0xFF:
            bytes.append(m | 24)
            bytes.append(UInt8(argument))
        case 0x100...0xFFFF:
            bytes.append(m | 25)
            appendBigEndian(UInt16(argument))
        case 0x1_0000...0xFFFF_FFFF:
            bytes.append(m | 26)
            appendBigEndian(UInt32(argument))
        default:
            bytes.append(m | 27)
            appendBigEndian(argument)
        }
    }

    private mutating func appendBigEndian<T: FixedWidthInteger>(_ value: T) {
        withUnsafeBytes(of: value.bigEndian) { bytes.append(contentsOf: $0) }
    }
}
