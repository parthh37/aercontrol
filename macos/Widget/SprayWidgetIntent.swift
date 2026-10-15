import AppIntents
import AppKit
import WidgetKit
import AerShared

/// The widget's Spray button. It never touches Bluetooth: extensions are short-lived and
/// the aer allows a single central, which the app owns. It hands the request to the app
/// and waits briefly for an acknowledgement.
struct SprayWidgetIntent: AppIntent {
    static let title: LocalizedStringResource = "Spray"
    static let description = IntentDescription("Asks AerControl to spray the aer Smart Matic.")
    static let isDiscoverable = false

    func perform() async throws -> some IntentResult {
        let request = AerShared.postSprayRequest()

        // A running app acks within a few hundred ms (Darwin notification → main queue).
        for _ in 0..<15 {
            try? await Task.sleep(for: .milliseconds(100))
            if AerShared.isAcknowledged(request) { break }
        }
        if !AerShared.isAcknowledged(request) {
            // Not running: launch it. The app picks up the pending request (still fresh)
            // when it handles the URL.
            NSWorkspace.shared.open(AerShared.sprayURL(for: request))
        }

        // The app reloads the timeline itself when the spray completes. This refresh shows
        // the in-flight state sooner.
        WidgetCenter.shared.reloadTimelines(ofKind: AerShared.widgetKind)
        return .result()
    }
}
