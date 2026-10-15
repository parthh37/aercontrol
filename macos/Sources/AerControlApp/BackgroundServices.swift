import AppKit
import Combine
import ServiceManagement
import AerKit

/// Things that run while only the menu bar icon is visible: the schedule, the global
/// hotkey, launch at login, and handoff from the widget (Darwin notification) and the
/// `aercontrol://spray` URL.
@MainActor
final class BackgroundServices: ObservableObject {
    @Published private(set) var hotkeyError: String?
    @Published private(set) var launchAtLogin = SMAppService.mainApp.status == .enabled
    @Published private(set) var launchAtLoginError: String?
    @Published private(set) var nextScheduledSpray: Date?
    @Published private(set) var availableUpdate: AppRelease?
    @Published private(set) var updateCheckState: String?
    @Published private(set) var isCheckingForUpdates = false

    private static let lastUpdateCheckKey = "updates.lastCheck"

    private unowned let model: AppModel
    private var scheduleTimer: Timer?
    private var hotKey: GlobalHotKey?
    private var widgetObservation: DarwinNotification.Observation?
    private var cancellables: Set<AnyCancellable> = []

    init(model: AppModel) {
        self.model = model
    }

    func start() {
        // React to setting changes (hotkey on/off, schedule edits).
        model.controller.$settings
            .map(\.hotkeyEnabled)
            .removeDuplicates()
            .sink { [weak self] enabled in self?.applyHotKey(enabled) }
            .store(in: &cancellables)
        model.controller.$settings
            .map(\.schedule)
            .removeDuplicates()
            .sink { [weak self] _ in
                // $settings fires before the stored value changes; recompute on the next turn.
                Task { @MainActor in self?.updateNextScheduled() }
            }
            .store(in: &cancellables)

        scheduleTimer = Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tickSchedule() }
        }
        scheduleTimer?.tolerance = 5

        widgetObservation = DarwinNotification.observe(AerShared.sprayRequestNotification) { [weak self] in
            Task { @MainActor in self?.handlePendingWidgetRequest() }
        }
        // The widget may have launched us through the URL fallback; pick up its request.
        handlePendingWidgetRequest()
        refreshStatusIfStale()
        checkForUpdatesIfDue()
    }

    // MARK: Schedule

    /// Time of the last scheduled spray, taken from history so it survives restarts.
    private var lastScheduledSpray: Date? {
        model.controller.history.first { $0.trigger == .schedule }?.date
    }

    /// Battery and refill change slowly, and every read briefly takes the link from the
    /// phone app, so read at launch and then at most every 6 hours (sprays refresh it too).
    private func refreshStatusIfStale() {
        let c = model.controller
        guard c.settings.deviceIdentifier != nil, !c.isSpraying else { return }
        if let at = c.deviceStatus?.updatedAt, Date().timeIntervalSince(at) < 6 * 3600 { return }
        Task { await c.refreshDeviceStatus() }
    }

    // MARK: Updates

    /// Once a day when enabled, plus on demand from Settings.
    private func checkForUpdatesIfDue() {
        guard model.controller.settings.checkForUpdates, !isCheckingForUpdates else { return }
        let last = UserDefaults.standard.object(forKey: Self.lastUpdateCheckKey) as? Date
        if let last, Date().timeIntervalSince(last) < 24 * 3600 { return }
        Task { await checkForUpdates() }
    }

    func checkForUpdates() async {
        isCheckingForUpdates = true
        defer { isCheckingForUpdates = false }
        let current = Bundle.main.shortVersion
        do {
            let release = try await UpdateChecker.latestRelease()
            UserDefaults.standard.set(Date(), forKey: Self.lastUpdateCheckKey)
            if UpdateChecker.isVersion(release.version, newerThan: current) {
                availableUpdate = release
                updateCheckState = "Version \(release.version) is available."
                model.log.info("update available: \(release.version) (running \(current))")
            } else {
                availableUpdate = nil
                updateCheckState = "You're up to date (\(current))."
            }
        } catch {
            updateCheckState = "Couldn't check for updates: \(error.localizedDescription)"
            model.log.warn("update check failed: \(error.localizedDescription)")
        }
    }

    func openUpdate() {
        guard let release = availableUpdate else { return }
        NSWorkspace.shared.open(release.macDownloadURL)
    }

    private func tickSchedule() {
        refreshStatusIfStale()
        checkForUpdatesIfDue()
        let evaluator = ScheduleEvaluator(schedule: model.controller.settings.schedule)
        if evaluator.isDue(now: .now, lastScheduledSpray: lastScheduledSpray), !model.controller.isSpraying {
            model.log.info("scheduled spray due")
            model.spray(.schedule)
        }
        updateNextScheduled()
    }

    private func updateNextScheduled() {
        let schedule = model.controller.settings.schedule
        guard schedule.enabled, schedule.intervalMinutes > 0 else {
            nextScheduledSpray = nil
            return
        }
        // Step forward minute by minute to the first due moment within the next two days.
        let evaluator = ScheduleEvaluator(schedule: schedule)
        let last = lastScheduledSpray
        var t = Date.now
        for _ in 0..<(48 * 60) {
            if evaluator.isDue(now: t, lastScheduledSpray: last) {
                nextScheduledSpray = t
                return
            }
            t.addTimeInterval(60)
        }
        nextScheduledSpray = nil
    }

    // MARK: Hotkey

    private func applyHotKey(_ enabled: Bool) {
        guard enabled else {
            hotKey = nil
            hotkeyError = nil
            return
        }
        guard hotKey == nil else { return }
        hotKey = GlobalHotKey { [weak self] in self?.model.spray(.hotkey) }
        hotkeyError = hotKey == nil ? "\(GlobalHotKey.defaultDisplay) is already used by another app." : nil
        model.log.info(hotKey == nil ? "hotkey registration failed" : "hotkey \(GlobalHotKey.defaultDisplay) registered")
    }

    // MARK: Launch at login

    func setLaunchAtLogin(_ on: Bool) {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            launchAtLoginError = nil
        } catch {
            launchAtLoginError = error.localizedDescription
            model.log.warn("launch at login: \(error.localizedDescription)")
        }
        launchAtLogin = SMAppService.mainApp.status == .enabled
    }

    // MARK: Widget / URL handoff

    private func handlePendingWidgetRequest() {
        guard let request = AerShared.takePendingRequest() else { return }
        AerShared.acknowledge(request)
        // Ignore stale requests, e.g. from a widget tap hours ago while the app was quit.
        guard Date().timeIntervalSince(request.date) < 60 else {
            model.log.info("ignoring stale widget request from \(request.date)")
            return
        }
        model.spray(.widget)
    }

    func handle(url: URL) {
        guard url.scheme == AerShared.urlScheme else { return }
        model.log.info("open url \(url.absoluteString)")
        switch url.host {
        case "spray":
            let requestID = URLComponents(url: url, resolvingAgainstBaseURL: false)?
                .queryItems?.first { $0.name == "request" }?.value.flatMap(UUID.init)
            if let requestID {
                // Widget fallback: spray only if this exact tap hasn't been handled already
                // (start() may have picked the pending request up at launch).
                guard !AerShared.isAcknowledged(id: requestID) else { return }
                handlePendingWidgetRequest()
            } else {
                model.spray(.url)
            }
        default:
            // aercontrol://open?tab=spray|devices|settings
            if let tab = URLComponents(url: url, resolvingAgainstBaseURL: false)?
                .queryItems?.first(where: { $0.name == "tab" })?.value.flatMap(MainTab.init(rawValue:)) {
                model.selectedTab = tab
            }
            model.showMainWindow?()
        }
    }
}

/// Catches `aercontrol://` before SwiftUI routes it to a scene, which could otherwise
/// open a window for a menu-bar-only app.
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationWillFinishLaunching(_ notification: Notification) {
        // With no windows open, macOS would otherwise consider the app idle and may quit it
        // ("automatic termination"), which would silently stop the schedule and hotkey.
        ProcessInfo.processInfo.disableAutomaticTermination("AerControl runs in the menu bar")
        ProcessInfo.processInfo.disableSuddenTermination()
        NSAppleEventManager.shared().setEventHandler(
            self, andSelector: #selector(handleGetURL(_:withReply:)),
            forEventClass: AEEventClass(kInternetEventClass), andEventID: AEEventID(kAEGetURL))
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        Task { @MainActor in
            AppModel.shared.services.start()
            // A menu bar app shouldn't pop its window at every login. SwiftUI opens the
            // Window scene at launch, so close it once a device is configured.
            if AppModel.shared.controller.settings.deviceIdentifier != nil {
                NSApp.windows.first { $0.identifier?.rawValue == "main" }?.close()
            }
        }
    }

    /// Keep running in the menu bar when the window closes.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    @objc private func handleGetURL(_ event: NSAppleEventDescriptor, withReply reply: NSAppleEventDescriptor) {
        guard let string = event.paramDescriptor(forKeyword: keyDirectObject)?.stringValue,
              let url = URL(string: string) else { return }
        Task { @MainActor in AppModel.shared.services.handle(url: url) }
    }
}
