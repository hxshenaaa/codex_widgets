# Codex Usage Widget

一个 Windows 桌面悬浮小组件，用本机已经登录的 Codex CLI 读取 ChatGPT/Codex 订阅额度。

另有一个 **Windows 11 自带小组件面板的原生小组件版本**（C# 实现，独立于 Python，含构建与安装说明），见 [codex_widget_provider\README.md](codex_widget_provider/README.md)。

## 功能

- 显示剩余额度百分比
- 显示额度重置时间
- 同时展示 Primary / Secondary（通常对应 5 小时 / Weekly，但以服务端实际返回的窗口为准）
- 每 60 秒刷新
- 本地 SQLite 保存最近 30 天历史并画趋势曲线
- 无第三方 Python 依赖
- 不直接读取或保存 `~/.codex/auth.json` 中的 token

## 前提

1. Windows 10/11
2. Python 3.10+（安装时勾选 `Add Python to PATH`）
3. 已安装 Codex CLI
4. Codex CLI 已使用 ChatGPT 账号登录

先在 PowerShell 验证：

```powershell
codex --version
codex
```

如果 Codex 内能正常看到 `/status`，通常就可以使用本小组件。

## 启动

双击：

```text
run.bat
```

或者 PowerShell：

```powershell
python codex_usage_widget.py
```

## 操作

- 鼠标左键拖动：移动悬浮窗
- 鼠标右键：立即刷新 / 关闭
- 窗口默认置顶

## 数据来源

程序会启动：

```text
codex app-server --stdio
```

随后完成 `initialize` / `initialized` 握手，并调用：

```text
account/rateLimits/read
```

程序优先显示 `rateLimitsByLimitId.codex`，若不存在则回退到 `rateLimits`。

历史数据保存在：

```text
%USERPROFILE%\.codex_usage_widget.sqlite3
```

## 常见问题

### 找不到 codex

小组件按以下顺序查找 codex 可执行文件：

1. 环境变量 `CODEX_PATH` 指定的文件
2. PATH 上的 `codex.exe`（Windows）或 `codex`（其他系统）
3. npm 全局安装（`%APPDATA%\npm`）中平台包 vendor 目录里的 `codex.exe`
4. 以上都失败时回退为直接执行 `codex`

注意：npm 安装时 PATH 上只有 `codex.cmd` 这类 shim，小组件（`shell=False`）无法直接运行它们，
所以 `where.exe codex` 能找到不代表第 2 步会命中——不过第 3 步会自动定位 npm 包内的真实二进制。
若使用非默认 npm 前缀或特殊安装方式，请设置 `CODEX_PATH` 指向真正的 codex 可执行文件，然后重新启动小组件。

### 提示需要认证

额度读取要求 ChatGPT 身份认证。API key 登录并不等价于 ChatGPT 订阅登录。先正常运行 `codex` 并完成 ChatGPT 登录。

### 为什么只有 Weekly，没有 5h？

服务端可能只返回一个额度窗口；小组件不会自行猜测缺失的窗口。它会按服务端实际返回内容展示。

### 曲线刚开始为空

趋势图需要至少两个采样点。默认每 60 秒采样一次。
