import WidgetKit

struct UsageEntry: TimelineEntry {
    let date: Date
    let snapshot: UsageSnapshot
}

struct UsageTimelineProvider: TimelineProvider {
    func placeholder(in context: Context) -> UsageEntry {
        UsageEntry(date: .now, snapshot: .empty(message: "Codex Usage"))
    }

    func getSnapshot(in context: Context, completion: @escaping (UsageEntry) -> Void) {
        completion(UsageEntry(date: .now, snapshot: SharedSnapshotStore.load() ?? .empty()))
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<UsageEntry>) -> Void) {
        let entry = UsageEntry(date: .now, snapshot: SharedSnapshotStore.load() ?? .empty())
        completion(Timeline(entries: [entry], policy: .after(.now.addingTimeInterval(15 * 60))))
    }
}
