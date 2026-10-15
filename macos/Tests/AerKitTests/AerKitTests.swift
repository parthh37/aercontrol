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
