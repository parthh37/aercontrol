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
    /// Battery / refill as last reported by the device.
    @Published public private(set) var deviceStatus: DeviceStatus?

    public let ble: BLEService
    public let log: FileLogger
    /// Called after the widget snapshot changes. The app reloads WidgetKit timelines here.
    public var onSnapshotPublished: (() -> Void)?

    private let settingsStore: SettingsStore
    private let historyStore: HistoryStore
    private var lastPublishedStatus: LinkStatus?
    private let statusDefaults: UserDefaults
    private static let statusKey = "deviceStatus.v1"

    private static func loadStatus(_ d: UserDefaults) -> DeviceStatus? {
        d.data(forKey: statusKey).flatMap { try? JSONDecoder().decode(DeviceStatus.self, from: $0) }
    }
    private var cancellables: Set<AnyCancellable> = []

    public init(defaults: UserDefaults = AerShared.defaults, ble: BLEService? = nil, log: FileLogger = .shared) {
        self.log = log
        self.settingsStore = SettingsStore(defaults: defaults)
        self.historyStore = HistoryStore(defaults: defaults)
        self.settings = settingsStore.load()
        self.history = historyStore.load()
        self.statusDefaults = defaults
        self.deviceStatus = Self.loadStatus(defaults)
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
            record = Self.sprayRecord(for: outcome, trigger: trigger, messageNumber: mn)
            if settings.autoIncrementMessageNumber {
                settings.messageNumber = MessageCounter.next(after: mn)
            }
            // The link is still up: refresh counters/battery while we're connected.
            if record.success, rawPayload == nil {
                Task { await self.refreshDeviceStatus() }
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


    /// The device acks a spray on the RESPONSE characteristic with `{mT:154, ack:200}`. A
    /// missing ack (older firmware, lost notification) still counts as sent.
    nonisolated static func sprayRecord(for outcome: WriteOutcome, trigger: SprayTrigger, messageNumber: UInt32) -> SprayRecord {
        let hex = outcome.reply?.hexString
        guard let ack = outcome.reply.flatMap({ AerReply.parse([UInt8]($0)) }),
              ack.messageType == Int(AerMessageType.sprayNow.responseType) else {
            return SprayRecord(success: true, trigger: trigger, messageNumber: messageNumber,
                               writeMode: outcome.mode, reply: hex)
        }
        if ack.isSuccess {
            return SprayRecord(success: true, trigger: trigger, messageNumber: messageNumber,
                               writeMode: "\(outcome.mode), confirmed", reply: hex)
        }
        return SprayRecord(success: false, trigger: trigger, messageNumber: messageNumber,
                           writeMode: outcome.mode,
                           error: "Device rejected the spray (ack \(ack.acknowledgement ?? -1)).", reply: hex)
    }

    /// Refreshes when the cached reading is older than `maxAge` (e.g. when a window opens).
    public func refreshDeviceStatusIfOlder(than maxAge: TimeInterval) {
        guard settings.deviceIdentifier != nil, !isSpraying else { return }
        if let at = deviceStatus?.updatedAt, Date().timeIntervalSince(at) < maxAge { return }
        Task { await refreshDeviceStatus() }
    }

    /// Uses and advances the shared message counter, like a spray.
    private func nextMessageNumber() -> UInt32 {
        let mn = settings.messageNumber
        if settings.autoIncrementMessageNumber { settings.messageNumber = MessageCounter.next(after: mn) }
        return mn
    }

    /// Asks the device for battery and refill level (fetch status: mT 107, reply 157).
    @discardableResult
    public func refreshDeviceStatus() async -> DeviceStatus? {
        do {
            let outcome = try await ble.send(Data(AerCommands.fetchStatus(messageNumber: nextMessageNumber())))
            guard let reply = outcome.reply, let r = DeviceStatusReport.parse([UInt8](reply)) else {
                log.warn("status: no fetch-status reply (got \(outcome.reply?.hexString ?? "nothing"))")
                return nil
            }
            apply(r)
            log.info("status: battery \(r.batteryMillivolts ?? -1) mV, \(r.totalSprays) sprays since refill, fw \(r.firmwareVersion ?? "?")")
            return deviceStatus
        } catch {
            log.warn("status refresh failed: \(error.localizedDescription)")
            return nil
        }
    }

    /// Tells the device a fresh refill went in (mT 105, rR:true), which resets its spray
    /// counters, then re-reads the status.
    public func resetRefill() async throws {
        let outcome = try await ble.send(Data(AerCommands.refill(messageNumber: nextMessageNumber())))
        if let reply = outcome.reply.flatMap({ AerReply.parse([UInt8]($0)) }),
           reply.messageType == Int(AerMessageType.refill.responseType), !reply.isSuccess {
            throw BLEError.writeFailed("device rejected the refill reset (ack \(reply.acknowledgement ?? -1))")
        }
        log.info("refill reset sent")
        await refreshDeviceStatus()
    }

    private func apply(_ r: DeviceStatusReport) {
        deviceStatus = DeviceStatus(batteryPercent: r.batteryPercentEstimate, batteryMillivolts: r.batteryMillivolts,
                                    spraysRemaining: r.spraysRemaining, refillCapacity: DeviceStatusReport.refillCapacity,
                                    refillPercent: r.refillPercent, totalSprays: r.totalSprays,
                                    firmwareVersion: r.firmwareVersion)
        if let data = try? JSONEncoder().encode(deviceStatus) { statusDefaults.set(data, forKey: Self.statusKey) }
        publishSnapshot(force: true)
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
