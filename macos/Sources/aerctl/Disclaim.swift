import Foundation
import Darwin

/// TCC attributes Bluetooth access to the *responsible* process, which is the terminal or
/// IDE that launched us. If that app has no NSBluetoothAlwaysUsageDescription, TCC kills
/// us. Re-spawning with responsibility disclaimed makes aerctl its own responsible process,
/// so TCC reads the Info.plist embedded in this binary instead.
enum Disclaim {
    private static let marker = "AERCTL_DISCLAIMED"

    private typealias SetDisclaim = @convention(c) (UnsafeMutablePointer<posix_spawnattr_t?>, Int32) -> Int32

    /// Returns normally in the disclaimed child. In the parent it waits for the child
    /// and exits with the child's status.
    static func reexecIfNeeded() {
        guard ProcessInfo.processInfo.environment[marker] == nil,
              let sym = dlsym(UnsafeMutableRawPointer(bitPattern: -2), "responsibility_spawnattrs_setdisclaim")
        else { return }
        let setDisclaim = unsafeBitCast(sym, to: SetDisclaim.self)

        var attr: posix_spawnattr_t?
        posix_spawnattr_init(&attr)
        defer { posix_spawnattr_destroy(&attr) }
        guard setDisclaim(&attr, 1) == 0 else { return }

        var path = [CChar](repeating: 0, count: Int(MAXPATHLEN))
        var size = UInt32(path.count)
        guard _NSGetExecutablePath(&path, &size) == 0 else { return }

        let args = CommandLine.arguments
        var argv: [UnsafeMutablePointer<CChar>?] = args.map { strdup($0) } + [nil]
        var envStrings = ProcessInfo.processInfo.environment.map { "\($0.key)=\($0.value)" }
        envStrings.append("\(marker)=1")
        var envp: [UnsafeMutablePointer<CChar>?] = envStrings.map { strdup($0) } + [nil]
        defer {
            argv.forEach { free($0) }
            envp.forEach { free($0) }
        }

        var pid: pid_t = 0
        guard posix_spawn(&pid, path, nil, &attr, &argv, &envp) == 0 else { return }

        // Let Ctrl-C reach the child rather than orphaning it.
        signal(SIGINT, SIG_IGN)
        var status: Int32 = 0
        while waitpid(pid, &status, 0) == -1 && errno == EINTR {}
        let exited = (status & 0x7f) == 0
        exit(exited ? (status >> 8) & 0xff : 128 + (status & 0x7f))
    }
}
