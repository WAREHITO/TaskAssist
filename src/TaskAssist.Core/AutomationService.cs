using System.Net.Mail;

namespace TaskAssist.Core;

public static class AutomationPolicy
{
    public static void Validate(Snapshot s)
    {
        var a = s.Automation;
        if (a.CapacityMinutes is < 1 or > 1440 || a.BackupGenerations is < 1 or > 30 || s.Tasks.Any(t => t.EstimatedMinutes is <= 0 or > 100000)) throw new RuleException("時間・バックアップ世代数の範囲を確認してください。");
        if (a.Connector.Enabled && (a.Connector.ConsentAt is null || a.Connector.Folders.Count == 0)) throw new RuleException("読取り範囲の確認が必要です。");
        if (a.Rules.GroupBy(r => r.Id).Any(g => g.Count() > 1) || a.Recurrences.GroupBy(r => r.Id).Any(g => g.Count() > 1) ||
            a.Occurrences.GroupBy(o => (o.RuleId, o.Day)).Any(g => g.Count() > 1) || a.Jobs.GroupBy(j => j.UniqueKey).Any(g => g.Count() > 1)) throw new RuleException("同じ処理が重複しています。");
        foreach (var rule in a.Rules)
            if (string.IsNullOrWhiteSpace(rule.Name) || string.IsNullOrWhiteSpace(rule.Sender) || string.IsNullOrWhiteSpace(rule.SubjectPrefix) || rule.ExactBodyTemplate.Length > 64000 ||
                rule.ExactBodyTemplate.Split("{date}", StringSplitOptions.None).Length > 2 || !Enum.IsDefined(rule.Action) || (rule.Enabled && rule.ApprovedAt is null)) throw new RuleException("規則には名前・差出人・件名条件と承認が必要です。日付置換は1箇所までです。");
        foreach (var r in a.Recurrences)
            if (string.IsNullOrWhiteSpace(r.Title) || r.Title.Length > 500 || r.MonthDay is < 1 or > 31 || !Enum.IsDefined(r.Cadence) || !Enum.IsDefined(r.CatchUp)) throw new RuleException("繰り返しの用件と周期を確認してください。");
        foreach (var job in a.Jobs)
        {
            if (job.ProfileId != s.ProfileId || !Enum.IsDefined(job.Kind) || !Enum.IsDefined(job.State) || string.IsNullOrWhiteSpace(job.UniqueKey)) throw new RuleException("外部処理の所属・状態が不正です。");
            if (job.Kind == JobKind.SelfDigest && (!SingleAddress(job.Recipient) || !string.Equals(job.Account, job.Recipient, StringComparison.OrdinalIgnoreCase))) throw new RuleException("通知は明示確認した送信元本人の1アドレスだけです。");
        }
        if (a.DigestEnabled && (!SingleAddress(a.DigestRecipient) || !string.Equals(a.DigestAccount, a.DigestRecipient, StringComparison.OrdinalIgnoreCase))) throw new RuleException("本人の送信元と通知先を一致させてください。");
        if (a.Workdays.Distinct().Count() != a.Workdays.Count || a.Workdays.Any(d => !Enum.IsDefined(d))) throw new RuleException("業務日設定が不正です。");
    }
    public static bool SingleAddress(string text) => MailAddress.TryCreate(text, out var address) && address.Address == text && !text.Contains(',') && !text.Contains(';');
    public static List<DateOnly> DueDates(RecurrenceRule r, DateOnly today)
    {
        var from = r.Through?.AddDays(1) ?? r.StartOn;
        if (today.DayNumber - from.DayNumber > 3660) throw new RuleException("10年を超える未処理期間です。開始日を確認してください。");
        var dates = new List<DateOnly>();
        for (var day = from; day <= today && dates.Count < 3661; day = day.AddDays(1))
            if (day >= r.StartOn && (r.Cadence == RecurrenceCadence.Daily || r.Cadence == RecurrenceCadence.Weekly && day.DayOfWeek == r.StartOn.DayOfWeek ||
                r.Cadence == RecurrenceCadence.Monthly && day.Day == Math.Min(r.MonthDay, DateTime.DaysInMonth(day.Year, day.Month)))) dates.Add(day);
        return dates;
    }
    public static int PendingCount(RecurrenceRule r, DateOnly today)
    { try { return DueDates(r,today).Count; } catch (RuleException) { return -1; } catch (ArgumentOutOfRangeException) { return -1; } }
    public static List<InboxItem> PendingDeadlineSources(Snapshot state, DateTimeOffset now)
    {
        var today = Japan.Day(now);
        var unknownSources = state.Tasks.Where(t => !t.Closed && t.Deadline.Kind == DeadlineKind.Unknown).SelectMany(t => t.SourceIds).ToHashSet();
        return state.Inbox.Where(m => !m.Purged && m.Status != IntakeStatus.Ignored && (m.Pending || unknownSources.Contains(m.Id)) &&
            m.DeadlineHints.Any(h => h.Day is {} day && day <= today && (h.EndDay is null || h.EndDay >= today))).ToList();
    }
    public static List<string> Conflicts(Snapshot state, DateTimeOffset now)
    {
        var open = state.Tasks.Where(t => !t.Closed).ToList(); var result = new List<string>();
        foreach (var group in open.Where(t => t.Deadline.Day is not null).GroupBy(t => t.Deadline.Day).Where(g => g.Count() > 1))
            result.Add($"要調整：{group.Key:yyyy/MM/dd}の期限が{group.Count()}件。両立可否は未確認です。");
        var today = open.Where(t => t.PlannedOn == Japan.Day(now) || t.Deadline.Day == Japan.Day(now)).ToList();
        var minutes = today.Sum(t => t.EstimatedMinutes ?? 0);
        if (minutes > state.Automation.CapacityMinutes) result.Add($"設定した時間では超過：入力済み{minutes}分／利用可能{state.Automation.CapacityMinutes}分。");
        else if (today.Count > 1 && today.Any(t => t.EstimatedMinutes is null)) result.Add("両立可否未確認：所要時間が未入力の仕事があります（0分として判定していません）。");
        if (open.Any(t => t.Status == WorkStatus.Working) && open.Any(t => t.UrgentConfirmed && t.Status != WorkStatus.Working)) result.Add("要調整：現在作業とは別に、確認済みの緊急案件があります。作業は切り替えていません。");
        return result;
    }
}

public sealed partial class TaskService
{
    public void SaveRecurrence(RecurrenceRule proposed) => Change("繰り返し規則を確認して保存", s =>
    {
        if (proposed.StartOn < Japan.Day(clock.Now).AddYears(-10) || proposed.StartOn > Japan.Day(clock.Now).AddYears(10))
            throw new RuleException("繰り返しの開始日は前後10年以内にしてください。");
        var old = s.Automation.Recurrences.SingleOrDefault(r => r.Id == proposed.Id);
        if (old is not null && old.Version != proposed.Version) throw new RuleException("繰り返し規則が更新されています。");
        var rule = Copy.Of(proposed); if (old is not null) { s.Automation.RecurrenceHistory.Add(Copy.Of(old)); rule.Through = old.Through; rule.Version++; s.Automation.Recurrences.Remove(old); }
        s.Automation.Recurrences.Add(rule);
    }, false);
    public void DisableRecurrence(string id) => Change("繰り返し規則を停止", s => { var r = s.Automation.Recurrences.Single(r => r.Id == id); s.Automation.RecurrenceHistory.Add(Copy.Of(r)); r.Enabled = false; r.Version++; }, false);
    public void GenerateOccurrences(string id, CatchUpChoice? explicitChoice = null) => Change("繰り返しの発生日を処理", s =>
    {
        var a = s.Automation; var r = a.Recurrences.Single(r => r.Id == id);
        if (!r.Enabled) return;
        var dates = AutomationPolicy.DueDates(r, Japan.Day(clock.Now)).Where(d => !a.Occurrences.Any(o => o.RuleId == id && o.Day == d)).ToList();
        var choice = explicitChoice ?? r.CatchUp;
        if (dates.Count > 1 && choice == CatchUpChoice.Ask) throw new RuleException($"停止期間を含む{dates.Count}回分があります。「各回必要」か「最新のみ」を選んでください。");
        foreach (var day in dates)
        {
            string? reason = choice == CatchUpChoice.LatestOnly && day != dates.Last() ? "本人の規則：最新の1回のみ" : null;
            if (r.SkipNonWorkdays && (!a.Workdays.Contains(day.DayOfWeek) || a.DaysOff.Contains(day))) reason = "設定された業務日以外のため省略";
            if (reason is not null) { a.Occurrences.Add(new(id, day, null, reason)); continue; }
            var task = new WorkItem { Profile = s.ProfileId, Title = r.Title, Completion = r.Completion, CreatedAt = clock.Now, PlannedOn = day,
                Note = $"繰り返し {day:yyyy/MM/dd} 発生。月末は当月最終日へ短縮。" + (a.WorkCalendarConfirmed ? "" : "業務日設定は暫定です。") };
            s.Tasks.Add(task); a.Occurrences.Add(new(id, day, task.Id, "生成済み"));
        }
        r.Through = Japan.Day(clock.Now);
    }, false);
    public void ConfigureWorkdays(IEnumerable<DayOfWeek> days, IEnumerable<DateOnly> daysOff, int capacity) => Change("業務日・休暇・利用可能時間を設定", s =>
    { s.Automation.Workdays = days.Distinct().ToList(); s.Automation.DaysOff = daysOff.Distinct().ToList(); s.Automation.WorkCalendarConfirmed = true; s.Automation.CapacityMinutes = capacity; }, false);
    public void ConfigureDigest(bool enabled, string account, string recipient, TimeOnly time) => Change("本人宛て集約通知の範囲を確認", s =>
    { s.Automation.DigestEnabled = enabled; s.Automation.DigestAccount = account.Trim(); s.Automation.DigestRecipient = recipient.Trim(); s.Automation.DigestTime = time; }, false);
    public string? QueueDigest() { string? id = null; Change("本人宛て通知を実行待ちとして保存", s =>
    {
        var a = s.Automation; var now = clock.Now.ToOffset(Japan.Offset); var key = "digest:" + s.ProfileId + ":" + Japan.Day(clock.Now).ToString("yyyy-MM-dd");
        if (!a.DigestEnabled || a.NotificationsPaused || TimeOnly.FromDateTime(now.DateTime) < a.DigestTime || a.Jobs.Any(j => j.UniqueKey == key)) return;
        var digest = DigestText(s, clock.Now); var hash = MailIdentity.Hash(digest + Copy.Json(s.Tasks.OrderBy(t => t.Id).Select(t => new { t.Id, t.Version }).ToList()) + Copy.Json(s.Inbox.OrderBy(m => m.Id).Select(m => new { m.Id,m.Status }).ToList()));
        if (hash == a.LastDigestHash) return;
        var job = new ExternalJob { ProfileId = s.ProfileId, RestoreGeneration = a.RestoreGeneration, Kind = JobKind.SelfDigest, State = JobState.Authorized, UniqueKey = key,
            Account = a.DigestAccount, Recipient = a.DigestRecipient, Subject = "仕事アシストの状況", Body = digest, DigestStateHash = hash, CreatedAt = clock.Now };
        a.Jobs.Add(job); id = job.Id;
    }, false); return id; }
    public static string DigestText(Snapshot s, DateTimeOffset now) => $"仕事アシストの集約通知\n未完了：{s.Tasks.Count(t => !t.Closed)}件\n期限の注意：{Policy.Alerts(s,now).Count}件\n受付の確認待ち：{s.Inbox.Count(m => m.Pending)}件\n本文・添付は転送していません。詳細は仕事アシストで確認してください。";
    public string ProposeAttachment(string mailId, int index, string destinationFolder)
    {
        var id = Guid.NewGuid().ToString("N");
        Change("添付保存の対象と保存先を確認", s =>
        {
            var mail = s.Inbox.Single(m => m.Id == mailId && m.IsOutlook && !m.Purged);
            var attachment = mail.Attachments.Single(a => a.Index == index);
            var path = SafeFiles.AttachmentPath(destinationFolder, attachment.Name, id);
            var key = "attachment:" + mailId + ":" + index + ":" + MailIdentity.Hash(Path.GetFullPath(destinationFolder).ToUpperInvariant());
            if (s.Automation.Jobs.Any(j => j.UniqueKey == key)) throw new RuleException("この添付の保存処理は既に記録されています。実行結果を確認してください。");
            s.Automation.Jobs.Add(new() { Id = id, ProfileId = s.ProfileId, RestoreGeneration = s.Automation.RestoreGeneration, Kind = JobKind.Attachment,
                State = JobState.Authorized, UniqueKey = key, SourceId = mailId, AttachmentIndex = index, AttachmentName = attachment.Name, AttachmentBytes = attachment.Bytes, ExpectedModifiedAt = mail.LastModifiedAt, Destination = path, CreatedAt = clock.Now });
        }, false); return id;
    }
    public string ProposeDraft(string account, string text)
    {
        if (!AutomationPolicy.SingleAddress(account.Trim())) throw new RuleException("下書きを作る送信元の本人アドレスを確認してください。");
        var id = Guid.NewGuid().ToString("N");
        Change("宛先なしのOutlook下書き作成を確認", s => s.Automation.Jobs.Add(new() { Id = id, ProfileId = s.ProfileId, RestoreGeneration = s.Automation.RestoreGeneration,
            Kind = JobKind.OutlookDraft, State = JobState.Authorized, UniqueKey = "draft:" + id, Account = account, Subject = "業務の優先順位・期限・分担の相談", Body = text, CreatedAt = clock.Now }), false); return id;
    }
    public void StartJob(string id) => Change("外部処理を開始（結果未確定）", s =>
    {
        var j = s.Automation.Jobs.Single(j => j.Id == id);
        if (j.State != JobState.Authorized || j.RestoreGeneration != s.Automation.RestoreGeneration || j.ProfileId != s.ProfileId) throw new RuleException("この処理は実行できません。結果未確認の処理は再実行しません。");
        if (j.Kind == JobKind.SelfDigest && (!s.Automation.DigestEnabled || j.Account != s.Automation.DigestAccount || j.Recipient != s.Automation.DigestRecipient)) throw new RuleException("通知の許可が変更されています。");
        j.State = JobState.Running; j.StartedAt = clock.Now;
    }, false);
    public void FinishJob(string id, JobState outcome, string code, string hash = "") => Change("外部処理の結果を記録", s =>
    {
        var j = s.Automation.Jobs.Single(j => j.Id == id);
        if (j.State != JobState.Running || outcome is not (JobState.ConfirmedSuccess or JobState.ConfirmedFailure or JobState.OutcomeUnknown)) throw new RuleException("実行結果の遷移が不正です。");
        j.State = outcome; j.ResultCode = code; j.ResultHash = hash; j.FinishedAt = clock.Now;
        if (j.Kind == JobKind.SelfDigest && outcome == JobState.ConfirmedSuccess) s.Automation.LastDigestHash = j.DigestStateHash;
    }, false);
    public void ConfirmJobResult(string id, bool completed) => Change("本人が外部処理の結果を確認", s =>
    {
        var j = s.Automation.Jobs.Single(j => j.Id == id);
        if (j.State is not (JobState.OutcomeUnknown or JobState.Suspended)) throw new RuleException("この処理は結果確認の対象ではありません。");
        j.State = completed ? JobState.ConfirmedSuccess : JobState.Cancelled; j.ResultCode = "user-confirmed"; j.FinishedAt = clock.Now;
        if (completed && j.Kind == JobKind.SelfDigest) s.Automation.LastDigestHash = j.DigestStateHash;
    }, false);
    public void VerifySavedAttachment(string id) => Change("添付の保存済み結果を検証",s =>
    {
        var j = s.Automation.Jobs.Single(j=>j.Id==id);
        if (j.Kind != JobKind.Attachment || j.State != JobState.OutcomeUnknown) throw new RuleException("結果未確認の添付保存だけを検証できます。");
        j.ResultHash = SafeFiles.VerifyReceipt(j); j.State = JobState.ConfirmedSuccess; j.ResultCode = "file_verified"; j.FinishedAt = clock.Now;
    },false);
    public void RecoverExternalJobs() => Change("前回の未確定外部処理を停止", s =>
    {
        foreach (var j in s.Automation.Jobs)
            if (j.State == JobState.Running) { j.State = JobState.OutcomeUnknown; j.ResultCode = "interrupted"; }
            else if (j.State == JobState.Authorized) { j.State = JobState.Suspended; j.ResultCode = "restart"; }
    }, false);
    public void SuspendAutomation() => Change("異動前に外部処理と繰り返しを停止", s => Suspend(s), false);
    public static void Suspend(Snapshot s)
    {
        s.Automation.Connector.Enabled = false; s.Automation.Connector.Generation++; s.Automation.DigestEnabled = false;
        s.Automation.Connector.Health = "読取り停止中です。停止後のメールは未確認です。";
        foreach (var r in s.Automation.Recurrences) r.Enabled = false;
        foreach (var j in s.Automation.Jobs)
            if (j.State == JobState.Running) j.State = JobState.OutcomeUnknown;
            else if (j.State == JobState.Authorized) j.State = JobState.Suspended;
    }
    public void ConfigureBackups(bool enabled, int generations) => Change("日次バックアップの保持を設定", s => { s.Automation.BackupsEnabled = enabled; s.Automation.BackupGenerations = generations; }, false);
    public void BackupCompleted() => Change("日次バックアップを確認", s => s.Automation.LastBackupDay = Japan.Day(clock.Now), false);
    public void PauseNotifications(bool pause) => Change("通知の割込み設定", s => s.Automation.NotificationsPaused = pause, false);
    public void SaveConsultation(string text) => Change("相談文をローカル保存", s => s.Automation.Consultation = text, false);
    public void PurgeMailBefore(DateTimeOffset cutoff) => Change("確認した期間の保存原文を削除し再取込みを防止", s =>
    {
        if (cutoff > clock.Now) throw new RuleException("未来の日付にはできません。");
        s.Automation.Connector.StartAt = cutoff > s.Automation.Connector.StartAt ? cutoff : s.Automation.Connector.StartAt;
        s.Automation.Connector.Generation++; s.Automation.Connector.Cursors.Clear();
        InboxItem Clean(InboxItem m) => m.ReceivedAt >= cutoff || m.Purged ? m : m with { Subject = "保持期間により原文削除済み", Body = "", Sender = "", InternetId = "", Fingerprint = "", Attachments = [], Locations = [], DeadlineCandidate = "", DeadlineHints = [], LastModifiedAt = null, Purged = true, Status = IntakeStatus.Ignored, Reason = "本人が期間を確認して削除" };
        s.Inbox = s.Inbox.Select(Clean).ToList();
        WorkItem CleanTask(WorkItem task) => RedactRetainedEvidence(s,task);
        s.Tasks = s.Tasks.Select(CleanTask).ToList();
        foreach (var e in s.Events) { foreach (var id in e.Before.Keys.ToArray()) e.Before[id] = CleanTask(e.Before[id]); foreach (var id in e.After.Keys.ToArray()) e.After[id] = CleanTask(e.After[id]); }
        foreach (var e in s.Events) { foreach (var id in e.InboxBefore.Keys.ToArray()) e.InboxBefore[id] = Clean(e.InboxBefore[id]); foreach (var id in e.InboxAfter.Keys.ToArray()) e.InboxAfter[id] = Clean(e.InboxAfter[id]); }
    }, false);
    private static WorkItem RedactRetainedEvidence(Snapshot s, WorkItem task)
    {
        if (s.Inbox.Any(m => m.Purged && (m.Id == task.Deadline.SourceId || task.SourceIds.Contains(m.Id) && task.Deadline.Evidence.StartsWith("承認規則 ",StringComparison.Ordinal))))
            return task with { Deadline = task.Deadline with { Evidence = "原文は保持期間により削除済み（確定した期限は保持）" } };
        return task;
    }
}

public static class SafeFiles
{
    private sealed record Receipt(string JobId, string Hash);
    public static void WriteReceipt(ExternalJob job, string hash)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Receipt(job.Id,hash));
        var encrypted = System.Security.Cryptography.ProtectedData.Protect(bytes,System.Text.Encoding.UTF8.GetBytes(job.Id),System.Security.Cryptography.DataProtectionScope.CurrentUser);
        using var output = new FileStream(job.Destination + ".taskassist-receipt",FileMode.CreateNew,FileAccess.Write,FileShare.None);
        output.Write(encrypted); output.Flush(true);
    }
    public static string VerifyReceipt(ExternalJob job)
    {
        ValidateDirectory(Path.GetDirectoryName(job.Destination)!);
        var receiptPath = job.Destination + ".taskassist-receipt";
        if (!Path.GetFileName(job.Destination).StartsWith(job.Id + "-",StringComparison.Ordinal) || !File.Exists(job.Destination) || !File.Exists(receiptPath) || new FileInfo(receiptPath).Length > 4096 ||
            (File.GetAttributes(job.Destination) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(receiptPath) & FileAttributes.ReparsePoint) != 0)
            throw new RuleException("この処理の保存済み結果を確認できません。再保存せず本人確認を続けてください。");
        var bytes = System.Security.Cryptography.ProtectedData.Unprotect(File.ReadAllBytes(receiptPath),System.Text.Encoding.UTF8.GetBytes(job.Id),System.Security.Cryptography.DataProtectionScope.CurrentUser);
        var receipt = System.Text.Json.JsonSerializer.Deserialize<Receipt>(bytes);
        using var content = File.OpenRead(job.Destination); var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
        if (receipt?.JobId != job.Id || receipt.Hash != hash) throw new RuleException("保存後に内容が変わったか、処理の対応を確認できません。上書きしません。");
        return hash;
    }
    public static bool ApprovedAttachmentMatches(ExternalJob job, string name, long bytes, DateTimeOffset modified) =>
        job.AttachmentName == name && job.AttachmentBytes == bytes && job.ExpectedModifiedAt is {} expected && expected == modified;
    public static void ValidateDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) throw new RuleException("ローカルの絶対パスを選んでください。");
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
            if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new RuleException("存在しない保存先やリンク先には保存できません。");
    }
    public static string AttachmentPath(string folder, string original, string id)
    {
        ValidateDirectory(folder);
        if (!Guid.TryParseExact(id, "N", out _)) throw new RuleException("保存処理の識別子が不正です。");
        var name = new string(original.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && !char.IsControl(c)).ToArray()).Trim().Trim('.');
        if (name.Length == 0) name = "attachment";
        if (name.Length > 100) name = name[..100];
        var path = Path.Combine(Path.GetFullPath(folder), id + "-" + name);
        if (File.Exists(path) || Directory.Exists(path)) throw new RuleException("保存先が既に存在します。上書きしません。");
        return path;
    }
}
