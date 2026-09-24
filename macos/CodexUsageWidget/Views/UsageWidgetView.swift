import SwiftUI
import WidgetKit

struct UsageWidgetView: View {
    @Environment(\.widgetFamily) private var family
    let entry: UsageEntry
    var familyOverride: WidgetFamily? = nil

    private var snapshot: UsageSnapshot { entry.snapshot }
    private var tokenPoints: [TokenPoint] { snapshot.tokenActivity ?? [] }
    private var tokenTotal: Int { tokenPoints.reduce(0) { $0 + $1.tokens } }
    private let activityColor = Color(red: 0.08, green: 0.61, blue: 0.35)
    private var accent: Color {
        guard let remaining = snapshot.preferred?.remainingPercent else { return .blue }
        if remaining <= 15 { return .red }
        if remaining >= 60 { return .green }
        return .blue
    }

    var body: some View {
        Group {
            switch familyOverride ?? family {
            case .systemSmall: small
            case .systemMedium: medium
            default: large
            }
        }
        .containerBackground(for: .widget) { Color(nsColor: .windowBackgroundColor) }
    }

    private var small: some View {
        VStack(alignment: .leading, spacing: 4) {
            header
            if let window = snapshot.preferred {
                Text("\(window.remainingPercent)%")
                    .font(.system(size: 40, weight: .bold, design: .rounded))
                    .minimumScaleFactor(0.6)
                    .foregroundStyle(accent)
                Text("\(window.shortName)剩余").font(.caption).foregroundStyle(.secondary)
                if let reset = window.resetsAt { resetLabel(reset) }
            } else { emptyMessage }
            if snapshot.state == .error { errorLabel }
            Spacer(minLength: 0)
            tokenSummary
            fullResetLabel
        }
        .padding(14)
    }

    private var medium: some View {
        HStack(alignment: .top, spacing: 12) {
            VStack(alignment: .leading, spacing: 5) {
                header
                if let window = snapshot.preferred {
                    Text("\(window.remainingPercent)%")
                        .font(.system(size: 46, weight: .bold, design: .rounded))
                        .foregroundStyle(accent)
                    Text("\(window.shortName)剩余").font(.caption).foregroundStyle(.secondary)
                    if let reset = window.resetsAt { resetLabel(reset) }
                } else { emptyMessage }
                if snapshot.state == .error { errorLabel }
                Spacer(minLength: 0)
                fullResetLabel
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            VStack(alignment: .leading, spacing: 4) {
                HStack(alignment: .top, spacing: 6) {
                    compactWindowLine("Primary", snapshot.primary)
                    compactWindowLine("Secondary", snapshot.secondary)
                }
                chartHeading
                TokenActivityView(points: tokenPoints, color: activityColor)
                    .frame(height: 54)
                chartAxis
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(16)
    }

    private var large: some View {
        VStack(alignment: .leading, spacing: 3) {
            header
            HStack(alignment: .firstTextBaseline) {
                Text(snapshot.preferred.map { "\($0.remainingPercent)%" } ?? "--")
                    .font(.system(size: 38, weight: .bold, design: .rounded))
                    .foregroundStyle(accent)
                Text(snapshot.preferred.map { "\($0.shortName)剩余" } ?? "暂无额度")
                    .font(.subheadline).foregroundStyle(.secondary)
            }
            if snapshot.preferred == nil { emptyMessage }
            if snapshot.state == .error { errorLabel }
            HStack {
                windowLine("Primary", snapshot.primary)
                Spacer()
                windowLine("Secondary", snapshot.secondary)
            }
            chartHeading
            TokenActivityView(points: tokenPoints, color: activityColor).frame(height: 48)
            chartAxis
            fullResetLabel
            Divider()
            Text("最近任务").font(.caption.weight(.semibold))
            if snapshot.recentTasks.isEmpty {
                Text("暂无本地任务").font(.caption).foregroundStyle(.secondary)
            } else {
                ForEach(snapshot.recentTasks.prefix(3)) { task in
                    HStack {
                        Text(task.title).lineLimit(1)
                        Spacer(minLength: 4)
                        Text(task.tokens.map { "⚡ \(formatTokens($0))" } ?? "用量未知")
                            .font(.caption2.monospacedDigit()).foregroundStyle(.secondary)
                    }
                    .font(.caption)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .padding(12)
    }

    private var header: some View {
        HStack(spacing: 5) {
            Image(systemName: "chart.bar.fill").foregroundStyle(.blue)
            Text("Codex Usage").font(.caption.weight(.bold))
            Spacer(minLength: 0)
            if snapshot.state == .error {
                Image(systemName: "exclamationmark.circle.fill").foregroundStyle(.orange)
            }
        }
    }

    private var emptyMessage: some View {
        Text(snapshot.message ?? "暂无额度数据")
            .font(.caption).foregroundStyle(.secondary).lineLimit(3)
    }

    private var errorLabel: some View {
        Text("读取失败 · 请打开 App")
            .font(.caption2).foregroundStyle(.orange).lineLimit(1)
    }

    private var updatedLabel: some View {
        Text("更新于 \(snapshot.updatedAt.formatted(date: .omitted, time: .shortened))")
    }

    private var tokenSummary: some View {
        Text(tokenTotal > 0 ? "24h 消耗 · \(formatTokens(tokenTotal)) token" : "24h · 暂无本地使用记录")
            .font(.caption2).foregroundStyle(.secondary).lineLimit(1)
    }

    private var chartHeading: some View {
        HStack(spacing: 4) {
            Text("24h 用量曲线")
            Spacer(minLength: 2)
            Text(tokenTotal > 0 ? formatTokens(tokenTotal) : "暂无记录")
                .monospacedDigit()
        }
        .font(.caption2.weight(.medium)).foregroundStyle(.secondary).lineLimit(1)
    }

    private var chartAxis: some View {
        HStack {
            Text("24小时前")
            Spacer()
            Text("现在")
        }
        .font(.system(size: 9)).foregroundStyle(.secondary)
    }

    private func formatTokens(_ value: Int) -> String {
        if value >= 1_000_000 { return String(format: "%.1fM", Double(value) / 1_000_000) }
        if value >= 1_000 { return String(format: "%.1fK", Double(value) / 1_000) }
        return String(value)
    }

    @ViewBuilder private var fullResetLabel: some View {
        if snapshot.availableFullResets > 0 {
            Text("Full reset · 可用 \(snapshot.availableFullResets) 次")
                .font(.caption2.weight(.medium))
                .lineLimit(1)
        }
    }

    private func resetLabel(_ date: Date) -> some View {
        Text("\(date.formatted(date: .abbreviated, time: .shortened)) 重置")
            .font(.caption2).foregroundStyle(.secondary).lineLimit(1)
    }

    private func windowLine(_ name: String, _ window: UsageWindow?) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(name).font(.caption2).foregroundStyle(.secondary)
            Text(window.map { "\($0.remainingPercent)%" } ?? "--")
                .font(.headline)
            if let reset = window?.resetsAt { resetLabel(reset) }
        }
    }

    private func compactWindowLine(_ name: String, _ window: UsageWindow?) -> some View {
        VStack(alignment: .leading, spacing: 1) {
            Text(name).font(.caption2).foregroundStyle(.secondary)
            Text(window.map { "\($0.remainingPercent)%" } ?? "--")
                .font(.subheadline.weight(.semibold))
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

private struct TokenActivityView: View {
    let points: [TokenPoint]
    let color: Color

    var body: some View {
        GeometryReader { geometry in
            let positions = chartPositions(in: geometry.size)
            ZStack {
                Path { path in
                    let y = geometry.size.height - 4
                    path.move(to: CGPoint(x: 0, y: y))
                    path.addLine(to: CGPoint(x: geometry.size.width, y: y))
                }
                .stroke(.secondary.opacity(0.2), lineWidth: 1)

                if positions.count >= 2 && points.contains(where: { $0.tokens > 0 }) {
                    Path { path in
                        path.move(to: CGPoint(x: positions[0].x, y: geometry.size.height - 4))
                        path.addLine(to: positions[0])
                        addCurve(to: &path, positions: positions)
                        path.addLine(to: CGPoint(x: positions[positions.count - 1].x,
                                                 y: geometry.size.height - 4))
                        path.closeSubpath()
                    }
                    .fill(LinearGradient(colors: [color.opacity(0.32), color.opacity(0.03)],
                                         startPoint: .top, endPoint: .bottom))

                    Path { path in
                        path.move(to: positions[0])
                        addCurve(to: &path, positions: positions)
                    }
                    .stroke(color, style: StrokeStyle(lineWidth: 2.5, lineCap: .round,
                                                     lineJoin: .round))

                    Circle().fill(color).frame(width: 6, height: 6)
                        .position(positions[positions.count - 1])
                } else {
                    Text("暂无本地使用记录").font(.caption2).foregroundStyle(.secondary)
                }
            }
        }
        .accessibilityLabel("过去 24 小时 token 用量曲线")
    }

    private func chartPositions(in size: CGSize) -> [CGPoint] {
        guard points.count >= 2 else { return [] }
        let peak = max(1, points.map(\.tokens).max() ?? 0)
        let base = size.height - 4
        let top: CGFloat = 5
        return points.enumerated().map { index, point in
            CGPoint(x: 4 + CGFloat(index) / CGFloat(points.count - 1) * (size.width - 8),
                    y: base - CGFloat(point.tokens) / CGFloat(peak) * (base - top))
        }
    }

    private func addCurve(to path: inout Path, positions: [CGPoint]) {
        guard positions.count >= 2 else { return }
        for index in 0..<(positions.count - 1) {
            let previous = positions[max(0, index - 1)]
            let current = positions[index]
            let next = positions[index + 1]
            let following = positions[min(positions.count - 1, index + 2)]
            if current.y == next.y {
                path.addLine(to: next)
            } else {
                let dx = (next.x - current.x) / 3
                let minY = min(current.y, next.y)
                let maxY = max(current.y, next.y)
                let control1 = CGPoint(x: current.x + dx,
                    y: min(max(current.y + (next.y - previous.y) / 6, minY), maxY))
                let control2 = CGPoint(x: next.x - dx,
                    y: min(max(next.y - (following.y - current.y) / 6, minY), maxY))
                path.addCurve(to: next, control1: control1, control2: control2)
            }
        }
    }
}
