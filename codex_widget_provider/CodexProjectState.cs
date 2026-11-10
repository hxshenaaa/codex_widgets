using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexUsageWidgetProvider;

internal sealed record CodexIdentity(string AccountId, string UserId)
{
    public string StorageKey => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes($"{AccountId}\0{UserId}")));
}

internal sealed class AccountSwitchException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

// The local-project inheritance algorithm is adapted from Poteto's MIT-licensed
// LocalProjectLayout and NativeProjectState (see THIRD_PARTY_NOTICES.md).
internal static class CodexProjectState
{
    private const string Projects = "local-projects";
    private const string Assignments = "thread-project-assignments";
    private const string ThreadOrder = "sidebar-project-thread-orders";
    private const string MigrationMap = "app-server-project-id-by-legacy-project-id-by-host";
    private const string MigrationFlags = "app-server-projects-migration-by-host";

    public static byte[]? Inherit(byte[] state, CodexIdentity source, CodexIdentity target)
    {
        try
        {
            return InheritCore(state, source, target);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException ||
                                   ex is InvalidOperationException and not AccountSwitchException)
        {
            throw new AccountSwitchException("无法识别当前 Codex 的项目状态格式。请用原生应用确认两个账号都已登录过。", ex);
        }
    }

    private static byte[]? InheritCore(byte[] state, CodexIdentity source, CodexIdentity target)
    {
        var root = RequireObject(JsonNode.Parse(state));
        var renderer = RequireObject(root["electron-account-renderer-state-v1"]);
        var inactive = RequireObject(renderer["inactive"]);
        if (!renderer.TryGetPropertyValue("owner", out var ownerNode))
            throw Incompatible();

        var owner = ownerNode?.GetValue<string>();
        if (owner is not null && (owner.Length == 0 || inactive.ContainsKey(owner)))
            throw Incompatible();

        JsonObject FindGlobals(CodexIdentity identity)
        {
            var candidates = new List<JsonObject>();
            if (owner is not null && Matches(root["electron-persisted-atom-state"], identity))
                candidates.Add(root);

            foreach (var entry in inactive)
            {
                var slot = RequireObject(entry.Value);
                if (Matches(slot["atoms"], identity))
                    candidates.Add(RequireObject(slot["globals"]));
            }

            return candidates.Count == 1
                ? candidates[0]
                : throw new AccountSwitchException("无法唯一确认账号的桌面状态。请在原生 Codex 中登录该账号一次，等待界面加载后正常退出。");
        }

        var from = FindGlobals(source);
        var into = FindGlobals(target);
        if (ReferenceEquals(from, into))
            return null;
        if (owner is not null && !ReferenceEquals(from, root))
            throw new AccountSwitchException("当前登录与桌面项目状态不一致。请重新打开 Codex，确认当前账号后正常退出。");

        var previous = (JsonObject)into.DeepClone();
        MergeLocalProjects(into, from);
        return JsonNode.DeepEquals(previous, into) ? null : JsonSerializer.SerializeToUtf8Bytes(root);
    }

    private static void MergeLocalProjects(JsonObject target, JsonObject source)
    {
        var incoming = GetMap(source, Projects);
        if (incoming.Count == 0)
            return;

        var projects = EnsureMap(target, Projects);
        foreach (var (id, record) in incoming)
        {
            var project = RequireObject(record);
            if (project["id"]?.GetValue<string>() != id)
                throw Incompatible();

            if (!projects.TryGetPropertyValue(id, out var existing) || JsonNode.DeepEquals(existing, project))
            {
                projects[id] = project.DeepClone();
                continue;
            }

            var old = RequireObject(existing);
            var time = project["updatedAt"]?.GetValue<long>();
            var oldTime = old["updatedAt"]?.GetValue<long>();
            if (!JsonNode.DeepEquals(old["createdAt"], project["createdAt"]) ||
                time is null || oldTime is null || time == oldTime)
                throw Conflict("本地项目");

            // A copied project can be renamed or edited before switching back.
            if (time > oldTime)
                projects[id] = project.DeepClone();
        }

        var order = Strings(source["project-order"]).Where(incoming.ContainsKey);
        MergeList(target, "project-order", order.Concat(incoming.Select(p => p.Key).Order(StringComparer.Ordinal)));
        MergeList(target, "pinned-project-ids", Strings(source["pinned-project-ids"]).Where(incoming.ContainsKey));
        foreach (var key in new[] { "project-appearances", "project-files" })
            MergeMap(target, source, key, incoming.ContainsKey);

        var importedThreads = new HashSet<string>(StringComparer.Ordinal);
        var newAssignments = new HashSet<string>(StringComparer.Ordinal);
        var projectless = Strings(target["projectless-thread-ids"]).ToHashSet(StringComparer.Ordinal);
        foreach (var (thread, value) in GetMap(source, Assignments))
        {
            var assignment = RequireObject(value);
            if (assignment["projectKind"]?.GetValue<string>() != "local")
                continue;

            var projectId = assignment["projectId"]?.GetValue<string>() ?? throw Incompatible();
            if (!incoming.ContainsKey(projectId) && assignment["projectOrigin"]?.GetValue<string>() == "chatgpt")
                continue;
            if (!incoming.ContainsKey(projectId) || projectless.Contains(thread))
                throw Conflict("会话归属");

            if (AddMatching(EnsureMap(target, Assignments), thread, assignment, "会话归属"))
                newAssignments.Add(thread);
            importedThreads.Add(thread);
        }

        foreach (var key in new[]
                 {
                     "thread-project-membership-host-ids",
                     "thread-workspace-root-hints",
                     "thread-projectless-output-directories"
                 })
            MergeMap(target, source, key, importedThreads.Contains);

        MergeList(target, "pinned-thread-ids", Strings(source["pinned-thread-ids"]).Where(importedThreads.Contains));
        foreach (var (projectId, value) in GetMap(source, ThreadOrder))
        {
            if (!incoming.ContainsKey(projectId))
                continue;

            var ids = Strings(RequireObject(value)["threadIds"]);
            var orders = EnsureMap(target, ThreadOrder);
            if (!orders.TryGetPropertyValue(projectId, out var current))
                orders[projectId] = current = new JsonObject();

            MergeList(RequireObject(current), "threadIds", ids);
        }

        // Newer Windows desktops already migrated many legacy projects to app-server IDs.
        // Reuse existing mappings on the same host so the native app can find those threads.
        foreach (var (host, value) in GetMap(source, MigrationMap))
        {
            foreach (var (projectId, mappedId) in RequireObject(value))
            {
                if (!incoming.ContainsKey(projectId))
                    continue;
                if (string.IsNullOrEmpty(mappedId?.GetValue<string>()))
                    throw Incompatible();

                var maps = EnsureMap(target, MigrationMap);
                AddMatching(EnsureMap(maps, host), projectId, mappedId, "本地项目迁移映射");
            }
        }

        // A target that already ran the native migration must revisit newly
        // inherited assignments. Keep existing pending IDs and host settings.
        foreach (var (host, value) in GetMap(target, MigrationFlags))
        {
            if (newAssignments.Count == 0 ||
                !(host == "local" || host.StartsWith("local:", StringComparison.Ordinal) ||
                  GetMap(source, MigrationFlags).ContainsKey(host) || GetMap(source, MigrationMap).ContainsKey(host)))
                continue;

            var flags = RequireObject(value);
            flags["threadAssignmentsMigrated"] = false;
            MergeList(flags, "pendingThreadAssignmentIds", newAssignments);
        }
    }

    private static bool Matches(JsonNode? atoms, CodexIdentity expected) =>
        atoms is JsonObject settings &&
        settings["local-document-identity"] is JsonObject identity &&
        identity["accountId"]?.GetValue<string>() == expected.AccountId &&
        identity["userId"]?.GetValue<string>() == expected.UserId;

    private static JsonObject RequireObject(JsonNode? value) => value as JsonObject ?? throw Incompatible();

    private static JsonObject GetMap(JsonObject container, string key) =>
        container.TryGetPropertyValue(key, out var value) ? RequireObject(value) : new JsonObject();

    private static JsonObject EnsureMap(JsonObject container, string key)
    {
        if (container.TryGetPropertyValue(key, out var value))
            return RequireObject(value);

        var created = new JsonObject();
        container.Add(key, created);
        return created;
    }

    private static string[] Strings(JsonNode? value) => value switch
    {
        null => [],
        JsonArray array => array.Select(item => item?.GetValue<string>() ?? throw Incompatible()).ToArray(),
        _ => throw Incompatible(),
    };

    private static void MergeList(JsonObject target, string key, IEnumerable<string> incoming)
    {
        var entries = incoming.ToArray();
        if (entries.Length == 0)
            return;

        var merged = new JsonArray();
        foreach (var id in Strings(target[key]).Concat(entries).Distinct(StringComparer.Ordinal))
            merged.Add(id);
        target[key] = merged;
    }

    private static void MergeMap(JsonObject target, JsonObject source, string key, Func<string, bool> includes)
    {
        foreach (var (id, value) in GetMap(source, key))
        {
            if (includes(id))
                AddMatching(EnsureMap(target, key), id, value, key);
        }
    }

    private static bool AddMatching(JsonObject target, string id, JsonNode? value, string label)
    {
        if (!target.TryGetPropertyValue(id, out var old))
        {
            target.Add(id, value?.DeepClone());
            return true;
        }

        if (!JsonNode.DeepEquals(old, value))
            throw Conflict($"{label}（{id}）");

        return false;
    }

    private static AccountSwitchException Incompatible() =>
        new("无法识别当前 Codex 的项目状态格式，切换已停止。");

    private static AccountSwitchException Conflict(string label) =>
        new($"两个账号的{label}存在冲突，切换已停止。请先在 Codex 中确认归属。");
}
