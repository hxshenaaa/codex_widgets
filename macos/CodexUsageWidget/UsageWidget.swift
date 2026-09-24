import SwiftUI
import WidgetKit

@main struct CodexUsageWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: SharedSnapshotStore.widgetKind,
                            provider: UsageTimelineProvider()) { entry in
            UsageWidgetView(entry: entry)
        }
        .configurationDisplayName("Codex Usage")
        .description("查看 Codex 的剩余额度与重置时间。")
        .supportedFamilies([.systemSmall, .systemMedium, .systemLarge])
    }
}
