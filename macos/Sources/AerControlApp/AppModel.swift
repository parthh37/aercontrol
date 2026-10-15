import SwiftUI
import Combine
import WidgetKit
import AerKit

/// App-wide composition root. The window, menu bar, widget handoff, scheduler and hotkey
/// all funnel into `controller.spray(trigger:)`.
@MainActor
final class AppModel: ObservableObject {
    static let shared = AppModel()

    let controller: SprayController
    var ble: BLEService { controller.ble }
    var log: FileLogger { controller.log }

    /// Last spray result, shown briefly as feedback on buttons.
    @Published private(set) var flash: SprayRecord?

    private(set) lazy var services = BackgroundServices(model: self)
    /// Set by a SwiftUI view that has access to `openWindow`.
    var showMainWindow: (() -> Void)?
    /// Selected tab of the main window; settable via aercontrol://open?tab=…
    @Published var selectedTab: MainTab = .control

    private var cancellables: Set<AnyCancellable> = []
    private var flashTask: Task<Void, Never>?

    private init() {
        controller = SprayController()
        controller.onSnapshotPublished = {
            WidgetCenter.shared.reloadTimelines(ofKind: AerShared.widgetKind)
        }
        // Re-publish nested changes so views observing AppModel refresh too.
        controller.objectWillChange.sink { [weak self] in self?.objectWillChange.send() }.store(in: &cancellables)
        ble.objectWillChange.sink { [weak self] in self?.objectWillChange.send() }.store(in: &cancellables)
        log.info("AerControl \(Bundle.main.shortVersion) started")
        controller.publishSnapshot(force: true)
    }

    func spray(_ trigger: SprayTrigger) {
        Task { await sprayNow(trigger) }
    }

    @discardableResult
    func sprayNow(_ trigger: SprayTrigger) async -> SprayRecord {
        let record = await controller.spray(trigger: trigger)
        flash = record
        flashTask?.cancel()
        flashTask = Task {
            try? await Task.sleep(for: .seconds(2.5))
            if !Task.isCancelled { flash = nil }
        }
        return record
    }

    func revealLog() {
        NSWorkspace.shared.activateFileViewerSelecting([log.fileURL])
    }
}

extension Bundle {
    var shortVersion: String {
        (infoDictionary?["CFBundleShortVersionString"] as? String) ?? "dev"
    }
}

extension LinkStatus {
    var color: Color {
        switch self {
        case .connected: .green
        case .connecting: .orange
        case .disconnected: .secondary
        case .noDevice: .yellow
        case .bluetoothOff, .unauthorized: .red
        }
    }

    var symbol: String {
        switch self {
        case .connected: "dot.radiowaves.left.and.right"
        case .connecting: "antenna.radiowaves.left.and.right"
        case .disconnected: "moon.zzz"
        case .noDevice: "questionmark.circle"
        case .bluetoothOff: "bolt.horizontal.circle"
        case .unauthorized: "lock.circle"
        }
    }
}

extension SprayTrigger {
    var symbol: String {
        switch self {
        case .button: "hand.tap"
        case .menuBar: "menubar.rectangle"
        case .widget: "square.grid.2x2"
        case .url: "link"
        case .schedule: "clock"
        case .hotkey: "keyboard"
        case .cli: "terminal"
        case .shortcut: "square.stack.3d.up"
        }
    }
}
