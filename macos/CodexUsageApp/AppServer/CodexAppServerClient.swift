import Foundation

enum CodexClientError: LocalizedError {
    case executableMissing
    case timedOut
    case exited
    case invalidResponse
    case server(String)

    var errorDescription: String? {
        switch self {
        case .executableMissing: return "找不到 Codex CLI。请在设置中指定 codex 可执行文件。"
        case .timedOut: return "Codex 响应超时。请检查 CLI 登录状态后重试。"
        case .exited: return "Codex app-server 已退出。请检查 CLI 登录状态。"
        case .invalidResponse: return "Codex 返回了无法识别的额度数据。"
        case .server(let message): return message
        }
    }
}

enum CodexLocator {
    static func executable(configuredPath: String?) throws -> URL {
        let fileManager = FileManager.default
        let home = fileManager.homeDirectoryForCurrentUser.path
        let pathDirectories = (ProcessInfo.processInfo.environment["PATH"] ?? "")
            .split(separator: ":").map(String.init)
        let candidates = [configuredPath, ProcessInfo.processInfo.environment["CODEX_PATH"]]
            .compactMap { $0 } +
            pathDirectories.map { $0 + "/codex" } + [
                "/opt/homebrew/bin/codex", "/usr/local/bin/codex",
                home + "/.local/bin/codex", home + "/.npm-global/bin/codex",
                "/Applications/ChatGPT.app/Contents/Resources/codex"
            ]
        for candidate in candidates where !candidate.isEmpty {
            let path = (candidate as NSString).expandingTildeInPath
            if fileManager.isExecutableFile(atPath: path) { return URL(fileURLWithPath: path) }
        }
        throw CodexClientError.executableMissing
    }
}

/// One short-lived JSONL session per refresh. The extension never launches the CLI.
enum CodexAppServerClient {
    static func readRateLimits(configuredPath: String?) throws -> [String: Any] {
        let process = Process()
        process.executableURL = try CodexLocator.executable(configuredPath: configuredPath)
        process.arguments = ["app-server", "--stdio"]
        let input = Pipe()
        let output = Pipe()
        process.standardInput = input
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice

        try process.run()
        let timeout = DispatchWorkItem { if process.isRunning { process.terminate() } }
        DispatchQueue.global().asyncAfter(deadline: .now() + 25, execute: timeout)
        defer {
            timeout.cancel()
            try? input.fileHandleForWriting.close()
            if process.isRunning { process.terminate() }
            process.waitUntilExit()
        }

        let initialize: [String: Any] = [
            "id": 1, "method": "initialize",
            "params": [
                "clientInfo": ["name": "codex_usage_widget_macos",
                               "title": "Codex Usage Widget", "version": "1.0.0"],
                "capabilities": ["experimentalApi": true]
            ]
        ]
        try write(initialize, to: input.fileHandleForWriting)
        _ = try response(for: 1, from: output.fileHandleForReading, process: process)
        try write(["method": "initialized", "params": [:]], to: input.fileHandleForWriting)
        try write(["id": 2, "method": "account/rateLimits/read"],
                  to: input.fileHandleForWriting)
        return try response(for: 2, from: output.fileHandleForReading, process: process)
    }

    private static func write(_ object: [String: Any], to handle: FileHandle) throws {
        let data = try JSONSerialization.data(withJSONObject: object)
        try handle.write(contentsOf: data + Data([10]))
    }

    private static func response(for id: Int, from handle: FileHandle,
                                 process: Process) throws -> [String: Any] {
        while let line = try readLine(from: handle), process.isRunning || !line.isEmpty {
            guard let root = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
                  let responseID = root["id"] else { continue }
            let matches = (responseID as? NSNumber)?.intValue == id
                || (responseID as? String) == String(id)
            guard matches else { continue }
            if let error = root["error"] as? [String: Any] {
                throw CodexClientError.server((error["message"] as? String) ?? "Codex app-server 错误")
            }
            guard let result = root["result"] as? [String: Any] else {
                throw CodexClientError.invalidResponse
            }
            return result
        }
        throw process.isRunning ? CodexClientError.timedOut : CodexClientError.exited
    }

    private static func readLine(from handle: FileHandle) throws -> Data? {
        var line = Data()
        while let byte = try handle.read(upToCount: 1), !byte.isEmpty {
            if byte[0] == 10 { return line }
            if line.count > 4_000_000 { throw CodexClientError.invalidResponse }
            line.append(byte)
        }
        return line.isEmpty ? nil : line
    }
}
