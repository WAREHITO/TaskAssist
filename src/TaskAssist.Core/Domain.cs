using System.Text.Json;

namespace TaskAssist.Core;

public enum WorkStatus { NotStarted, Working, Paused, Waiting, Decision, Completed, Cancelled, HandedOver }
public enum DeadlineKind { Unknown, None, Date, DateTime }
public enum IntakeStatus { NeedsReview, ReadBlocked, LinkedTask, Ignored }

public sealed record Deadline(DeadlineKind Kind = DeadlineKind.Unknown, DateOnly? Date = null,
    DateTimeOffset? At = null, string Evidence = "未確認")
{
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) ||
            (Kind == DeadlineKind.Date && (Date is null || At is not null)) ||
            (Kind == DeadlineKind.DateTime && (At is null || Date is not null)) ||
            (Kind is DeadlineKind.None or DeadlineKind.Unknown && (Date is not null || At is not null)) ||
            (Kind != DeadlineKind.Unknown && string.IsNullOrWhiteSpace(Evidence)))
            throw new RuleException("締切の種類・日付・確認根拠を確認してください。");
    }
    public DateOnly? Day => Kind == DeadlineKind.Date ? Date : At is {} at ? Japan.Day(at) : null;
    public int Band(DateTimeOffset now) => Kind switch
    {
        DeadlineKind.Unknown => 3, DeadlineKind.None => 4,
        DeadlineKind.Date when Date < Japan.Day(now) => 0,
        DeadlineKind.DateTime when At < now => 0,
        _ when Day == Japan.Day(now) => 1, _ => 2
    };
    public string Display(DateTimeOffset now) => Kind switch
    {
        DeadlineKind.Unknown => "期限未確認",
        DeadlineKind.None => "確認済み・期限なし",
        DeadlineKind.Date => $"{Date:yyyy年M月d日}・時刻未確認" + (Date < Japan.Day(now) ? "（期限日経過）" : Date == Japan.Day(now) ? "（今日）" : ""),
        _ => $"{At!.Value.ToOffset(Japan.Offset):yyyy年M月d日 HH:mm}（日本時間）" + (At < now ? "・時刻超過" : Day == Japan.Day(now) ? "・今日" : "")
    };
}

public static class Japan
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(9);
    public static DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(at.ToOffset(Offset).DateTime);
}
public interface IClock { DateTimeOffset Now { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset Now => DateTimeOffset.UtcNow; }
public sealed class RuleException(string message) : Exception(message);

public sealed record WorkItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Profile { get; init; } = "demo";
    public int Version { get; set; } = 1;
    public string Title { get; set; } = "";
    public WorkStatus Status { get; set; }
    public Deadline Deadline { get; set; } = new();
    public DateOnly? ReviewOn { get; set; }
    public DateOnly? PlannedOn { get; set; }
    public DateOnly? ReceivedOn { get; set; }
    public CaseColor CalendarColor { get; set; }
    public List<TeamResponse> Responses { get; set; } = [];
    public string DemoTemplate { get; set; } = "";
    public int Order { get; set; }
    public string NextAction { get; set; } = "内容を確認する";
    public string Completion { get; set; } = "依頼された対応を終える";
    public bool ConfirmCompletion { get; set; }
    public string Note { get; set; } = "";
    public string DoneSteps { get; set; } = "";
    public string Material { get; set; } = "";
    public string? ParentId { get; set; }
    public List<string> Prerequisites { get; set; } = [];
    public List<string> SourceIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool Closed => Status is WorkStatus.Completed or WorkStatus.Cancelled or WorkStatus.HandedOver;
    public string StatusText => Status switch
    {
        WorkStatus.NotStarted => "未着手", WorkStatus.Working => "作業中", WorkStatus.Paused => "中断",
        WorkStatus.Waiting => "相手待ち", WorkStatus.Decision => "判断待ち", WorkStatus.Completed => "完了",
        WorkStatus.Cancelled => "取りやめ", _ => "引継ぎ済み"
    };
}
public sealed record InboxItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string SourceKey { get; init; } = "";
    public int Version { get; set; } = 1;
    public string Subject { get; init; } = "";
    public string Body { get; init; } = "";
    public string Sender { get; init; } = "";
    public DateTimeOffset ReceivedAt { get; init; }
    public IntakeStatus Status { get; set; }
    public string Reason { get; set; } = "定型規則なし・本人確認が必要";
    public DateOnly? ReviewOn { get; set; }
    public string Rule { get; init; } = "";
    public string? TaskId { get; set; }
    public bool Pending => Status is IntakeStatus.NeedsReview or IntakeStatus.ReadBlocked;
}
public sealed record ChangeEvent
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset At { get; init; }
    public string Label { get; init; } = "";
    public Dictionary<string, WorkItem> Before { get; init; } = [];
    public Dictionary<string, WorkItem> After { get; init; } = [];
    public Dictionary<string, InboxItem> InboxBefore { get; init; } = [];
    public Dictionary<string, InboxItem> InboxAfter { get; init; } = [];
    public bool Undoable { get; init; } = true;
    public bool Undone { get; set; }
}
public sealed class Snapshot
{
    public string ProfileId { get; set; } = "demo";
    public bool IsDemo => ProfileId == "demo";
    public long Revision { get; set; }
    public DateTimeOffset? SavedAt { get; set; }
    public DateTimeOffset? LastScan { get; set; }
    public bool DemoConnected { get; set; } = true;
    public List<WorkItem> Tasks { get; set; } = [];
    public List<InboxItem> Inbox { get; set; } = [];
    public List<ChangeEvent> Events { get; set; } = [];
    public List<string> TeamRoster { get; set; } = [];
    public string IntakeText => DemoConnected
        ? "架空メールのみ／Outlook未接続・実メール未確認"
        : "架空メール受付停止中／停止中の新着は未確認・Outlook未接続";
}
public static class Copy
{
    public static T Of<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public static string Json<T>(T value) => JsonSerializer.Serialize(value);
}

public static class Policy
{
    public static bool Ready(WorkItem task, Snapshot state, DateTimeOffset now) =>
        task.Status is WorkStatus.NotStarted or WorkStatus.Paused &&
        (task.ReviewOn is null || task.ReviewOn <= Japan.Day(now)) &&
        task.Prerequisites.All(id => state.Tasks.Any(t => t.Id == id && t.Status == WorkStatus.Completed));
    public static List<WorkItem> Candidates(Snapshot state, DateTimeOffset now) => state.Tasks
        .Where(t => Ready(t, state, now)).OrderBy(t => t.Deadline.Band(now))
        .ThenBy(t => t.Deadline.Day).ThenBy(t => t.Deadline.At?.ToOffset(Japan.Offset).TimeOfDay ?? TimeSpan.Zero)
        .ThenBy(t => t.Order).ThenBy(t => t.CreatedAt).ThenBy(t => t.Id, StringComparer.Ordinal).ToList();
    public static string Reason(WorkItem task, DateTimeOffset now) => task.Deadline.Band(now) switch
    {
        0 => "締切を過ぎているため", 1 => "本日の締切のため", 2 => "締切の近い順",
        3 => "期限未確認・受付の古い順", _ => "確認済み期限なし・本人の順序／受付順"
    } + (task.Deadline.Kind == DeadlineKind.Date ? "（時刻指定の仕事との前後は未確認）" : "");
    public static List<WorkItem> Alerts(Snapshot state, DateTimeOffset now) => state.Tasks
        .Where(t => !t.Closed && (t.Deadline.Band(now) <= 1 ||
            t.ReviewOn is {} review && t.Deadline.Day is {} day && review > day))
        .OrderBy(t => t.Deadline.Day).ThenBy(t => t.CreatedAt).ToList();
    public static List<WorkItem> Reviews(Snapshot state, DateTimeOffset now) => state.Tasks
        .Where(t => !t.Closed && (t.Deadline.Kind == DeadlineKind.Unknown || t.ReviewOn <= Japan.Day(now)))
        .OrderBy(t => t.ReviewOn is {} day && day <= Japan.Day(now) ? 0 : 1)
        .ThenBy(t => t.ReviewOn).ThenBy(t => t.CreatedAt).ThenBy(t => t.Id).ToList();
    public static void Validate(Snapshot state)
    {
        if (state.ProfileId != "demo" && !Guid.TryParseExact(state.ProfileId, "N", out _))
            throw new RuleException("所属の識別情報が不正です。");
        if (state.Tasks.Concat(state.Events.SelectMany(e => e.Before.Values.Concat(e.After.Values))).Any(t => t.Profile != state.ProfileId))
            throw new RuleException("異なる所属の記録が混在しています。保存・復元を止めました。");
        if (!state.IsDemo && (state.Inbox.Count > 0 || state.Tasks.Any(t => t.DemoTemplate.Length > 0)))
            throw new RuleException("本番の手動登録版に架空受付の記録を混ぜることはできません。");
        ResponsePolicy.ValidateRoster(state.TeamRoster);
        if (state.Tasks.GroupBy(t => t.Profile).Any(g => g.Count(t => t.Status == WorkStatus.Working) > 1))
            throw new RuleException("作業中の仕事は1件です。現在の作業を中断してから戻してください。");
        var byId = state.Tasks.ToDictionary(t => t.Id);
        foreach (var task in state.Tasks)
        {
            task.Deadline.Validate();
            ResponsePolicy.Validate(task);
            if (string.IsNullOrWhiteSpace(task.Title) || task.Title.Length > 500 || !Enum.IsDefined(task.Status))
                throw new RuleException("用件は1〜500文字で入力してください。");
            if (task.Prerequisites.Any(id => !byId.ContainsKey(id) || byId[id].Profile != task.Profile))
                throw new RuleException("前提作業が存在しません。");
            if (task.ParentId is {} parent && (!byId.ContainsKey(parent) || byId[parent].Profile != task.Profile))
                throw new RuleException("親の仕事が存在しません。");
        }
        foreach (var task in state.Tasks)
        {
            Visit(task.Id, [], new HashSet<string>());
            var parents = new HashSet<string> { task.Id };
            var parent = task.ParentId;
            while (parent is not null)
            {
                if (!parents.Add(parent)) throw new RuleException("親子関係が循環しています。");
                parent = byId[parent].ParentId;
            }
        }
        void Visit(string id, HashSet<string> path, HashSet<string> done)
        {
            if (done.Contains(id)) return;
            if (!path.Add(id)) throw new RuleException("前提作業が循環しています。保存しませんでした。");
            foreach (var next in byId[id].Prerequisites) Visit(next, path, done);
            path.Remove(id); done.Add(id);
        }
    }
}
