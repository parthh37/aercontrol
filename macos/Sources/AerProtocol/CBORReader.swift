import Foundation

/// A decoded CBOR data item (RFC 8949), enough for the aer's replies: ints, strings,
/// arrays, maps (definite and indefinite), simple values, floats and tags.
public indirect enum CBORValue: Equatable, Sendable {
    case unsigned(UInt64)
    case negative(Int64)
    case bytes([UInt8])
    case text(String)
    case array([CBORValue])
    case map([CBORPair])
    case bool(Bool)
    case null
    case undefined
    case double(Double)
    case tagged(UInt64, CBORValue)

    public subscript(key: String) -> CBORValue? {
        guard case .map(let pairs) = self else { return nil }
        return pairs.first { $0.key == .text(key) }?.value
    }

    public var intValue: Int? {
        switch self {
        case .unsigned(let u): return u <= UInt64(Int.max) ? Int(u) : nil
        case .negative(let n): return Int(n)
        default: return nil
        }
    }

    public var stringValue: String? {
        if case .text(let s) = self { return s }
        return nil
    }

    public var arrayValue: [CBORValue]? {
        if case .array(let a) = self { return a }
        return nil
    }
}

public struct CBORPair: Equatable, Sendable {
    public var key: CBORValue
    public var value: CBORValue
}

public enum CBORError: Error, Equatable {
    case truncated
    case invalid(String)
}

public struct CBORReader {
    private let bytes: [UInt8]
    private var index = 0
    private var depth = 0

    public init(_ bytes: [UInt8]) { self.bytes = bytes }

    /// Decodes exactly one item. Trailing bytes are ignored, because BLE notifications can
    /// be padded.
    public static func decode(_ bytes: [UInt8]) throws -> CBORValue {
        var r = CBORReader(bytes)
        return try r.readItem()
    }

    private mutating func byte() throws -> UInt8 {
        guard index < bytes.count else { throw CBORError.truncated }
        defer { index += 1 }
        return bytes[index]
    }

    private mutating func take(_ n: Int) throws -> [UInt8] {
        guard n >= 0, index + n <= bytes.count else { throw CBORError.truncated }
        defer { index += n }
        return Array(bytes[index..<index + n])
    }

    private mutating func argument(_ info: UInt8) throws -> UInt64 {
        switch info {
        case 0..<24: return UInt64(info)
        case 24: return UInt64(try byte())
        case 25, 26, 27:
            return try take(1 << Int(info - 24)).reduce(0) { $0 << 8 | UInt64($1) }
        default: throw CBORError.invalid("additional info \(info)")
        }
    }

    public mutating func readItem() throws -> CBORValue {
        depth += 1
        defer { depth -= 1 }
        guard depth < 32 else { throw CBORError.invalid("nesting too deep") }

        let initial = try byte()
        let major = initial >> 5, info = initial & 0x1F

        if info == 31 {
            switch major {
            case 2, 3:
                var chunks: [UInt8] = []
                while try peekBreak() == false {
                    switch try readItem() {
                    case .bytes(let b): chunks += b
                    case .text(let s): chunks += Array(s.utf8)
                    default: throw CBORError.invalid("bad string chunk")
                    }
                }
                return major == 2 ? .bytes(chunks) : .text(String(decoding: chunks, as: UTF8.self))
            case 4:
                var items: [CBORValue] = []
                while try peekBreak() == false { items.append(try readItem()) }
                return .array(items)
            case 5:
                var pairs: [CBORPair] = []
                while try peekBreak() == false {
                    pairs.append(CBORPair(key: try readItem(), value: try readItem()))
                }
                return .map(pairs)
            default:
                throw CBORError.invalid("indefinite length for major type \(major)")
            }
        }

        switch major {
        case 0: return .unsigned(try argument(info))
        case 1:
            let a = try argument(info)
            guard a < UInt64(Int64.max) else { throw CBORError.invalid("negative overflow") }
            return .negative(-1 - Int64(a))
        case 2: return .bytes(try take(Int(try argument(info))))
        case 3: return .text(String(decoding: try take(Int(try argument(info))), as: UTF8.self))
        case 4:
            let n = try argument(info)
            guard n <= UInt64(bytes.count) else { throw CBORError.truncated }
            return .array(try (0..<n).map { _ in try readItem() })
        case 5:
            let n = try argument(info)
            guard n <= UInt64(bytes.count) else { throw CBORError.truncated }
            return .map(try (0..<n).map { _ in CBORPair(key: try readItem(), value: try readItem()) })
        case 6: return .tagged(try argument(info), try readItem())
        default:
            switch info {
            case 20: return .bool(false)
            case 21: return .bool(true)
            case 22: return .null
            case 23: return .undefined
            case 24: _ = try byte(); return .undefined
            case 25: return .double(Double(Self.half(try take(2))))
            case 26:
                let bits = try take(4).reduce(UInt32(0)) { $0 << 8 | UInt32($1) }
                return .double(Double(Float(bitPattern: bits)))
            case 27:
                let bits = try take(8).reduce(UInt64(0)) { $0 << 8 | UInt64($1) }
                return .double(Double(bitPattern: bits))
            default: return .undefined
            }
        }
    }

    /// Consumes a break byte (0xFF) if present.
    private mutating func peekBreak() throws -> Bool {
        guard index < bytes.count else { throw CBORError.truncated }
        if bytes[index] == 0xFF { index += 1; return true }
        return false
    }

    private static func half(_ b: [UInt8]) -> Float {
        let h = UInt16(b[0]) << 8 | UInt16(b[1])
        let exp = Int((h >> 10) & 0x1F), mant = Float(h & 0x3FF)
        let sign: Float = (h & 0x8000) != 0 ? -1 : 1
        if exp == 0 { return sign * mant * powf(2, -24) }
        if exp == 31 { return mant == 0 ? sign * .infinity : .nan }
        return sign * (1 + mant / 1024) * powf(2, Float(exp - 15))
    }
}
