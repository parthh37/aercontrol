import Foundation
import AerKit

// aerctl: Phase 1 command-line spray test for the Godrej aer Smart Matic.

let usage = """
usage: aerctl <command> [options]

commands:
  scan [--seconds N] [--all]       list nearby devices (default 10s, aer/service filter)
  spray [--device UUID] [--mn N] [--no-increment] [--mode auto|with|without]
        [--count N] [--interval SECONDS]
                                   connect and send the spray payload
        [--raw HEX]                use HEX bytes instead of the CBOR payload
        [--settle SECONDS]         wait after connecting before writing (default 2)
  inspect [--device UUID]          connect and list all GATT services/characteristics
  probe [--device UUID] [--seconds N]
                                   read every characteristic + listen to notifications
                                   (never writes; for finding battery/refill data)
  status [--device UUID]           battery, refill level, firmware (fetch status)
  refill-reset [--device UUID] --yes
                                   tell the device a new refill went in (resets counters)
  remember <UUID>                  set the default device for `spray`
  forget                           clear the default device
  payload [--mn N]                 print the payload hex without sending
  log                              print the log file path

The default device and mN are stored in the `com.aercontrol.cli` defaults domain.
The device allows one BLE connection at a time; close the phone app first.
"""

struct Args {
    var command: String
    var positional: [String] = []
    var options: [String: String] = [:]
    var flags: Set<String> = []

    init(_ argv: [String]) {
        command = argv.first ?? "help"
        var i = 1
        while i < argv.count {
            let a = argv[i]
            if a.hasPrefix("--") {
                let key = String(a.dropFirst(2))
                if i + 1 < argv.count, !argv[i + 1].hasPrefix("--"),
                   ["seconds", "device", "mn", "mode", "count", "interval", "raw", "settle"].contains(key) {
                    options[key] = argv[i + 1]
                    i += 1
                } else {
                    flags.insert(key)
                }
            } else {
                positional.append(a)
            }
            i += 1
        }
    }
}

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data("error: \(message)\n".utf8))
    exit(1)
}

@MainActor
func run() async {
    let args = Args(Array(CommandLine.arguments.dropFirst()))
    let log = FileLogger.shared
    log.echoToStderr = ProcessInfo.processInfo.environment["AER_VERBOSE"] != nil
    let defaults = UserDefaults(suiteName: "com.aercontrol.cli") ?? .standard

    switch args.command {
    case "payload":
        let mn = args.options["mn"].flatMap(UInt32.init) ?? SprayPayload.capturedMessageNumber
        let bytes = SprayPayload(messageNumber: mn).encoded()
        print(bytes.map { String(format: "%02x", $0) }.joined(separator: " "), "(\(bytes.count) bytes)")
        return

    case "log":
        print(log.fileURL.path)
        return

    case "remember":
        guard let s = args.positional.first, let id = UUID(uuidString: s) else { fail("remember needs a peripheral UUID") }
        let controller = SprayController(defaults: defaults, log: log)
        controller.settings.deviceIdentifier = id
        print("default device set to \(id)")
        return

    case "forget":
        let controller = SprayController(defaults: defaults, log: log)
        controller.settings.deviceIdentifier = nil
        controller.settings.deviceName = nil
        print("default device cleared")
        return

    case "scan":
        let controller = SprayController(defaults: defaults, log: log)
        let ble = controller.ble
        if args.flags.contains("all") { ble.scanFilter = .all }
        let seconds = args.options["seconds"].flatMap(Double.init) ?? 10
        await waitForBluetooth(ble)
        ble.startScan(duration: seconds)
        print("scanning \(Int(seconds))s (filter: \(ble.scanFilter.label))…")
        try? await Task.sleep(for: .seconds(seconds + 0.3))
        ble.stopScan()
        if ble.discovered.isEmpty {
            print("no matching devices. Try --all, and make sure the phone app is disconnected.")
        }
        for d in ble.discovered.sorted(by: { $0.rssi > $1.rssi }) {
            let svc = d.advertisesService ? "svc✓" : "    "
            print(String(format: "%4d dBm  %@  %@  %@", d.rssi, svc, d.id.uuidString, d.name))
        }
        return

    case "inspect":
        let controller = SprayController(defaults: defaults, log: log)
        if let s = args.options["device"] {
            guard let id = UUID(uuidString: s) else { fail("--device must be a peripheral UUID") }
            controller.settings.deviceIdentifier = id
        }
        await waitForBluetooth(controller.ble)
        do {
            for line in try await controller.ble.inspect() { print(line) }
        } catch {
            fail(error.localizedDescription)
        }
        controller.ble.disconnect(reason: "cli done")
        try? await Task.sleep(for: .milliseconds(300))
        return

    case "status", "refill-reset":
        let controller = SprayController(defaults: defaults, log: log)
        if let s = args.options["device"] {
            guard let id = UUID(uuidString: s) else { fail("--device must be a peripheral UUID") }
            controller.settings.deviceIdentifier = id
        }
        guard controller.settings.deviceIdentifier != nil else { fail("no device. Use --device or `aerctl remember`") }
        controller.settings.idleDisconnectSeconds = 0
        await waitForBluetooth(controller.ble)
        if args.command == "refill-reset" {
            guard args.flags.contains("yes") else { fail("this resets the device's spray counter; re-run with --yes after inserting a new refill") }
            do { try await controller.resetRefill() } catch { fail(error.localizedDescription) }
            print("refill reset")
        } else {
            _ = await controller.refreshDeviceStatus()
        }
        guard let s = controller.deviceStatus else { fail("no status reply from the device") }
        print("battery:  \(s.batteryVoltsText ?? "?")  (~\(s.batteryPercent ?? 0)%, estimated)\(s.batteryLow ? "  LOW" : "")")
        print("refill:   \(s.refillPercent ?? 0)%  (\(s.spraysRemaining ?? 0) of \(s.refillCapacity ?? 2200) left, \(s.totalSprays ?? 0) sprays since reset)")
        print("firmware: \(s.firmwareVersion ?? "?")")
        controller.ble.disconnect(reason: "cli done")
        try? await Task.sleep(for: .milliseconds(300))
        return

    case "probe":
        let controller = SprayController(defaults: defaults, log: log)
        if let s = args.options["device"] {
            guard let id = UUID(uuidString: s) else { fail("--device must be a peripheral UUID") }
            controller.settings.deviceIdentifier = id
        }
        let seconds = args.options["seconds"].flatMap(Double.init) ?? 5
        await waitForBluetooth(controller.ble)
        do {
            let events = try await controller.ble.probe(listen: seconds)
            if events.isEmpty { print("no values read or notified") }
            for e in events {
                let bytes = [UInt8](e.value)
                print("\(e.kind.padding(toLength: 6, withPad: " ", startingAt: 0)) \(e.characteristic.prefix(8))  \(bytes.hexString)")
                print("       ascii: \(String(decoding: bytes.map { (32...126).contains($0) ? $0 : 46 }, as: UTF8.self))")
                print("       cbor:  \(CBORDiagnostic.describe(bytes))")
            }
        } catch {
            fail(error.localizedDescription)
        }
        controller.ble.disconnect(reason: "cli done")
        try? await Task.sleep(for: .milliseconds(300))
        return

    case "spray":
        let controller = SprayController(defaults: defaults, log: log)
        var raw: Data?
        if let s = args.options["raw"] {
            guard let bytes = [UInt8](hex: s) else { fail("--raw must be hex, e.g. bf626d54…ff") }
            raw = Data(bytes)
        }
        if let s = args.options["device"] {
            guard let id = UUID(uuidString: s) else { fail("--device must be a peripheral UUID (see `aerctl scan`)") }
            controller.settings.deviceIdentifier = id
        }
        if let s = args.options["mn"] {
            guard let mn = UInt32(s) else { fail("--mn must be a non-negative integer") }
            controller.settings.messageNumber = mn
        }
        // mN auto-increments by default as a safeguard (see protocol/README.md field notes).
        controller.settings.autoIncrementMessageNumber = !args.flags.contains("no-increment")
        switch args.options["mode"] ?? "auto" {
        case "auto": controller.settings.writeMode = .automatic
        case "with": controller.settings.writeMode = .withResponse
        case "without": controller.settings.writeMode = .withoutResponse
        default: fail("--mode must be auto, with, or without")
        }
        controller.settings.idleDisconnectSeconds = 0 // CLI disconnects explicitly
        if let s = args.options["settle"] {
            guard let seconds = Double(s), seconds >= 0 else { fail("--settle must be a number of seconds") }
            controller.ble.postConnectSettle = seconds
        }
        guard controller.settings.deviceIdentifier != nil else {
            fail("no device. Run `aerctl scan`, then `aerctl remember <UUID>` or pass --device")
        }

        let count = max(1, args.options["count"].flatMap(Int.init) ?? 1)
        let interval = args.options["interval"].flatMap(Double.init) ?? 3
        await waitForBluetooth(controller.ble)
        var failures = 0
        for i in 1...count {
            let r = await controller.spray(trigger: .cli, rawPayload: raw)
            if r.success {
                print("[\(i)/\(count)] sent (mN=\(r.messageNumber ?? 0), \(r.writeMode ?? "?"))")
                if let reply = r.reply, let bytes = [UInt8](hex: reply) {
                    print("    reply: \(reply)  \(CBORDiagnostic.describe(bytes))")
                } else {
                    print("    no reply within \(controller.ble.replyWindow)s")
                }
            } else {
                failures += 1
                print("[\(i)/\(count)] FAILED: \(r.error ?? "unknown error")")
            }
            if i < count { try? await Task.sleep(for: .seconds(interval)) }
        }
        controller.ble.disconnect(reason: "cli done")
        try? await Task.sleep(for: .milliseconds(300))
        exit(failures == 0 ? 0 : 2)

    default:
        print(usage)
    }
}

@MainActor
func waitForBluetooth(_ ble: BLEService) async {
    for _ in 0..<50 where ble.managerState == .unknown || ble.managerState == .resetting {
        try? await Task.sleep(for: .milliseconds(100))
    }
    switch ble.managerState {
    case .poweredOn: return
    case .unauthorized: fail("Bluetooth permission denied. Allow your terminal in System Settings › Privacy & Security › Bluetooth.")
    case .poweredOff: fail("Bluetooth is off.")
    default: fail("Bluetooth unavailable (\(ble.managerState.debugName)).")
    }
}

let offlineCommands: Set<String> = ["payload", "log", "remember", "forget", "help", "--help", "-h"]
let needsBluetooth = !offlineCommands.contains(CommandLine.arguments.dropFirst().first ?? "help")
if needsBluetooth { Disclaim.reexecIfNeeded() }
await run()
