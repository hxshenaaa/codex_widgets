using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexUsageWidgetProvider;

internal sealed record AccountUsageDay(string Date, long Tokens);

internal sealed record AccountUsageSnapshot(
    DateTimeOffset SampledAt,
    string? Plan,
    int? FiveHourRemaining,
    long? FiveHourResetAt,
    int? WeeklyRemaining,
    long? WeeklyResetAt,
    long? LifetimeTokens,
    AccountUsageDay[] DailyUsage)
{
    [JsonIgnore]
    public long? LatestDailyTokens => DailyUsage.Length == 0 ? null : DailyUsage[^1].Tokens;

    public static AccountUsageSnapshot Parse(JsonElement account, JsonElement limits, JsonElement usage)
    {
        var plan = Text(Property(account, "account"), "planType");
        var bucket = Property(limits, "rateLimits");
        var byId = Property(limits, "rateLimitsByLimitId");
        var codex = Property(byId, "codex");
        if (codex.ValueKind == JsonValueKind.Object)
            bucket = codex;

        (int? Remaining, long? Reset) FindWindow(int duration)
        {
            foreach (var field in new[] { "primary", "secondary" })
            {
                var window = Property(bucket, field);
                if (Number(window, "windowDurationMins") != duration)
                    continue;
                var used = Number(window, "usedPercent");
                return (used is null ? null : 100 - (int)Math.Clamp(used.Value, 0, 100), Number(window, "resetsAt"));
            }

            return default;
        }

        var days = new List<AccountUsageDay>();
        var daily = Property(usage, "dailyUsageBuckets");
        if (daily.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in daily.EnumerateArray())
            {
                if (Text(item, "startDate") is { } date && Number(item, "tokens") is >= 0 and var tokens)
                    days.Add(new AccountUsageDay(date, tokens));
            }
        }

        var fiveHour = FindWindow(300);
        var weekly = FindWindow(10_080);
        return new AccountUsageSnapshot(DateTimeOffset.UtcNow, plan ?? Text(bucket, "planType"),
            fiveHour.Remaining, fiveHour.Reset, weekly.Remaining, weekly.Reset,
            Number(Property(usage, "summary"), "lifetimeTokens"), days.OrderBy(day => day.Date, StringComparer.Ordinal).ToArray());
    }

    private static JsonElement Property(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) ? value : default;

    private static string? Text(JsonElement element, string key)
    {
        var value = Property(element, key);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static long? Number(JsonElement element, string key)
    {
        var value = Property(element, key);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result) ? result : null;
    }
}

internal sealed record AccountInspection(SavedCodexAccount Credentials, AccountUsageSnapshot? Usage, string? Error);

internal static class CodexAccountConnection
{
    public static async Task<SavedCodexAccount> LoginAsync(string parent, CancellationToken cancellationToken)
    {
        using var home = new TemporaryCodexHome(parent);
        try
        {
            await home.Client.LoginAsync(url =>
            {
                Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            }, cancellationToken);
            return home.ReadCredentials();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AccountSwitchException("浏览器登录已超时，请重新添加账号。");
        }
        catch (Exception ex) when (ex is CodexServerException or TimeoutException or JsonException or KeyNotFoundException)
        {
            throw new AccountSwitchException("无法完成 Codex 官方浏览器登录，请检查 Codex 版本或稍后重试。");
        }
    }

    public static async Task<AccountInspection> InspectAsync(
        string parent, SavedCodexAccount saved, bool allowRefresh, bool includeUsage)
    {
        using var home = new TemporaryCodexHome(parent);
        var client = home.Client;
        var current = saved;
        try
        {
            if (allowRefresh)
                home.WriteCredentials(saved);
            else
                await client.UseExternalAccountAsync(saved.Identity.AccountId, saved.AccessToken);

            JsonElement limits;
            try
            {
                limits = await client.GetRateLimitsAsync();
            }
            catch (CodexServerException) when (allowRefresh)
            {
                await client.ReadAccountAsync(refreshToken: true);
                limits = await client.GetRateLimitsAsync();
            }

            if (allowRefresh)
            {
                current = home.ReadCredentials();
                if (current.Identity != saved.Identity)
                    return new AccountInspection(saved, null, "验证后的身份与目标账号不一致。");
            }

            var account = await client.ReadAccountAsync();
            if (!account.TryGetProperty("account", out var identity) || identity.ValueKind != JsonValueKind.Object ||
                !identity.TryGetProperty("type", out var type) || type.GetString() != "chatgpt")
                return new AccountInspection(current, null, "Codex 未识别到有效的 ChatGPT 登录。");

            if (identity.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String &&
                !string.Equals(saved.Email, email.GetString(), StringComparison.OrdinalIgnoreCase))
                return new AccountInspection(saved, null, "验证后的邮箱与目标账号不一致。");

            JsonElement usage = default;
            if (includeUsage)
            {
                try { usage = await client.GetUsageAsync(); }
                catch (Exception ex) when (ex is CodexServerException or TimeoutException)
                {
                    // Older app servers and some accounts do not provide token summaries.
                }
            }

            return new AccountInspection(current, AccountUsageSnapshot.Parse(account, limits, usage), null);
        }
        catch (Exception ex) when (ex is CodexServerException or TimeoutException or IOException or
                                   InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A managed refresh may already have rotated the refresh token even
            // if the following quota request failed. Keep the new same-account copy.
            if (allowRefresh)
            {
                try
                {
                    var refreshed = home.ReadCredentials();
                    if (refreshed.Identity == saved.Identity)
                        current = refreshed;
                }
                catch (Exception readError) when (readError is AccountSwitchException or IOException) { }
            }

            return new AccountInspection(current, null, ex is CodexServerException
                ? "账号额度验证失败。登录可能已失效，请重新添加账号后重试。"
                : "暂时无法连接 Codex 验证账号，请稍后重试。");
        }
    }

    private sealed class TemporaryCodexHome : IDisposable
    {
        private readonly string _path;
        private readonly string _authPath;
        public CodexAppServerClient Client { get; }

        public TemporaryCodexHome(string parent)
        {
            Directory.CreateDirectory(parent);
            _path = Path.Combine(parent, $"probe-{Guid.NewGuid():N}");
            _authPath = Path.Combine(_path, "auth.json");
            using var owner = WindowsIdentity.GetCurrent();
            var user = owner.User ?? throw new AccountSwitchException("无法确认当前 Windows 用户。");
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(_path).Create(acl);
            try
            {
                File.WriteAllText(Path.Combine(_path, "config.toml"), """
                    cli_auth_credentials_store = "file"
                    [history]
                    persistence = "none"
                    """);
            }
            catch
            {
                Directory.Delete(_path, recursive: true);
                throw;
            }

            Client = new CodexAppServerClient(_path);
        }

        public void WriteCredentials(SavedCodexAccount account) => File.WriteAllBytes(_authPath, account.Credentials);

        public SavedCodexAccount ReadCredentials()
        {
            using var file = new FileStream(_authPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > 1024 * 1024)
                throw new AccountSwitchException("Codex 返回的登录文件大小无效。");

            var bytes = new byte[file.Length];
            file.ReadExactly(bytes);
            return SavedCodexAccount.Read(bytes);
        }

        public void Dispose()
        {
            Client.Stop();
            Directory.Delete(_path, recursive: true);
        }
    }
}
