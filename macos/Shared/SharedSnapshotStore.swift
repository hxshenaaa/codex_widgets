import Foundation

enum SharedSnapshotStore {
    static let groupID = "group.com.hxshenaaa.codexusagewidget"
    static let widgetKind = "com.hxshenaaa.codexusagewidget.usage"

    private static func fileURL() throws -> URL {
        guard let container = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: groupID) else {
            throw StoreError.groupUnavailable
        }
        return container.appendingPathComponent("usage-snapshot.json")
    }

    static func load() -> UsageSnapshot? {
        guard let url = try? fileURL(), let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(UsageSnapshot.self, from: data)
    }

    static func save(_ snapshot: UsageSnapshot) throws {
        let data = try JSONEncoder().encode(snapshot)
        try data.write(to: fileURL(), options: .atomic)
    }

    enum StoreError: LocalizedError {
        case groupUnavailable
        var errorDescription: String? {
            "App Group 不可用。请检查两个 target 的签名与 App Groups capability。"
        }
    }
}
