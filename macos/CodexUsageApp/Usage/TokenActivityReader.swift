import Foundation

enum TokenActivityReader {
    private struct UsageSample {
        let date: Date
        let tokens: Int
    }

    static func last24Hours(
        in sessionRoot: URL = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".codex/sessions"),
        at now: Date = .now
    ) -> [TokenPoint] {
        let cutoff = now.addingTimeInterval(-24 * 3600)
        var buckets = Array(repeating: 0, count: 24)
        let manager = FileManager.default

        if let files = manager.enumerator(at: sessionRoot,
                                          includingPropertiesForKeys: [.contentModificationDateKey],
                                          options: [.skipsHiddenFiles]) {
            for case let file as URL in files where file.pathExtension == "jsonl" {
                guard let modified = try? file.resourceValues(forKeys: [.contentModificationDateKey])
                    .contentModificationDate, modified >= cutoff else { continue }
                for sample in samples(in: file) ?? [] where sample.date >= cutoff && sample.date <= now {
                    let bucket = Int(sample.date.timeIntervalSince(cutoff) / 3600)
                    if (0..<24).contains(bucket) { buckets[bucket] += sample.tokens }
                }
            }
        }

        return buckets.enumerated().map { index, tokens in
            TokenPoint(date: cutoff.addingTimeInterval(Double(index) * 3600), tokens: tokens)
        }
    }

    static func totalTokens(in sessionFile: URL) -> Int? {
        samples(in: sessionFile)?.reduce(0) { $0 + $1.tokens }
    }

    private static func samples(in file: URL) -> [UsageSample]? {
        guard let data = try? Data(contentsOf: file, options: .mappedIfSafe) else { return nil }
        var recorded: [UsageSample] = []
        var fallback: [UsageSample] = []
        var hasUsageRecords = false
        var hasTokenCounts = false
        var previousCumulative: Int?
        let fractionalDate = ISO8601DateFormatter()
        fractionalDate.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let wholeDate = ISO8601DateFormatter()

        for line in data.split(separator: 0x0A) {
            let text = String(decoding: line, as: UTF8.self)
            guard text.contains("\"token_usage_record\"") || text.contains("\"token_count\"") else {
                continue
            }
            guard let object = try? JSONSerialization.jsonObject(with: Data(line)) as? [String: Any],
                  let timestamp = object["timestamp"] as? String,
                  let date = fractionalDate.date(from: timestamp) ?? wholeDate.date(from: timestamp),
                  let payload = object["payload"] as? [String: Any],
                  let type = object["type"] as? String else { continue }

            if type == "token_usage_record" {
                guard let usage = payload["usage"] as? [String: Any],
                      let tokens = (usage["total_tokens"] as? NSNumber)?.intValue,
                      tokens >= 0 else { continue }
                hasUsageRecords = true
                recorded.append(UsageSample(date: date, tokens: tokens))
            } else if type == "event_msg", payload["type"] as? String == "token_count",
                      let info = payload["info"] as? [String: Any],
                      let usage = info["total_token_usage"] as? [String: Any],
                      let cumulative = (usage["total_tokens"] as? NSNumber)?.intValue,
                      cumulative >= 0 {
                hasTokenCounts = true
                let delta = previousCumulative.map { cumulative >= $0 ? cumulative - $0 : cumulative }
                    ?? cumulative
                previousCumulative = cumulative
                fallback.append(UsageSample(date: date, tokens: delta))
            }
        }

        if hasUsageRecords { return recorded }
        if hasTokenCounts { return fallback }
        return nil
    }
}
