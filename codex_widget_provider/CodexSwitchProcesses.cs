using System.ComponentModel;
using System.Diagnostics;

namespace CodexUsageWidgetProvider;

internal sealed class CodexSwitchProcesses
{
    private static readonly string[] Names = ["ChatGPT", "Codex", "codex-app-server"];
    private readonly HashSet<string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _packagePrefix;
    private readonly int _sessionId;

    public CodexSwitchProcesses(string desktopBackend, string cli)
    {
        using var current = Process.GetCurrentProcess();
        _sessionId = current.SessionId;
        string app = Path.GetDirectoryName(Path.GetDirectoryName(desktopBackend))!;
        _packagePrefix = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(app))!, "OpenAI.Codex_");
        _knownPaths.Add(desktopBackend);
        _knownPaths.Add(Path.Combine(app, "ChatGPT.exe"));
        _knownPaths.Add(Path.Combine(app, "Codex.exe"));
        _knownPaths.Add(Path.Combine(app, "resources", "codex-app-server.exe"));
        if (Path.IsPathFullyQualified(cli)) _knownPaths.Add(Path.GetFullPath(cli));
    }

    public Snapshot Capture(bool prepareToStop = false)
    {
        var snapshot = new Snapshot();
        try
        {
            foreach (string name in Names)
            foreach (var process in Process.GetProcessesByName(name))
            {
                bool keep = false;
                try
                {
                    if (process.HasExited || process.SessionId != _sessionId) continue;
                    string? path = process.MainModule?.FileName;
                    bool known = path is not null && _knownPaths.Contains(path);
                    // ChatGPT is also a process name used outside the Codex package.
                    if (name == "ChatGPT" && path is not null && !known &&
                        !path.StartsWith(_packagePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (prepareToStop)
                    {
                        if (!known)
                            throw new AccountSwitchException($"无法确认 {name}（PID {process.Id}）属于当前 Codex 安装。请先退出该进程再切换。");
                        _ = process.SafeHandle; // Keep the confirmed process handle across the prompt.
                    }
                    snapshot.Processes.Add(process);
                    keep = true;
                }
                catch (InvalidOperationException ex) when (ex is not AccountSwitchException && process.HasExited) { }
                catch (Win32Exception ex)
                {
                    if (!process.HasExited)
                        throw new AccountSwitchException($"无法检查 {name}（PID {process.Id}）。请先退出该进程再切换。", ex);
                }
                finally { if (!keep) process.Dispose(); }
            }
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    }

    public void EnsureClosed()
    {
        using var running = Capture();
        if (running.Count > 0)
            throw new AccountSwitchException("检测到 Codex 仍在运行或已经重新启动。请重新发起切换。");
    }

    internal sealed class Snapshot : IDisposable
    {
        internal List<Process> Processes { get; } = new();
        public int Count => Processes.Count;

        public async Task StopAsync()
        {
            var closing = new List<Process>();
            foreach (var process in Processes)
            {
                if (!process.HasExited && process.CloseMainWindow()) closing.Add(process);
            }
            await WaitAsync(closing, TimeSpan.FromSeconds(2));
            foreach (var process in Processes)
            {
                if (process.HasExited) continue;
                try { process.Kill(); }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (Win32Exception ex)
                {
                    if (!process.HasExited)
                        throw new AccountSwitchException($"无法结束 Codex 进程（PID {process.Id}），已停止切换。", ex);
                }
            }
            await WaitAsync(Processes, TimeSpan.FromSeconds(5));
            if (Processes.Any(process => !process.HasExited))
                throw new AccountSwitchException("等待 Codex 进程退出超时，已停止切换。");
        }

        private static async Task WaitAsync(List<Process> processes, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            try { await Task.WhenAll(processes.Select(process => process.WaitForExitAsync(cancellation.Token))); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }

        public void Dispose()
        {
            foreach (var process in Processes) process.Dispose();
        }
    }
}
