# macOS 小组件实现记录

总体计划见根目录 `PLAN.md`。第一版采用 macOS 14+ 的 WidgetKit 和 SwiftUI，包含 `CodexUsage` 宿主 App 与 `CodexUsageWidget` 扩展。

数据流：宿主 App 启动 `codex app-server --stdio` → 完成 `initialize` / `initialized` → 调用 `account/rateLimits/read` → 映射成 `UsageSnapshot` → 写入 App Group → 请求 `WidgetCenter.reloadTimelines`。扩展只读共享快照并创建时间线，不执行 CLI。宿主启动、手动刷新、约 15 分钟定时和睡眠唤醒时读取；系统决定小组件的实际重载时间。

签名使用两个 target 的 App Groups entitlement `group.com.hxshenaaa.codexusagewidget`。宿主 App 保持非沙箱状态以运行用户安装的 CLI 并读取本地 Codex 任务；扩展启用 App Sandbox，只访问共享快照。首次安装后需要启动宿主 App，组件才会出现在添加列表。提醒由用户在 App 内开启权限，按最早可用 Full reset 的到期前 24 小时预约；若首次读取已进入最后 24 小时则立即提醒一次。

当前验证：plist / pbxproj 语法、两个 Swift target 的构建、字段映射和 24 小时 token 汇总的可执行自检，以及本机已登录 CLI 的真实握手和额度读取。本机已使用同一 Apple Development Team 完成签名构建，`CodexUsage.app` 内含 `CodexUsageWidget.appex`，并安装到个人“应用程序”目录。宿主快照已写入 24 个逐小时 token 桶；睡眠唤醒、通知投递和桌面视觉布局仍需继续验收。
