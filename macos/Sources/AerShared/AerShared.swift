import Foundation

/// State shared between the main app and the widget extension through the App Group.
/// This module never touches CoreBluetooth: the widget only reads state and posts requests.
public enum AerShared {
    /// Both Info.plists set `AerAppGroup` to `$(TeamIdentifierPrefix)com.aercontrol.shared`.
    /// Unbundled builds (`swift run`, the CLI) fall back to a plain suite name.
    public static let appGroupID: String = {
        if let id = Bundle.main.object(forInfoDictionaryKey: "AerAppGroup") as? String,
           !id.isEmpty, !id.contains("$(") {
            return id
        }
        return "com.aercontrol.shared"
    }()

    public static let defaults: UserDefaults = UserDefaults(suiteName: appGroupID) ?? .standard

    public static let urlScheme = "aercontrol"
    public static let sprayURL = URL(string: "aercontrol://spray")!

    /// Darwin notification posted by the widget intent. Names are prefixed with the App
    /// Group ID, which the sandbox requires for cross-process notify names.
    public static var sprayRequestNotification: String { "\(appGroupID).spray-request" }

    public static let widgetKind = "AerSprayWidget"

    enum Key {
        static let snapshot = "widget.snapshot"
        static let pendingRequest = "widget.pendingRequest"
        static let ackedRequestID = "widget.ackedRequestID"
    }
}

public enum LinkStatus: String, Codable, Sendable {
    case bluetoothOff, unauthorized, noDevice, disconnected, connecting, connected

    public var label: String {
        switch self {
        case .bluetoothOff: "Bluetooth off"
        case .unauthorized: "No Bluetooth access"
        case .noDevice: "No device"
        case .disconnected: "Idle"
        case .connecting: "Connecting…"
        case .connected: "Connected"
        }
    }
}

public enum SprayTrigger: String, Codable, Sendable {
    case button, menuBar, widget, url, schedule, hotkey, cli, shortcut
}

public struct SprayRecord: Codable, Identifiable, Hashable, Sendable {
    public var id = UUID()
    public var date: Date
    public var success: Bool
    public var trigger: SprayTrigger
    public var messageNumber: UInt32?
    public var writeMode: String?
    public var error: String?
    /// Device notification received after the write (hex), for diagnostics.
    public var reply: String?

    public init(date: Date = .now, success: Bool, trigger: SprayTrigger,
                messageNumber: UInt32? = nil, writeMode: String? = nil, error: String? = nil,
                reply: String? = nil) {
        self.date = date
        self.success = success
        self.trigger = trigger
        self.messageNumber = messageNumber
        self.writeMode = writeMode
        self.error = error
        self.reply = reply
    }
}

/// Battery and refill level, as last reported by the device (fetch-status reply).
public struct DeviceStatus: Codable, Equatable, Sendable {
    /// Estimated from voltage (the device reports millivolts only).
    public var batteryPercent: Int?
    public var batteryMillivolts: Int?
    public var spraysRemaining: Int?
    public var refillCapacity: Int?
    public var refillPercent: Int?
    public var totalSprays: Int?
    public var firmwareVersion: String?
    public var updatedAt: Date

    public init(batteryPercent: Int? = nil, batteryMillivolts: Int? = nil, spraysRemaining: Int? = nil,
                refillCapacity: Int? = nil, refillPercent: Int? = nil, totalSprays: Int? = nil,
                firmwareVersion: String? = nil, updatedAt: Date = .now) {
        self.batteryPercent = batteryPercent
        self.batteryMillivolts = batteryMillivolts
        self.spraysRemaining = spraysRemaining
        self.refillCapacity = refillCapacity
        self.refillPercent = refillPercent
        self.totalSprays = totalSprays
        self.firmwareVersion = firmwareVersion
        self.updatedAt = updatedAt
    }

    public var refillFraction: Double? { refillPercent.map { Double($0) / 100 } }

    /// More sprays counted than a can holds: almost always a new can that was never
    /// reset (the device only zeroes its counter on a refill-reset command).
    public var refillNeedsReset: Bool {
        guard let t = totalSprays, let c = refillCapacity else { return false }
        return t > c
    }

    /// "2.51 V" style.
    public var batteryVoltsText: String? {
        batteryMillivolts.map { String(format: "%.2f V", Double($0) / 1000) }
    }

    /// Below 2.8 V the vendor app warns; below 2.7 V it shows a danger icon.
    public var batteryLow: Bool { (batteryMillivolts ?? .max) < 2800 }
}

/// What the widget renders. Written by the app on every state change that matters.
public struct WidgetSnapshot: Codable, Sendable {
    public var deviceName: String?
    public var status: LinkStatus
    public var lastSpray: SprayRecord?
    public var deviceStatus: DeviceStatus?
    public var updatedAt: Date

    public init(deviceName: String? = nil, status: LinkStatus = .noDevice,
                lastSpray: SprayRecord? = nil, deviceStatus: DeviceStatus? = nil, updatedAt: Date = .now) {
        self.deviceName = deviceName
        self.status = status
        self.lastSpray = lastSpray
        self.deviceStatus = deviceStatus
        self.updatedAt = updatedAt
    }

    public static let placeholder = WidgetSnapshot(
        deviceName: "aer Smart Matic", status: .disconnected,
        lastSpray: SprayRecord(date: .now.addingTimeInterval(-600), success: true, trigger: .widget))
}

/// A spray requested from outside the app process (widget). The app acknowledges it by
/// writing the id back, which tells the intent it doesn't need the URL fallback.
public struct SprayRequest: Codable, Sendable {
    public var id: UUID
    public var date: Date
    public init(id: UUID = UUID(), date: Date = .now) { self.id = id; self.date = date }
}

public extension AerShared {
    static func readSnapshot() -> WidgetSnapshot {
        guard let data = defaults.data(forKey: Key.snapshot),
              let snap = try? JSONDecoder().decode(WidgetSnapshot.self, from: data) else {
            return WidgetSnapshot()
        }
        return snap
    }

    static func writeSnapshot(_ snapshot: WidgetSnapshot) {
        if let data = try? JSONEncoder().encode(snapshot) {
            defaults.set(data, forKey: Key.snapshot)
        }
    }

    static func postSprayRequest() -> SprayRequest {
        let req = SprayRequest()
        if let data = try? JSONEncoder().encode(req) {
            defaults.set(data, forKey: Key.pendingRequest)
        }
        DarwinNotification.post(sprayRequestNotification)
        return req
    }

    /// Returns the pending request and clears it, so each one is handled once.
    static func takePendingRequest() -> SprayRequest? {
        guard let data = defaults.data(forKey: Key.pendingRequest),
              let req = try? JSONDecoder().decode(SprayRequest.self, from: data) else { return nil }
        defaults.removeObject(forKey: Key.pendingRequest)
        return req
    }

    static func acknowledge(_ request: SprayRequest) {
        defaults.set(request.id.uuidString, forKey: Key.ackedRequestID)
    }

    static func isAcknowledged(_ request: SprayRequest) -> Bool {
        isAcknowledged(id: request.id)
    }

    static func isAcknowledged(id: UUID) -> Bool {
        defaults.string(forKey: Key.ackedRequestID) == id.uuidString
    }

    /// Widget fallback URL. Carries the request id so a launch-time pickup and the URL
    /// handler can't both spray for the same tap.
    static func sprayURL(for request: SprayRequest) -> URL {
        URL(string: "aercontrol://spray?request=\(request.id.uuidString)")!
    }
}
