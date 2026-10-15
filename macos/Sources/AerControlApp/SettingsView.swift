import SwiftUI
import AerKit

struct SettingsView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var controller: SprayController

    init(model: AppModel) {
        self.model = model
        self.controller = model.controller
    }

    var body: some View {
        Form {
            Section("Connection") {
                Stepper(value: $controller.settings.idleDisconnectSeconds, in: 0...3600, step: 1) {
                    LabeledContent("Disconnect when idle") {
                        Text(controller.settings.idleDisconnectSeconds == 0
                             ? "Never (stay connected)"
                             : "after \(controller.settings.idleDisconnectSeconds) s")
                    }
                }
                Text("The aer accepts one connection at a time. A short hold (a few seconds) lets your other computers and the phone app spray right after this one.")
                    .font(.caption).foregroundStyle(.secondary)
                Toggle("Auto-reconnect if the link drops while in use", isOn: $controller.settings.autoReconnect)
                Picker("Write type", selection: $controller.settings.writeMode) {
                    ForEach(WriteMode.allCases) { Text($0.label).tag($0) }
                }
            }

            Section("Payload") {
                Toggle("Auto-increment mN after each spray", isOn: $controller.settings.autoIncrementMessageNumber)
                LabeledContent("Next mN") {
                    TextField("", value: $controller.settings.messageNumber, format: .number)
                        .labelsHidden()
                        .frame(width: 80)
                        .multilineTextAlignment(.trailing)
                }
                LabeledContent("Bytes") {
                    Text(SprayPayload(messageNumber: controller.settings.messageNumber).encoded().hexString)
                        .font(.caption.monospaced())
                        .textSelection(.enabled)
                }
            }

            ExtraSettingsSections(model: model)

            Section("Diagnostics") {
                LabeledContent("Log file") {
                    Button("Show in Finder") { model.revealLog() }
                }
                LabeledContent("Version", value: Bundle.main.shortVersion)
            }
        }
        .formStyle(.grouped)
    }
}
