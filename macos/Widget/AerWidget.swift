import SwiftUI
import WidgetKit
import AerShared

struct AerEntry: TimelineEntry {
    let date: Date
    let snapshot: WidgetSnapshot
}

struct AerProvider: TimelineProvider {
    func placeholder(in context: Context) -> AerEntry {
        AerEntry(date: .now, snapshot: .placeholder)
    }

    func getSnapshot(in context: Context, completion: @escaping (AerEntry) -> Void) {
        completion(AerEntry(date: .now, snapshot: context.isPreview ? .placeholder : AerShared.readSnapshot()))
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<AerEntry>) -> Void) {
        // The app reloads the timeline after every spray and status change; the periodic
        // refresh just keeps the "connected" state from going stale if the app quits.
        let entry = AerEntry(date: .now, snapshot: AerShared.readSnapshot())
        completion(Timeline(entries: [entry], policy: .after(.now.addingTimeInterval(30 * 60))))
    }
}

struct AerWidgetView: View {
    @Environment(\.widgetFamily) private var family
    let entry: AerEntry

    private var snap: WidgetSnapshot { entry.snapshot }

    var body: some View {
        Group {
            switch family {
            case .systemMedium: medium
            default: small
            }
        }
        .containerBackground(for: .widget) {
            LinearGradient(colors: [Color(red: 0.08, green: 0.72, blue: 0.65), Color(red: 0.05, green: 0.43, blue: 0.49)],
                           startPoint: .topLeading, endPoint: .bottomTrailing)
        }
        .foregroundStyle(.white)
        .widgetURL(URL(string: "aercontrol://open"))
    }

    private var small: some View {
        VStack(alignment: .leading, spacing: 6) {
            header
            Spacer(minLength: 0)
            lastSpray
            batteryWarning
            sprayButton(compact: true)
        }
    }

    private var medium: some View {
        HStack(spacing: 16) {
            sprayButton(compact: false)
                .frame(width: 104)
            VStack(alignment: .leading, spacing: 6) {
                header
                lastSpray
                batteryWarning
                Spacer(minLength: 0)
                HStack(spacing: 10) {
                    Label(snap.deviceStatus?.batteryPercent.map { "~\($0)%" } ?? "—",
                          systemImage: batterySymbol)
                    Label(snap.deviceStatus?.refillPercent.map { "\($0)%" } ?? "—", systemImage: "drop.fill")
                }
                .font(.caption.monospacedDigit())
                .opacity(0.85)
            }
            Spacer(minLength: 0)
        }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(snap.deviceName ?? "AerControl")
                .font(.headline)
                .lineLimit(1)
            HStack(spacing: 4) {
                Circle().fill(statusColor).frame(width: 7, height: 7)
                Text(snap.status.label).font(.caption2.weight(.medium))
            }
            .opacity(0.9)
        }
    }

    @ViewBuilder
    private var lastSpray: some View {
        if let last = snap.lastSpray {
            HStack(spacing: 4) {
                Image(systemName: last.success ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                Text(last.date, style: .relative)
            }
            .font(.caption2)
            .opacity(0.85)
        } else {
            Text("No sprays yet").font(.caption2).opacity(0.85)
        }
    }

    /// Below the vendor app's 2.7 V danger level the aer may ack sprays it can't fire.
    @ViewBuilder
    private var batteryWarning: some View {
        if snap.deviceStatus?.batteryCritical == true {
            Label("Replace batteries", systemImage: "exclamationmark.triangle.fill")
                .font(.caption2.weight(.semibold))
                .foregroundStyle(.yellow)
                .lineLimit(1)
        }
    }

    private var batterySymbol: String {
        if snap.deviceStatus?.batteryCritical == true { return "battery.0" }
        return snap.deviceStatus?.batteryLow == true ? "battery.25" : "battery.75"
    }

    private func sprayButton(compact: Bool) -> some View {
        Button(intent: SprayWidgetIntent()) {
            if compact {
                Label("Spray", systemImage: "aqi.medium")
                    .font(.callout.weight(.semibold))
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 7)
                    .background(.white.opacity(0.22), in: Capsule())
            } else {
                VStack(spacing: 4) {
                    Image(systemName: "aqi.medium").font(.system(size: 30, weight: .semibold))
                    Text("Spray").font(.callout.weight(.bold))
                }
                .frame(width: 96, height: 96)
                .background(.white.opacity(0.22), in: Circle())
            }
        }
        .buttonStyle(.plain)
        .disabled(snap.status == .noDevice)
    }

    private var statusColor: Color {
        switch snap.status {
        case .connected: .green
        case .connecting: .yellow
        case .disconnected: .white.opacity(0.7)
        case .noDevice: .orange
        case .bluetoothOff, .unauthorized: .red
        }
    }
}

struct AerSprayWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: AerShared.widgetKind, provider: AerProvider()) { entry in
            AerWidgetView(entry: entry)
        }
        .configurationDisplayName("aer Spray")
        .description("Spray your Godrej aer Smart Matic and see when it last sprayed.")
        .supportedFamilies([.systemSmall, .systemMedium])
    }
}

@main
struct AerWidgetBundle: WidgetBundle {
    var body: some Widget {
        AerSprayWidget()
    }
}
