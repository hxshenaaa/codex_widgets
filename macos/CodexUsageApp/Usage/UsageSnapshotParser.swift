import Foundation

enum UsageSnapshotParser {
    static func parse(_ response: [String: Any], at date: Date = .now) -> UsageSnapshot {
        let limits: [String: Any]? = {
            if let byID = response["rateLimitsByLimitId"] as? [String: Any], !byID.isEmpty {
                return (byID["codex"] as? [String: Any])
                    ?? (byID.sorted { $0.key < $1.key }.first?.value as? [String: Any])
            }
            return response["rateLimits"] as? [String: Any]
        }()

        let primary = window(limits?["primary"])
        let secondary = window(limits?["secondary"])
        let credits = response["rateLimitResetCredits"] as? [String: Any]
        let available = max(0, integer(credits?["availableCount"]) ?? 0)
        let now = date.timeIntervalSince1970
        let expires = (credits?["credits"] as? [[String: Any]] ?? []).compactMap { credit -> Date? in
            if let status = credit["status"] as? String, status.lowercased() != "available" {
                return nil
            }
            guard let epoch = number(credit["expiresAt"]), epoch > now else { return nil }
            return Date(timeIntervalSince1970: epoch)
        }.min()

        let hasWindow = primary != nil || secondary != nil
        return UsageSnapshot(
            state: hasWindow ? .ready : .noData,
            updatedAt: date,
            primary: primary,
            secondary: secondary,
            planType: limits?["planType"] as? String,
            availableFullResets: available,
            fullResetExpiresAt: available > 0 ? expires : nil,
            trend: [], tokenActivity: nil, recentTasks: [],
            message: hasWindow ? nil : "账号未返回可用的额度窗口"
        )
    }

    private static func window(_ value: Any?) -> UsageWindow? {
        guard let object = value as? [String: Any],
              let used = number(object["usedPercent"]), used.isFinite else { return nil }
        let clamped = Int(min(100, max(0, used)))
        let reset = number(object["resetsAt"]).map(Date.init(timeIntervalSince1970:))
        return UsageWindow(usedPercent: clamped, remainingPercent: 100 - clamped,
                           durationMinutes: integer(object["windowDurationMins"]),
                           resetsAt: reset)
    }

    private static func integer(_ value: Any?) -> Int? {
        guard let value = number(value), value.isFinite,
              value >= Double(Int.min), value < Double(Int.max) else { return nil }
        return Int(value)
    }

    private static func number(_ value: Any?) -> Double? {
        if let value = value as? NSNumber { return value.doubleValue }
        if let value = value as? String { return Double(value) }
        return nil
    }
}
