import SwiftUI
import AerKit

struct DevicesView: View {
    @ObservedObject var controller: SprayController
    @ObservedObject var ble: BLEService

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Picker("Filter", selection: $controller.settings.scanFilter) {
                    ForEach(ScanFilter.allCases) { Text($0.label).tag($0) }
                }
                .frame(maxWidth: 260)
                TextField("Name contains (comma-separated)", text: $controller.settings.nameFilter)
                    .textFieldStyle(.roundedBorder)
                    .frame(maxWidth: 220)
                    .disabled(controller.settings.scanFilter == .service || controller.settings.scanFilter == .all)
                Spacer()
                if ble.isScanning {
                    ProgressView().controlSize(.small)
                    Button("Stop") { ble.stopScan() }
                } else {
                    Button {
                        ble.startScan()
                    } label: {
                        Label("Scan", systemImage: "dot.radiowaves.left.and.right")
                    }
                    .disabled(ble.managerState != .poweredOn)
                }
            }
            .padding(12)
            Divider()

            if ble.discovered.isEmpty {
                ContentUnavailableView {
                    Label(ble.isScanning ? "Scanning…" : "No devices", systemImage: "antenna.radiowaves.left.and.right")
                } description: {
                    Text(ble.managerState == .poweredOn
                         ? "The aer advertises as “Smart Matic”. Close the phone app first, because the device accepts only one connection."
                         : "Bluetooth is \(ble.managerState.debugName).")
                }
            } else {
                List(ble.discovered) { device in
                    DeviceRow(device: device,
                              isSelected: device.id == controller.settings.deviceIdentifier) {
                        controller.select(device)
                    }
                }
            }

            if let name = controller.settings.deviceName, let id = controller.settings.deviceIdentifier {
                Divider()
                HStack {
                    Image(systemName: "checkmark.seal.fill").foregroundStyle(.green)
                    VStack(alignment: .leading) {
                        Text("Selected: \(name)").bold()
                        Text(id.uuidString).font(.caption.monospaced()).foregroundStyle(.secondary).textSelection(.enabled)
                    }
                    Spacer()
                    Button("Forget", role: .destructive) { controller.forgetDevice() }
                }
                .padding(12)
            }
        }
    }
}

struct DeviceRow: View {
    let device: DiscoveredDevice
    let isSelected: Bool
    let select: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            SignalBars(rssi: device.rssi)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(device.name).font(.body.weight(.medium))
                    if device.advertisesService {
                        Text("aer service").font(.caption2.bold())
                            .padding(.horizontal, 5).padding(.vertical, 1)
                            .background(.blue.opacity(0.15), in: Capsule())
                    }
                }
                Text("\(device.id.uuidString) · \(device.rssi) dBm")
                    .font(.caption.monospaced())
                    .foregroundStyle(.secondary)
            }
            Spacer()
            if isSelected {
                Label("Selected", systemImage: "checkmark").foregroundStyle(.green)
            } else {
                Button("Use", action: select)
            }
        }
        .padding(.vertical, 2)
    }
}

struct SignalBars: View {
    let rssi: Int

    private var level: Int {
        switch rssi {
        case -60...0 where rssi != 0: 4
        case -70 ..< -60: 3
        case -80 ..< -70: 2
        case ..<(-80): 1
        default: 4 // 0 = already connected (no RSSI reading)
        }
    }

    var body: some View {
        HStack(alignment: .bottom, spacing: 2) {
            ForEach(1...4, id: \.self) { i in
                RoundedRectangle(cornerRadius: 1)
                    .fill(i <= level ? Color.accentColor : Color.secondary.opacity(0.25))
                    .frame(width: 4, height: CGFloat(4 + i * 4))
            }
        }
        .frame(width: 24, height: 20, alignment: .bottom)
        .help("\(rssi) dBm")
    }
}
