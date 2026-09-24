import Foundation

@main struct TokenActivitySmoke {
    static func main() throws {
        let manager = FileManager.default
        let root = manager.temporaryDirectory.appendingPathComponent("codex-token-activity-\(UUID())")
        try manager.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? manager.removeItem(at: root) }

        let now = Date(timeIntervalSince1970: 2_000_000_000)
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        func line(_ date: Date, _ type: String, _ payload: [String: Any]) throws -> String {
            let data = try JSONSerialization.data(withJSONObject: [
                "timestamp": formatter.string(from: date), "type": type, "payload": payload
            ], options: [.sortedKeys])
            return String(decoding: data, as: UTF8.self)
        }

        let first = root.appendingPathComponent("record.jsonl")
        let recordLines = try [
            line(now.addingTimeInterval(-2 * 3600), "token_usage_record",
                 ["usage": ["total_tokens": 120]]),
            line(now.addingTimeInterval(-2 * 3600), "event_msg",
                 ["type": "token_count", "info": ["total_token_usage": ["total_tokens": 120]]])
        ]
        try recordLines.joined(separator: "\n").write(to: first, atomically: true, encoding: .utf8)

        let second = root.appendingPathComponent("cumulative.jsonl")
        let cumulativeLines = try [
            line(now.addingTimeInterval(-25 * 3600), "event_msg",
                 ["type": "token_count", "info": ["total_token_usage": ["total_tokens": 40]]]),
            line(now.addingTimeInterval(-3600), "event_msg",
                 ["type": "token_count", "info": ["total_token_usage": ["total_tokens": 70]]]),
            line(now.addingTimeInterval(-1800), "event_msg",
                 ["type": "token_count", "info": ["total_token_usage": ["total_tokens": 70]]])
        ]
        try cumulativeLines.joined(separator: "\n").write(to: second, atomically: true,
                                                     encoding: .utf8)
        try manager.setAttributes([.modificationDate: now], ofItemAtPath: first.path)
        try manager.setAttributes([.modificationDate: now], ofItemAtPath: second.path)

        let points = TokenActivityReader.last24Hours(in: root, at: now)
        precondition(points.count == 24)
        precondition(points.reduce(0) { $0 + $1.tokens } == 150)
        precondition(points[22].tokens == 120)
        precondition(points[23].tokens == 30)
        precondition(TokenActivityReader.totalTokens(in: first) == 120)
        precondition(TokenActivityReader.totalTokens(in: second) == 70)
        if CommandLine.arguments.contains("--live") {
            let live = TokenActivityReader.last24Hours()
            print("live local tokens (24h): \(live.reduce(0) { $0 + $1.tokens })")
            let sessionRoot = manager.homeDirectoryForCurrentUser.appendingPathComponent(".codex/sessions")
            if let files = manager.enumerator(at: sessionRoot, includingPropertiesForKeys: nil) {
                let taskTotals = files.compactMap { item -> Int? in
                    guard let file = item as? URL, file.pathExtension == "jsonl" else { return nil }
                    return TokenActivityReader.totalTokens(in: file)
                }
                print("live local tasks with usage: \(taskTotals.count)")
            }
        }
        print("token activity smoke: passed")
    }
}
