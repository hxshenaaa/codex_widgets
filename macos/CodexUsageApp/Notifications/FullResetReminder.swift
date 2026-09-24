import Foundation
import UserNotifications

enum FullResetReminder {
    private static let identifier = "codex-usage-full-reset-expiry"
    private static let markerKey = "fullResetReminderExpiry"
    private static let enabledKey = "fullResetReminderEnabled"

    static var isEnabled: Bool { UserDefaults.standard.bool(forKey: enabledKey) }

    static func setEnabled(_ enabled: Bool, snapshot: UsageSnapshot) async throws {
        if enabled {
            let allowed = try await UNUserNotificationCenter.current()
                .requestAuthorization(options: [.alert, .sound])
            guard allowed else {
                throw ReminderError.permissionDenied
            }
        }
        UserDefaults.standard.set(enabled, forKey: enabledKey)
        await sync(snapshot)
    }

    static func sync(_ snapshot: UsageSnapshot) async {
        let center = UNUserNotificationCenter.current()
        // A temporary CLI/offline failure must not revoke an existing reminder.
        if snapshot.state == .error && isEnabled { return }
        guard isEnabled, snapshot.state == .ready,
              snapshot.availableFullResets > 0,
              let expiry = snapshot.fullResetExpiresAt,
              expiry > .now else {
            center.removePendingNotificationRequests(withIdentifiers: [identifier])
            UserDefaults.standard.removeObject(forKey: markerKey)
            return
        }
        let epoch = Int(expiry.timeIntervalSince1970)
        if UserDefaults.standard.integer(forKey: markerKey) == epoch { return }
        center.removePendingNotificationRequests(withIdentifiers: [identifier])
        let settings = await center.notificationSettings()
        guard settings.authorizationStatus == .authorized else { return }

        let content = UNMutableNotificationContent()
        content.title = "Codex Full reset 即将到期"
        content.body = "你的 Full reset 将于 \(expiry.formatted(date: .abbreviated, time: .shortened)) 到期。"
        content.sound = .default
        let interval = max(1, expiry.addingTimeInterval(-24 * 3600).timeIntervalSinceNow)
        let trigger = UNTimeIntervalNotificationTrigger(timeInterval: interval, repeats: false)
        do {
            try await center.add(UNNotificationRequest(identifier: identifier,
                                                        content: content, trigger: trigger))
            UserDefaults.standard.set(epoch, forKey: markerKey)
        } catch {
            // Keep the marker unset so a later refresh can retry.
        }
    }

    enum ReminderError: LocalizedError {
        case permissionDenied
        var errorDescription: String? { "通知权限未开启。请在系统设置中允许 Codex Usage 发送通知。" }
    }
}
