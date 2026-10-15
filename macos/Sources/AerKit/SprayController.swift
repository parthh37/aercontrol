import Foundation
import Combine

/// Orchestrates a spray: BLE write, `mN` bookkeeping, history, and the widget snapshot.
/// The app, the CLI, the widget handoff, the scheduler, and the hotkey all go through
/// `spray(trigger:)`.
@MainActor
public final class SprayController: ObservableObject {
    @Published public var settings: AerSettings {
        didSet {
            guard settings != oldValue else { return }
            settingsStore.save(settings)
            applySettingsToBLE()
            if settings.deviceIdentifier != oldValue.deviceIdentifier || settings.deviceName != oldValue.deviceName {
                publishSnapshot(force: true)
            }
        }
    }
    @Published public private(set) var history: [SprayRecord]
    @Published public private(set) var isSpraying = false
    /// Battery / refill as last reported by the device (nil until the status query is decoded).
    @Published public private(set) var deviceStatus: DeviceStatus?
    public var supportsDeviceStatus: Bool { AerStatusProtocol.requestPayload != nil }

    public let ble: BLEService
    public let log: FileLogger
    /// Called after the widget snapshot changes. The app reloads WidgetKit timelines here.
    public var onSnapshotPublished: (() -> Void)?

    private let settingsStore: SettingsStore
    private let historyStore: HistoryStore
    private var lastPublishedStatus: LinkStatus?
    private var cancellables: Set<AnyCancellable> = []

    public init(defaults: UserDefaults = AerShared.defaults, ble: BLEService? = nil, log: FileLogger = .shared) {
        self.log = log
        self.settingsStore = SettingsStore(defaults: defaults)
        self.historyStore = HistoryStore(defaults: defaults)
        self.settings = settingsStore.load()
        self.history = historyStore.load()
        self.ble = ble ?? BLEService(log: log)
        applySettingsToBLE()

        self.ble.$status
            .removeDuplicates()
            .sink { [weak self] _ in
                // $status fires before the stored value changes; publish on the next turn.
                Task { @MainActor in self?.publishSnapshot() }
            }
            .store(in: &cancellables)
    }

    public var lastSpray: SprayRecord? { history.first }

    private func applySettingsToBLE() {
        ble.scanFilter = settings.scanFilter
        ble.nameFilter = settings.nameFilter
        ble.autoReconnect = settings.autoReconnect
        ble.writeMode = settings.writeMode
        if ble.idleDisconnectSeconds != settings.idleDisconnectSeconds {
            ble.idleDisconnectSeconds = settings.idleDisconnectSeconds
        }
        ble.selectDevice(settings.deviceIdentifier)
    }

    // MARK: Device

    public func select(_ device: DiscoveredDevice) {
        settings.deviceIdentifier = device.id
        settings.deviceName = device.name
        log.info("selected device \(device.name) [\(device.id)]")
    }

    public func forgetDevice() {
        ble.disconnect(reason: "forget device")
        settings.deviceIdentifier = nil
        settings.deviceName = nil
    }

    // MARK: Spray

    /// `rawPayload` overrides the CBOR payload, for protocol experiments from the CLI.
    @discardableResult
    public func spray(trigger: SprayTrigger, rawPayload: Data? = nil) async -> SprayRecord {
        isSpraying = true
        defer { isSpraying = false }

        let mn = settings.messageNumber
        let data = rawPayload ?? SprayPayload(messageNumber: mn).data
        log.info("spray requested via \(trigger.rawValue), mN=\(mn)")

        var record: SprayRecord
        do {
            let outcome = try await ble.send(data)
            record = SprayRecord(success: true, trigger: trigger, messageNumber: mn,
                                 writeMode: outcome.mode, reply: outcome.reply?.hexString)
            if settings.autoIncrementMessageNumber {
                settings.messageNumber = MessageCounter.next(after: mn)
            }
        } catch {
            record = SprayRecord(success: false, trigger: trigger, messageNumber: mn,
                                 error: error.localizedDescription)
            log.error("spray failed: \(error.localizedDescription)")
        }
        history.insert(record, at: 0)
        if history.count > historyStore.limit { history.removeLast(history.count - historyStore.limit) }
        historyStore.save(history)
        publishSnapshot(force: true)
        return record
    }

    /// Asks the device for battery / refill level. A no-op until the status query is decoded.
    public func refreshDeviceStatus() async {
        guard let request = AerStatusProtocol.requestPayload else { return }
        do {
            let outcome = try await ble.send(Data(request))
            if let reply = outcome.reply, let f = AerStatusProtocol.parse([UInt8](reply)) {
                deviceStatus = DeviceStatus(batteryPercent: f.batteryPercent, spraysRemaining: f.spraysRemaining,
                                            refillCapacity: f.refillCapacity)
                publishSnapshot(force: true)
            }
        } catch {
            log.warn("status refresh failed: \(error.localizedDescription)")
        }
    }

    public func clearHistory() {
        history.removeAll()
        historyStore.save(history)
        publishSnapshot(force: true)
    }

    // MARK: Widget snapshot

    public func publishSnapshot(force: Bool = false) {
        let status = ble.status
        guard force || status != lastPublishedStatus else { return }
        lastPublishedStatus = status
        AerShared.writeSnapshot(WidgetSnapshot(deviceName: settings.deviceName, status: status,
                                               lastSpray: history.first, deviceStatus: deviceStatus,
                                               updatedAt: .now))
        onSnapshotPublished?()
    }
}
