import Foundation

struct UsageWindow: Codable, Equatable {
    let usedPercent: Int
    let remainingPercent: Int
    let durationMinutes: Int?
    let resetsAt: Date?

    var shortName: String {
        switch durationMinutes {
        case 300: return "5 小时"
        case 10080: return "每周"
        case .some(let minutes): return "\(minutes) 分钟"
        case .none: return "额度"
        }
    }
}

struct UsagePoint: Codable, Equatable {
    let date: Date
    let remainingPercent: Int
}

struct TokenPoint: Codable, Equatable {
    let date: Date
    let tokens: Int
}

struct RecentTask: Codable, Equatable, Identifiable {
    let id: String
    let title: String
    let date: Date
    let tokens: Int?
}

struct UsageSnapshot: Codable, Equatable {
    enum State: String, Codable {
        case ready
        case noData
        case error
    }

    let state: State
    let updatedAt: Date
    let primary: UsageWindow?
    let secondary: UsageWindow?
    let planType: String?
    let availableFullResets: Int
    let fullResetExpiresAt: Date?
    let trend: [UsagePoint]
    let tokenActivity: [TokenPoint]?
    let recentTasks: [RecentTask]
    let message: String?

    var preferred: UsageWindow? { secondary ?? primary }

    static func empty(message: String = "打开 Codex Usage 读取额度") -> UsageSnapshot {
        UsageSnapshot(state: .noData, updatedAt: .now, primary: nil, secondary: nil,
                      planType: nil, availableFullResets: 0, fullResetExpiresAt: nil,
                      trend: [], tokenActivity: nil, recentTasks: [], message: message)
    }

    static func failure(_ message: String, previous: UsageSnapshot? = nil) -> UsageSnapshot {
        UsageSnapshot(state: .error, updatedAt: .now, primary: previous?.primary,
                      secondary: previous?.secondary, planType: previous?.planType,
                      availableFullResets: previous?.availableFullResets ?? 0,
                      fullResetExpiresAt: previous?.fullResetExpiresAt,
                      trend: previous?.trend ?? [], tokenActivity: previous?.tokenActivity,
                      recentTasks: previous?.recentTasks ?? [],
                      message: message)
    }
}
