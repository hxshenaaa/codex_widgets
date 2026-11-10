using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

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
internal sealed class CodexAppServerClient(string? isolatedHome = null)
{
    private readonly Channel<JsonElement> _loginEvents = Channel.CreateUnbounded<JsonElement>();
    private readonly object _gate = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process? _proc;
    private int _nextId = 1;
    private bool _initialized;

    /// <summary>Optional diagnostic trace hook (set by the probe).</summary>
    public static Action<string>? Log { get; set; }
    private void Trace(string message)
    {
        if (isolatedHome is null)
            Log?.Invoke(message);
    }

    public Task<JsonElement> GetRateLimitsAsync() => CallAsync("account/rateLimits/read");
    public Task<JsonElement> GetUsageAsync() => CallAsync("account/usage/read");

    public Task<JsonElement> UseExternalAccountAsync(string accountId, string accessToken) =>
        CallAsync("account/login/start", new { type = "chatgptAuthTokens", chatgptAccountId = accountId, accessToken });

    public Task<JsonElement> ReadAccountAsync(bool refreshToken = false) =>
        CallAsync("account/read", new { refreshToken });

    public async Task LoginAsync(Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        var response = await CallAsync("account/login/start", new { type = "chatgpt", useHostedLoginSuccessPage = true });
        var loginId = response.GetProperty("loginId").GetString();
        if (string.IsNullOrEmpty(loginId) ||
            !Uri.TryCreate(response.GetProperty("authUrl").GetString(), UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo) ||
            url.Host is not ("auth.openai.com" or "chatgpt.com" or "auth0.openai.com"))
            throw new AccountSwitchException("Codex 未返回受支持的官方登录地址。");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            openBrowser(url);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                var result = await _loginEvents.Reader.ReadAsync(timeout.Token);
                if (!result.TryGetProperty("loginId", out var id) || id.GetString() != loginId)
                    continue;
                if (result.GetProperty("success").ValueKind != JsonValueKind.True)
                    throw new AccountSwitchException("官方登录未完成，请重新添加账号。");
                return;
            }
        }
        finally
        {
            // Stopping the isolated app-server also closes its callback listener.
            Stop();
        }
    }

    private async Task<JsonElement> CallAsync(string method, object? parameters = null)
    {
        if (!IsReady())
            await StartAsync();

        return await RequestAsync(method, parameters, TimeSpan.FromSeconds(20));
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
                if (isolatedHome is not null)
                {
                    psi.WorkingDirectory = isolatedHome;
                    psi.Environment["CODEX_HOME"] = isolatedHome;
                    psi.Environment.Remove("OPENAI_API_KEY");
                    psi.Environment.Remove("CODEX_API_KEY");
                    psi.Environment.Remove("CODEX_MANAGED_BY_NPM");
                }

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
                    if (root.ValueKind != JsonValueKind.Object)
                        continue;
                    if (!root.TryGetProperty("id", out var idEl))
                    {
                        if (root.TryGetProperty("method", out var method) &&
                            method.ValueKind == JsonValueKind.String && method.GetString() == "account/login/completed" &&
                            root.TryGetProperty("params", out var parameters))
                            _loginEvents.Writer.TryWrite(parameters.Clone());
                        continue;
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
            lock (_gate)
            {
                if (ReferenceEquals(_proc, proc))
                    FailAllPending();
            }
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
            FailAllPending();
        }
        try
        {
            if (proc is { HasExited: false })
            {
                proc.StandardInput.Close();
                if (!proc.WaitForExit(1500))
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(2000);
                }
            }
        }
        catch
        {
            // Best effort; a dead process will be relaunched on next use.
        }
        finally
        {
            proc?.Dispose();
        }
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
