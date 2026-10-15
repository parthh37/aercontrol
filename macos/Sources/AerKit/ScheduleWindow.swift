import Foundation

/// Daily time-of-day window for scheduled sprays, stored as minutes after midnight
/// (0…1439) so it's timezone- and DST-agnostic.
public struct ScheduleWindow: Codable, Equatable, Sendable {
    public var startMinutes: Int
    public var endMinutes: Int

    public init(startMinutes: Int, endMinutes: Int) {
        self.startMinutes = startMinutes
        self.endMinutes = endMinutes
    }

    public static func minutesOfDay(_ date: Date, calendar: Calendar = .current) -> Int {
        let c = calendar.dateComponents([.hour, .minute], from: date)
        return (c.hour ?? 0) * 60 + (c.minute ?? 0)
    }

    /// Whether `date` falls inside the window.
    public func contains(_ date: Date, calendar: Calendar = .current) -> Bool {
        let now = Self.minutesOfDay(date, calendar: calendar)
        // TODO(you): decide the window semantics. See the request in chat.
        // Placeholder: same-day window only, end exclusive.
        return now >= startMinutes && now < endMinutes
    }
}

/// Decides when the next scheduled spray is due.
public struct ScheduleEvaluator {
    public var schedule: SpraySchedule
    public init(schedule: SpraySchedule) { self.schedule = schedule }

    public func isDue(now: Date, lastScheduledSpray: Date?, calendar: Calendar = .current) -> Bool {
        guard schedule.enabled, schedule.intervalMinutes > 0,
              schedule.window.contains(now, calendar: calendar) else { return false }
        guard let last = lastScheduledSpray else { return true }
        return now.timeIntervalSince(last) >= TimeInterval(schedule.intervalMinutes * 60) - 1
    }
}
