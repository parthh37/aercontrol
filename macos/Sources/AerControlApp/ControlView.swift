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

    init(model: AppModel) {
        self.model = model
        self.controller = model.controller
        self.ble = model.ble
    }

    var body: some View {
        VStack(spacing: 0) {
            header
                .padding(20)
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
                if let flash = model.flash, !flash.success, let err = flash.error {
                    Text(err).font(.callout).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true)
                }
                HStack {
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

/// Battery and refill chips; "—" until the device's status query is decoded.
struct DeviceStatusChips: View {
    let status: DeviceStatus?

    var body: some View {
        HStack(spacing: 6) {
            chip(symbol: batterySymbol, text: status?.batteryPercent.map { "\($0)%" } ?? "—",
                 help: "Battery")
            chip(symbol: "drop.fill", text: refillText, help: "Sprays remaining in refill")
        }
        .help(status == nil ? "Battery and refill level aren't decoded from the aer protocol yet." : "")
    }

    private var refillText: String {
        guard let r = status?.spraysRemaining else { return "—" }
        if let c = status?.refillCapacity { return "\(r)/\(c)" }
        return "\(r)"
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

    private func chip(symbol: String, text: String, help: String) -> some View {
        Label(text, systemImage: symbol)
            .font(.callout.monospacedDigit())
            .foregroundStyle(.secondary)
            .padding(.horizontal, 8)
            .padding(.vertical, 4)
            .background(.quaternary.opacity(0.5), in: Capsule())
            .help(help)
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
