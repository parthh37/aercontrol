import SwiftUI
import AerKit

struct StatusBadge: View {
    let status: LinkStatus

    var body: some View {
        Label(status.label, systemImage: status.symbol)
            .font(.callout.weight(.medium))
            .foregroundStyle(status.color)
            .padding(.horizontal, 10)
            .padding(.vertical, 4)
            .background(status.color.opacity(0.12), in: Capsule())
    }
}

struct SprayButton: View {
    @ObservedObject var model: AppModel
    var size: CGFloat = 150
    var trigger: SprayTrigger = .button

    var body: some View {
        let spraying = model.controller.isSpraying
        Button {
            model.spray(trigger)
        } label: {
            ZStack {
                Circle()
                    .fill(fill.gradient)
                    .shadow(color: fill.opacity(0.35), radius: 12, y: 4)
                if spraying {
                    ProgressView().controlSize(.large).tint(.white)
                } else {
                    VStack(spacing: 4) {
                        Image(systemName: icon).font(.system(size: size * 0.26, weight: .semibold))
                        Text("Spray").font(.system(size: size * 0.13, weight: .bold))
                    }
                    .foregroundStyle(.white)
                }
            }
            .frame(width: size, height: size)
            .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .disabled(spraying || model.controller.settings.deviceIdentifier == nil)
        .keyboardShortcut(.return, modifiers: [.command])
        .help(model.controller.settings.deviceIdentifier == nil ? "Select a device first" : "Spray now (⌘↩)")
        .animation(.spring(duration: 0.3), value: model.flash?.id)
    }

    private var fill: Color {
        if let f = model.flash { return f.success ? .green : .red }
        return model.controller.settings.deviceIdentifier == nil ? .gray : .accentColor
    }

    private var icon: String {
        if let f = model.flash { return f.success ? "checkmark" : "xmark" }
        return "aqi.medium"
    }
}

struct ControlView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var controller: SprayController
    @ObservedObject var ble: BLEService
    @State private var confirmRefill = false
    @State private var refillError: String?

    init(model: AppModel) {
        self.model = model
        self.controller = model.controller
        self.ble = model.ble
    }

    var body: some View {
        VStack(spacing: 0) {
            UpdateBanner(services: model.services)
            header
                .padding(20)
                .onAppear { controller.refreshDeviceStatusIfOlder(than: 600) }
            Divider()
            HistoryList(controller: controller)
        }
    }

    private var header: some View {
        HStack(alignment: .center, spacing: 28) {
            SprayButton(model: model)
            VStack(alignment: .leading, spacing: 10) {
                Text(controller.settings.deviceName ?? "No device selected")
                    .font(.title2.bold())
                HStack(spacing: 8) {
                    StatusBadge(status: ble.status)
                    DeviceStatusChips(status: controller.deviceStatus)
                }
                if let last = controller.lastSpray {
                    LastSprayLine(record: last)
                } else {
                    Text("No sprays yet").foregroundStyle(.secondary)
                }
                if controller.deviceStatus?.refillNeedsReset == true {
                    Text("The device has counted more sprays than a can holds. Just put in a new can? Press “Reset refill…”.")
                        .font(.callout).foregroundStyle(.orange)
                        .fixedSize(horizontal: false, vertical: true)
                }
                if let refillError {
                    Text(refillError).font(.callout).foregroundStyle(.red)
                }
                if let flash = model.flash, !flash.success, let err = flash.error {
                    Text(err).font(.callout).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true)
                }
                HStack {
                    Button {
                        Task { await controller.refreshDeviceStatus() }
                    } label: {
                        Label("Refresh status", systemImage: "arrow.clockwise")
                    }
                    .disabled(controller.settings.deviceIdentifier == nil)
                    Button("Reset refill…") { confirmRefill = true }
                        .disabled(controller.settings.deviceIdentifier == nil)
                        .confirmationDialog("Reset the refill level?", isPresented: $confirmRefill) {
                            Button("I inserted a new refill: reset") {
                                Task {
                                    do { try await controller.resetRefill() }
                                    catch { refillError = error.localizedDescription }
                                }
                            }
                        } message: {
                            Text("This tells the aer that a fresh can is in, which sets its spray counter back to zero (like “Reset level” in the Godrej app).")
                        }
                    if ble.status == .connected {
                        Button("Disconnect") { ble.disconnect() }
                    } else if controller.settings.deviceIdentifier != nil {
                        Button("Connect") { Task { try? await ble.connect() } }
                            .disabled(ble.status == .connecting)
                    }
                }
                .controlSize(.small)
            }
            Spacer(minLength: 0)
        }
    }
}

/// Battery and refill chips from the device's fetch-status reply ("—" before the first read).
struct DeviceStatusChips: View {
    let status: DeviceStatus?

    var body: some View {
        HStack(spacing: 6) {
            chip(symbol: batterySymbol, text: batteryText,
                 help: "Battery \(status?.batteryVoltsText ?? "unknown"). The percentage is estimated from voltage (2.4–3.2 V); the official app warns below 2.8 V.",
                 warn: status?.batteryLow == true)
            chip(symbol: "drop.fill", text: refillText,
                 help: status?.totalSprays.map { "\($0) sprays since the last refill reset (a refill lasts about \(status?.refillCapacity ?? 2200))." } ?? "Refill level",
                 warn: (status?.refillPercent ?? 100) <= 7)
        }
    }

    private var batteryText: String {
        guard let s = status, let v = s.batteryVoltsText else { return "—" }
        return s.batteryPercent.map { "~\($0)% · \(v)" } ?? v
    }

    private var refillText: String {
        guard let r = status?.spraysRemaining, let p = status?.refillPercent else { return "—" }
        return "\(p)% · \(r) left"
    }

    private var batterySymbol: String {
        switch status?.batteryPercent ?? -1 {
        case 75...: "battery.100"
        case 50..<75: "battery.75"
        case 25..<50: "battery.50"
        case 0..<25: "battery.25"
        default: "battery.0"
        }
    }

    private func chip(symbol: String, text: String, help: String, warn: Bool) -> some View {
        Label(text, systemImage: symbol)
            .font(.callout.monospacedDigit())
            .foregroundStyle(warn ? Color.orange : Color.secondary)
            .padding(.horizontal, 8)
            .padding(.vertical, 4)
            .background(.quaternary.opacity(0.5), in: Capsule())
            .help(help)
    }
}

/// Shown above the Spray tab when a newer GitHub release exists.
struct UpdateBanner: View {
    @ObservedObject var services: BackgroundServices

    var body: some View {
        if let update = services.availableUpdate {
            HStack {
                Image(systemName: "arrow.down.circle.fill").foregroundStyle(.blue)
                Text("AerControl \(update.version) is available.")
                Spacer()
                Button("Release notes") { NSWorkspace.shared.open(update.pageURL) }
                Button("Download") { services.openUpdate() }.buttonStyle(.borderedProminent)
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 8)
            .background(.blue.opacity(0.08))
        }
    }
}

struct LastSprayLine: View {
    let record: SprayRecord

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: record.success ? "checkmark.circle.fill" : "xmark.octagon.fill")
                .foregroundStyle(record.success ? .green : .red)
            Text("Last spray ") + Text(record.date, style: .relative) + Text(" ago")
        }
        .font(.callout)
        .foregroundStyle(.secondary)
    }
}

struct HistoryList: View {
    @ObservedObject var controller: SprayController

    var body: some View {
        Group {
            if controller.history.isEmpty {
                ContentUnavailableView("No spray history", systemImage: "clock.arrow.circlepath",
                                       description: Text("Sprays from the app, menu bar, widget, schedule and hotkey show up here."))
            } else {
                Table(controller.history) {
                    TableColumn("") { r in
                        Image(systemName: r.success ? "checkmark.circle.fill" : "xmark.octagon.fill")
                            .foregroundStyle(r.success ? .green : .red)
                    }
                    .width(18)
                    TableColumn("Time") { r in
                        Text(r.date.formatted(date: .abbreviated, time: .standard))
                    }
                    .width(min: 150, ideal: 170)
                    TableColumn("Source") { r in
                        Label(r.trigger.rawValue, systemImage: r.trigger.symbol)
                    }
                    .width(min: 80, ideal: 100)
                    TableColumn("mN") { r in
                        Text(r.messageNumber.map(String.init) ?? "–").monospacedDigit()
                    }
                    .width(40)
                    TableColumn("Details") { r in
                        Text(r.error ?? r.writeMode ?? "")
                            .foregroundStyle(r.success ? Color.secondary : Color.red)
                            .help(r.error ?? r.reply.map { "reply: \($0)" } ?? "")
                    }
                }
                .contextMenu {
                    Button("Clear History", role: .destructive) { controller.clearHistory() }
                }
            }
        }
        .frame(minHeight: 180)
    }
}
