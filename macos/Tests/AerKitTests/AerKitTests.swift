import Foundation
import Testing
@testable import AerKit

@Suite struct SettingsTests {
    @Test func decodingToleratesMissingKeys() throws {
        let json = #"{"nameFilter":"godrej","idleDisconnectSeconds":0}"#
        let s = try JSONDecoder().decode(AerSettings.self, from: Data(json.utf8))
        #expect(s.nameFilter == "godrej")
        #expect(s.idleDisconnectSeconds == 0)
        #expect(s.messageNumber == 154)
        #expect(s.writeMode == .automatic)
        #expect(s.autoIncrementMessageNumber)
        #expect(s.deviceIdentifier == nil)
    }

    @Test func roundTripsThroughStore() {
        let defaults = UserDefaults(suiteName: "aer.tests.\(UUID())")!
        let store = SettingsStore(defaults: defaults)
        var s = AerSettings()
        s.deviceIdentifier = UUID()
        s.autoIncrementMessageNumber = true
        s.schedule.enabled = true
        store.save(s)
        #expect(store.load() == s)
    }
}

@Suite struct ScheduleTests {
    let cal: Calendar = {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone(identifier: "UTC")!
        return c
    }()

    func at(_ h: Int, _ m: Int) -> Date {
        cal.date(from: DateComponents(year: 2026, month: 10, day: 15, hour: h, minute: m))!
    }

    @Test func daytimeWindow() {
        let w = ScheduleWindow(startMinutes: 9 * 60, endMinutes: 21 * 60)
        #expect(w.contains(at(9, 0), calendar: cal))
        #expect(w.contains(at(12, 30), calendar: cal))
        #expect(!w.contains(at(8, 59), calendar: cal))
        #expect(!w.contains(at(23, 0), calendar: cal))
    }

    @Test func evaluatorRespectsInterval() {
        var sched = SpraySchedule()
        sched.enabled = true
        sched.intervalMinutes = 30
        let e = ScheduleEvaluator(schedule: sched)
        #expect(e.isDue(now: at(10, 0), lastScheduledSpray: nil, calendar: cal))
        #expect(!e.isDue(now: at(10, 20), lastScheduledSpray: at(10, 0), calendar: cal))
        #expect(e.isDue(now: at(10, 30), lastScheduledSpray: at(10, 0), calendar: cal))
        sched.enabled = false
        #expect(!ScheduleEvaluator(schedule: sched).isDue(now: at(10, 0), lastScheduledSpray: nil, calendar: cal))
    }
}

@Suite struct SprayAckTests {
    private func outcome(_ hex: String?) -> WriteOutcome {
        WriteOutcome(mode: "without-response", reply: hex.flatMap { [UInt8](hex: $0) }.map { Data($0) })
    }

    @Test func confirmedWhenDeviceAcks200() {
        // {mT:154, mN:160, ack:200}
        let r = SprayController.sprayRecord(for: outcome("bf626d54189a626d4e18a06361636b18c8ff"),
                                            trigger: .button, messageNumber: 160)
        #expect(r.success)
        #expect(r.writeMode == "without-response, confirmed")
    }

    @Test func failedWhenDeviceRejects() {
        // {mT:154, mN:160, ack:100}
        let r = SprayController.sprayRecord(for: outcome("bf626d54189a626d4e18a06361636b1864ff"),
                                            trigger: .button, messageNumber: 160)
        #expect(!r.success)
        #expect(r.error?.contains("ack 100") == true)
    }

    @Test func noReplyStillCountsAsSent() {
        let r = SprayController.sprayRecord(for: outcome(nil), trigger: .schedule, messageNumber: 1)
        #expect(r.success)
        #expect(r.writeMode == "without-response")
    }
}

@Suite struct UpdateCheckerTests {
    @Test func comparesVersions() {
        #expect(UpdateChecker.isVersion("v1.2.0", newerThan: "1.1.9"))
        #expect(UpdateChecker.isVersion("2.0.0", newerThan: "1.99.99"))
        #expect(UpdateChecker.isVersion("1.0.1", newerThan: "1.0.0-dev"))
        #expect(!UpdateChecker.isVersion("1.0.0", newerThan: "1.0.0"))
        #expect(!UpdateChecker.isVersion("1.2", newerThan: "1.2.0"))
        #expect(!UpdateChecker.isVersion("v1.0.0", newerThan: "1.0.1"))
    }

    @Test func parsesGitHubReleaseJSON() throws {
        let json = #"""
        {"tag_name":"v1.3.0","html_url":"https://github.com/parthh37/aercontrol/releases/tag/v1.3.0","body":"notes",
         "assets":[{"name":"AerControl-macOS.dmg","browser_download_url":"https://example.com/AerControl-macOS.dmg"},
                   {"name":"AerControl-windows-x64.zip","browser_download_url":"https://example.com/w.zip"}]}
        """#
        let r = try UpdateChecker.parse(Data(json.utf8))
        #expect(r.version == "1.3.0")
        #expect(r.macDownloadURL.absoluteString == "https://example.com/AerControl-macOS.dmg")
    }
}
