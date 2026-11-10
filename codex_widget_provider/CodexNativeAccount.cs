using System.Text.Json;

namespace CodexUsageWidgetProvider;

// Verify the identity loaded by the desktop's own executable, in the actual
// home, after a new process starts. Reading auth.json alone is not a switch test.
internal static class CodexNativeAccount
{
    public static async Task VerifyProjectsAsync(string home, SavedCodexAccount expected, CodexLocalThreads plan)
    {
        var client = new CodexAppServerClient(home, CodexLocator.FindDesktopCodex());
        try
        {
            RequireIdentity(await client.ReadAuthenticationAsync(), expected);
            var projectIds = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            do
            {
                var page = await client.ReadProjectsAsync(cursor);
                foreach (var project in page.GetProperty("data").EnumerateArray())
                    projectIds.Add(project.GetProperty("id").GetString()!);
                cursor = page.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
            } while (cursor is not null);
            if (plan.ProjectIds.Any(id => !projectIds.Contains(id)))
                throw new AccountSwitchException("原生后台缺少切换前的本地项目。");
            foreach (var change in plan.Changes)
            {
                var result = await client.ReadThreadAsync(change.Id);
                var thread = result.GetProperty("thread");
                if (thread.GetProperty("id").GetString() != change.Id ||
                    thread.GetProperty("projectId").GetString() != change.TargetProjectId)
                    throw new AccountSwitchException("原生后台中的原会话或项目归属未通过核对。");
            }
        }
        catch (Exception ex) when (ex is CodexServerException or TimeoutException or JsonException or KeyNotFoundException)
        {
            throw new AccountSwitchException("原生后台未能逐条读取切换前的项目会话，切换未通过验证。", ex);
        }
        finally { client.Stop(); }
    }

    public static async Task VerifyAsync(string home, SavedCodexAccount expected)
    {
        var client = new CodexAppServerClient(home, CodexLocator.FindDesktopCodex());
        try
        {
            var auth = await client.ReadAuthenticationAsync();
            RequireIdentity(auth, expected);
        }
        catch (Exception ex) when (ex is CodexServerException or TimeoutException or JsonException or KeyNotFoundException)
        {
            throw new AccountSwitchException("无法通过桌面端原生后台确认账号身份，切换未通过验证。", ex);
        }
        finally { client.Stop(); }
    }

    internal static void RequireIdentity(JsonElement auth, SavedCodexAccount expected)
    {
        if (auth.GetProperty("authMethod").GetString() is not ("chatgpt" or "chatgptAuthTokens"))
            throw new AccountSwitchException("原生后台没有使用 ChatGPT 登录，切换未通过验证。");
        var token = auth.GetProperty("authToken").GetString();
        try
        {
            var segment = token!.Split('.')[1].Replace('-', '+').Replace('_', '/');
            segment = segment.PadRight((segment.Length + 3) / 4 * 4, '=');
            using var claims = JsonDocument.Parse(Convert.FromBase64String(segment));
            var identity = claims.RootElement.GetProperty("https://api.openai.com/auth");
            var user = identity.TryGetProperty("chatgpt_user_id", out var id) ? id : identity.GetProperty("user_id");
            if (identity.GetProperty("chatgpt_account_id").GetString() != expected.Identity.AccountId ||
                user.GetString() != expected.Identity.UserId)
                throw new AccountSwitchException("原生后台的用户或工作区与目标不一致，切换未通过验证。");
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or NullReferenceException)
        {
            throw new AccountSwitchException("原生后台没有返回可核对的登录身份，切换未通过验证。", ex);
        }
    }
}
