using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace CodexUsageWidgetProvider;

internal sealed record SavedCodexAccount(
    CodexIdentity Identity, string Email, byte[] Credentials, string AccessToken, DateTimeOffset? LastRefresh)
{
    public string DisplayName => $"{Email} · {Identity.AccountId[^Math.Min(8, Identity.AccountId.Length)..]}";

    public static SavedCodexAccount Read(byte[] data)
    {
        try
        {
            using var auth = JsonDocument.Parse(data);
            var root = auth.RootElement;
            if (root.TryGetProperty("auth_mode", out var mode) &&
                mode.GetString() is not ("chatgpt" or "chatgptAuthTokens"))
                throw InvalidAuth();

            var tokens = root.GetProperty("tokens");
            var idToken = Required(tokens, "id_token");
            var accessToken = Required(tokens, "access_token");
            _ = Required(tokens, "refresh_token");
            var parts = idToken.Split('.');
            if (parts.Length != 3)
                throw InvalidAuth();

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var jwt = JsonDocument.Parse(Convert.FromBase64String(payload));
            var claims = jwt.RootElement;
            var identity = claims.GetProperty("https://api.openai.com/auth");
            var accountId = Required(identity, "chatgpt_account_id");
            if (tokens.TryGetProperty("account_id", out var selected) &&
                selected.ValueKind != JsonValueKind.Null && selected.GetString() != accountId)
                throw InvalidAuth();

            var userId = identity.TryGetProperty("chatgpt_user_id", out _)
                ? Required(identity, "chatgpt_user_id") : Required(identity, "user_id");
            if (identity.TryGetProperty("user_id", out var otherUserId) && otherUserId.GetString() != userId)
                throw InvalidAuth();
            var email = Required(claims, "email");
            if (email.Length > 256 || email.Any(char.IsControl) || accountId.Length > 256 || userId.Length > 256)
                throw InvalidAuth();

            // The token came from the native login file. Claims identify the local
            // renderer slot; the Codex client authenticates the restored session.
            DateTimeOffset? refreshedAt = root.TryGetProperty("last_refresh", out var refresh) &&
                refresh.ValueKind == JsonValueKind.String && refresh.TryGetDateTimeOffset(out var timestamp)
                ? timestamp : null;
            return new SavedCodexAccount(new CodexIdentity(accountId, userId), email, data, accessToken, refreshedAt);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException ||
                                   ex is InvalidOperationException and not AccountSwitchException)
        {
            throw new AccountSwitchException("当前 Codex 登录文件不可用。请在原生 Codex 中登录 ChatGPT 账号后重试。", ex);
        }
    }

    private static string Required(JsonElement element, string key)
    {
        var value = element.GetProperty(key).GetString();
        return !string.IsNullOrWhiteSpace(value) ? value : throw InvalidAuth();
    }

    private static AccountSwitchException InvalidAuth() =>
        new("仅支持使用本地登录文件的 ChatGPT 账号。请在原生 Codex 中登录后重试。");
}

internal sealed record CodexAccountProfile(string Key, CodexIdentity? Identity, string Label,
    DateTimeOffset? LastRefresh, AccountUsageSnapshot? Usage, string? Error);

internal sealed record CodexSwitchReceipt(string SwitchId, CodexIdentity? Source, CodexIdentity Target,
    DateTimeOffset NativeVerifiedAt, bool DesktopVerified, LocalThreadChange[] Changes,
    string[] ProjectIds, string[] MissingThreadIdsBeforeSwitch);

internal enum CodexSwitchResult { AlreadyCurrent, Cancelled, Switched }

internal sealed class CodexAccounts
{
    private const int AuthLimit = 1024 * 1024;
    private const int StateLimit = 32 * 1024 * 1024;
    private readonly string _authPath;
    private readonly string _statePath;
    private readonly string _storeDirectory;

    public static string CodexHome => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
        ? Path.GetFullPath(home)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public CodexAccounts()
    {
        var codexHome = CodexHome;
        _authPath = Path.Combine(codexHome, "auth.json");
        _statePath = Path.Combine(codexHome, ".codex-global-state.json");
        _storeDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexUsageWidget", "accounts");
    }

    public SavedCodexAccount? ReadCurrent()
    {
        var data = ReadFile(_authPath, AuthLimit);
        return data is null ? null : SavedCodexAccount.Read(data);
    }

    public IReadOnlyList<CodexAccountProfile> ListSaved()
    {
        using var gate = LockStore();
        return Directory.EnumerateFiles(_storeDirectory, "*.account")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(key => key is { Length: 64 } && key.All(Uri.IsHexDigit))
            .Select(key => ReadProfile(key!))
            .OrderBy(profile => profile.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public void SaveCurrent()
    {
        using var gate = LockStore();
        var current = ReadCurrent() ?? throw new AccountSwitchException("请先在原生 Codex 中登录账号。");
        Save(current);
    }

    public void SyncCurrent()
    {
        using var gate = LockStore();
        var current = ReadCurrent();
        if (current is not null)
            SyncEnrolled(current);
    }

    public async Task AddAccountAsync(CancellationToken cancellationToken)
    {
        var account = await CodexAccountConnection.LoginAsync(_storeDirectory, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var gate = LockStore();
        Save(account);
    }

    public async Task RefreshSavedAsync(CancellationToken cancellationToken)
    {
        foreach (var profile in ListSaved())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (profile.Identity is null)
                continue;
            using var runtime = LockRuntime();
            using var gate = LockStore();
            try
            {
                var account = ReadSaved(AccountPath(profile.Identity));
                var inspection = await CodexAccountConnection.InspectAsync(_storeDirectory, account, false, true);
                RememberInspection(account, inspection);
            }
            catch (Exception ex) when (IsProfileReadError(ex))
            {
                WriteProfile(profile with { Error = "无法读取保存的登录。可移除此记录或重新添加。" });
            }
        }
    }

    public void Forget(string key)
    {
        using var gate = LockStore();
        File.Delete(AccountPath(key));
        File.Delete(ProfilePath(key));
    }

    public bool HasSwitchReceipt => File.Exists(Path.Combine(_storeDirectory, "last-switch.json"));

    public CodexProjectLayout ReadProjectLayout() => CodexProjectState.ReadLayout(ReadFile(_statePath, StateLimit));

    public async Task<string> VerifyLastSwitchAsync()
    {
        using var runtime = LockRuntime();
        using var gate = LockStore();
        var state = ReadFile(_statePath, StateLimit) ?? throw new AccountSwitchException("无法读取桌面状态。");
        if (CodexProjectState.ReadLayout(state) == CodexProjectLayout.Shared)
            throw CodexProjectState.UseNativeSwitch();
        var path = Path.Combine(_storeDirectory, "last-switch.json");
        var bytes = ReadFile(path, StateLimit) ?? throw new AccountSwitchException("没有待验证的切换记录。");
        var receipt = JsonSerializer.Deserialize<CodexSwitchReceipt>(bytes)
            ?? throw new AccountSwitchException("切换记录不可读。");
        var current = ReadCurrent();
        if (current?.Identity != receipt.Target)
            throw new AccountSwitchException("当前登录与上次切换目标不同，桌面验收未通过。");
        var processes = Process.GetProcessesByName("ChatGPT");
        try
        {
            if (!processes.Any(p => !p.HasExited && p.SessionId == Process.GetCurrentProcess().SessionId))
                throw new AccountSwitchException("请先重新打开 Codex 并等待项目加载，然后再验证。");
        }
        finally { foreach (var process in processes) process.Dispose(); }
        if (!CodexProjectState.IsActiveAccount(state, receipt.Target))
            throw new AccountSwitchException("桌面仍未加载目标账号，不能确认切换成功。");
        var home = Path.GetDirectoryName(_authPath)!;
        var plan = new CodexLocalThreads(Path.Combine(home, "state_5.sqlite"), receipt.Target,
            receipt.Changes, receipt.ProjectIds, receipt.MissingThreadIdsBeforeSwitch);
        await CodexNativeAccount.VerifyAsync(home, current);
        if (receipt.ProjectIds.Length > 0 || receipt.Changes.Length > 0)
        {
            plan.Verify();
            await CodexNativeAccount.VerifyProjectsAsync(home, current, plan);
        }
        ReplaceIfUnchanged(path, JsonSerializer.SerializeToUtf8Bytes(receipt with { DesktopVerified = true }), bytes, StateLimit);
        var missing = receipt.MissingThreadIdsBeforeSwitch.Length;
        return $"验收通过：{current.Email}；{receipt.ProjectIds.Length} 个本地项目、{receipt.Changes.Length} 条现存项目会话已逐条核对。" +
            (missing > 0 ? $" 另有 {missing} 条旧索引在切换前已无本地会话记录。" : "");
    }

    public async Task<CodexSwitchResult> SwitchToAsync(CodexIdentity targetIdentity, bool inheritProjects,
        Func<bool, bool> confirmSwitch)
    {
        if (ReadProjectLayout() == CodexProjectLayout.Shared)
            throw CodexProjectState.UseNativeSwitch();
        using var runtime = LockRuntime();
        using var gate = LockStore();
        if (ReadCurrent()?.Identity == targetIdentity)
            return CodexSwitchResult.AlreadyCurrent;
        var target = ReadSaved(AccountPath(targetIdentity));
        if (target.Identity != targetIdentity)
            throw new AccountSwitchException("保存的登录与所选账号不一致，请删除该记录并重新保存。");

        // Holding the runtime lock keeps the widget's own app-server out of the
        // confirmed shutdown list while the user decides whether to continue.
        var processes = new CodexSwitchProcesses(CodexLocator.FindDesktopCodex(), CodexLocator.FindCodex());
        using (var running = processes.Capture(prepareToStop: true))
        {
            if (!confirmSwitch(running.Count > 0)) return CodexSwitchResult.Cancelled;
            if (running.Count > 0) await running.StopAsync();
        }
        processes.EnsureClosed();
        var source = ReadCurrent(); // Native shutdown can save newer credentials.
        if (source?.Identity == targetIdentity) return CodexSwitchResult.AlreadyCurrent;
        if (ReadProjectLayout() == CodexProjectLayout.Shared)
            throw CodexProjectState.UseNativeSwitch();
        var home = Path.GetDirectoryName(_authPath)!;
        if (source is not null)
            await CodexNativeAccount.VerifyAsync(home, source);
        processes.EnsureClosed();

        byte[]? state = null;
        byte[]? inherited = null;
        CodexLocalThreads? localThreads = null;
        if (inheritProjects)
        {
            if (source is null)
                throw new AccountSwitchException("当前没有本地登录。请先选择「仅切换账号」初始化。");
            state = ReadFile(_statePath, StateLimit)
                ?? throw new AccountSwitchException("未找到桌面项目状态。请先仅切换到目标账号，启动 Codex 初始化后再退出。");
            inherited = CodexProjectState.Inherit(state, source.Identity, target.Identity);
            localThreads = CodexLocalThreads.Capture(home, state, source.Identity, target.Identity);
        }

        if (source is not null)
        {
            if (File.Exists(AccountPath(source.Identity)))
                SyncEnrolled(source);
            else
                Save(source);
        }
        processes.EnsureClosed();
        EnsureUnchanged(_authPath, source?.Credentials, AuthLimit);
        // This encrypted journal preserves original assignments and a recoverable
        // pre-switch snapshot. It is written before any native state is changed.
        var switchId = Guid.NewGuid().ToString("N");
        var journal = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Source = source?.Identity, Target = target.Identity,
            SourceAuth = source?.Credentials, StateBefore = state,
            Changes = localThreads?.Changes, StartedAt = DateTimeOffset.UtcNow,
        });
        var encryptedJournal = ProtectedData.Protect(journal, null, DataProtectionScope.CurrentUser);
        var journalPath = Path.Combine(_storeDirectory, $"switch-{switchId}.backup");
        ReplaceIfUnchanged(journalPath, encryptedJournal, null, StateLimit * 2, protectNewFile: true);
        if (inherited is not null)
            ReplaceIfUnchanged(_statePath, inherited, state, StateLimit);

        var threadsApplied = false;
        var authWritten = false;
        try
        {
            processes.EnsureClosed();
            if (inheritProjects)
                EnsureUnchanged(_statePath, inherited ?? state, StateLimit);
            localThreads?.Apply();
            threadsApplied = localThreads is not null;
            ReplaceIfUnchanged(_authPath, target.Credentials, source?.Credentials, AuthLimit, protectNewFile: true);
            authWritten = true;
            await CodexNativeAccount.VerifyAsync(home, target);
            processes.EnsureClosed();
            localThreads?.Verify();
            if (localThreads is not null)
                await CodexNativeAccount.VerifyProjectsAsync(home, target, localThreads);
        }
        catch (Exception ex)
        {
            try
            {
                processes.EnsureClosed();
                if (authWritten)
                {
                    var actual = ReadCurrent();
                    if (actual?.Identity != target.Identity)
                        throw new AccountSwitchException("回滚前登录身份已被其他程序更改。");
                    Save(actual); // preserve a refresh made by native startup
                    if (source is null)
                    {
                        EnsureUnchanged(_authPath, actual.Credentials, AuthLimit);
                        File.Delete(_authPath);
                    }
                    else ReplaceIfUnchanged(_authPath, source.Credentials, actual.Credentials, AuthLimit, protectNewFile: true);
                }
                else EnsureUnchanged(_authPath, source?.Credentials, AuthLimit);
                if (threadsApplied) localThreads!.Rollback();
                if (inherited is not null)
                    ReplaceIfUnchanged(_statePath, state!, inherited, StateLimit);
            }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                throw new AccountSwitchException($"切换未通过验证且未能完整回滚。请保持 Codex 关闭。恢复备份编号：{switchId}", ex);
            }
            throw new AccountSwitchException("切换未通过验证，已恢复原账号、项目和会话归属。", ex);
        }

        try
        {
            processes.EnsureClosed();
            if (ReadCurrent()?.Identity != target.Identity)
                throw new AccountSwitchException("切换后登录身份已发生变化。");
            if (inheritProjects)
                EnsureUnchanged(_statePath, inherited ?? state, StateLimit);
        }
        catch (Exception ex) when (ex is AccountSwitchException or IOException)
        {
            throw new AccountSwitchException("切换期间 Codex 已启动或写入状态。请确认当前登录和项目归属后再继续。", ex);
        }

        var receipt = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SwitchId = switchId, Source = source?.Identity, Target = target.Identity,
            NativeVerifiedAt = DateTimeOffset.UtcNow, DesktopVerified = false,
            Changes = localThreads?.Changes ?? [],
            ProjectIds = localThreads?.ProjectIds ?? [],
            MissingThreadIdsBeforeSwitch = localThreads?.MissingThreadIds ?? [],
        });
        var receiptPath = Path.Combine(_storeDirectory, "last-switch.json");
        ReplaceIfUnchanged(receiptPath, receipt, ReadFile(receiptPath, StateLimit), StateLimit);
        return CodexSwitchResult.Switched;
    }

    // The widget and the Start menu app run in separate processes. Hold this
    // lock until the widget's temporary app-server has exited, or the switch
    // has finished writing both files.
    public FileStream LockRuntime()
    {
        Directory.CreateDirectory(_storeDirectory);
        var path = Path.Combine(_storeDirectory, "runtime.lock");
        var wait = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                if (wait.Elapsed >= TimeSpan.FromSeconds(45))
                    throw new AccountSwitchException("另一个 Codex 额度读取或账号切换仍在运行，请稍后重试。", ex);

                Thread.Sleep(150);
            }
        }
    }

    private FileStream LockStore()
    {
        Directory.CreateDirectory(_storeDirectory);
        return new FileStream(Path.Combine(_storeDirectory, "switch.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    private string AccountPath(CodexIdentity identity) => AccountPath(identity.StorageKey);

    private string AccountPath(string key)
    {
        if (key.Length != 64 || !key.All(Uri.IsHexDigit))
            throw new AccountSwitchException("账号记录编号无效。");
        return Path.Combine(_storeDirectory, key + ".account");
    }

    private string ProfilePath(string key) => Path.ChangeExtension(AccountPath(key), ".json");

    private CodexAccountProfile ReadProfile(string key)
    {
        try
        {
            var bytes = ReadFile(ProfilePath(key), AuthLimit);
            var cached = bytes is null ? null : JsonSerializer.Deserialize<CodexAccountProfile>(bytes);
            if (cached?.Key == key && cached.Identity?.StorageKey == key)
                return cached.Usage is { DailyUsage: null } usage ? cached with { Usage = usage with { DailyUsage = [] } } : cached;
        }
        catch (Exception ex) when (IsProfileReadError(ex)) { }

        try
        {
            var account = ReadSaved(AccountPath(key));
            var profile = NewProfile(account);
            WriteProfile(profile);
            return profile;
        }
        catch (Exception ex) when (IsProfileReadError(ex))
        {
            return new CodexAccountProfile(key, null, $"无法读取的账号 · {key[..8]}", null, null,
                "可移除此记录或重新添加。");
        }
    }

    private static bool IsProfileReadError(Exception error) =>
        error is AccountSwitchException or IOException or UnauthorizedAccessException or CryptographicException or JsonException;

    private static CodexAccountProfile NewProfile(SavedCodexAccount account) =>
        new(account.Identity.StorageKey, account.Identity, account.DisplayName, account.LastRefresh, null, null);

    private void WriteProfile(CodexAccountProfile profile)
    {
        var path = ProfilePath(profile.Key);
        ReplaceIfUnchanged(path, JsonSerializer.SerializeToUtf8Bytes(profile), ReadFile(path, AuthLimit), AuthLimit);
    }

    private void SyncEnrolled(SavedCodexAccount current)
    {
        var key = current.Identity.StorageKey;
        if (!File.Exists(AccountPath(key)))
            return;
        var profile = ReadProfile(key);
        if (profile.Identity is null || current.LastRefresh > profile.LastRefresh ||
            current.LastRefresh is not null && profile.LastRefresh is null)
            Save(current);
    }

    private void RememberInspection(SavedCodexAccount original, AccountInspection inspection)
    {
        if (inspection.Credentials.Identity != original.Identity)
            throw new AccountSwitchException("验证后的身份与目标账号不一致。");

        if (!inspection.Credentials.Credentials.AsSpan().SequenceEqual(original.Credentials))
            Save(inspection.Credentials);
        var profile = ReadProfile(original.Identity.StorageKey);
        var snapshot = inspection.Usage;
        if (snapshot is not null && profile.Usage is { } previous &&
            snapshot.LifetimeTokens is null && snapshot.DailyUsage.Length == 0)
            snapshot = snapshot with { LifetimeTokens = previous.LifetimeTokens, DailyUsage = previous.DailyUsage };

        WriteProfile(profile with { Usage = snapshot ?? profile.Usage, Error = inspection.Error });
    }

    private SavedCodexAccount ReadSaved(string path)
    {
        var encrypted = ReadFile(path, AuthLimit * 2)
            ?? throw new AccountSwitchException("找不到已保存的账号。请重新登录并保存。");
        var auth = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
        var account = SavedCodexAccount.Read(auth);
        if (!StringComparer.OrdinalIgnoreCase.Equals(path, AccountPath(account.Identity)))
            throw new AccountSwitchException("保存的登录与账号记录不一致，请重新保存。");

        return account;
    }

    private void Save(SavedCodexAccount account)
    {
        var path = AccountPath(account.Identity);
        var old = ReadFile(path, AuthLimit * 2);
        var profile = old is null ? NewProfile(account) : ReadProfile(account.Identity.StorageKey);
        var encrypted = ProtectedData.Protect(account.Credentials, optionalEntropy: null, DataProtectionScope.CurrentUser);
        ReplaceIfUnchanged(path, encrypted, old, AuthLimit * 2);
        WriteProfile(profile with { Identity = account.Identity, Label = account.DisplayName, LastRefresh = account.LastRefresh, Error = null });
    }

    private static byte[]? ReadFile(string path, int limit)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new AccountSwitchException("账号切换无法操作链接形式的登录或状态文件。");

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            if (stream.Length > limit)
                throw new AccountSwitchException("登录或状态文件大小超出支持范围，切换已停止。");

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
                throw new IOException("读取时文件已变化，请重试。");

            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void EnsureUnchanged(string path, byte[]? expected, int limit)
    {
        var current = ReadFile(path, limit);
        if (expected is null ? current is not null : current is null || !expected.AsSpan().SequenceEqual(current))
            throw new AccountSwitchException("登录或项目状态在切换期间发生变化，请退出 Codex 后重试。");
    }

    private static void ReplaceIfUnchanged(string path, byte[] bytes, byte[]? expected, int limit, bool protectNewFile = false)
    {
        if (bytes.Length > limit)
            throw new AccountSwitchException("合并后的状态超出支持范围，切换已停止。");

        var directory = Path.GetDirectoryName(path)!;
        var temporary = Path.Combine(directory, $".codex-widget-{Guid.NewGuid():N}.tmp");
        try
        {
            FileSecurity? security = null;
            if (expected is null && protectNewFile)
            {
                using var owner = WindowsIdentity.GetCurrent();
                var user = owner.User ?? throw new AccountSwitchException("无法确认当前 Windows 用户。");
                security = new FileSecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            }
            else if (expected is not null)
            {
                EnsureUnchanged(path, expected, limit);
                security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            }

            using (var output = security is null
                       ? new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                       : new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.Write,
                           FileShare.None, 4096, FileOptions.None, security))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }

            EnsureUnchanged(path, expected, limit);
            File.Move(temporary, path, overwrite: expected is not null);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
