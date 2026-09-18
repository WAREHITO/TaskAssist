using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaskAssist.Core;

public sealed partial class TaskService(IRepository repository, IClock clock)
{
    private readonly object gate = new();
    public Snapshot Read() => repository.Load();
    private void Change(string label, Action<Snapshot> change, bool undoable = true)
    {
        lock (gate)
        {
            var before = repository.Load(); var after = Copy.Of(before);
            change(after); Policy.Validate(after);
            if (Copy.Json(before) == Copy.Json(after)) return;
            var configurationBefore = Configuration(before.Automation); var configurationAfter = Configuration(after.Automation);
            var configChanged = configurationBefore != configurationAfter;
            var ev = new ChangeEvent { Label = label, At = clock.Now, Undoable = undoable,
                ConfigurationBefore = configChanged ? configurationBefore : "", ConfigurationAfter = configChanged ? configurationAfter : "" };
            var tasksBefore = before.Tasks.ToDictionary(t => t.Id);
            var inboxBefore = before.Inbox.ToDictionary(m => m.Id);
            foreach (var task in after.Tasks)
            {
                tasksBefore.TryGetValue(task.Id, out var old);
                if (old is not null && Copy.Json(old) == Copy.Json(task)) continue;
                if (old is not null) { ev.Before[task.Id] = Copy.Of(RedactRetainedEvidence(after,old)); task.Version = old.Version + 1; }
                ev.After[task.Id] = Copy.Of(task);
            }
            foreach (var mail in after.Inbox)
            {
                inboxBefore.TryGetValue(mail.Id, out var old);
                if (old is not null && Copy.Json(old) == Copy.Json(mail)) continue;
                // A retention operation must not copy deleted source text back into its own undo history.
                if (mail.Purged) { mail.Version = (old?.Version ?? 0) + 1; continue; }
                if (old is not null) { ev.InboxBefore[mail.Id] = Copy.Of(old); mail.Version = old.Version + 1; }
                ev.InboxAfter[mail.Id] = Copy.Of(mail);
            }
            after.Events.Add(ev); after.SavedAt = clock.Now; repository.Save(before, after);
        }
    }
    private static string Configuration(AutomationState a) => JsonSerializer.Serialize(new
    {
        読取り = new { a.Connector.Enabled, a.Connector.Generation, a.Connector.ConsentAt, a.Connector.StartAt, a.Connector.Folders },
        規則 = a.Rules, 繰り返し = a.Recurrences.Select(r => r with { Through = null }),
        本人通知 = new { a.DigestEnabled, a.DigestAccount, a.DigestRecipient, a.DigestTime, a.NotificationsPaused },
        業務日 = new { a.Workdays, a.DaysOff, a.WorkCalendarConfirmed, a.CapacityMinutes },
        バックアップ = new { a.BackupsEnabled, a.BackupGenerations }
    },new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    private static WorkItem Find(Snapshot state, string id, int? version = null)
    {
        var task = state.Tasks.SingleOrDefault(t => t.Id == id) ?? throw new RuleException("仕事が見つかりません。");
        if (version is {} v && task.Version != v) throw new RuleException("この仕事に新しい変更があります。開き直してください。");
        return task;
    }
    public string Add(string title)
    {
        var task = new WorkItem { Title = title.Trim(), CreatedAt = clock.Now };
        Change("用件を登録", s => s.Tasks.Add(task with { Profile = s.ProfileId }), false); return task.Id;
    }
    public void Edit(WorkItem proposed, int expectedVersion)
    {
        Change("内容・締切・根拠を変更", s =>
        {
            var task = Find(s, proposed.Id, expectedVersion);
            task.Title = proposed.Title.Trim(); task.Deadline = proposed.Deadline;
            task.NextAction = proposed.NextAction; task.Completion = proposed.Completion;
            task.ConfirmCompletion = proposed.ConfirmCompletion; task.Note = proposed.Note;
            task.DoneSteps = proposed.DoneSteps; task.Material = proposed.Material;
            task.EstimatedMinutes = proposed.EstimatedMinutes; task.UrgentConfirmed = proposed.UrgentConfirmed;
            task.ReviewOn = proposed.ReviewOn; task.PlannedOn = proposed.PlannedOn;
            task.ParentId = proposed.ParentId; task.Prerequisites = [.. proposed.Prerequisites]; task.Order = proposed.Order;
        });
    }
    public void Start(string id, int version) => Change("これを進める", s =>
    {
        var task = Find(s, id, version);
        if (task.Status == WorkStatus.Working) return;
        if (!Policy.Ready(task, s, clock.Now)) throw new RuleException("待ち・確認日・前提作業を確認してください。まだ着手できません。");
        foreach (var old in s.Tasks.Where(t => t.Profile == task.Profile && t.Status == WorkStatus.Working)) old.Status = WorkStatus.Paused;
        task.Status = WorkStatus.Working;
    });
    public void Transition(string id, int version, WorkStatus status, string? note = null, DateOnly? review = null, bool confirmed = false) => Change(status switch
    { WorkStatus.Completed => "完了", WorkStatus.Paused => "中断", WorkStatus.Waiting => "相手を待つ", WorkStatus.Decision => "判断を待つ", WorkStatus.Cancelled => "取りやめ", WorkStatus.HandedOver => "引継ぎ済み", _ => "待ちが解消した" }, s =>
    {
        var task = Find(s, id);
        if (task.Status == status) return; // Repeated completion is idempotent even with an old screen version.
        if (task.Version != version) throw new RuleException("この仕事に新しい変更があります。開き直してください。");
        if (task.Closed) throw new RuleException("終了した仕事は履歴から元に戻してください。");
        if (status == WorkStatus.Working || status == WorkStatus.Paused && task.Status != WorkStatus.Working ||
            status == WorkStatus.NotStarted && task.Status is not (WorkStatus.Waiting or WorkStatus.Decision) ||
            status is WorkStatus.Waiting or WorkStatus.Decision && task.Status is not (WorkStatus.NotStarted or WorkStatus.Working or WorkStatus.Paused))
            throw new RuleException("この状態ではその操作を使えません。");
        if (status == WorkStatus.Completed)
        {
            if (task.ConfirmCompletion && !confirmed) throw new RuleException("完了条件の確認が必要です。");
            if (s.Tasks.Any(t => t.ParentId == id && !t.Closed)) throw new RuleException("未完了の子作業があります。子作業を確認してから親を完了してください。");
            task.CompletedAt = clock.Now;
        }
        task.Status = status;
        if (note is not null) task.Note = note;
        if (status is WorkStatus.Waiting or WorkStatus.Decision) task.ReviewOn = review ?? Japan.Day(clock.Now).AddDays(1);
        if (status == WorkStatus.NotStarted) task.ReviewOn = null;
    });
    public void Undo(string eventId) => Change("元に戻す（補償操作）", s =>
    {
        var ev = s.Events.Single(e => e.Id == eventId);
        if (ev.Undone) return;
        if (!ev.Undoable || ev.Before.Count != ev.After.Count || ev.InboxBefore.Count != ev.InboxAfter.Count)
            throw new RuleException("この履歴は取消対象ではありません。");
        foreach (var pair in ev.After)
            if (Find(s, pair.Key).Version != pair.Value.Version) throw new RuleException("この後に変更されています。新しい変更を消さないため取消を止めました。");
        foreach (var pair in ev.InboxAfter)
            if (s.Inbox.Single(m => m.Id == pair.Key).Version != pair.Value.Version) throw new RuleException("受付に新しい変更があります。取消を止めました。");
        foreach (var pair in ev.Before)
        {
            var restored = Copy.Of(pair.Value); restored.Version = Find(s, pair.Key).Version;
            s.Tasks[s.Tasks.FindIndex(t => t.Id == pair.Key)] = restored;
        }
        foreach (var pair in ev.InboxBefore)
        {
            var restored = Copy.Of(pair.Value); restored.Version = s.Inbox.Single(m => m.Id == pair.Key).Version;
            s.Inbox[s.Inbox.FindIndex(m => m.Id == pair.Key)] = restored;
        }
        ev.Undone = true;
    }, false);
    public void ReviewInbox(string id, string decision) => Change("受付の確認：" + decision, s =>
    {
        var mail = s.Inbox.Single(m => m.Id == id);
        if (!mail.Pending) return;
        if (decision == "後で確認") { mail.ReviewOn = Japan.Day(clock.Now).AddDays(1); return; }
        if (decision == "対応不要") { mail.Status = IntakeStatus.Ignored; mail.Reason = "本人が今回のみ対応不要と確認"; return; }
        if (decision != "仕事にする") throw new RuleException("確認方法が不明です。");
        var task = new WorkItem { Title = string.IsNullOrWhiteSpace(mail.Subject) ? "件名なしの依頼" : mail.Subject[..Math.Min(500, mail.Subject.Length)], Profile = s.ProfileId, CreatedAt = clock.Now, ReceivedOn = Japan.Day(mail.ReceivedAt), SourceIds = [mail.Id] };
        s.Tasks.Add(task); mail.Status = IntakeStatus.LinkedTask; mail.TaskId = task.Id;
        mail.Reason = "本人が仕事として採用・期限は未確認";
    }, false);
    public void RestoreInboxReview(string id) => Change("対応不要の判定を戻す", s =>
    {
        var mail = s.Inbox.Single(m => m.Id == id);
        if (mail.Status != IntakeStatus.Ignored) throw new RuleException("対応不要の受付のみ戻せます。");
        mail.Status = IntakeStatus.NeedsReview; mail.Reason = "本人が再確認へ戻した";
    });
    private static void RequireDemo(Snapshot state)
    {
        if (!state.IsDemo) throw new RuleException("架空データの操作は本番の記録では利用できません。");
    }
    public void SetDemoConnection(bool connected) => Change("架空受付の接続状態", s => { RequireDemo(s); s.DemoConnected = connected; }, false);
    public void ImportDemo(string json) => Change("同梱の架空メールを照合", s =>
    {
        RequireDemo(s);
        if (!s.DemoConnected) throw new RuleException("架空メールの受付を再開してから照合してください。");
        using var doc = JsonDocument.Parse(json);
        var keys = s.Inbox.Select(m => m.SourceKey).ToHashSet();
        foreach (var input in doc.RootElement.GetProperty("scenarios").EnumerateArray())
        {
            if (!input.GetProperty("synthetic").GetBoolean()) throw new RuleException("架空データ以外は取り込めません。");
            var key = "fixture-v1:" + input.GetProperty("id").GetString();
            if (!keys.Add(key)) continue;
            var readable = input.GetProperty("readable").GetBoolean();
            var mail = new InboxItem { SourceKey = key, Subject = input.GetProperty("subject").GetString()!,
                Body = input.GetProperty("body").GetString() ?? "本文を確認できません。推測していません。",
                Sender = input.GetProperty("sender").GetString()!,
                ReceivedAt = input.GetProperty("received_at").GetDateTimeOffset(),
                Status = readable ? IntakeStatus.NeedsReview : IntakeStatus.ReadBlocked,
                Rule = input.TryGetProperty("approved_fixture_rule", out var rule) ? rule.GetString()! : "" };
            s.Inbox.Add(mail);
            // Test expectations are deliberately never read as application outputs.
            if (mail.Rule == "fixture-reference-only-v1" && mail.Body == "【参考配布のみ】この通知への対応は不要です。")
            { mail.Status = IntakeStatus.Ignored; mail.Reason = "同梱架空規則 fixture-reference-only-v1 に一致"; continue; }
            var pattern = mail.Rule switch
            {
                "fixture-count-report-v1" => @"^【定型試験】提出期限：(\d{4})年(\d{1,2})月(\d{1,2})日(\d{2}):(\d{2})。回答人数を報告してください。$",
                "fixture-date-only-v1" => @"^【定型試験】提出期限：(\d{4})年(\d{1,2})月(\d{1,2})日。回答を提出してください。$",
                _ => "(?!)"
            };
            var match = Regex.Match(mail.Body, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (!match.Success) continue;
            try
            {
                var date = new DateOnly(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
                var deadline = match.Groups.Count > 4
                    ? new Deadline(DeadlineKind.DateTime, At: new DateTimeOffset(date.ToDateTime(new TimeOnly(int.Parse(match.Groups[4].Value), int.Parse(match.Groups[5].Value))), Japan.Offset), Evidence: mail.Rule + "／" + mail.Body)
                    : new Deadline(DeadlineKind.Date, Date: date, Evidence: mail.Rule + "／" + mail.Body);
                var task = new WorkItem { Title = mail.Subject, CreatedAt = mail.ReceivedAt, SourceIds = [mail.Id], Deadline = deadline,
                    NextAction = "原文を確認し、回答を準備する", Completion = "回答を提出する" };
                s.Tasks.Add(task); mail.Status = IntakeStatus.LinkedTask; mail.TaskId = task.Id; mail.Reason = "同梱架空規則に一致：" + mail.Rule;
            }
            catch (ArgumentOutOfRangeException) { mail.Reason = "規則内の日付が不正・本人確認が必要"; }
        }
        s.LastScan = clock.Now;
    }, false);
}
