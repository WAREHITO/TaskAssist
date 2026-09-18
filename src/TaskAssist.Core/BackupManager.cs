using System.Text.Json;

namespace TaskAssist.Core;

public static class BackupManager
{
    public static void Daily(SqliteRepository repository, TaskService service, string folder, IClock clock)
    {
        var state = service.Read(); var a = state.Automation;
        if (!a.BackupsEnabled || a.LastBackupDay == Japan.Day(clock.Now)) return;
        var root = Path.GetFullPath(Path.Combine(folder,"backups","daily")); Directory.CreateDirectory(root); SafeFiles.ValidateDirectory(root);
        var file = repository.Backup(root);
        // A separate application-owned catalog limits retention deletion to backups this feature created.
        var catalogPath = Path.Combine(root,"daily-catalog.json");
        var entries = File.Exists(catalogPath) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(catalogPath)) ?? [] : [];
        if (entries.Any(e => Path.GetFileName(e) != e || !e.StartsWith("backup-",StringComparison.Ordinal) || !e.EndsWith(".db",StringComparison.Ordinal))) throw new RuleException("退避管理情報を確認できません。古い退避は削除しません。");
        entries.Add(Path.GetFileName(file));
        foreach (var old in entries.Take(Math.Max(0,entries.Count-a.BackupGenerations)).ToList())
        {
            var path = Path.GetFullPath(Path.Combine(root,old));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new RuleException("退避先が範囲外です。");
            foreach (var candidate in new[] { path, path + ".manifest.json" })
                if (File.Exists(candidate)) { if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) throw new RuleException("退避のリンクは削除しません。"); File.Delete(candidate); }
            entries.Remove(old);
        }
        File.WriteAllText(catalogPath + ".new", JsonSerializer.Serialize(entries)); File.Move(catalogPath + ".new",catalogPath,true);
        service.BackupCompleted();
    }
}
