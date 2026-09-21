using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexUsageWidgetProvider;

internal sealed class CodexServerException : Exception
{
    public CodexServerException(string message) : base(message) { }
}

/// <summary>
/// JSONL client for `codex app-server --stdio`, ported from
/// CodexAppServer in codex_usage_widget.py: initialize/initialized
/// handshake, then account/rateLimits/read requests.
/// </summary>
internal sealed class CodexAppServerClient
{
    private readonly object _gate = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process? _proc;
    private int _nextId = 1;
    private bool _initialized;

    /// <summary>Optional diagnostic trace hook (set by the probe).</summary>
    public static Action<string>? Log { get; set; }
    private static void Trace(string message) => Log?.Invoke(message);

    public async Task<JsonElement> GetRateLimitsAsync()
    {
        if (!IsReady())
        {
            await StartAsync();
        }
        return await RequestAsync("account/rateLimits/read", null, TimeSpan.FromSeconds(20));
    }

    private bool IsReady()
    {
        lock (_gate)
        {
            return _proc is { HasExited: false } && _initialized;
        }
    }

    private async Task StartAsync()
    {
        await _startLock.WaitAsync();
        try
        {
            if (IsReady())
            {
                return;
            }
            Stop(); // clean up a dead or half-initialized process

            Process proc;
            lock (_gate)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = CodexLocator.FindCodex(),
                    Arguments = "app-server --stdio",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // Note: StandardInputEncoding must stay unset; the payload is
                    // pure ASCII and setting it prevents codex from seeing stdin.
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                proc = Process.Start(psi)
                    ?? throw new InvalidOperationException("failed to start codex app-server");
                _proc = proc;
                Trace($"started pid={proc.Id} exe={psi.FileName}");
            }
            _ = ReadLoopAsync(proc);
            _ = proc.StandardError.ReadToEndAsync(); // keep stderr drained

            await RequestAsync("initialize", new
            {
                clientInfo = new
                {
                    name = "codex_usage_widget_provider",
                    title = "Codex Usage Widget",
                    version = "1.0.0",
                },
                capabilities = new { experimentalApi = true },
            }, TimeSpan.FromSeconds(15));

            lock (_gate)
            {
                if (proc.HasExited)
                {
                    throw new InvalidOperationException("codex app-server exited during handshake");
                }
                var notifyLine = JsonSerializer.Serialize(new { method = "initialized", @params = new { } });
                proc.StandardInput.WriteLine(notifyLine);
                proc.StandardInput.Flush();
                Trace("tx notify: initialized");
                _initialized = true;
            }
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task<JsonElement> RequestAsync(string method, object? parameters, TimeSpan timeout)
    {
        TaskCompletionSource<JsonElement> tcs;
        int id;
        lock (_gate)
        {
            if (_proc is not { HasExited: false })
            {
                throw new InvalidOperationException("codex app-server is not running");
            }
            id = _nextId++;
            tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            object payload = parameters is null
                ? new { id, method }
                : new Dictionary<string, object?> { ["id"] = id, ["method"] = method, ["params"] = parameters };
            var line = JsonSerializer.Serialize(payload);
            _proc.StandardInput.WriteLine(line);
            _proc.StandardInput.Flush(); // redirected stdin is not auto-flushed
            Trace($"tx id={id} {method}: {line}");
        }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != tcs.Task)
        {
            lock (_gate)
            {
                _pending.Remove(id);
            }
            throw new TimeoutException($"{method} timed out");
        }
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(Process proc)
    {
        try
        {
            while (true)
            {
                string? line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
                line = line.Trim();
                if (line.Length == 0)
                {
                    continue;
                }
                Trace($"rx: {line}");

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    Trace($"rx: not JSON: {line}");
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var idEl))
                    {
                        continue; // notification; not needed for rate limit reads
                    }
                    int rid = ParseId(idEl);
                    if (rid < 0)
                    {
                        continue;
                    }

                    TaskCompletionSource<JsonElement>? tcs = null;
                    lock (_gate)
                    {
                        if (_pending.Remove(rid, out var found))
                        {
                            tcs = found;
                        }
                    }
                    if (tcs is null)
                    {
                        continue;
                    }

                    if (root.TryGetProperty("error", out var err))
                    {
                        string msg = "codex app-server error";
                        if (err.ValueKind == JsonValueKind.Object &&
                            err.TryGetProperty("message", out var msgEl) &&
                            msgEl.ValueKind == JsonValueKind.String)
                        {
                            msg = msgEl.GetString() ?? msg;
                        }
                        tcs.TrySetException(new CodexServerException(msg));
                    }
                    else if (root.TryGetProperty("result", out var resultEl))
                    {
                        tcs.TrySetResult(resultEl.Clone());
                    }
                    else
                    {
                        tcs.TrySetException(new CodexServerException("unexpected response from codex app-server"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Reader failures surface through the pending request failures below.
            Trace($"reader crashed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            FailAllPending();
        }
    }

    private static int ParseId(JsonElement idEl)
    {
        if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out var n))
        {
            return n;
        }
        if (idEl.ValueKind == JsonValueKind.String && int.TryParse(idEl.GetString(), out var s))
        {
            return s;
        }
        return -1;
    }

    private void FailAllPending()
    {
        lock (_gate)
        {
            foreach (var tcs in _pending.Values)
            {
                tcs.TrySetException(new InvalidOperationException("codex app-server exited"));
            }
            _pending.Clear();
            _initialized = false;
        }
    }

    public void Stop()
    {
        Process? proc;
        lock (_gate)
        {
            proc = _proc;
            _proc = null;
            _initialized = false;
        }
        try
        {
            if (proc is { HasExited: false })
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(2000);
            }
        }
        catch
        {
            // Best effort; a dead process will be relaunched on next use.
        }
        FailAllPending();
    }
}

/// <summary>
/// Resolves the codex executable path with the same order as
/// find_codex() in codex_usage_widget.py: CODEX_PATH env override,
/// codex.exe on PATH, npm vendor binary, bare "codex".
/// </summary>
internal static class CodexLocator
{
    public static string FindCodex()
    {
        string? overridePath = Environment.GetEnvironmentVariable("CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        string? onPath = FindOnPath("codex.exe");
        if (onPath is not null)
        {
            return onPath;
        }

        string? appData = Environment.GetEnvironmentVariable("APPDATA");
        if (appData is not null)
        {
            var scoped = Path.Combine(appData, "npm", "node_modules", "@openai");
            // npm on Windows only puts .cmd shims on PATH; the real binary
            // lives in the platform package's vendor directory. Match the same
            // two layouts as find_codex() in codex_usage_widget.py:
            //   @openai/codex/node_modules/@openai/codex-win32-*/vendor/*/bin/codex.exe
            //   @openai/codex-win32-*/vendor/*/bin/codex.exe
            string? hit = FindInCodexWin32Packages(Path.Combine(scoped, "codex", "node_modules", "@openai"))
                ?? FindInCodexWin32Packages(scoped);
            if (hit is not null)
            {
                return hit;
            }
        }

        return "codex";
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Malformed PATH entries are skipped.
            }
        }
        return null;
    }

    private static string? FindInCodexWin32Packages(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }
        foreach (var pkg in Directory.EnumerateDirectories(root, "codex-win32-*"))
        {
            var vendorDir = Path.Combine(pkg, "vendor");
            if (!Directory.Exists(vendorDir))
            {
                continue;
            }
            foreach (var archDir in Directory.EnumerateDirectories(vendorDir))
            {
                var exe = Path.Combine(archDir, "bin", "codex.exe");
                if (File.Exists(exe))
                {
                    return exe;
                }
            }
        }
        return null;
    }
}
