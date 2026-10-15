import Foundation

/// A published GitHub release.
public struct AppRelease: Equatable, Sendable {
    public var version: String
    public var pageURL: URL
    public var notes: String?
    /// Asset file name → download URL.
    public var assets: [String: URL]

    /// The macOS installer if attached, otherwise the release page.
    public var macDownloadURL: URL {
        assets.first { $0.key.hasSuffix("-macOS.dmg") }?.value
            ?? assets.first { $0.key.hasSuffix(".dmg") }?.value
            ?? pageURL
    }
}

/// Checks GitHub Releases for a newer version. It only notifies: installs go through the
/// signed DMG (macOS) or App Installer (Windows), never a silent in-place swap.
public enum UpdateChecker {
    public static let repository = "parthh37/aercontrol"

    public static func latestRelease(session: URLSession = .shared) async throws -> AppRelease {
        var request = URLRequest(url: URL(string: "https://api.github.com/repos/\(repository)/releases/latest")!)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("AerControl-macOS", forHTTPHeaderField: "User-Agent")
        request.timeoutInterval = 15
        let (data, response) = try await session.data(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw URLError(.badServerResponse)
        }
        return try parse(data)
    }

    static func parse(_ data: Data) throws -> AppRelease {
        struct Asset: Decodable { let name: String; let browser_download_url: URL }
        struct Release: Decodable { let tag_name: String; let html_url: URL; let body: String?; let assets: [Asset] }
        let r = try JSONDecoder().decode(Release.self, from: data)
        return AppRelease(version: normalize(r.tag_name), pageURL: r.html_url, notes: r.body,
                          assets: Dictionary(r.assets.map { ($0.name, $0.browser_download_url) }, uniquingKeysWith: { a, _ in a }))
    }

    /// "v1.2.3" → "1.2.3"; drops any "-suffix" (e.g. "-dev").
    static func normalize(_ version: String) -> String {
        var v = version.trimmingCharacters(in: .whitespaces)
        if v.hasPrefix("v") || v.hasPrefix("V") { v.removeFirst() }
        return v.split(separator: "-").first.map(String.init) ?? v
    }

    /// Numeric dotted comparison; missing parts count as 0 ("1.2" == "1.2.0").
    public static func isVersion(_ candidate: String, newerThan current: String) -> Bool {
        let a = normalize(candidate).split(separator: ".").map { Int($0) ?? 0 }
        let b = normalize(current).split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0, y = i < b.count ? b[i] : 0
            if x != y { return x > y }
        }
        return false
    }
}
