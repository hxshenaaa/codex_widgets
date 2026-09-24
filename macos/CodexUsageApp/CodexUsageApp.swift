import AppKit
import Combine
import SwiftUI
import WidgetKit

@MainActor final class UsageAppModel: ObservableObject {
    @Published var snapshot = SharedSnapshotStore.load() ?? .empty()
    @Published var isRefreshing = false
    @Published var cliPath = UserDefaults.standard.string(forKey: "codexExecutablePath") ?? ""
    @Published var remindersEnabled = FullResetReminder.isEnabled
    @Published var notice: String?

    func refresh() {
        guard !isRefreshing else { return }
        isRefreshing = true
        let path = cliPath.trimmingCharacters(in: .whitespacesAndNewlines)
        Task {
            let result = await Task.detached(priority: .utility) { () -> Result<UsageSnapshot, Error> in
                do {
                    let response = try CodexAppServerClient.readRateLimits(
                        configuredPath: path.isEmpty ? nil : path)
                    var parsed = UsageSnapshotParser.parse(response)
                    parsed = UsageSnapshot(state: parsed.state, updatedAt: parsed.updatedAt,
                        primary: parsed.primary, secondary: parsed.secondary,
                        planType: parsed.planType, availableFullResets: parsed.availableFullResets,
                        fullResetExpiresAt: parsed.fullResetExpiresAt,
                        trend: LocalUsageReader.trend(for: parsed),
                        tokenActivity: TokenActivityReader.last24Hours(at: parsed.updatedAt),
                        recentTasks: LocalUsageReader.recentTasks(), message: parsed.message)
                    return .success(parsed)
                } catch { return .failure(error) }
            }.value
            let next: UsageSnapshot
            switch result {
            case .success(let value): next = value
            case .failure(let error):
                next = .failure(error.localizedDescription, previous: snapshot)
            }
            do {
                try SharedSnapshotStore.save(next)
                snapshot = next
                WidgetCenter.shared.reloadTimelines(ofKind: SharedSnapshotStore.widgetKind)
            } catch {
                snapshot = .failure(error.localizedDescription, previous: next)
            }
            await FullResetReminder.sync(snapshot)
            isRefreshing = false
        }
    }

    func saveCLIPath() {
        UserDefaults.standard.set(cliPath, forKey: "codexExecutablePath")
        refresh()
    }

    func toggleReminders() {
        Task {
            do {
                try await FullResetReminder.setEnabled(!remindersEnabled, snapshot: snapshot)
                remindersEnabled = FullResetReminder.isEnabled
                notice = nil
            } catch {
                notice = error.localizedDescription
            }
        }
    }
}

struct MainView: View {
    @StateObject private var model = UsageAppModel()
    private let timer = Timer.publish(every: 15 * 60, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack {
                Image(systemName: "chart.bar.fill")
                    .foregroundStyle(.blue)
                Text("Codex Usage").font(.title2.bold())
                Spacer()
                Button("刷新") { model.refresh() }.disabled(model.isRefreshing)
            }
            if model.isRefreshing { ProgressView().controlSize(.small) }
            if let message = model.snapshot.message {
                Text(message).foregroundStyle(model.snapshot.state == .error ? .red : .secondary)
            }
            if let notice = model.notice { Text(notice).foregroundStyle(.orange) }
            HStack(spacing: 20) {
                windowCard("Primary", model.snapshot.primary)
                windowCard("Secondary", model.snapshot.secondary)
            }
            if model.snapshot.availableFullResets > 0 {
                Text("Full reset：可用 \(model.snapshot.availableFullResets) 次")
                if let date = model.snapshot.fullResetExpiresAt {
                    Text("最近到期：\(date.formatted(date: .abbreviated, time: .shortened))")
                        .foregroundStyle(.secondary)
                }
            }
            Divider()
            Text("Codex CLI 路径（留空自动查找）").font(.subheadline)
            HStack {
                TextField("/opt/homebrew/bin/codex", text: $model.cliPath)
                    .textFieldStyle(.roundedBorder)
                Button("保存") { model.saveCLIPath() }
            }
            Button(model.remindersEnabled ? "关闭 Full reset 提醒" : "开启 Full reset 提醒") {
                model.toggleReminders()
            }
            Text("更新于 \(model.snapshot.updatedAt.formatted(date: .abbreviated, time: .standard))。App 运行时约每 15 分钟读取一次；小组件刷新由 macOS 调度。")
                .font(.caption).foregroundStyle(.secondary)
        }
        .padding(24)
        .frame(minWidth: 500, minHeight: 350)
        .task { model.refresh() }
        .onReceive(timer) { _ in model.refresh() }
        .onReceive(NSWorkspace.shared.notificationCenter.publisher(for: NSWorkspace.didWakeNotification)) { _ in
            model.refresh()
        }
    }

    private func windowCard(_ name: String, _ window: UsageWindow?) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(name).font(.caption).foregroundStyle(.secondary)
            Text(window.map { "\($0.remainingPercent)%" } ?? "--")
                .font(.system(size: 36, weight: .bold, design: .rounded))
            Text(window?.shortName ?? "暂无数据")
            if let reset = window?.resetsAt {
                Text("\(reset.formatted(date: .abbreviated, time: .shortened)) 重置")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding()
        .background(.quaternary.opacity(0.35), in: RoundedRectangle(cornerRadius: 12))
    }
}

@main struct CodexUsageApp: App {
    var body: some Scene {
        WindowGroup("Codex Usage") { MainView() }
    }
}
