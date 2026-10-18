import SwiftUI
import AerKit

/// The menu bar popover: status at a glance plus the one action that matters.
struct MenuBarView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var controller: SprayController
    @ObservedObject var ble: BLEService
    @ObservedObject var services: BackgroundServices
    @Environment(\.openWindow) private var openWindow

    init(model: AppModel) {
        self.model = model
        self.controller = model.controller
        self.ble = model.ble
        self.services = model.services
    }

    var body: some View {
        VStack(spacing: 14) {
            HStack(spacing: 10) {
                Image(systemName: ble.status.symbol)
                    .font(.title2)
                    .foregroundStyle(ble.status.color)
                    .frame(width: 28)
                VStack(alignment: .leading, spacing: 2) {
                    Text(controller.settings.deviceName ?? "No device").font(.headline)
                    Text(ble.status.label).font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
            }

            SprayButton(model: model, size: 112, trigger: .menuBar)

            DeviceStatusChips(status: controller.deviceStatus)
            BatteryWarningLine(status: controller.deviceStatus, font: .caption)
                .multilineTextAlignment(.center)

            VStack(spacing: 4) {
                if let last = controller.lastSpray {
                    LastSprayLine(record: last)
                }
                if let next = services.nextScheduledSpray {
                    (Text("Next scheduled ") + Text(next, style: .relative))
                        .font(.caption).foregroundStyle(.secondary)
                }
                if let flash = model.flash, !flash.success, let error = flash.error {
                    Text(error).font(.caption).foregroundStyle(.red)
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            if let update = services.availableUpdate {
                Button {
                    services.openUpdate()
                } label: {
                    Label("Update to \(update.version)", systemImage: "arrow.down.circle.fill")
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.small)
            }

            Divider()

            HStack {
                Button("Open AerControl…") {
                    openWindow(id: "main")
                    NSApp.activate(ignoringOtherApps: true)
                }
                Spacer()
                Button("Quit") { NSApp.terminate(nil) }
            }
            .buttonStyle(.borderless)
        }
        .padding(16)
        .frame(width: 280)
        .onAppear { controller.refreshDeviceStatusIfOlder(than: 600) }
    }
}

/// Phase 3 settings: schedule, hotkey, launch at login.
struct ExtraSettingsSections: View {
    @ObservedObject var model: AppModel
    @ObservedObject var controller: SprayController
    @ObservedObject var services: BackgroundServices

    init(model: AppModel) {
        self.model = model
        self.controller = model.controller
        self.services = model.services
    }

    var body: some View {
        Section("Schedule") {
            Toggle("Spray on a schedule", isOn: $controller.settings.schedule.enabled)
            Stepper(value: $controller.settings.schedule.intervalMinutes, in: 1...720) {
                LabeledContent("Every", value: "\(controller.settings.schedule.intervalMinutes) min")
            }
            .disabled(!controller.settings.schedule.enabled)
            DatePicker("Between", selection: minutesBinding(\.startMinutes), displayedComponents: .hourAndMinute)
                .disabled(!controller.settings.schedule.enabled)
            DatePicker("and", selection: minutesBinding(\.endMinutes), displayedComponents: .hourAndMinute)
                .disabled(!controller.settings.schedule.enabled)
            if let next = services.nextScheduledSpray {
                LabeledContent("Next spray") {
                    Text(next.formatted(date: .abbreviated, time: .shortened))
                }
            }
        }

        Section("Shortcuts") {
            Toggle("Global hotkey \(GlobalHotKey.defaultDisplay) sprays", isOn: $controller.settings.hotkeyEnabled)
            if let error = services.hotkeyError {
                Text(error).font(.caption).foregroundStyle(.red)
            }
            Text("Shortcuts, Siri and Spotlight also offer “Spray Air Freshener” and “Get Air Freshener Status” actions.")
                .font(.caption).foregroundStyle(.secondary)
        }

        Section("Updates") {
            Toggle("Check for updates automatically (daily)", isOn: $controller.settings.checkForUpdates)
            HStack {
                Button("Check Now") { Task { await services.checkForUpdates() } }
                    .disabled(services.isCheckingForUpdates)
                if services.isCheckingForUpdates { ProgressView().controlSize(.small) }
                if let update = services.availableUpdate {
                    Button("Download \(update.version)") { services.openUpdate() }
                        .buttonStyle(.borderedProminent)
                }
            }
            if let state = services.updateCheckState {
                Text(state).font(.caption).foregroundStyle(.secondary)
            }
        }

        Section("General") {
            Toggle("Launch at login", isOn: Binding(
                get: { services.launchAtLogin },
                set: { services.setLaunchAtLogin($0) }))
            if let error = services.launchAtLoginError {
                Text(error).font(.caption).foregroundStyle(.red)
            }
        }
    }

    /// Bridges minutes-after-midnight storage to a DatePicker.
    private func minutesBinding(_ keyPath: WritableKeyPath<ScheduleWindow, Int>) -> Binding<Date> {
        Binding(
            get: {
                let m = controller.settings.schedule.window[keyPath: keyPath]
                return Calendar.current.date(bySettingHour: m / 60, minute: m % 60, second: 0, of: .now) ?? .now
            },
            set: { date in
                controller.settings.schedule.window[keyPath: keyPath] = ScheduleWindow.minutesOfDay(date)
            })
    }
}
