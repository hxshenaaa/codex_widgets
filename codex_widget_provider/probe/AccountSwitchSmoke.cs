using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexUsageWidgetProvider;

internal static class AccountSwitchSmoke
{
    private const string ActiveAtoms = "electron-persisted-atom-state";
    private const string Renderer = "electron-account-renderer-state-v1";
    private const string Projects = "local-projects";
    private const string Assignments = "thread-project-assignments";

    public static int Run()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "account-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var authPath = Path.Combine(directory, "auth.json");
        var statePath = Path.Combine(directory, ".codex-global-state.json");
        var store = Path.Combine(directory, "vault");
        var a = new CodexIdentity("account-a", "user-a");
        var b = new CodexIdentity("account-b", "user-b");
        var authA = Auth(a);
        var authB = Auth(b);
        var isRunning = false;
        var accounts = new CodexAccounts(directory, store, () => isRunning, Verify);

        try
        {
            File.WriteAllBytes(authPath, authA);
            accounts.SaveCurrent();
            File.WriteAllBytes(authPath, authB);
            accounts.SaveCurrent();
            File.WriteAllBytes(authPath, authA);
            Require(accounts.ListSaved().Count == 2, "profiles are stored separately");
            Require(Directory.EnumerateFiles(store, "*.json")
                .All(file => !File.ReadAllText(file).Contains("fake-access")), "profile metadata contains no credentials");
            Require(Directory.EnumerateFiles(store, "*.account")
                .All(file => !Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains("fake-access")),
                "saved credentials are encrypted");

            var before = Fixture();
            var original = JsonSerializer.SerializeToUtf8Bytes(before);
            File.WriteAllBytes(statePath, original);

            // A second process must wait for an in-flight widget quota read.
            var usageLock = accounts.LockRuntime();
            var manager = new CodexAccounts(directory, store, () => false);
            using var switchStarted = new ManualResetEventSlim();
            var waiting = Task.Run(() =>
            {
                switchStarted.Set();
                using var gate = manager.LockRuntime();
            });
            try
            {
                Require(switchStarted.Wait(TimeSpan.FromSeconds(3)), "manager lock attempt starts");
                Require(!waiting.Wait(TimeSpan.FromMilliseconds(250)), "runtime gate excludes a second caller");
            }
            finally
            {
                usageLock.Dispose();
                waiting.GetAwaiter().GetResult();
            }

            isRunning = true;
            Reject(() => accounts.SwitchToAsync(b, inheritProjects: true).GetAwaiter().GetResult());
            Unchanged(authPath, authA);
            Unchanged(statePath, original);
            isRunning = false;

            var checks = 0;
            var interrupted = new CodexAccounts(directory, store, () => ++checks == 3, Verify);
            Reject(() => interrupted.SwitchToAsync(b, inheritProjects: true).GetAwaiter().GetResult());
            Unchanged(authPath, authA);
            Unchanged(statePath, original);

            var rejected = new CodexAccounts(directory, store, () => false, (account, _, _) =>
                Task.FromResult(new AccountInspection(account, null, "已撤销的凭据")));
            Reject(() => rejected.SwitchToAsync(b, inheritProjects: true).GetAwaiter().GetResult());
            Unchanged(authPath, authA);
            Unchanged(statePath, original);
            Require(accounts.ListSaved().Single(p => p.Identity == b).Error is not null, "failed preflight is shown for B");

            var legacy = (JsonObject)before.DeepClone();
            legacy.Remove("app-server-project-id-by-legacy-project-id-by-host");
            var legacyGlobals = Target(legacy);
            var legacyFlags = legacyGlobals["app-server-projects-migration-by-host"]!.AsObject();
            legacyFlags["local:fixture"] = legacyFlags["host"]!.DeepClone();
            legacyFlags.Remove("host");
            var legacyResult = JsonNode.Parse(CodexProjectState.Inherit(JsonSerializer.SerializeToUtf8Bytes(legacy), a, b)!)!;
            var migrated = Target(legacyResult)["app-server-projects-migration-by-host"]!["local:fixture"]!;
            Require(!migrated["threadAssignmentsMigrated"]!.GetValue<bool>() &&
                migrated["pendingThreadAssignmentIds"]!.AsArray().Any(value => value!.GetValue<string>() == "thread-a"),
                "legacy source still queues the target local host migration");

            var conflict = (JsonObject)before.DeepClone();
            Target(conflict)["projectless-thread-ids"] = new JsonArray("thread-a");
            var conflictingState = JsonSerializer.SerializeToUtf8Bytes(conflict);
            File.WriteAllBytes(statePath, conflictingState);
            Reject(() => accounts.SwitchToAsync(b, inheritProjects: true).GetAwaiter().GetResult());
            Unchanged(authPath, authA);
            Unchanged(statePath, conflictingState);

            var initializedB = (JsonObject)before.DeepClone();
            initializedB[Renderer]!["inactive"]!.AsObject().Remove("slot-b");
            Reject(() => CodexProjectState.Inherit(JsonSerializer.SerializeToUtf8Bytes(initializedB), a, b));

            var signedOut = (JsonObject)before.DeepClone();
            signedOut[Renderer]!["owner"] = null;
            var archived = new JsonObject
            {
                ["atoms"] = before[ActiveAtoms]!.DeepClone(),
                ["globals"] = new JsonObject { [Projects] = before[Projects]!.DeepClone() },
            };
            signedOut[Renderer]!["inactive"]!["slot-a"] = archived;
            var signedOutMerge = JsonNode.Parse(CodexProjectState.Inherit(
                JsonSerializer.SerializeToUtf8Bytes(signedOut), a, b)!)!;
            Require(Target(signedOutMerge)[Projects]!["project-a"] is not null, "signed-out source slot is located");

            File.WriteAllBytes(statePath, original);
            Require(accounts.SwitchToAsync(b, inheritProjects: true).GetAwaiter().GetResult(), "switches from A to B");
            Unchanged(authPath, authB);
            var inheritedBytes = File.ReadAllBytes(statePath);
            var inherited = JsonNode.Parse(inheritedBytes)!;
            var target = Target(inherited);
            Require(target[Projects]!.AsObject().Count == 2, "both projects remain");
            Require(target[Assignments]!["thread-a"]!["projectId"]!.GetValue<string>() == "project-a", "A chat remains under A project");
            Require(target[Assignments]!["thread-b"] is not null, "B chat remains under B project");
            Require(target[Assignments]!["cloud-a"] is null, "cloud chat is excluded");
            Require(target["sidebar-project-thread-orders"]!["project-a"]!["threadIds"]![0]!.GetValue<string>() ==
                "thread-a", "chat sidebar order is inherited");
            Require(target["app-server-project-id-by-legacy-project-id-by-host"]!["host"]!["project-a"]!.GetValue<string>() ==
                "native-a", "Windows project alias is inherited");
            var flags = target["app-server-projects-migration-by-host"]!["host"]!;
            Require(!flags["threadAssignmentsMigrated"]!.GetValue<bool>() &&
                flags["pendingThreadAssignmentIds"]!.AsArray().Select(id => id!.GetValue<string>())
                    .SequenceEqual(["already-pending", "thread-a"]), "native migration is requeued for inherited chats");
            Require(target["thread-project-membership-host-ids"]!["thread-a"]!.GetValue<string>() ==
                "local", "local host affiliation is inherited");
            Require(target["selected-model"]!.GetValue<string>() == "model-b" &&
                inherited["selected-model"]!.GetValue<string>() == "model-a", "account-specific settings are retained");
            Require(inherited["precise-number"]!.ToJsonString() == "90071992547409931234567", "unrelated JSON number remains exact");
            Require(CodexProjectState.Inherit(inheritedBytes, a, b) is null, "replaying the same merge is idempotent");

            var afterDesktopOpenedB = (JsonObject)target.DeepClone();
            afterDesktopOpenedB[ActiveAtoms] = inherited[Renderer]!["inactive"]!["slot-b"]!["atoms"]!.DeepClone();
            var oldGlobals = (JsonObject)before.DeepClone();
            oldGlobals.Remove(ActiveAtoms);
            oldGlobals.Remove(Renderer);
            afterDesktopOpenedB[Renderer] = new JsonObject
            {
                ["owner"] = "slot-b",
                ["inactive"] = new JsonObject
                {
                    ["slot-a"] = new JsonObject
                    {
                        ["atoms"] = before[ActiveAtoms]!.DeepClone(),
                        ["globals"] = oldGlobals,
                    },
                },
            };
            afterDesktopOpenedB[Projects]!["project-a"]!["name"] = "renamed";
            afterDesktopOpenedB[Projects]!["project-a"]!["updatedAt"] = 3;
            afterDesktopOpenedB[Assignments]!["thread-new"] = new JsonObject
            {
                ["projectId"] = "project-a", ["projectKind"] = "local",
            };
            File.WriteAllBytes(statePath, JsonSerializer.SerializeToUtf8Bytes(afterDesktopOpenedB));
            Require(accounts.SwitchToAsync(a, inheritProjects: true).GetAwaiter().GetResult(), "switches from B to A");
            Unchanged(authPath, authA);
            var roundTrip = JsonNode.Parse(File.ReadAllBytes(statePath))![Renderer]!["inactive"]!["slot-a"]!["globals"]!;
            Require(roundTrip[Projects]!["project-a"]!["name"]!.GetValue<string>() == "renamed", "newer project metadata survives");
            Require(roundTrip[Assignments]!["thread-new"] is not null, "new chats survive the return switch");
            var beforeLoginOnly = File.ReadAllBytes(statePath);
            Require(accounts.SwitchToAsync(b, inheritProjects: false).GetAwaiter().GetResult(), "login-only switch succeeds");
            Unchanged(statePath, beforeLoginOnly);
            Require(accounts.SwitchToAsync(a, inheritProjects: false).GetAwaiter().GetResult(), "login-only return succeeds");

            File.Delete(Path.Combine(store, b.StorageKey + ".json"));
            File.WriteAllBytes(Path.Combine(store, b.StorageKey + ".account"), [1, 2, 3]);
            Require(accounts.ListSaved().Any(profile => profile.Key == b.StorageKey && profile.Identity is null),
                "a corrupt profile remains independently removable");
            accounts.Forget(b.StorageKey);
            Require(accounts.ListSaved().Single().Identity == a, "removing a profile keeps the other profile");
            Unchanged(authPath, authA);

            using var quota = JsonDocument.Parse("""
                {"rateLimits":{"primary":{"windowDurationMins":300,"usedPercent":25,"resetsAt":7},
                               "secondary":{"windowDurationMins":10080,"usedPercent":50}}}
                """);
            using var usage = JsonDocument.Parse("""
                {"summary":{"lifetimeTokens":2000},"dailyUsageBuckets":[
                  {"startDate":"2026-11-10","tokens":200},{"startDate":"2026-11-09","tokens":100}]}
                """);
            var snapshot = AccountUsageSnapshot.Parse(default, quota.RootElement, usage.RootElement);
            Require(snapshot.FiveHourRemaining == 75 && snapshot.WeeklyRemaining == 50 &&
                snapshot.FiveHourResetAt == 7 && snapshot.LifetimeTokens == 2000 &&
                snapshot.LatestDailyTokens == 200, "quota windows and daily token buckets stay distinct");

            Console.WriteLine("Account smoke passed (preflight rejection, migration requeue, round trip, recovery, and usage parsing).");
            return 0;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Task<AccountInspection> Verify(SavedCodexAccount account, bool refresh, bool usage) =>
        Task.FromResult(new AccountInspection(account,
            new AccountUsageSnapshot(DateTimeOffset.UtcNow, "test", 90, null, 80, null, null, []), null));

    private static JsonNode Target(JsonNode root) => root[Renderer]!["inactive"]!["slot-b"]!["globals"]!;

    private static byte[] Auth(CodexIdentity identity)
    {
        var claims = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["email"] = identity.UserId + "@example.invalid",
            ["https://api.openai.com/auth"] = new
            {
                chatgpt_account_id = identity.AccountId, chatgpt_user_id = identity.UserId,
            },
        });
        var payload = Convert.ToBase64String(claims).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            auth_mode = "chatgpt",
            tokens = new { id_token = $"header.{payload}.signature", access_token = "fake-access", refresh_token = "fake-refresh" },
        });
    }

    private static JsonObject Fixture() => JsonNode.Parse("""
        {
          "electron-persisted-atom-state": {"local-document-identity":{"accountId":"account-a","userId":"user-a"}},
          "local-projects": {"project-a":{"id":"project-a","name":"A","createdAt":1,"updatedAt":1}},
          "thread-project-assignments": {
            "thread-a":{"projectKind":"local","projectId":"project-a"},
            "cloud-a":{"projectKind":"cloud","projectId":"cloud-project"}
          },
          "sidebar-project-thread-orders":{"project-a":{"threadIds":["thread-a"]}},
          "thread-project-membership-host-ids":{"thread-a":"local"},
          "app-server-project-id-by-legacy-project-id-by-host":{"host":{"project-a":"native-a"}},
          "precise-number":90071992547409931234567,
          "selected-model":"model-a",
          "electron-account-renderer-state-v1": {
            "owner": "slot-a",
            "inactive": {
              "slot-b": {
                "atoms": {"local-document-identity":{"accountId":"account-b","userId":"user-b"}},
                "globals": {
                  "local-projects": {"project-b":{"id":"project-b","name":"B","createdAt":2,"updatedAt":2}},
                  "project-order": ["project-b"],
                  "thread-project-assignments": {"thread-b":{"projectKind":"local","projectId":"project-b"}},
                  "app-server-projects-migration-by-host":{"host":{"version":1,"projectsMigrated":true,"threadAssignmentsMigrated":true,"threadAssignmentsReadMigrated":true,"pendingThreadAssignmentIds":["already-pending"]}},
                  "selected-model":"model-b"
                }
              }
            }
          }
        }
        """)!.AsObject();

    private static void Unchanged(string path, byte[] expected) =>
        Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(expected), Path.GetFileName(path) + " expected bytes");

    private static void Reject(Action action)
    {
        try
        {
            action();
        }
        catch (AccountSwitchException)
        {
            return;
        }

        throw new InvalidOperationException("Expected account switch to stop.");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException("Account smoke failed: " + label);
    }
}
