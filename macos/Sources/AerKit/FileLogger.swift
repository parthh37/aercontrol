import Foundation
import os

/// Appends timestamped lines to `~/Library/Logs/AerControl/aercontrol.log` (inside the
/// container when sandboxed) and mirrors them to the unified log. Rotates at 1 MB.
public final class FileLogger: @unchecked Sendable {
    public static let shared = FileLogger()

    public let fileURL: URL
    public var echoToStderr = false

    private let queue = DispatchQueue(label: "com.aercontrol.log")
    private let osLog = Logger(subsystem: "com.aercontrol", category: "app")
    private let maxBytes = 1_000_000
    private let formatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    public init(directory: URL? = nil, fileName: String = "aercontrol.log") {
        let dir = directory ?? FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Logs/AerControl", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        fileURL = dir.appendingPathComponent(fileName)
    }

    public func info(_ message: String) { write("INFO ", message) }
    public func warn(_ message: String) { write("WARN ", message) }
    public func error(_ message: String) { write("ERROR", message) }

    private func write(_ level: String, _ message: String) {
        let line = "\(formatter.string(from: Date())) \(level) \(message)\n"
        osLog.log("\(level, privacy: .public) \(message, privacy: .public)")
        if echoToStderr { FileHandle.standardError.write(Data(line.utf8)) }
        queue.async { [fileURL, maxBytes] in
            let fm = FileManager.default
            if let size = (try? fm.attributesOfItem(atPath: fileURL.path)[.size]) as? Int, size > maxBytes {
                let rotated = fileURL.appendingPathExtension("1")
                try? fm.removeItem(at: rotated)
                try? fm.moveItem(at: fileURL, to: rotated)
            }
            if !fm.fileExists(atPath: fileURL.path) {
                fm.createFile(atPath: fileURL.path, contents: nil)
            }
            if let handle = try? FileHandle(forWritingTo: fileURL) {
                defer { try? handle.close() }
                _ = try? handle.seekToEnd()
                try? handle.write(contentsOf: Data(line.utf8))
            }
        }
    }
}
