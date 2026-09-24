import Foundation
import SQLite3

enum LocalUsageReader {
    private struct Sample: Codable {
        let date: Date
        let primary: Int?
        let secondary: Int?
    }

    static func trend(for snapshot: UsageSnapshot) -> [UsagePoint] {
        guard snapshot.state == .ready else { return [] }
        guard let container = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: SharedSnapshotStore.groupID) else { return [] }
        let url = container.appendingPathComponent("usage-history.json")
        var samples = (try? JSONDecoder().decode([Sample].self, from: Data(contentsOf: url))) ?? []
        let now = snapshot.updatedAt
        samples.removeAll { $0.date < now.addingTimeInterval(-30 * 86400) }
        if snapshot.preferred != nil {
            samples.append(Sample(date: now, primary: snapshot.primary?.remainingPercent,
                                  secondary: snapshot.secondary?.remainingPercent))
        }
        if let data = try? JSONEncoder().encode(samples) {
            try? data.write(to: url, options: .atomic)
        }
        let start = now.addingTimeInterval(-24 * 3600)
        let values = samples.filter { $0.date >= start }.compactMap { sample -> UsagePoint? in
            guard let remaining = snapshot.secondary != nil
                ? sample.secondary ?? sample.primary
                : sample.primary ?? sample.secondary else { return nil }
            return UsagePoint(date: sample.date, remainingPercent: remaining)
        }
        // Keep the chart small in the shared snapshot.
        guard values.count > 96 else { return values }
        return (0..<96).map { values[Int(Double($0) * Double(values.count - 1) / 95.0)] }
    }

    static func recentTasks(limit: Int = 3) -> [RecentTask] {
        let home = FileManager.default.homeDirectoryForCurrentUser
        let database = home.appendingPathComponent(".codex/sqlite/codex-dev.db")
        let sessionRoot = home.appendingPathComponent(".codex/sessions")
        guard FileManager.default.fileExists(atPath: database.path),
              FileManager.default.fileExists(atPath: sessionRoot.path) else { return [] }

        var db: OpaquePointer?
        guard sqlite3_open_v2(database.path, &db, SQLITE_OPEN_READONLY, nil) == SQLITE_OK,
              let db else { return [] }
        defer { sqlite3_close(db) }
        let sql = "SELECT thread_id, display_title, source_updated_at FROM local_thread_catalog WHERE display_title IS NOT NULL AND display_title != '' ORDER BY source_updated_at DESC"
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &statement, nil) == SQLITE_OK,
              let statement else { return [] }
        defer { sqlite3_finalize(statement) }

        guard let files = FileManager.default.enumerator(at: sessionRoot,
            includingPropertiesForKeys: nil) else { return [] }
        let sessionFiles = Dictionary(files.compactMap { item -> (String, URL)? in
            guard let url = item as? URL, url.pathExtension == "jsonl" else { return nil }
            let stem = url.deletingPathExtension().lastPathComponent
            guard stem.count >= 36 else { return nil }
            let id = String(stem.suffix(36))
            return UUID(uuidString: id) == nil ? nil : (id, url)
        }, uniquingKeysWith: { first, _ in first })
        var result: [RecentTask] = []
        while sqlite3_step(statement) == SQLITE_ROW && result.count < limit {
            guard let idPointer = sqlite3_column_text(statement, 0),
                  let titlePointer = sqlite3_column_text(statement, 1) else { continue }
            let id = String(cString: idPointer)
            guard let sessionFile = sessionFiles[id] else { continue }
            let title = String(cString: titlePointer)
            let timestamp = sqlite3_column_double(statement, 2)
            result.append(RecentTask(id: id, title: title,
                                     date: Date(timeIntervalSince1970: timestamp),
                                     tokens: TokenActivityReader.totalTokens(in: sessionFile)))
        }
        return result
    }
}
