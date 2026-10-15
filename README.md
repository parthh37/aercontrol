# AerControl

Desktop control for the **Godrej aer Smart Matic** Bluetooth air freshener on **macOS**
and **Windows**: a big Spray button, menu bar / tray app, schedules, a global hotkey,
Shortcuts and jump-list actions, and home-screen widgets (WidgetKit on macOS, the
Widgets Board on Windows 11).

> Not affiliated with or endorsed by Godrej. Uses the device's local BLE protocol only.

| | macOS 14+ | Windows 10/11 |
|---|---|---|
| Scan, pick and remember a device (with RSSI) | ✅ peripheral UUID | ✅ BLE address |
| Spray button + history (time, source, success) | ✅ | ✅ |
| Connection status, auto-reconnect, idle disconnect | ✅ | ✅ |
| Schedule (every *N* min between HH:MM and HH:MM) | ✅ | ✅ |
| Global hotkey | ⌃⌥⌘S | Ctrl+Alt+Shift+S |
| Menu bar / tray, launch at login | ✅ | ✅ (left-click tray to spray) |
| Actions | Shortcuts / Siri / Spotlight: *Spray Air Freshener*, *Get Air Freshener Status* | Jump list: *Spray now* |
| Widget (small + medium) with Spray button | ✅ WidgetKit | ✅ Windows 11 Widgets Board |
| CLI (`aerctl`) | ✅ | ✅ |
| Battery % / sprays remaining | 🚧 UI ready, protocol not yet decoded ([why](#battery-and-refill-level)) | 🚧 |
| Log file | `~/Library/Logs/AerControl/` (in the app container when sandboxed) | app LocalFolder `\logs` |

## Before you start

**The aer accepts one Bluetooth connection at a time.** Close or disconnect the Godrej
phone app before you use AerControl. AerControl disconnects after 20 s idle by default
(*Settings › Disconnect when idle*), so the phone app can connect again.

## macOS

### Quick start (no Xcode needed)

```bash
make mac-run        # builds macos/dist/AerControl.app with SwiftPM and opens it
```

The app lives in the menu bar (🌫 icon). On first launch, open **Devices**, press
**Scan**, and pick **Smart Matic**. This dev build is ad-hoc signed and has no widget.

### Full build with the widget (Xcode 16+)

```bash
brew install xcodegen
export DEVELOPMENT_TEAM=ABCDE12345     # your Apple team id
make mac-xcode                          # generates and opens AerControl.xcodeproj
```

Run the **AerControl** scheme. Automatic signing registers the App Group
`<TEAMID>.com.aercontrol.shared` for the app and the widget extension.

**Add the widget:** right-click the desktop › *Edit Widgets…* (or open Notification
Center › *Edit Widgets*), search for **AerControl**, then drag **aer Spray** (small or
medium) onto the desktop. The widget's button asks the running app to spray. If the
app isn't running, it launches it, which is a good reason to turn on *Launch at login*.

### Signed and notarized release

```bash
xcrun notarytool store-credentials aercontrol-notary --apple-id you@example.com \
  --team-id ABCDE12345 --password <app-specific-password>
DEVELOPMENT_TEAM=ABCDE12345 make mac-release   # → macos/build/release/AerControl.{zip,dmg}
```

This needs a *Developer ID Application* certificate. See the comments in
[`macos/scripts/release.sh`](macos/scripts/release.sh). The app is sandboxed with
the Bluetooth and App Groups entitlements and the hardened runtime.

### CLI

```bash
make mac-cli
macos/.build/release/aerctl scan                 # find your device
macos/.build/release/aerctl remember <UUID>
macos/.build/release/aerctl spray                # also: --count 3 --interval 5, --mn 200
macos/.build/release/aerctl inspect              # dump the GATT table
macos/.build/release/aerctl probe                # read every characteristic + listen (never writes)
```

`aerctl` embeds its own Info.plist and re-spawns itself as its own "responsible
process," so it works from any terminal or IDE without that app needing Bluetooth
permission.

## Windows

### Install a release

1. Download `AerControl-windows-x64.zip` (or `ARM64`) from
   [Releases](../../releases) and extract it.
2. Right-click `install.ps1` › *Run with PowerShell*. It trusts the bundled
   self-signed certificate (one admin prompt), installs the Windows App Runtime
   dependency, then installs AerControl.
3. Start **AerControl** from the Start menu. It lives in the system tray.

**Add the widget (Windows 11):** press **Win+W** › **+** (Add widgets) › **AerControl**
› **aer Spray**.

### Build it yourself

Requirements: Windows 10 19041+ (11 for widgets), Visual Studio 2022 with the
*.NET desktop* and *Windows App SDK* components, and the .NET 8 SDK.

```powershell
cd windows
.\scripts\new-dev-cert.ps1 -Trust      # once, elevated: creates + trusts CN=AerControl Dev
.\scripts\build-msix.ps1               # -> windows\dist\AerControl_x64 (MSIX) and aerctl_x64
.\dist\AerControl_x64\install.ps1
```

To develop, open `windows\AerControl.sln` in Visual Studio and run
**AerControl.App (Package)**.

The certificate subject must match `Publisher` in
`src/AerControl.App/Package.appxmanifest` (`CN=AerControl Dev`). If you use your own
certificate, change both.

### CLI

```powershell
aerctl scan
aerctl remember D8:84:00:A8:FD:E1
aerctl spray
```

## Repository layout

```
protocol/            BLE spec, field notes, shared CBOR test vectors (both test suites read them)
macos/               Swift package: AerProtocol, AerShared, AerKit (CoreBluetooth), aerctl, app
  Widget/            WidgetKit extension (built by the Xcode project from project.yml)
  App/               Info.plist, entitlements, asset catalog
windows/
  src/AerControl.Protocol    CBOR payload builder (net8.0)
  src/AerControl.Core        settings, history, schedule, BleSession state machine (net8.0)
  src/AerControl.Ble.Windows Windows.Devices.Bluetooth implementation
  src/AerControl.Cli         aerctl.exe
  src/AerControl.App         WinUI 3 app + tray + widget provider (MSIX)
  tests/                     xUnit (runs on any OS: dotnet test AerControl.Portable.slnf)
tools/gen_icons.py   dependency-free icon generator
```

## Protocol in one paragraph

Write the 16-byte indefinite-length CBOR map `{"mT":104,"mN":154,"rI":0}`
(`bf 62 6d 54 18 68 62 6d 4e 18 9a 62 72 49 00 ff`) **without response** to
characteristic `6E400003-B5A3-F393-E0A9-E50E24DCCA9E` in service `6E400000-…`. Two
details matter, as live testing showed: **enable notifications** on that characteristic
first, and **don't disconnect immediately** after the write (both clients wait 1 s).
Without those, the write is silently dropped. Details are in
[`protocol/README.md`](protocol/README.md).

## Battery and refill level

The official app shows battery % and sprays remaining. On the device, every
characteristic reads `00 00` and nothing is sent unprompted, so the app must send a
status-query command. That command hasn't been decoded yet, because it needs the
official APK (`com.godrejcp.aermatic`). Both apps already have the UI slots, widget
fields and a single parser stub (`AerStatusProtocol` in each client), so supporting it
is a small, contained change. Contributions welcome.

## Troubleshooting

* **"Timed out during connect"**: the phone app is probably connected. Close it, or
  turn Bluetooth off on the phone.
* **Nothing sprays but no error**: check `aerctl probe` and the log. The device needs
  the notify subscription and the settle delay described above.
* **macOS: "No Bluetooth access"**: go to System Settings › Privacy & Security ›
  Bluetooth and allow AerControl.
* **Windows widget missing**: widgets need Windows 11 and the MSIX-installed app (not
  an unpackaged debug run).

## Development

```bash
make test            # Swift + .NET unit tests (the .NET ones run on macOS/Linux too)
```

CI ([`.github/workflows`](.github/workflows)) builds and tests both platforms on
every push and attaches release artifacts to `v*` tags.

## License

MIT. See [LICENSE](LICENSE).
