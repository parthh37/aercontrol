import Foundation
@_exported import AerProtocol
@_exported import AerShared

public enum ScanFilter: String, Codable, CaseIterable, Identifiable, Sendable {
    case serviceOrName, service, name, all
    public var id: String { rawValue }
    public var label: String {
        switch self {
        case .serviceOrName: "Service UUID or name"
        case .service: "Service UUID only"
        case .name: "Name only"
        case .all: "Show all devices"
        }
    }
}

public enum WriteMode: String, Codable, CaseIterable, Identifiable, Sendable {
    case automatic, withResponse, withoutResponse
    public var id: String { rawValue }
    public var label: String {
        switch self {
        case .automatic: "Automatic (with response, then fallback)"
        case .withResponse: "With response only"
        case .withoutResponse: "Without response only"
        }
    }
}

public struct SpraySchedule: Codable, Equatable, Sendable {
    public var enabled = false
    public var intervalMinutes = 60
    public var window = ScheduleWindow(startMinutes: 9 * 60, endMinutes: 21 * 60)
    public init() {}
}

public struct AerSettings: Codable, Equatable, Sendable {
    /// CoreBluetooth hides MAC addresses, so the device is remembered by the per-host
    /// peripheral UUID that CoreBluetooth assigns.
    public var deviceIdentifier: UUID?
    public var deviceName: String?
    public var scanFilter: ScanFilter = .serviceOrName
    /// Comma-separated, case-insensitive substrings. The device advertises "Smart Matic"
    /// and does not include the service UUID in its advertisement.
    public static let defaultNameFilter = "aer, Smart Matic"
    public var nameFilter = AerSettings.defaultNameFilter
    /// 0 = stay connected. Otherwise disconnect so the phone app can connect.
    public var idleDisconnectSeconds = 20
    public var autoReconnect = true
    public var writeMode: WriteMode = .automatic
    /// On by default as a safeguard. Live tests showed a reused mN is accepted; the real fix
    /// for the first failed spray was the post-write settle and notify subscription.
    public var autoIncrementMessageNumber = true
    public var messageNumber: UInt32 = SprayPayload.capturedMessageNumber
    public var schedule = SpraySchedule()
    public var hotkeyEnabled = false

    public init() {}

    // Decoding tolerates missing keys, so adding a setting never resets the others.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = AerSettings()
        deviceIdentifier = try c.decodeIfPresent(UUID.self, forKey: .deviceIdentifier)
        deviceName = try c.decodeIfPresent(String.self, forKey: .deviceName)
        scanFilter = (try? c.decodeIfPresent(ScanFilter.self, forKey: .scanFilter)) ?? d.scanFilter
        nameFilter = try c.decodeIfPresent(String.self, forKey: .nameFilter) ?? d.nameFilter
        idleDisconnectSeconds = try c.decodeIfPresent(Int.self, forKey: .idleDisconnectSeconds) ?? d.idleDisconnectSeconds
        autoReconnect = try c.decodeIfPresent(Bool.self, forKey: .autoReconnect) ?? d.autoReconnect
        writeMode = (try? c.decodeIfPresent(WriteMode.self, forKey: .writeMode)) ?? d.writeMode
        autoIncrementMessageNumber = try c.decodeIfPresent(Bool.self, forKey: .autoIncrementMessageNumber) ?? d.autoIncrementMessageNumber
        messageNumber = try c.decodeIfPresent(UInt32.self, forKey: .messageNumber) ?? d.messageNumber
        schedule = (try? c.decodeIfPresent(SpraySchedule.self, forKey: .schedule)) ?? d.schedule
        hotkeyEnabled = try c.decodeIfPresent(Bool.self, forKey: .hotkeyEnabled) ?? d.hotkeyEnabled
    }
}

/// Persists settings as JSON in a UserDefaults suite (the App Group suite for the app).
public final class SettingsStore {
    private let defaults: UserDefaults
    private let key: String

    public init(defaults: UserDefaults, key: String = "settings.v1") {
        self.defaults = defaults
        self.key = key
    }

    public func load() -> AerSettings {
        guard let data = defaults.data(forKey: key),
              let s = try? JSONDecoder().decode(AerSettings.self, from: data) else { return AerSettings() }
        return s
    }

    public func save(_ settings: AerSettings) {
        if let data = try? JSONEncoder().encode(settings) {
            defaults.set(data, forKey: key)
        }
    }
}

/// Spray history, newest first, capped so UserDefaults stays small.
public final class HistoryStore {
    private let defaults: UserDefaults
    private let key = "history.v1"
    public let limit: Int

    public init(defaults: UserDefaults, limit: Int = 200) {
        self.defaults = defaults
        self.limit = limit
    }

    public func load() -> [SprayRecord] {
        guard let data = defaults.data(forKey: key),
              let items = try? JSONDecoder().decode([SprayRecord].self, from: data) else { return [] }
        return items
    }

    public func save(_ items: [SprayRecord]) {
        if let data = try? JSONEncoder().encode(Array(items.prefix(limit))) {
            defaults.set(data, forKey: key)
        }
    }
}
