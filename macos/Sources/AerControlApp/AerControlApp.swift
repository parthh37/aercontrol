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
            MenuBarLabel(model: model)
        }
        .menuBarExtraStyle(.window)

        Window("AerControl", id: "main") {
            MainView(model: model)
                .frame(minWidth: 620, minHeight: 480)
        }
        .defaultSize(width: 720, height: 560)
    }
}

enum MainTab: String, Hashable {
    case control = "spray", devices, settings
}

struct MainView: View {
    @ObservedObject var model: AppModel

    var body: some View {
        TabView(selection: $model.selectedTab) {
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
            if model.controller.settings.deviceIdentifier == nil { model.selectedTab = .devices }
        }
    }
}

/// The menu bar icon. It's always rendered, so it's where the `openWindow` action is
/// captured for code that runs outside SwiftUI (URL handler, app delegate).
struct MenuBarLabel: View {
    @ObservedObject var model: AppModel
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Image(systemName: model.controller.isSpraying ? "aqi.high" : "aqi.medium")
            .onAppear {
                model.showMainWindow = {
                    openWindow(id: "main")
                    NSApp.activate(ignoringOtherApps: true)
                }
            }
    }
}
