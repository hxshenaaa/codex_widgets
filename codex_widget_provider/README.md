# Codex Usage Widget — Windows 小组件版

把 Codex 订阅额度显示到 Windows 11 自带小组件面板（任务栏天气 / Win+W）的原生小组件。
显示内容与上层目录的 Python 悬浮窗对应（剩余百分比、重置时间、Primary/Secondary 明细、Plan 类型、趋势图），
但实现完全独立（C# / Windows App SDK），**运行时不需要 Python**。

趋势图为**股票组件风格的 SVG 曲线**（平滑样条 + 渐变填充 + 端点圆点）：
组件以官方 Web widget 模式渲染——卡片是一个指向 `https://codexwidget.invalid/` 的桩模板，
manifest 的 `WebRequestFilter` 让宿主把该域名下的所有资源请求拦截到 provider 的
`IWidgetResourceProvider.OnResourceRequested`，由 provider 在内存中直接返回 HTML 页面和 `data.json`
（完全离线，`.invalid` 是保留域，不会解析到真实服务器）。页面每 60 秒拉一次数据自绘曲线。

- 曲线口径与 Python 版一致：secondary/Weekly 窗口看 7 天，primary 窗口看 6 小时
- 曲线和百分比同色：剩余 ≥60% 绿色、≤15% 红色，其余跟随主题的蓝色
- 自动跟随系统深/浅色主题；小尺寸自动隐藏曲线和明细
- 采样自动进行（组件被固定期间每 60 秒一次，无需打开面板），
  历史保存在 `%USERPROFILE%\.codex_usage_widget_history.json`（保留 30 天，与 Python 版的 sqlite 历史相互独立）

## 前提

- Windows 11 22H2+，小组件面板可用
- 已安装 Codex CLI 并完成 ChatGPT 登录（与 Python 版相同）
- .NET SDK 8+（仅构建时需要）
- codex 安装在特殊位置时设置 `CODEX_PATH`（与 Python 版相同）

## 构建并安装

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build_msix.ps1
```

脚本会自动：

1. 首次运行创建自签名代码签名证书 `CN=CodexUsageWidgetDev`（导出 `.pfx`/`.cer` 到本目录）
2. 安装证书到本机信任（首次会弹一次 UAC）
3. `dotnet publish` 生成签名的 MSIX 到 `AppPackages\`

安装或更新（已构建好的包）：

```powershell
Add-AppxPackage .\AppPackages\CodexUsageWidgetProvider_1.0.0.0_x64_Test\CodexUsageWidgetProvider_1.0.0.0_x64.msix
```

卸载：

```powershell
Get-AppxPackage CodexUsageWidget | Remove-AppxPackage
```

## 使用

打开小组件面板 → 右上角 **+** → 列表底部找到 **Codex Usage** → 固定。

- 支持小/中/大三个尺寸；大字为剩余百分比（优先 Weekly/secondary 窗口，逻辑与 Python 版一致）
- 重置时间与 Primary/Secondary 明细在中/大尺寸显示，Plan 类型在大尺寸显示
- 有可用的 usage limit reset 时，各尺寸显示 `Full reset · 可用 N 次`；使用账号响应中的 `rateLimitResetCredits.availableCount`，无可用次数或字段缺失时隐藏
- Full reset 有到期时间时，预约到期前 24 小时的 Windows 通知；若首次读取已进入最后 24 小时，会立即提醒一次。额度变化或取消固定小组件时撤销旧预约。
- 面板可见时每 60 秒刷新；不可见时由宿主暂停
- 数据链路与 Python 版相同：`codex app-server --stdio` → `account/rateLimits/read`

## 自检（不用进面板）

`probe\` 是独立控制台工程，只共享 codex 客户端源码，不加载 Windows App SDK：

```powershell
dotnet run --project .\probe -c Release
```

打印出 `WidgetDisplayData` JSON 即表示 codex 查询、握手、解析全部正常。

## 结构

| 文件 | 作用 |
|---|---|
| `Program.cs` | 入口：`CoRegisterClassObject` 注册 COM 类；无控制台时等到所有组件被取消固定再退出 |
| `FactoryHelper.cs` | `IClassFactory` 样板，小组件宿主通过它 `CoCreateInstance` 出 `IWidgetProvider` |
| `WidgetProvider.cs` | `IWidgetProvider` + `IWidgetResourceProvider` 实现、60 秒刷新定时器、Web 模式开关（`UseWebWidget=false` 可回退纯 Adaptive Cards 渲染） |
| `WebWidgetPage.cs` | Web widget 的桩卡片模板与 HTML 页面（SVG 曲线、主题适配、响应式） |
| `CodexAppServerClient.cs` | codex app-server 的 JSONL 客户端（`find_codex` 查找顺序的 C# 移植） |
| `CodexUsageService.cs` | 快照选择/格式化（与 Python 版对齐）、数据模型、30 天历史采样与序列导出 |
| `Package.appxmanifest` | COM ExeServer + `com.microsoft.windows.widgets` 扩展 + `WebRequestFilter` 注册 |
| `DesignAssets/CodexUsageIcon.png` | 蓝色用量柱形图标的高分辨率原稿 |
| `DesignAssets/CodexUsagePreview.png` | 小组件选择器预览图原稿（底部信息完整保留） |
| `gen_assets.ps1` | 导出 44/48/50/150 像素图标和 300×304 选择器预览图；等比缩放，不裁剪 |
| `build_msix.ps1` | 证书 + 签名打包一条龙 |
| `probe\` | 数据链路自检工具（`dotnet run --project .\probe -c Release`；`page <dir>` 导出页面 + 无头浏览器测试文件） |

## 注意

- `CodexUsageWidgetDev.pfx` 是本地签名私钥，**请勿分发或提交到公开仓库**
- 已知坑：给 `ProcessStartInfo` 设置 `StandardInputEncoding` 会导致 codex 收不到 stdin（代码中已注释说明）
- 打包任务 `WinAppSdkGenerateAppxManifest` 在 dotnet CLI 下缺 `System.Security.Permissions` 程序集，
  需要把它从 NuGet 缓存复制到 `microsoft.windows.sdk.buildtools.msix\...\tools\net6.0\`（详见 csproj 注释）
- **已知宿主 Bug**：当 Windows Widgets 切换到新外观/新布局（Switch to new look / lock）且卡片并排摆放时，点开卡片右上角更多操作（More actions / `...`）的下拉弹窗会被右侧相邻卡片遮盖。此问题属于 Windows 11 小组件看板（`Widgets.exe`）在多列网格下的层叠上下文（`z-index`）渲染缺陷。
