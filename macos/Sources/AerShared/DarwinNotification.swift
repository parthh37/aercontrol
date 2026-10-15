import Foundation

/// Thin wrapper over the Darwin notify center, which carries no payload and works
/// across sandboxed processes in the same App Group.
public enum DarwinNotification {
    public static func post(_ name: String) {
        CFNotificationCenterPostNotification(
            CFNotificationCenterGetDarwinNotifyCenter(),
            CFNotificationName(name as CFString), nil, nil, true)
    }

    /// Observes `name` until the returned token is released. The handler runs on the main queue.
    public final class Observation {
        let name: String
        let handler: () -> Void

        init(name: String, handler: @escaping () -> Void) {
            self.name = name
            self.handler = handler
            let observer = Unmanaged.passUnretained(self).toOpaque()
            CFNotificationCenterAddObserver(
                CFNotificationCenterGetDarwinNotifyCenter(), observer,
                { _, observer, _, _, _ in
                    guard let observer else { return }
                    let me = Unmanaged<Observation>.fromOpaque(observer).takeUnretainedValue()
                    DispatchQueue.main.async { me.handler() }
                },
                name as CFString, nil, .deliverImmediately)
        }

        deinit {
            CFNotificationCenterRemoveObserver(
                CFNotificationCenterGetDarwinNotifyCenter(),
                Unmanaged.passUnretained(self).toOpaque(),
                CFNotificationName(name as CFString), nil)
        }
    }

    public static func observe(_ name: String, handler: @escaping () -> Void) -> Observation {
        Observation(name: name, handler: handler)
    }
}
