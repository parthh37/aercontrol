// swift-tools-version:5.10
import PackageDescription
import Foundation

// Embedding an Info.plist into the CLI binary gives it an
// NSBluetoothAlwaysUsageDescription. Without one, TCC kills unbundled tools that touch
// CoreBluetooth.
let packageDir = URL(fileURLWithPath: #filePath).deletingLastPathComponent().path
let cliInfoPlist = "\(packageDir)/Sources/aerctl/Info.plist"

let package = Package(
    name: "AerControl",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "AerProtocol", targets: ["AerProtocol"]),
        .library(name: "AerShared", targets: ["AerShared"]),
        .library(name: "AerKit", targets: ["AerKit"]),
        .executable(name: "aerctl", targets: ["aerctl"]),
        .executable(name: "AerControl", targets: ["AerControlApp"]),
    ],
    targets: [
        // Pure payload encoding. No Apple frameworks beyond Foundation.
        .target(name: "AerProtocol"),

        // App Group state shared by the app and the widget extension (no BLE here).
        .target(name: "AerShared"),

        // CoreBluetooth service, settings, history, scheduler, hotkey, logging.
        .target(name: "AerKit", dependencies: ["AerProtocol", "AerShared"]),

        // Phase 1: command-line spray test.
        .executableTarget(
            name: "aerctl",
            dependencies: ["AerKit"],
            exclude: ["Info.plist"],
            linkerSettings: [
                .unsafeFlags(["-Xlinker", "-sectcreate", "-Xlinker", "__TEXT",
                              "-Xlinker", "__info_plist", "-Xlinker", cliInfoPlist])
            ]
        ),

        // Phases 2–3: the SwiftUI menu bar app. The Xcode project (project.yml) builds
        // the same sources as a signed .app with the widget embedded. This target exists
        // so the app also builds with plain `swift build` (see scripts/bundle-dev-app.sh).
        .executableTarget(name: "AerControlApp", dependencies: ["AerKit"]),

        .testTarget(name: "AerProtocolTests", dependencies: ["AerProtocol"]),
        .testTarget(name: "AerKitTests", dependencies: ["AerKit"]),
    ],
    swiftLanguageVersions: [.v5]
)
