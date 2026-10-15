import Foundation

/// Renders CBOR bytes in diagnostic notation, e.g. `{_ "mT": 104, "mN": 154}`. Used only
/// to log what the device sends back, so it's lenient: on malformed input it returns
/// what it parsed so far plus the remaining hex.
public enum CBORDiagnostic {
    public static func describe(_ bytes: [UInt8]) -> String {
        var p = Parser(bytes: bytes)
        var items: [String] = []
        while p.index < bytes.count {
            guard let item = p.item() else {
                items.append("<undecodable: \(bytes[p.index...].hexString)>")
                break
            }
            items.append(item)
        }
        return items.joined(separator: " ")
    }

    private struct Parser {
        let bytes: [UInt8]
        var index = 0
        var depth = 0

        mutating func byte() -> UInt8? {
            guard index < bytes.count else { return nil }
            defer { index += 1 }
            return bytes[index]
        }

        mutating func argument(_ info: UInt8) -> UInt64? {
            switch info {
            case 0..<24: return UInt64(info)
            case 24: return byte().map(UInt64.init)
            case 25, 26, 27:
                let n = 1 << (info - 24)
                guard index + n <= bytes.count else { return nil }
                defer { index += n }
                return bytes[index..<index + n].reduce(0) { $0 << 8 | UInt64($1) }
            default: return nil
            }
        }

        mutating func item() -> String? {
            guard depth < 16, let initial = byte() else { return nil }
            let major = initial >> 5, info = initial & 0x1f
            if info == 31 {
                return indefinite(major)
            }
            guard let arg = argument(info) else { return nil }
            switch major {
            case 0: return "\(arg)"
            case 1: return "-\(arg + 1)"
            case 2, 3:
                let n = Int(arg)
                guard index + n <= bytes.count else { return nil }
                let slice = Array(bytes[index..<index + n])
                index += n
                return major == 2 ? "h'\(slice.hexString)'"
                    : "\"\(String(decoding: slice, as: UTF8.self))\""
            case 4:
                depth += 1; defer { depth -= 1 }
                var parts: [String] = []
                for _ in 0..<arg { guard let v = item() else { return nil }; parts.append(v) }
                return "[\(parts.joined(separator: ", "))]"
            case 5:
                depth += 1; defer { depth -= 1 }
                var parts: [String] = []
                for _ in 0..<arg {
                    guard let k = item(), let v = item() else { return nil }
                    parts.append("\(k): \(v)")
                }
                return "{\(parts.joined(separator: ", "))}"
            case 6:
                guard let v = item() else { return nil }
                return "\(arg)(\(v))"
            default:
                switch info {
                case 20: return "false"
                case 21: return "true"
                case 22: return "null"
                default: return "simple(\(arg))"
                }
            }
        }

        mutating func indefinite(_ major: UInt8) -> String? {
            depth += 1; defer { depth -= 1 }
            var parts: [String] = []
            while true {
                guard index < bytes.count else { return nil }
                if bytes[index] == 0xff { index += 1; break }
                if major == 5 {
                    guard let k = item(), let v = item() else { return nil }
                    parts.append("\(k): \(v)")
                } else {
                    guard let v = item() else { return nil }
                    parts.append(v)
                }
            }
            switch major {
            case 4: return "[_ \(parts.joined(separator: ", "))]"
            case 5: return "{_ \(parts.joined(separator: ", "))}"
            default: return "(_ \(parts.joined(separator: ", ")))"
            }
        }
    }
}
