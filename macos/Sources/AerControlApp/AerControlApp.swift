import SwiftUI
import AerKit

@main
struct AerControlApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var model = AppModel.shared

    var body: some Scene {
        MenuBarExtra {
            MenuBarView(model: model)
        } label: {
            Image(systemName: model.controller.isSpraying ? "aqi.high" : "aqi.medium")
        }
        .menuBarExtraStyle(.window)

        Window("AerControl", id: "main") {
            MainView(model: model)
                .frame(minWidth: 620, minHeight: 480)
        }
        .defaultSize(width: 720, height: 560)
    }
}

enum MainTab: Hashable { case control, devices, settings }

struct MainView: View {
    @ObservedObject var model: AppModel
    @State private var tab: MainTab = .control

    var body: some View {
        TabView(selection: $tab) {
            ControlView(model: model)
                .tabItem { Label("Spray", systemImage: "aqi.medium") }
                .tag(MainTab.control)
            DevicesView(controller: model.controller, ble: model.ble)
                .tabItem { Label("Devices", systemImage: "dot.radiowaves.left.and.right") }
                .tag(MainTab.devices)
            SettingsView(model: model)
                .tabItem { Label("Settings", systemImage: "gearshape") }
                .tag(MainTab.settings)
        }
        .padding(.top, 4)
        .onAppear {
            if model.controller.settings.deviceIdentifier == nil { tab = .devices }
        }
    }
}
