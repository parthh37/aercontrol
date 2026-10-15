import Foundation
import Testing
@testable import AerProtocol

private struct VectorFile: Decodable {
    struct Vector: Decodable {
        let name: String
        let mT: UInt32
        let mN: UInt32
        let rI: UInt32
        let hex: String
    }
    let service_uuid: String
    let characteristic_uuid: String
    let vectors: [Vector]
}

/// Loads /protocol/test-vectors.json, the same file the Windows tests use.
private func loadVectors() throws -> VectorFile {
    let url = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()   // AerProtocolTests
        .deletingLastPathComponent()   // Tests
        .deletingLastPathComponent()   // macos
        .deletingLastPathComponent()   // repo root
        .appendingPathComponent("protocol/test-vectors.json")
    return try JSONDecoder().decode(VectorFile.self, from: Data(contentsOf: url))
}

@Suite struct SprayPayloadTests {
    @Test func capturedPayloadMatchesExactly() {
        let expected: [UInt8] = [0xbf, 0x62, 0x6d, 0x54, 0x18, 0x68, 0x62, 0x6d,
                                 0x4e, 0x18, 0x9a, 0x62, 0x72, 0x49, 0x00, 0xff]
        #expect(SprayPayload().encoded() == expected)
        #expect(SprayPayload().encoded().count == 16)
    }

    @Test func allSharedVectors() throws {
        let file = try loadVectors()
        #expect(!file.vectors.isEmpty)
        for v in file.vectors {
            let payload = SprayPayload(messageType: v.mT, messageNumber: v.mN, requestIndex: v.rI)
            #expect(payload.encoded().hexString == v.hex, "\(v.name)")
        }
    }

    @Test func uuidsMatchSpec() throws {
        let file = try loadVectors()
        #expect(file.service_uuid == AerGATT.serviceUUIDString)
        #expect(file.characteristic_uuid == AerGATT.characteristicUUIDString)
    }

    @Test func messageCounterWrapsWithinOneByte() {
        #expect(MessageCounter.next(after: 154) == 155)
        #expect(MessageCounter.next(after: 255) == 0)
        for n: UInt32 in 0...255 {
            #expect(SprayPayload(messageNumber: MessageCounter.next(after: n)).encoded().count <= 16)
        }
    }

    @Test func hexRoundTrip() {
        let bytes = [UInt8](hex: "bf626d541868626d4e189a62724900ff")
        #expect(bytes == SprayPayload().encoded())
        #expect([UInt8](hex: "abc") == nil)
    }
}
