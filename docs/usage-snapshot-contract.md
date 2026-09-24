# Usage snapshot 字段约定

Windows 与 macOS 分别实现 UI，只共享下面的字段含义。macOS 的 `UsageSnapshot` 是 App 与 Widget Extension 的 Codable 快照，不直接复用 Windows 的 `WidgetDisplayData`。

| 字段 | 语义与优先级 |
| --- | --- |
| 额度来源 | 优先 `rateLimitsByLimitId.codex`，否则使用其他 limit bucket；旧响应回退到 `rateLimits`。 |
| Primary / Secondary | `primary`、`secondary` 是独立窗口。`usedPercent` 表示已使用比例；剩余比例为 `clamp(100 - usedPercent, 0...100)`。窗口不存在或没有有效百分比时显示 `--`。 |
| 主要百分比 | 有 Secondary 时优先展示 Secondary，通常是 Weekly；否则展示 Primary。小、中、大尺寸均可看到主要百分比，较大尺寸显示两个窗口。 |
| 窗口时间 | `windowDurationMins` 的 300、10080 分别标为 5 小时和每周；`resetsAt` 为 Unix 秒，按本地时区显示。缺失时不猜测重置时间。 |
| Full reset | `rateLimitResetCredits.availableCount` 是可用次数。仅当大于零才显示。`credits` 中选 `status=available` 且尚未过期的最早 `expiresAt` 用于提醒；详情列表可能缺失。 |
| 状态 | `ready` 表示至少一个可用窗口；`noData` 表示服务正常但无可用窗口；`error` 表示读取失败。Mac 的错误快照可保留上次百分比，但小组件必须明确标出“读取失败”。 |
| 更新时间 | `updatedAt` 是上一次读取尝试时间。旧快照不因 WidgetKit 重新生成时间线而改写。 |
| 24 小时消耗 | Mac 宿主 App 从本机 `~/.codex/sessions` 的 `token_usage_record` 汇总最近 24 小时的逐小时 token 消耗；旧日志没有该记录时，使用 `token_count` 的累计差值。小尺寸显示总量，中、大尺寸显示平滑曲线。本地日志不可用时显示“暂无本地使用记录”。额度接口不提供历史 token 用量。 |
| 最近任务 | Mac 从本机 `~/.codex/sqlite/codex-dev.db` 的 `local_thread_catalog` 读取标题，并只显示存在本地 session 文件的任务；从对应 session 日志汇总任务 token 用量。没有本地目录或表时留空，日志无法读取时用量标为未知。 |

示例 `account/rateLimits/read` 响应的 `result`（示例时间戳仅用于说明）：

```json
{
  "rateLimitsByLimitId": {
    "codex": {
      "planType": "plus",
      "primary": { "usedPercent": 65, "windowDurationMins": 300, "resetsAt": 2000010000 },
      "secondary": { "usedPercent": 20, "windowDurationMins": 10080, "resetsAt": 2000100000 }
    }
  },
  "rateLimitResetCredits": {
    "availableCount": 2,
    "credits": [
      { "status": "available", "expiresAt": 2000040000 },
      { "status": "used", "expiresAt": 2000030000 }
    ]
  }
}
```

该示例应显示 Primary 剩余 35%、Secondary 剩余 80%，主要百分比为 80%，Full reset 可用 2 次。对应的可执行检查在 `macos/Tests/UsageContractSmoke.swift`。
