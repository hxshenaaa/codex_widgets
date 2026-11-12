using Microsoft.Data.Sqlite;

namespace CodexUsageWidgetProvider;

internal sealed record LocalThreadChange(string Id, string? ProjectId, string TargetProjectId);

// Native projects are shared by the local app-server. Legacy renderer slots
// still hold assignments which have not reached threads.project_id. Preserve
// the original creator identity, IDs, history, token counters and timestamps.
internal sealed class CodexLocalThreads(string database, CodexIdentity target, LocalThreadChange[] changes,
    string[]? projectIds = null, string[]? missingThreadIds = null)
{
    public LocalThreadChange[] Changes { get; } = changes;
    public CodexIdentity Target { get; } = target;
    public string[] ProjectIds { get; } = projectIds ?? [];
    public string[] MissingThreadIds { get; } = missingThreadIds ?? [];

    public static CodexLocalThreads? Capture(string home, byte[] state, CodexIdentity source, CodexIdentity target)
    {
        var database = Path.Combine(home, "state_5.sqlite");
        if (!File.Exists(database)) return null;
        if ((File.GetAttributes(database) & FileAttributes.ReparsePoint) != 0)
            throw new AccountSwitchException("不支持链接形式的会话数据库，切换已停止。");
        using var connection = Open(database, readOnly: true);
        using var transaction = connection.BeginTransaction();
        var assignments = CodexProjectState.LocalAssignments(state, source);
        var projectIds = CodexProjectState.LocalProjectIds(state, source);
        var projects = new HashSet<string>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id FROM projects";
            using var reader = command.ExecuteReader();
            while (reader.Read()) projects.Add(reader.GetString(0));
        }
        var changes = new List<LocalThreadChange>();
        var missing = assignments.Keys.ToHashSet(StringComparer.Ordinal);
        if (projectIds.Any(id => !projects.Contains(id)))
            throw new AccountSwitchException("本地项目尚未迁移到原生数据库。请先用原账号打开 Codex 完成项目加载。");
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id, project_id FROM threads";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string? Value(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                var id = reader.GetString(0);
                var project = Value(1);
                if (!assignments.TryGetValue(id, out var nextProject)) continue;
                missing.Remove(id);
                if (!projects.Contains(nextProject))
                    throw new AccountSwitchException("本地项目尚未迁移到原生数据库。请先用原账号打开 Codex 完成项目加载。");
                if (project is not null && project != nextProject)
                    throw new AccountSwitchException("桌面项目索引与原生会话归属冲突，切换已停止。");
                changes.Add(new LocalThreadChange(id, project, nextProject));
            }
        }
        return new CodexLocalThreads(database, target, changes.ToArray(), projectIds, missing.Order(StringComparer.Ordinal).ToArray());
    }

    public void Apply() => Write(rollback: false);
    public void Rollback() => Write(rollback: true);

    private void Write(bool rollback)
    {
        var pending = Changes.Where(row => row.ProjectId != row.TargetProjectId).ToArray();
        if (pending.Length == 0) return;

        using var connection = Open(database, readOnly: false);
        using var transaction = connection.BeginTransaction();
        foreach (var row in pending)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE threads SET project_id = $project WHERE id = $id AND project_id IS $oldProject
                """;
            command.Parameters.AddWithValue("$id", row.Id);
            command.Parameters.AddWithValue("$project", Db(rollback ? row.ProjectId : row.TargetProjectId));
            command.Parameters.AddWithValue("$oldProject", Db(rollback ? row.TargetProjectId : row.ProjectId));
            if (command.ExecuteNonQuery() != 1)
                throw new AccountSwitchException("会话数据库在切换期间发生变化，已停止操作。");
        }
        transaction.Commit();
    }

    public void Verify()
    {
        using var connection = Open(database, readOnly: true);
        using var transaction = connection.BeginTransaction();
        foreach (var row in Changes)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT count(*) FROM threads WHERE id = $id AND project_id IS $project
                """;
            command.Parameters.AddWithValue("$id", row.Id);
            command.Parameters.AddWithValue("$project", Db(row.TargetProjectId));
            if (Convert.ToInt64(command.ExecuteScalar()) != 1)
                throw new AccountSwitchException("切换后的会话归属或项目关联未通过核对。");
        }
    }

    private static object Db(string? value) => value is null ? DBNull.Value : value;

    private static SqliteConnection Open(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false, DefaultTimeout = 5,
        }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }
}
