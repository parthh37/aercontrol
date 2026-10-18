import AppIntents
import AerKit

// Shortcuts / Siri / Spotlight actions. These run in the app process (which owns the BLE
// link), unlike the widget's button intent, which only signals the app.

struct SprayNowIntent: AppIntent {
    static let title: LocalizedStringResource = "Spray Air Freshener"
    static let description = IntentDescription("Triggers your Godrej aer Smart Matic once.")

    @MainActor
    func perform() async throws -> some IntentResult & ReturnsValue<Bool> & ProvidesDialog {
        let record = await AppModel.shared.sprayNow(.shortcut)
        let dialog: IntentDialog = record.success
            ? "Sprayed."
            : "Couldn't spray: \(record.error ?? "unknown error")"
        return .result(value: record.success, dialog: dialog)
    }
}

struct GetStatusIntent: AppIntent {
    static let title: LocalizedStringResource = "Get Air Freshener Status"
    static let description = IntentDescription("Reports the connection status, last spray, battery and refill level.")

    @MainActor
    func perform() async throws -> some IntentResult & ReturnsValue<String> & ProvidesDialog {
        let c = AppModel.shared.controller
        var parts = ["\(c.settings.deviceName ?? "No device"): \(c.ble.status.label)"]
        if let last = c.lastSpray {
            parts.append("last spray \(last.date.formatted(.relative(presentation: .named)))\(last.success ? "" : " (failed)")")
        }
        if let b = c.deviceStatus?.batteryPercent {
            parts.append("battery \(b)%\(c.deviceStatus?.batteryCritical == true ? " (low: replace the batteries)" : "")")
        }
        if let r = c.deviceStatus?.spraysRemaining { parts.append("\(r) sprays left") }
        let text = parts.joined(separator: ", ")
        return .result(value: text, dialog: "\(text)")
    }
}

struct AerShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
        AppShortcut(intent: SprayNowIntent(),
                    phrases: ["Spray with \(.applicationName)",
                              "Freshen the air with \(.applicationName)"],
                    shortTitle: "Spray",
                    systemImageName: "aqi.medium")
        AppShortcut(intent: GetStatusIntent(),
                    phrases: ["\(.applicationName) status"],
                    shortTitle: "Status",
                    systemImageName: "info.circle")
    }
}
