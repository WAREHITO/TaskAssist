using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaskAssist.Core;

public interface IRepository
{
    Snapshot Load();
    void Save(Snapshot before, Snapshot after);
}

// No plaintext search index or diagnostic payload is written to disk.
public sealed class SqliteRepository : IRepository, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    private Snapshot? cache;
    private readonly string profileId;
    private readonly bool readOnly;
    public string FilePath { get; }
    public Action? BeforeCommit { get; set; } // Failure injection; unset in the desktop composition root.
    public SqliteRepository(string path, string profileId = "demo", bool readOnly = false)
    {
        this.profileId = profileId; this.readOnly = readOnly;
        Policy.Validate(new Snapshot { ProfileId = profileId });
        FilePath = Path.GetFullPath(path);
        if (FilePath.StartsWith(@"\\", StringComparison.Ordinal)) throw new RuleException("共有ドライブは保存先にできません。");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = FilePath, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false, ForeignKeys = true, DefaultTimeout = 5 }.ToString());
        try
        {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version is not (0 or 1 or 2 or 3 or AppRelease.Schema)) throw new RuleException("この版で開けない記録形式です。元の記録は保持しています。");
        if (version != 0)
        {
            command.CommandText = "SELECT payload FROM metadata WHERE id=1";
            var metadata = Unprotect<Snapshot>((byte[])(command.ExecuteScalar() ?? throw new RuleException("管理情報がありません。")));
            EnsureProfile(metadata);
        }
        if (readOnly)
        {
            if (version is not (3 or AppRelease.Schema)) throw new RuleException("閲覧できない記録形式です。");
            return;
        }
        // Guard old applications from silently discarding newly introduced JSON fields.
        // Back up before changing the format marker; a failed backup aborts the upgrade.
        if (version is 1 or 2 or 3)
        {
            try { Backup(Path.Combine(Path.GetDirectoryName(FilePath)!, "backups")); }
            catch { connection.Dispose(); throw; }
        }
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS metadata(id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL, payload BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS tasks(id TEXT PRIMARY KEY, profile TEXT NOT NULL, status INTEGER NOT NULL, payload BLOB NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS one_working ON tasks(profile) WHERE status=1;
            CREATE TABLE IF NOT EXISTS inbox(id TEXT PRIMARY KEY, source_key TEXT UNIQUE NOT NULL, payload BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS history(id TEXT PRIMARY KEY, payload BLOB NOT NULL);
            PRAGMA user_version=4;
            """;
        command.ExecuteNonQuery();
        command.CommandText = "INSERT OR IGNORE INTO metadata VALUES(1,0,$data)";
        command.Parameters.AddWithValue("$data", Protect(new Snapshot { ProfileId = profileId, DemoConnected = profileId == "demo" }));
        command.ExecuteNonQuery();
        }
        catch { connection.Dispose(); throw; }
    }
    private void EnsureProfile(Snapshot state)
    {
        if (state.ProfileId != profileId) throw new RuleException("保存記録の所属が一致しません。異なる所属や架空データからの復元はできません。");
    }
    private static byte[] Protect<T>(T value) => ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value),
        Encoding.UTF8.GetBytes("TaskAssist-demo-schema-1"), DataProtectionScope.CurrentUser);
    private static T Unprotect<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(ProtectedData.Unprotect(bytes,
        Encoding.UTF8.GetBytes("TaskAssist-demo-schema-1"), DataProtectionScope.CurrentUser))
        ?? throw new RuleException("保存記録を読み取れません。元の記録は保持しています。");
    public Snapshot Load()
    {
        lock (gate)
        {
            using var tx = connection.BeginTransaction(deferred: true);
            using var query = connection.CreateCommand(); query.Transaction = tx;
            query.CommandText = "SELECT revision,payload FROM metadata WHERE id=1";
            using var reader = query.ExecuteReader();
            if (!reader.Read()) throw new RuleException("記録の管理情報がありません。");
            var revision = reader.GetInt64(0);
            if (cache is not null && cache.Revision == revision) { reader.Close(); tx.Commit(); return Copy.Of(cache); }
            var state = Unprotect<Snapshot>((byte[])reader[1]); state.Revision = revision;
            EnsureProfile(state);
            reader.Close();
            state.Tasks = ReadRows<WorkItem>("tasks", tx);
            state.Inbox = ReadRows<InboxItem>("inbox", tx);
            state.Events = ReadRows<ChangeEvent>("history", tx);
            Policy.Validate(state);
            tx.Commit(); cache = Copy.Of(state); return state;
        }
    }
    private List<T> ReadRows<T>(string table, SqliteTransaction tx)
    {
        using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = $"SELECT payload FROM {table}"; // table is internal, never user input.
        using var reader = command.ExecuteReader(); var values = new List<T>();
        while (reader.Read()) values.Add(Unprotect<T>((byte[])reader[0]));
        return values;
    }
    public void Save(Snapshot before, Snapshot after)
    {
        lock (gate)
        {
            if (readOnly) throw new RuleException("旧所属の記録は閲覧専用です。");
            EnsureProfile(before); EnsureProfile(after);
            Policy.Validate(after);
            using var tx = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = tx;
            command.CommandText = "SELECT revision FROM metadata WHERE id=1";
            if (Convert.ToInt64(command.ExecuteScalar()) != before.Revision)
                throw new RuleException("別の変更が保存されています。画面を更新してから操作してください。");
            // Release the previous working slot before assigning the next, within the same transaction.
            foreach (var old in before.Tasks.Where(t => t.Status == WorkStatus.Working && after.Tasks.Any(n => n.Id == t.Id && n.Status != WorkStatus.Working)))
                PutTask(after.Tasks.Single(t => t.Id == old.Id), tx);
            var oldTasks = before.Tasks.ToDictionary(t => t.Id);
            foreach (var task in after.Tasks)
                if (!oldTasks.TryGetValue(task.Id, out var old) || Copy.Json(old) != Copy.Json(task)) PutTask(task, tx);
            var oldInbox = before.Inbox.ToDictionary(t => t.Id);
            foreach (var mail in after.Inbox)
                if (!oldInbox.TryGetValue(mail.Id, out var old) || Copy.Json(old) != Copy.Json(mail))
                {
                    using var put = connection.CreateCommand(); put.Transaction = tx;
                    put.CommandText = "INSERT INTO inbox VALUES($id,$key,$data) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
                    put.Parameters.AddWithValue("$id", mail.Id); put.Parameters.AddWithValue("$key", mail.SourceKey);
                    put.Parameters.AddWithValue("$data", Protect(mail)); put.ExecuteNonQuery();
                }
            var oldEvents = before.Events.ToDictionary(e => e.Id);
            foreach (var ev in after.Events)
                if (!oldEvents.TryGetValue(ev.Id, out var old) || Copy.Json(old) != Copy.Json(ev))
                {
                    using var put = connection.CreateCommand(); put.Transaction = tx;
                    put.CommandText = "INSERT INTO history VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
                    put.Parameters.AddWithValue("$id", ev.Id); put.Parameters.AddWithValue("$data", Protect(ev)); put.ExecuteNonQuery();
                }
            var metadata = new Snapshot { ProfileId = profileId, Revision = before.Revision + 1, SavedAt = after.SavedAt,
                LastScan = after.LastScan, DemoConnected = after.DemoConnected, TeamRoster = [.. after.TeamRoster], Automation = Copy.Of(after.Automation) };
            command.CommandText = "UPDATE metadata SET revision=$revision,payload=$data WHERE id=1";
            command.Parameters.AddWithValue("$revision", metadata.Revision);
            command.Parameters.AddWithValue("$data", Protect(metadata)); command.ExecuteNonQuery();
            BeforeCommit?.Invoke();
            tx.Commit(); after.Revision = metadata.Revision; cache = Copy.Of(after);
        }
    }
    private void PutTask(WorkItem task, SqliteTransaction tx)
    {
        using var put = connection.CreateCommand(); put.Transaction = tx;
        put.CommandText = "INSERT INTO tasks VALUES($id,$profile,$status,$data) ON CONFLICT(id) DO UPDATE SET status=excluded.status,payload=excluded.payload";
        put.Parameters.AddWithValue("$id", task.Id); put.Parameters.AddWithValue("$profile", task.Profile);
        put.Parameters.AddWithValue("$status", (int)task.Status); put.Parameters.AddWithValue("$data", Protect(task)); put.ExecuteNonQuery();
    }
    public string Backup(string folder)
    {
        lock (gate)
        {
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
            using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
            target.Open(); connection.BackupDatabase(target);
            using var query = target.CreateCommand(); query.CommandText = "PRAGMA integrity_check";
            if (!Equals(query.ExecuteScalar(), "ok")) throw new RuleException("退避の整合性検査に失敗しました。");
            query.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; query.ExecuteNonQuery();
            query.CommandText = "PRAGMA user_version"; var schema = Convert.ToInt32(query.ExecuteScalar());
            File.WriteAllText(file + ".manifest.json", JsonSerializer.Serialize(new
            { appVersion = AppRelease.Version, schema, at = DateTimeOffset.UtcNow, protection = "DPAPI CurrentUser payload", integrity = "ok", restorePolicy = "disable external automation" }));
            return file;
        }
    }
    public static void RestoreToNew(string backup, string destination, string profileId = "demo", bool forLegacyApplication = false)
    {
        if (File.Exists(destination)) throw new RuleException("既存データへは上書き復元しません。");
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        source.Open(); using var check = source.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
        if (!Equals(check.ExecuteScalar(), "ok")) throw new RuleException("退避が破損しています。");
        check.CommandText = "PRAGMA user_version";
        var sourceVersion = Convert.ToInt32(check.ExecuteScalar());
        if (sourceVersion is not (1 or 2 or 3 or AppRelease.Schema)) throw new RuleException("未対応の記録形式です。");
        if (forLegacyApplication && sourceVersion != 3) throw new RuleException("旧版へ戻すには更新前の記録形式3の退避を選んでください。");
        check.CommandText = "SELECT payload FROM metadata WHERE id=1";
        var state = Unprotect<Snapshot>((byte[])(check.ExecuteScalar() ?? throw new RuleException("管理情報がありません。")));
        if (state.ProfileId != profileId) throw new RuleException("別の所属や架空データの退避は復元できません。");
        // Validate every protected row before creating any replacement database.
        foreach (var table in new[] { "metadata", "tasks", "inbox", "history" })
        {
            check.CommandText = $"SELECT payload FROM {table}"; using var reader = check.ExecuteReader();
            while (reader.Read()) _ = Unprotect<JsonElement>((byte[])reader[0]);
        }
        List<T> Rows<T>(string table)
        {
            check.CommandText = $"SELECT payload FROM {table}"; using var reader = check.ExecuteReader(); var rows = new List<T>();
            while (reader.Read()) rows.Add(Unprotect<T>((byte[])reader[0])); return rows;
        }
        state.Tasks = Rows<WorkItem>("tasks"); state.Inbox = Rows<InboxItem>("inbox"); state.Events = Rows<ChangeEvent>("history");
        Policy.Validate(state);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        target.Open(); source.BackupDatabase(target);
        target.Close();
        if (forLegacyApplication) return; // Verified schema 3 copy for 0.4; do not migrate it back to 4.
        using var restored = new SqliteRepository(destination, profileId);
        var before = restored.Load(); var after = Copy.Of(before);
        after.Automation.RestoreGeneration++; TaskService.Suspend(after);
        after.Automation.Connector.Health = "復元後は自動処理を停止しています。対象範囲と実行結果を確認してください。";
        after.SavedAt = DateTimeOffset.UtcNow; restored.Save(before, after);
    }
    public void Dispose() { lock (gate) connection.Dispose(); }
}

public sealed class InstanceLease : IDisposable
{
    private readonly FileStream stream;
    public InstanceLease(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        stream = new FileStream(Path.Combine(dataFolder, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public void Dispose() => stream.Dispose();
}
