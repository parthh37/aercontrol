import Foundation
import CoreBluetooth
import Combine

public struct DiscoveredDevice: Identifiable, Hashable, Sendable {
    public let id: UUID
    public var name: String
    public var rssi: Int
    public var advertisesService: Bool
    public var lastSeen: Date
}

/// Result of a successful write: which write type worked and what (if anything) the device
/// sent back on the notify characteristic shortly afterwards.
public struct WriteOutcome: Sendable {
    public var mode: String
    public var reply: Data?
}

public enum BLEError: LocalizedError, Equatable {
    case bluetoothOff
    case unauthorized
    case unsupported
    case noDeviceSelected
    case deviceNotFound
    case timeout(String)
    case serviceNotFound
    case characteristicNotFound
    case writeFailed(String)
    case disconnected(String?)
    case cancelled

    public var errorDescription: String? {
        switch self {
        case .bluetoothOff: "Bluetooth is turned off."
        case .unauthorized: "AerControl isn't allowed to use Bluetooth (System Settings › Privacy & Security › Bluetooth)."
        case .unsupported: "This Mac doesn't support Bluetooth Low Energy."
        case .noDeviceSelected: "No device selected. Scan and pick your aer first."
        case .deviceNotFound: "Device not found. Is it in range and powered?"
        case .timeout(let step): "Timed out during \(step). The aer accepts one connection at a time: is the phone app or another computer connected to it?"
        case .serviceNotFound: "The aer GATT service wasn't found on this device."
        case .characteristicNotFound: "The spray characteristic wasn't found on this device."
        case .writeFailed(let why): "Write failed: \(why)"
        case .disconnected(let why): "Disconnected\(why.map { ": \($0)" } ?? "")."
        case .cancelled: "Cancelled."
        }
    }
}

/// One-shot async waiter bridging a CoreBluetooth delegate callback to `await`, with a timeout.
@MainActor
final class Waiter<T> {
    private var continuation: CheckedContinuation<T, Error>?
    private var timeoutTask: Task<Void, Never>?

    var isWaiting: Bool { continuation != nil }

    func wait(timeout: TimeInterval, step: String) async throws -> T {
        fail(BLEError.cancelled) // supersede a stale waiter rather than leak it
        return try await withCheckedThrowingContinuation { c in
            continuation = c
            timeoutTask = Task { [weak self] in
                try? await Task.sleep(for: .seconds(timeout))
                guard !Task.isCancelled else { return }
                self?.fail(BLEError.timeout(step))
            }
        }
    }

    func succeed(_ value: T) { finish(.success(value)) }
    func fail(_ error: Error) { finish(.failure(error)) }

    private func finish(_ result: Result<T, Error>) {
        guard let c = continuation else { return }
        continuation = nil
        timeoutTask?.cancel()
        timeoutTask = nil
        c.resume(with: result)
    }
}

/// Owns the CBCentralManager. Every public API is main-actor isolated, and CoreBluetooth
/// delivers callbacks on the main queue, so there's no locking.
@MainActor
public final class BLEService: NSObject, ObservableObject {
    public static let serviceUUID = CBUUID(string: AerGATT.serviceUUIDString)
    public static let characteristicUUID = CBUUID(string: AerGATT.characteristicUUIDString)
    public static let responseUUID = CBUUID(string: AerGATT.responseUUIDString)

    @Published public private(set) var managerState: CBManagerState = .unknown
    @Published public private(set) var status: LinkStatus = .noDevice
    @Published public private(set) var discovered: [DiscoveredDevice] = []
    @Published public private(set) var isScanning = false
    @Published public private(set) var connectedName: String?

    public var scanFilter: ScanFilter = .serviceOrName
    public var nameFilter = AerSettings.defaultNameFilter
    public var idleDisconnectSeconds = 3 { didSet { scheduleIdleDisconnect() } }
    /// How long a connect may stay pending. While another computer or the phone holds the
    /// aer it doesn't advertise; CoreBluetooth's pending connect completes as soon as that
    /// host lets go, so this is mostly time spent waiting for the device to become free.
    public var connectTimeout: TimeInterval = 30
    public var autoReconnect = true
    public var writeMode: WriteMode = .automatic
    public private(set) var targetIdentifier: UUID?

    private let log: FileLogger
    private var central: CBCentralManager!
    private var peripheral: CBPeripheral?
    private var characteristic: CBCharacteristic?

    private var userScanActive = false
    private var userScanStopTask: Task<Void, Never>?
    private var findTarget: UUID?

    private var intentionalDisconnect = false
    private var lastActivity = Date.distantPast
    private var idleTask: Task<Void, Never>?
    private var reconnectTask: Task<Void, Never>?
    private var reconnectAttempt = 0
    private var sendTail: Task<Void, Never>?

    private let powerWaiter = Waiter<Void>()
    private let findWaiter = Waiter<CBPeripheral>()
    private let connectWaiter = Waiter<Void>()
    private let servicesWaiter = Waiter<Void>()
    private let characteristicsWaiter = Waiter<Void>()
    private let writeWaiter = Waiter<Void>()
    private let readyWaiter = Waiter<Void>()
    private let notifyStateWaiter = Waiter<Void>()
    private let replyWaiter = Waiter<Data>()
    private var replies: [Data] = []
    private let readWaiter = Waiter<Data>()
    private var readTarget: CBUUID?

    /// How long to listen for a notification after a write. It also acts as a settle time,
    /// so a write-without-response isn't dropped by a disconnect right behind it.
    public var replyWindow: TimeInterval = 1.0
    /// How long a fresh link must be up before the first write. A spray written right after
    /// connecting is acked and counted but doesn't fire; 2 s later it does (live tests
    /// 2026-10-18). The vendor app never writes sooner than ~2.5 s after connecting.
    public var postConnectSettle: TimeInterval = 2.0
    private var readyAt = Date.distantPast
    /// How long after a spray no other command may be written. A fetch-status ~100 ms behind
    /// a spray left it acked and counted but not fired; with nothing written behind it, it
    /// fires (live test 2026-10-18). This also spaces out double-taps and scheduled sprays.
    public var postSprayQuiet: TimeInterval = 3.0
    private var lastSprayAt = Date.distantPast
    /// Every notification from the device, for diagnostics.
    public var onNotification: ((Data) -> Void)?

    public init(log: FileLogger = .shared) {
        self.log = log
        super.init()
        central = CBCentralManager(delegate: self, queue: .main,
                                   options: [CBCentralManagerOptionShowPowerAlertKey: true])
    }

    // MARK: Device selection

    public func selectDevice(_ id: UUID?) {
        guard id != targetIdentifier else { return }
        targetIdentifier = id
        if let p = peripheral, p.identifier != id {
            disconnect(reason: "device changed")
            peripheral = nil
        }
        refreshStatus()
    }

    // MARK: Scanning

    public func startScan(duration: TimeInterval = 15) {
        guard managerState == .poweredOn else {
            log.warn("scan requested but Bluetooth state is \(managerState.debugName)")
            return
        }
        discovered.removeAll()
        userScanActive = true
        isScanning = true
        // Devices this Mac is already connected to don't advertise, so list them explicitly.
        for p in central.retrieveConnectedPeripherals(withServices: [Self.serviceUUID]) {
            upsert(p, name: p.name, rssi: 0, advertisesService: true)
        }
        updateScanning()
        userScanStopTask?.cancel()
        userScanStopTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(duration))
            guard !Task.isCancelled else { return }
            self?.stopScan()
        }
        log.info("scan started (filter: \(scanFilter.rawValue), \(Int(duration))s)")
    }

    public func stopScan() {
        userScanStopTask?.cancel()
        guard userScanActive else { return }
        userScanActive = false
        isScanning = false
        updateScanning()
        log.info("scan stopped, \(discovered.count) device(s)")
    }

    /// Runs the radio while either a user scan or a peripheral lookup needs it.
    private func updateScanning() {
        guard managerState == .poweredOn else { return }
        if userScanActive || findTarget != nil {
            // Filtering by service in hardware only when the user asked for it: many
            // devices don't put the 128-bit service UUID in their advertisement.
            let hardwareFilter = userScanActive && scanFilter == .service && findTarget == nil
            let services: [CBUUID]? = hardwareFilter ? [Self.serviceUUID] : nil
            central.scanForPeripherals(withServices: services,
                                       options: [CBCentralManagerScanOptionAllowDuplicatesKey: true])
        } else if central.isScanning {
            central.stopScan()
        }
    }

    private func passesFilter(name: String?, advertisesService: Bool) -> Bool {
        let terms = nameFilter.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
        let nameMatch = name.map { n in terms.contains { n.localizedCaseInsensitiveContains($0) } } ?? false
        switch scanFilter {
        case .all: return true
        case .service: return advertisesService
        case .name: return nameMatch
        case .serviceOrName: return advertisesService || nameMatch
        }
    }

    private func upsert(_ p: CBPeripheral, name: String?, rssi: Int, advertisesService: Bool) {
        let display = name ?? p.name ?? "Unnamed (\(p.identifier.uuidString.prefix(8)))"
        if let i = discovered.firstIndex(where: { $0.id == p.identifier }) {
            discovered[i].rssi = rssi == 127 ? discovered[i].rssi : rssi
            discovered[i].lastSeen = .now
            discovered[i].advertisesService = discovered[i].advertisesService || advertisesService
            if name != nil { discovered[i].name = display }
        } else {
            discovered.append(DiscoveredDevice(id: p.identifier, name: display, rssi: rssi,
                                               advertisesService: advertisesService, lastSeen: .now))
            discovered.sort { $0.rssi > $1.rssi }
        }
    }

    // MARK: Connection

    public func connect() async throws {
        _ = try await ensureConnected()
        touch()
    }

    public func disconnect(reason: String = "user") {
        reconnectTask?.cancel()
        idleTask?.cancel()
        guard let p = peripheral, p.state == .connected || p.state == .connecting else {
            refreshStatus()
            return
        }
        log.info("disconnecting (\(reason))")
        intentionalDisconnect = true
        central.cancelPeripheralConnection(p)
    }

    private func waitForPoweredOn() async throws {
        if managerState == .unknown || managerState == .resetting {
            try await powerWaiter.wait(timeout: 5, step: "Bluetooth power-on")
        }
        switch managerState {
        case .poweredOn: return
        case .unauthorized: throw BLEError.unauthorized
        case .unsupported: throw BLEError.unsupported
        default: throw BLEError.bluetoothOff
        }
    }

    private func ensureConnected() async throws -> (CBPeripheral, CBCharacteristic) {
        try await waitForPoweredOn()
        if let p = peripheral, p.state == .connected, let c = characteristic {
            return (p, c)
        }
        guard let id = targetIdentifier else { throw BLEError.noDeviceSelected }

        let p: CBPeripheral
        if let existing = peripheral, existing.identifier == id {
            p = existing
        } else if let known = central.retrievePeripherals(withIdentifiers: [id]).first {
            p = known
        } else {
            p = try await find(id)
        }
        peripheral = p
        p.delegate = self

        if p.state != .connected {
            status = .connecting
            intentionalDisconnect = false
            log.info("connecting to \(p.name ?? id.uuidString)")
            central.connect(p)
            do {
                try await connectWaiter.wait(timeout: connectTimeout, step: "connect")
            } catch {
                central.cancelPeripheralConnection(p)
                refreshStatus()
                throw error
            }
        }

        characteristic = nil
        p.discoverServices([Self.serviceUUID])
        try await servicesWaiter.wait(timeout: 8, step: "service discovery")
        guard let service = p.services?.first(where: { $0.uuid == Self.serviceUUID }) else {
            throw BLEError.serviceNotFound
        }
        p.discoverCharacteristics([Self.characteristicUUID, Self.responseUUID], for: service)
        try await characteristicsWaiter.wait(timeout: 8, step: "characteristic discovery")
        guard let c = service.characteristics?.first(where: { $0.uuid == Self.characteristicUUID }) else {
            throw BLEError.characteristicNotFound
        }
        // The device replies on the RESPONSE characteristic; keep notify on the command
        // characteristic too (enabling it was part of what made the first spray work).
        let response = service.characteristics?.first(where: { $0.uuid == Self.responseUUID })
        for n in [c, response].compactMap({ $0 })
        where n.properties.contains(.notify) || n.properties.contains(.indicate) {
            p.setNotifyValue(true, for: n)
            do {
                try await notifyStateWaiter.wait(timeout: 3, step: "enable notifications")
            } catch {
                // Seen on the RESPONSE characteristic even though replies still arrive.
                log.info("notify setup on \(n.uuid) reported: \(error.localizedDescription)")
            }
        }
        characteristic = c
        reconnectAttempt = 0
        readyAt = .now
        connectedName = p.name
        status = .connected
        log.info("ready: \(p.name ?? "?") characteristic properties=\(c.properties.debugName), "
                 + "max write \(p.maximumWriteValueLength(for: .withoutResponse))B")
        return (p, c)
    }

    /// `ensureConnected`, then waits until a write is safe: `postConnectSettle` after the link
    /// came up and `postSprayQuiet` after the last spray. Both are measured from those moments,
    /// so a command long after either doesn't wait. If the link drops meanwhile,
    /// `ensureConnected` reconnects, which restarts the settle clock.
    private func writableLink() async throws -> (CBPeripheral, CBCharacteristic) {
        var link = try await ensureConnected()
        for _ in 0..<3 {
            let settle = postConnectSettle - Date().timeIntervalSince(readyAt)
            let quiet = postSprayQuiet - Date().timeIntervalSince(lastSprayAt)
            let remaining = max(settle, quiet)
            guard remaining > 0 else { break }
            log.info(quiet >= settle
                     ? "waiting \(Int(remaining * 1000)) ms for the spray to finish before writing"
                     : "letting the aer wake up for \(Int(remaining * 1000)) ms before writing")
            idleTask?.cancel() // a short idle hold mustn't drop the link mid-wait; send() re-arms it
            try await Task.sleep(for: .seconds(remaining))
            link = try await ensureConnected()
        }
        return link
    }

    private static func isSpray(_ data: Data) -> Bool {
        AerReply.parse([UInt8](data))?.messageType == Int(AerMessageType.sprayNow.rawValue)
    }

    /// Scans until the peripheral with `id` shows up. Used when CoreBluetooth's cache has
    /// forgotten it, for example after Bluetooth was reset.
    private func find(_ id: UUID) async throws -> CBPeripheral {
        log.info("peripheral \(id) not cached; scanning for it")
        findTarget = id
        updateScanning()
        defer {
            findTarget = nil
            updateScanning()
        }
        do {
            return try await findWaiter.wait(timeout: 10, step: "device lookup")
        } catch BLEError.timeout {
            throw BLEError.deviceNotFound
        }
    }

    // MARK: Writing

    /// Connects if needed, writes `data`, and returns the write mode that worked. Calls are
    /// serialized, so a hotkey press during a scheduled spray queues instead of interleaving.
    @discardableResult
    public func send(_ data: Data) async throws -> WriteOutcome {
        let previous = sendTail
        let task = Task { @MainActor [weak self] () -> Result<WriteOutcome, Error> in
            await previous?.value
            guard let self else { return .failure(BLEError.cancelled) }
            do { return .success(try await self.performSend(data)) } catch { return .failure(error) }
        }
        sendTail = Task { _ = await task.value }
        return try await task.value.get()
    }

    private func performSend(_ data: Data) async throws -> WriteOutcome {
        // The idle timer armed by the previous command must not fire mid-send: with the 3 s hold
        // it cut the link in the same millisecond as a spray write. `touch()` re-arms it after.
        idleTask?.cancel()
        defer { touch() }
        let (p, c) = try await writableLink()
        let mode = try await write(data, to: c, on: p)
        if Self.isSpray(data) { lastSprayAt = .now }
        let reply = await awaitReply()
        return WriteOutcome(mode: mode, reply: reply)
    }

    private func awaitReply() async -> Data? {
        if replies.isEmpty {
            _ = try? await replyWaiter.wait(timeout: replyWindow, step: "reply")
        }
        return replies.first
    }

    private func write(_ data: Data, to c: CBCharacteristic, on p: CBPeripheral) async throws -> String {
        replies.removeAll()
        let props = c.properties

        let tryWithResponse: Bool
        switch writeMode {
        case .withResponse: tryWithResponse = true
        case .withoutResponse: tryWithResponse = false
        case .automatic:
            tryWithResponse = props.contains(.write) || !props.contains(.writeWithoutResponse)
        }

        if tryWithResponse {
            do {
                p.writeValue(data, for: c, type: .withResponse)
                try await writeWaiter.wait(timeout: 5, step: "write acknowledgement")
                log.info("wrote \(data.count)B with response: \(data.hexString)")
                return "with-response"
            } catch {
                guard writeMode == .automatic, p.state == .connected else { throw error }
                log.warn("write with response failed (\(error.localizedDescription)); falling back")
            }
        }

        if !p.canSendWriteWithoutResponse {
            try? await readyWaiter.wait(timeout: 2, step: "write-without-response readiness")
        }
        p.writeValue(data, for: c, type: .withoutResponse)
        log.info("wrote \(data.count)B without response: \(data.hexString)")
        return "without-response"
    }

    // MARK: Idle / reconnect

    private func touch() {
        lastActivity = .now
        scheduleIdleDisconnect()
    }

    private func scheduleIdleDisconnect() {
        idleTask?.cancel()
        guard idleDisconnectSeconds > 0, peripheral?.state == .connected else { return }
        let seconds = idleDisconnectSeconds
        idleTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(seconds))
            guard !Task.isCancelled else { return }
            self?.disconnect(reason: "idle \(seconds)s")
        }
    }

    /// Reconnect after an unexpected drop only if the link was in use: always in
    /// stay-connected mode, otherwise only within the idle window. That way an idle app
    /// doesn't grab the device back from the phone.
    private func maybeReconnect() {
        guard autoReconnect, targetIdentifier != nil else { return }
        let inUse = idleDisconnectSeconds == 0
            || Date().timeIntervalSince(lastActivity) < TimeInterval(idleDisconnectSeconds)
        guard inUse else { return }
        reconnectAttempt += 1
        let delay = min(60.0, pow(2.0, Double(min(reconnectAttempt, 6))))
        log.info("auto-reconnect attempt \(reconnectAttempt) in \(Int(delay))s")
        reconnectTask?.cancel()
        reconnectTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled, let self else { return }
            do {
                try await self.connect()
            } catch {
                self.log.warn("auto-reconnect failed: \(error.localizedDescription)")
                self.maybeReconnect()
            }
        }
    }

    private func refreshStatus() {
        switch managerState {
        case .poweredOff, .resetting: status = .bluetoothOff
        case .unauthorized: status = .unauthorized
        case .unsupported: status = .bluetoothOff
        default:
            if targetIdentifier == nil { status = .noDevice }
            else if peripheral?.state == .connected, characteristic != nil { status = .connected }
            else if peripheral?.state == .connecting { status = .connecting }
            else { status = .disconnected }
        }
    }

    /// Connects and lists every service and characteristic, for debugging.
    public func inspect() async throws -> [String] {
        let (p, _) = try await ensureConnected()
        p.discoverServices(nil)
        try await servicesWaiter.wait(timeout: 8, step: "service discovery")
        var lines: [String] = []
        for service in p.services ?? [] {
            lines.append("service \(service.uuid.uuidString)")
            p.discoverCharacteristics(nil, for: service)
            try await characteristicsWaiter.wait(timeout: 8, step: "characteristic discovery")
            for c in service.characteristics ?? [] {
                lines.append("  char \(c.uuid.uuidString)  [\(c.properties.debugName)]")
            }
        }
        touch()
        return lines
    }

    /// One characteristic's value as read or notified during `probe`.
    public struct ProbeEvent: Sendable {
        public var characteristic: String
        public var kind: String   // "read" or "notify"
        public var value: Data
    }

    /// Diagnostics: reads every readable characteristic in the aer service, enables every
    /// notify, then listens for `listen` seconds. Never writes to the device.
    public func probe(listen: TimeInterval = 5) async throws -> [ProbeEvent] {
        let (p, _) = try await ensureConnected()
        guard let service = p.services?.first(where: { $0.uuid == Self.serviceUUID }) else {
            throw BLEError.serviceNotFound
        }
        p.discoverCharacteristics(nil, for: service)
        try await characteristicsWaiter.wait(timeout: 8, step: "characteristic discovery")
        var events: [ProbeEvent] = []
        let previous = onNotification
        probeSink = { uuid, value in events.append(ProbeEvent(characteristic: uuid, kind: "notify", value: value)) }
        defer { probeSink = nil; onNotification = previous }

        for c in service.characteristics ?? [] {
            if c.properties.contains(.notify) && !c.isNotifying {
                p.setNotifyValue(true, for: c)
                try? await notifyStateWaiter.wait(timeout: 3, step: "enable notifications")
            }
        }
        for c in service.characteristics ?? [] where c.properties.contains(.read) {
            readTarget = c.uuid
            p.readValue(for: c)
            if let v = try? await readWaiter.wait(timeout: 3, step: "read \(c.uuid)") {
                events.append(ProbeEvent(characteristic: c.uuid.uuidString, kind: "read", value: v))
            }
            readTarget = nil
        }
        try? await Task.sleep(for: .seconds(listen))
        touch()
        return events
    }

    private var probeSink: ((String, Data) -> Void)?

    private func failPendingOperations(_ error: Error) {
        connectWaiter.fail(error)
        servicesWaiter.fail(error)
        characteristicsWaiter.fail(error)
        writeWaiter.fail(error)
        readyWaiter.fail(error)
        notifyStateWaiter.fail(error)
    }
}

// MARK: - CBCentralManagerDelegate

extension BLEService: CBCentralManagerDelegate {
    nonisolated public func centralManagerDidUpdateState(_ central: CBCentralManager) {
        MainActor.assumeIsolated {
            managerState = central.state
            log.info("Bluetooth state: \(central.state.debugName)")
            if central.state != .unknown && central.state != .resetting {
                powerWaiter.succeed(())
            }
            if central.state == .poweredOn {
                updateScanning()
            } else {
                isScanning = false
                userScanActive = false
                characteristic = nil
                failPendingOperations(BLEError.bluetoothOff)
            }
            refreshStatus()
        }
    }

    nonisolated public func centralManager(_ central: CBCentralManager, didDiscover peripheral: CBPeripheral,
                                           advertisementData: [String: Any], rssi RSSI: NSNumber) {
        MainActor.assumeIsolated {
            if let target = findTarget, peripheral.identifier == target {
                findWaiter.succeed(peripheral)
            }
            guard userScanActive else { return }
            let name = advertisementData[CBAdvertisementDataLocalNameKey] as? String ?? peripheral.name
            let uuids = (advertisementData[CBAdvertisementDataServiceUUIDsKey] as? [CBUUID] ?? [])
                + (advertisementData[CBAdvertisementDataOverflowServiceUUIDsKey] as? [CBUUID] ?? [])
            let hasService = uuids.contains(Self.serviceUUID)
            guard passesFilter(name: name, advertisesService: hasService) else { return }
            upsert(peripheral, name: name, rssi: RSSI.intValue, advertisesService: hasService)
        }
    }

    nonisolated public func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) {
        MainActor.assumeIsolated {
            log.info("connected to \(peripheral.name ?? peripheral.identifier.uuidString)")
            connectWaiter.succeed(())
        }
    }

    nonisolated public func centralManager(_ central: CBCentralManager, didFailToConnect peripheral: CBPeripheral,
                                           error: Error?) {
        MainActor.assumeIsolated {
            log.error("failed to connect: \(error?.localizedDescription ?? "unknown")")
            connectWaiter.fail(BLEError.disconnected(error?.localizedDescription))
            refreshStatus()
        }
    }

    nonisolated public func centralManager(_ central: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral,
                                           error: Error?) {
        MainActor.assumeIsolated {
            let wasIntentional = intentionalDisconnect
            intentionalDisconnect = false
            characteristic = nil
            connectedName = nil
            idleTask?.cancel()
            failPendingOperations(BLEError.disconnected(error?.localizedDescription))
            refreshStatus()
            if wasIntentional {
                log.info("disconnected")
            } else {
                log.warn("connection lost: \(error?.localizedDescription ?? "no error")")
                maybeReconnect()
            }
        }
    }
}

// MARK: - CBPeripheralDelegate

extension BLEService: CBPeripheralDelegate {
    nonisolated public func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        MainActor.assumeIsolated {
            if let error { servicesWaiter.fail(error) } else { servicesWaiter.succeed(()) }
        }
    }

    nonisolated public func peripheral(_ peripheral: CBPeripheral,
                                       didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        MainActor.assumeIsolated {
            if let error { characteristicsWaiter.fail(error) } else { characteristicsWaiter.succeed(()) }
        }
    }

    nonisolated public func peripheral(_ peripheral: CBPeripheral,
                                       didWriteValueFor characteristic: CBCharacteristic, error: Error?) {
        MainActor.assumeIsolated {
            if let error {
                writeWaiter.fail(BLEError.writeFailed(error.localizedDescription))
            } else {
                writeWaiter.succeed(())
            }
        }
    }

    nonisolated public func peripheral(_ peripheral: CBPeripheral,
                                       didUpdateNotificationStateFor characteristic: CBCharacteristic, error: Error?) {
        MainActor.assumeIsolated {
            if let error {
                notifyStateWaiter.fail(error)
            } else {
                log.info("notifications \(characteristic.isNotifying ? "on" : "off") for \(characteristic.uuid)")
                notifyStateWaiter.succeed(())
            }
        }
    }

    nonisolated public func peripheral(_ peripheral: CBPeripheral,
                                       didUpdateValueFor characteristic: CBCharacteristic, error: Error?) {
        MainActor.assumeIsolated {
            guard error == nil, let value = characteristic.value else { return }
            if let target = readTarget, target == characteristic.uuid, readWaiter.isWaiting {
                readWaiter.succeed(value)
                return
            }
            probeSink?(characteristic.uuid.uuidString, value)
            let bytes = [UInt8](value)
            log.info("notify \(characteristic.uuid): \(bytes.hexString)  \(CBORDiagnostic.describe(bytes))")
            replies.append(value)
            replyWaiter.succeed(value)
            onNotification?(value)
        }
    }

    nonisolated public func peripheralIsReady(toSendWriteWithoutResponse peripheral: CBPeripheral) {
        MainActor.assumeIsolated { readyWaiter.succeed(()) }
    }

    nonisolated public func peripheral(_ peripheral: CBPeripheral, didModifyServices invalidatedServices: [CBService]) {
        MainActor.assumeIsolated {
            log.warn("services invalidated; will rediscover on next spray")
            characteristic = nil
        }
    }
}

// MARK: - Debug names

extension CBManagerState {
    public var debugName: String {
        switch self {
        case .unknown: "unknown"
        case .resetting: "resetting"
        case .unsupported: "unsupported"
        case .unauthorized: "unauthorized"
        case .poweredOff: "poweredOff"
        case .poweredOn: "poweredOn"
        @unknown default: "state(\(rawValue))"
        }
    }
}

extension CBCharacteristicProperties {
    var debugName: String {
        var parts: [String] = []
        if contains(.read) { parts.append("read") }
        if contains(.write) { parts.append("write") }
        if contains(.writeWithoutResponse) { parts.append("writeWithoutResponse") }
        if contains(.notify) { parts.append("notify") }
        if contains(.indicate) { parts.append("indicate") }
        return parts.isEmpty ? "none" : parts.joined(separator: ",")
    }
}
