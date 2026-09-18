namespace TaskAssist.Core;

public sealed partial class TaskService
{
    public string AddCalendarCase(string title, DateOnly? receivedOn, Deadline deadline, CaseColor color)
    {
        var task = new WorkItem { Title = title.Trim(), ReceivedOn = receivedOn, Deadline = deadline, CalendarColor = color, CreatedAt = clock.Now };
        Change("カレンダーから案件を登録", s => s.Tasks.Add(task with { Profile = s.ProfileId }), false); return task.Id;
    }
    public void SetTeamRoster(IEnumerable<string> names)
    {
        var roster = names.Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        ResponsePolicy.ValidateRoster(roster);
        Change("次回選択する担当一覧を変更（過去の回答は保持）", s => s.TeamRoster = roster, false);
    }
    public void SetCalendar(string id, int version, string title, DateOnly? receivedOn, Deadline deadline, CaseColor color) =>
        Change("案件の受付日・回答締切・色を変更", s =>
        {
            var task = Find(s, id, version); task.Title = title.Trim(); task.ReceivedOn = receivedOn; task.Deadline = deadline; task.CalendarColor = color;
        });
    public void AddTeams(string id, int version, IEnumerable<string> names)
    {
        var requested = names.Select(n => n.Trim()).Where(n => n.Length > 0).DistinctBy(ResponsePolicy.Key).ToList();
        if (requested.Count == 0) throw new RuleException("担当を選ぶか、担当名を入力してください。");
        Change("回答を集める担当を追加", s =>
        {
            var task = Find(s, id, version);
            foreach (var name in requested)
            {
                var key = ResponsePolicy.Key(name);
                if (!s.TeamRoster.Any(n => ResponsePolicy.Key(n) == key)) s.TeamRoster.Add(name);
                if (!task.Responses.Any(r => ResponsePolicy.Key(r.TeamName) == key)) task.Responses.Add(new TeamResponse { TeamName = name });
            }
        });
    }
    public void MarkRequested(string id, int version, string? responseId = null) => Change("課内への依頼済みを記録（送信なし）", s =>
    {
        var task = Find(s, id);
        if (responseId is not null && !task.Responses.Any(r => r.Id == responseId)) throw new RuleException("担当が見つかりません。");
        var rows = task.Responses.Where(r => (responseId is null || r.Id == responseId) && r.Stage == ResponseStage.NotRequested).ToList();
        if (rows.Count == 0) return;
        _ = Find(s, id, version);
        foreach (var row in rows) { row.Stage = ResponseStage.Requested; row.RequestedOn = Japan.Day(clock.Now); }
    });
    public void RecordAnswer(string id, int version, string responseId, AnswerKind answer) => Change("担当からの回答を記録", s =>
    {
        if (answer is AnswerKind.Unrecorded or AnswerKind.FreeText) throw new RuleException("回答の内容を選択してください。");
        var task = Find(s, id); var row = task.Responses.Single(r => r.Id == responseId);
        if (row.Stage == ResponseStage.Received && row.Answer == answer) return;
        _ = Find(s, id, version);
        if (row.Stage == ResponseStage.Reviewed) throw new RuleException("確認済みの回答は「記録を直す」から変更してください。");
        row.Stage = ResponseStage.Received; row.Answer = answer; row.AnsweredOn ??= Japan.Day(clock.Now);
    });
    public void MarkReviewed(string id, int version, string responseId) => Change("担当の回答を確認済みに", s =>
    {
        var task = Find(s, id); var row = task.Responses.Single(r => r.Id == responseId);
        if (row.Stage == ResponseStage.Reviewed) return;
        _ = Find(s, id, version);
        if (row.Stage != ResponseStage.Received) throw new RuleException("回答を記録してから確認済みにしてください。");
        row.Stage = ResponseStage.Reviewed; row.ReviewedOn = Japan.Day(clock.Now);
    });
    public void EditResponse(string id, int version, TeamResponse proposed) => Change("担当の回答内容・日付を修正", s =>
    {
        var task = Find(s, id, version); var index = task.Responses.FindIndex(r => r.Id == proposed.Id);
        if (index < 0) throw new RuleException("担当が見つかりません。");
        // Team identity is immutable here; an answer cannot silently move to another team.
        task.Responses[index] = Copy.Of(proposed) with { TeamName = task.Responses[index].TeamName };
    });
    public string AddCalendarExample()
    {
        string id = "";
        Change("架空のカレンダー例を追加", s =>
        {
            RequireDemo(s);
            var existing = s.Tasks.SingleOrDefault(t => t.DemoTemplate == "calendar-example-2026");
            if (existing is not null) { id = existing.Id; return; }
            var task = new WorkItem { Title = "【架空】課内調査の回答をまとめる", ReceivedOn = new DateOnly(2026,9,21),
                Deadline = new Deadline(DeadlineKind.Date, new DateOnly(2026,10,31), Evidence:"本人要望の架空例・年は2026年として表示"),
                CalendarColor = CaseColor.Teal, CreatedAt = clock.Now, DemoTemplate = "calendar-example-2026",
                NextAction = "課内の担当へ調査を依頼する", Completion = "担当の回答をまとめ、正式回答する" };
            foreach (var name in new[] { "担当A", "担当B", "担当C" })
            {
                task.Responses.Add(new TeamResponse { TeamName = name });
                if (!s.TeamRoster.Any(n => ResponsePolicy.Key(n) == ResponsePolicy.Key(name))) s.TeamRoster.Add(name);
            }
            id = task.Id; s.Tasks.Add(task);
        }, false); return id;
    }
}
