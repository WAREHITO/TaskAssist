using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaskAssist.Core;

public enum HandoverChoice { ContinueSeparately, HandOver, Cancel }
public sealed record HandoverDecision(string TaskId, int Version, HandoverChoice Choice, string Note);
public sealed record WorkProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ArchivedAt { get; set; }
    public List<HandoverDecision> Decisions { get; set; } = [];
}
public sealed class ProfileCatalog
{
    public int Format { get; set; } = 1;
    public string ActiveId { get; set; } = "";
    public List<WorkProfile> Profiles { get; set; } = [];
    public WorkProfile Active => Profiles.Single(p => p.Id == ActiveId);
}

// Names and handover notes stay encrypted; directory names contain random identifiers only.
// The application holds one root InstanceLease for the whole catalog session.
public sealed class ProfileStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    private string CatalogPath => Path.Combine(Root, "profiles.dat");
    private string FirstPreparation => Path.Combine(Root, "first-profile.pending");
    public Action? BeforePublish { get; set; } // Test-only fault injection.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TaskAssist-local-profiles-1");

    public ProfileCatalog Load()
    {
        if (!File.Exists(CatalogPath))
        {
            if (Directory.Exists(Path.Combine(Root, "profiles")) && Directory.EnumerateFileSystemEntries(Path.Combine(Root, "profiles")).Any() && RecoverFirst() is null)
                throw new RuleException("所属の管理情報が見つかりません。既存記録を空の状態で上書きせず停止しました。（PROFILE-01）");
            return new();
        }
        var catalog = JsonSerializer.Deserialize<ProfileCatalog>(ProtectedData.Unprotect(File.ReadAllBytes(CatalogPath), Entropy, DataProtectionScope.CurrentUser))
            ?? throw new RuleException("所属の管理情報を読み取れません。");
        Validate(catalog); return catalog;
    }
    private static void Validate(ProfileCatalog catalog)
    {
        if (catalog.Format != 1 || catalog.Profiles.Count == 0 || catalog.Profiles.Select(p => p.Id).Distinct().Count() != catalog.Profiles.Count ||
            catalog.Profiles.Count(p => p.Id == catalog.ActiveId && p.ArchivedAt is null) != 1 || catalog.Profiles.Count(p => p.ArchivedAt is null) != 1)
            throw new RuleException("所属の管理情報が不正です。既存記録を保持して停止しました。");
        foreach (var p in catalog.Profiles) { ValidateId(p.Id); ValidateName(p.Name); }
    }
    private static void ValidateId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new RuleException("所属の識別情報が不正です。");
    }
    private static string ValidateName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 120) throw new RuleException("所属の表示名を1〜120文字で入力してください。局・課・担当など、分かる呼び方で構いません。");
        return name;
    }
    public string Folder(string id) { ValidateId(id); return Path.Combine(Root, "profiles", id); }
    public string DatabasePath(string id)
    {
        var folder = Folder(id); var pointer = Path.Combine(folder, "active-data.txt");
        var name = File.Exists(pointer) ? File.ReadAllText(pointer).Trim() : "tasks.db";
        if (Path.GetFileName(name) != name || !name.EndsWith(".db", StringComparison.Ordinal) || name.Contains(':'))
            throw new RuleException("保存先の管理情報が不正です。");
        var path = Path.Combine(folder, name);
        if (!File.Exists(path)) throw new RuleException("所属の保存記録が見つかりません。空の記録へ置換せず停止しました。（PROFILE-02）");
        return path;
    }
    public SqliteRepository Open(string id)
    {
        var profile = Load().Profiles.SingleOrDefault(p => p.Id == id) ?? throw new RuleException("所属が見つかりません。");
        return new(DatabasePath(id), id, profile.ArchivedAt is not null);
    }
    public WorkProfile CreateFirst(string name)
    {
        if (Load().Profiles.Count > 0) throw new RuleException("所属は既に登録されています。");
        name = ValidateName(name);
        var profile = RecoverFirst() ?? new WorkProfile { Name = name };
        profile.Name = name;
        if (!File.Exists(FirstPreparation))
        {
            Directory.CreateDirectory(Root);
            var temporary = FirstPreparation + "." + Guid.NewGuid().ToString("N");
            var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(profile), Entropy, DataProtectionScope.CurrentUser);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, FirstPreparation);
        }
        CreateEmpty(profile); Publish(new() { ActiveId = profile.Id, Profiles = [profile] }); return profile;
    }
    private WorkProfile? RecoverFirst()
    {
        // Recovery is limited to the app's explicitly prepared, still-empty first DB.
        // Never infer ownership from an arbitrary database or erase a failed attempt.
        if (!File.Exists(FirstPreparation) || File.Exists(CatalogPath + ".previous")) return null;
        var profile = JsonSerializer.Deserialize<WorkProfile>(ProtectedData.Unprotect(File.ReadAllBytes(FirstPreparation), Entropy, DataProtectionScope.CurrentUser))
            ?? throw new RuleException("初回設定の準備情報を読み取れません。");
        ValidateId(profile.Id); ValidateName(profile.Name);
        var container = Path.Combine(Root, "profiles");
        if (Directory.Exists(container) && Directory.EnumerateFileSystemEntries(container).Any(p => !string.Equals(Path.GetFullPath(p), Folder(profile.Id), StringComparison.OrdinalIgnoreCase))) return null;
        var db = Path.Combine(Folder(profile.Id), "tasks.db");
        if (Directory.Exists(Folder(profile.Id)) && Directory.EnumerateFileSystemEntries(Folder(profile.Id)).Any(p => Path.GetFileName(p) is not ("tasks.db" or "tasks.db-wal" or "tasks.db-shm"))) return null;
        if (Directory.Exists(Folder(profile.Id)) && !File.Exists(db)) return null;
        if (File.Exists(db))
        {
            using var repository = new SqliteRepository(db, profile.Id, readOnly: true);
            var state = repository.Load();
            if (state.Revision != 0 || state.Tasks.Count != 0 || state.Inbox.Count != 0 || state.Events.Count != 0 || state.TeamRoster.Count != 0) return null;
        }
        return profile;
    }
    public void RenameActive(string name)
    {
        var catalog = Load(); catalog.Active.Name = ValidateName(name); Publish(catalog);
    }
    public WorkProfile Transfer(string expectedActive, long expectedRevision, string name, IEnumerable<HandoverDecision> decisions)
    {
        var catalog = Load();
        if (catalog.ActiveId != expectedActive) throw new RuleException("所属が変更されています。画面を開き直してください。");
        var next = new WorkProfile { Name = ValidateName(name) };
        using var old = Open(expectedActive); var snapshot = old.Load();
        if (snapshot.Revision != expectedRevision) throw new RuleException("案件が更新されています。異動の確認をやり直してください。");
        if (snapshot.Automation.Connector.Enabled || snapshot.Automation.DigestEnabled || snapshot.Automation.Recurrences.Any(r => r.Enabled) ||
            snapshot.Automation.Jobs.Any(j => j.State is JobState.Running or JobState.OutcomeUnknown or JobState.Authorized))
            throw new RuleException("先に自動処理を停止し、実行結果が未確認の処理を確認してください。");
        var unfinished = snapshot.Tasks.Where(t => !t.Closed).ToDictionary(t => t.Id);
        var chosen = decisions.ToList();
        if (chosen.Count != unfinished.Count || chosen.Select(d => d.TaskId).Distinct().Count() != chosen.Count ||
            chosen.Any(d => !unfinished.TryGetValue(d.TaskId, out var t) || t.Version != d.Version || !Enum.IsDefined(d.Choice) || string.IsNullOrWhiteSpace(d.Note) || d.Note.Length > 2000))
            throw new RuleException("未完了の各案件について、異動後の扱いと引継ぎ先・理由・次の対応を記録してください。");
        old.Backup(Path.Combine(Folder(expectedActive), "backups"));
        CreateEmpty(next);
        catalog.Active.ArchivedAt = DateTimeOffset.UtcNow; catalog.Active.Decisions = chosen;
        catalog.Profiles.Add(next); catalog.ActiveId = next.Id;
        Publish(catalog); // Commit point. Until this succeeds, the old profile remains active.
        return next;
    }
    private void CreateEmpty(WorkProfile profile)
    {
        using var repository = new SqliteRepository(Path.Combine(Folder(profile.Id), "tasks.db"), profile.Id);
        var state = repository.Load();
        if (state.IsDemo || state.Tasks.Count != 0 || state.Inbox.Count != 0 || state.TeamRoster.Count != 0) throw new RuleException("空の所属記録を準備できませんでした。");
    }
    private void Publish(ProfileCatalog catalog)
    {
        Validate(catalog); Directory.CreateDirectory(Root);
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(catalog), Entropy, DataProtectionScope.CurrentUser);
        var temporary = CatalogPath + "." + Guid.NewGuid().ToString("N") + ".pending";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        BeforePublish?.Invoke();
        if (File.Exists(CatalogPath)) File.Replace(temporary, CatalogPath, CatalogPath + ".previous");
        else File.Move(temporary, CatalogPath);
    }
}
