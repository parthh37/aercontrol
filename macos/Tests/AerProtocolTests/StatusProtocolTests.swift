import Foundation
import Testing
@testable import AerProtocol

private struct Vectors: Decodable {
    struct Command: Decodable { let name: String; let mT: UInt32; let mN: UInt32; let rR: Bool?; let hex: String }
    struct Expect: Decodable {
        let mT: Int; let mN: Int; let ack: Int; let batteryMillivolts: Int; let firmware: String
        let currentStatus: Int; let sprayNow: [Int]; let schedule: [Int]; let buttonPress: [Int]
        let totalSprays: Int; let spraysRemaining: Int; let refillPercent: Int; let batteryPercentEstimate: Int
    }
    struct Reply: Decodable { let name: String; let hex: String; let expect: Expect }
    let commands: [Command]
    let responses: [Reply]
}

private func vectors() throws -> Vectors {
    let url = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent()
        .deletingLastPathComponent().deletingLastPathComponent()
        .appendingPathComponent("protocol/test-vectors.json")
    return try JSONDecoder().decode(Vectors.self, from: Data(contentsOf: url))
}

@Suite struct StatusProtocolTests {
    @Test func commandsMatchVectors() throws {
        for c in try vectors().commands {
            let bytes: [UInt8]
            switch c.mT {
            case 107: bytes = AerCommands.fetchStatus(messageNumber: c.mN)
            case 105: bytes = AerCommands.refill(messageNumber: c.mN, reset: c.rR ?? true)
            default: Issue.record("unknown command \(c.mT)"); continue
            }
            #expect(bytes.hexString == c.hex, "\(c.name)")
        }
    }

    @Test func decodesCapturedStatusReply() throws {
        for r in try vectors().responses {
            let bytes = try #require([UInt8](hex: r.hex))
            let reply = try #require(AerReply.parse(bytes))
            #expect(reply.messageType == r.expect.mT)
            #expect(reply.messageNumber == r.expect.mN)
            #expect(reply.acknowledgement == r.expect.ack)
            #expect(reply.isSuccess)

            let s = try #require(DeviceStatusReport.parse(bytes))
            #expect(s.batteryMillivolts == r.expect.batteryMillivolts)
            #expect(s.firmwareVersion == r.expect.firmware)
            #expect(s.currentStatus == r.expect.currentStatus)
            #expect(s.sprayNowCounts == r.expect.sprayNow)
            #expect(s.scheduleCounts == r.expect.schedule)
            #expect(s.buttonPressCounts == r.expect.buttonPress)
            #expect(s.totalSprays == r.expect.totalSprays)
            #expect(s.spraysRemaining == r.expect.spraysRemaining)
            #expect(s.refillPercent == r.expect.refillPercent)
            #expect(s.batteryPercentEstimate == r.expect.batteryPercentEstimate)
        }
    }

    @Test func refillPercentMatchesVendorFormula() {
        func report(_ total: Int) -> DeviceStatusReport {
            DeviceStatusReport(sprayNowCounts: [total], scheduleCounts: [], buttonPressCounts: [])
        }
        #expect(report(0).refillPercent == 100)
        #expect(report(1100).refillPercent == 50)
        #expect(report(2189).refillPercent == 1)   // round(0.5) → vendor app shows 1
        #expect(report(2200).refillPercent == 0)
        #expect(report(5000).spraysRemaining == 0)
    }

    @Test func statusParserRejectsOtherReplies() {
        // A spray ack {mT:154, mN:160, ack:200}
        let ack: [UInt8] = [0xbf, 0x62, 0x6d, 0x54, 0x18, 0x9a, 0x62, 0x6d, 0x4e, 0x18, 0xa0,
                            0x63, 0x61, 0x63, 0x6b, 0x18, 0xc8, 0xff]
        #expect(AerReply.parse(ack)?.messageType == 154)
        #expect(AerReply.parse(ack)?.isSuccess == true)
        #expect(DeviceStatusReport.parse(ack) == nil)
        #expect(AerReply.parse([0x00, 0x00]) == nil)   // what idle characteristics read
        #expect(AerReply.parse([]) == nil)
    }

    @Test func cborReaderHandlesDefiniteAndIndefiniteForms() throws {
        // {"a": [1, -2, "x", true, null]} definite, and the same with indefinite containers
        let definite: [UInt8] = [0xa1, 0x61, 0x61, 0x85, 0x01, 0x21, 0x61, 0x78, 0xf5, 0xf6]
        let indefinite: [UInt8] = [0xbf, 0x61, 0x61, 0x9f, 0x01, 0x21, 0x61, 0x78, 0xf5, 0xf6, 0xff, 0xff]
        for b in [definite, indefinite] {
            let v = try CBORReader.decode(b)
            #expect(v["a"]?.arrayValue == [.unsigned(1), .negative(-2), .text("x"), .bool(true), .null])
        }
        #expect(throws: CBORError.truncated) { try CBORReader.decode([0xbf, 0x61]) }
    }
}
