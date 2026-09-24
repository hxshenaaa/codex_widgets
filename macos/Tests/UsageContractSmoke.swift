import Foundation

@main struct UsageContractSmoke {
    static func main() throws {
        let now = Date(timeIntervalSince1970: 2_000_000_000)
        let response: [String: Any] = [
            "rateLimitsByLimitId": [
                "codex": [
                    "planType": "plus",
                    "primary": ["usedPercent": 65, "windowDurationMins": 300,
                                "resetsAt": 2_000_010_000],
                    "secondary": ["usedPercent": 20, "windowDurationMins": 10080,
                                  "resetsAt": 2_000_100_000]
                ]
            ],
            "rateLimitResetCredits": [
                "availableCount": 2,
                "credits": [
                    ["status": "available", "expiresAt": 2_000_050_000],
                    ["status": "available", "expiresAt": 2_000_040_000],
                    ["status": "used", "expiresAt": 2_000_030_000]
                ]
            ]
        ]
        let snapshot = UsageSnapshotParser.parse(response, at: now)
        precondition(snapshot.state == .ready)
        precondition(snapshot.primary?.remainingPercent == 35)
        precondition(snapshot.secondary?.remainingPercent == 80)
        precondition(snapshot.preferred?.remainingPercent == 80)
        precondition(snapshot.fullResetExpiresAt == Date(timeIntervalSince1970: 2_000_040_000))
        precondition(snapshot.availableFullResets == 2)
        let encoded = try JSONEncoder().encode(snapshot)
        let decoded = try JSONDecoder().decode(UsageSnapshot.self, from: encoded)
        precondition(decoded == snapshot)

        let legacy = UsageSnapshotParser.parse(["rateLimits": [
            "primary": ["usedPercent": 115, "windowDurationMins": 300]
        ]], at: now)
        precondition(legacy.primary?.remainingPercent == 0)
        precondition(legacy.secondary == nil)
        precondition(UsageSnapshotParser.parse([:], at: now).state == .noData)

        if CommandLine.arguments.contains("--live") {
            let response = try CodexAppServerClient.readRateLimits(configuredPath: nil)
            let current = UsageSnapshotParser.parse(response)
            print("live: state=\(current.state.rawValue) primary=\(current.primary?.remainingPercent.description ?? "--") secondary=\(current.secondary?.remainingPercent.description ?? "--")")
        }
        print("usage contract smoke: passed")
    }
}
