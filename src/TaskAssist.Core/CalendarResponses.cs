using System.Text;

namespace TaskAssist.Core;

public static class AppRelease
{
    public const string Version = "1.0.0-rc.2";
    public const int Schema = 4;
}
public enum CaseColor { Blue, Teal, Violet, Amber, Rose }
public enum ResponseStage { NotRequested, Requested, Received, Reviewed }
public enum AnswerKind { Unrecorded, Later, NoItems, NoIssues, HasItems, NeedsReview, FreeText }

public sealed record TeamResponse
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string TeamName { get; set; } = "";
    public ResponseStage Stage { get; set; }
    public DateOnly? RequestedOn { get; set; }
    public DateOnly? AnsweredOn { get; set; }
    public DateOnly? ReviewedOn { get; set; }
    public AnswerKind Answer { get; set; }
    public string AnswerText { get; set; } = "";
    public string Note { get; set; } = "";
    public string StageText => Stage switch
    {
        ResponseStage.NotRequested => "未依頼", ResponseStage.Requested => "依頼済み",
        ResponseStage.Received => "回答あり", _ => "確認済み"
    };
    public string AnswerLabel => ResponsePolicy.AnswerLabel(Answer);
}

public static class ResponsePolicy
{
    // A form may still contain values from a later stage. Only unchanged, previously
    // saved values may be removed by an intentional rollback; never drop fresh input.
    public static TeamResponse PrepareEdit(TeamResponse original, TeamResponse input)
    {
        if (input.Stage < ResponseStage.Received &&
            (input.AnswerText.Length > 0 && input.AnswerText != original.AnswerText ||
             input.Answer != AnswerKind.Unrecorded && input.Answer != original.Answer ||
             input.AnsweredOn is not null && input.AnsweredOn != original.AnsweredOn))
            throw new RuleException("入力した回答を残すには、記録の状態を「回答あり」または「確認済み」にしてください。入力は残しています。");
        if (input.Stage != ResponseStage.Reviewed && input.ReviewedOn is not null && input.ReviewedOn != original.ReviewedOn)
            throw new RuleException("入力した確認日を残すには、記録の状態を「確認済み」にしてください。入力は残しています。");
        if (input.Stage == ResponseStage.NotRequested && input.RequestedOn is not null && input.RequestedOn != original.RequestedOn)
            throw new RuleException("入力した依頼日を残すには、記録の状態を「依頼済み」以降にしてください。入力は残しています。");
        return input with
        {
            RequestedOn=input.Stage==ResponseStage.NotRequested?null:input.RequestedOn,
            AnsweredOn=input.Stage>=ResponseStage.Received?input.AnsweredOn:null,
            ReviewedOn=input.Stage==ResponseStage.Reviewed?input.ReviewedOn:null,
            Answer=input.Stage>=ResponseStage.Received?input.Answer:AnswerKind.Unrecorded,
            AnswerText=input.Stage>=ResponseStage.Received?input.AnswerText:""
        };
    }
    public static string Key(string name) => name.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    public static string AnswerLabel(AnswerKind kind) => kind switch
    {
        AnswerKind.Unrecorded => "回答内容は未記録", AnswerKind.Later => "内容は後で記録",
        AnswerKind.NoItems => "該当なし", AnswerKind.NoIssues => "問題なし", AnswerKind.HasItems => "該当あり",
        AnswerKind.NeedsReview => "要確認", _ => "自由記述"
    };
    public static void ValidateRoster(List<string> names)
    {
        if (names.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 100) || names.Select(Key).Distinct().Count() != names.Count)
            throw new RuleException("担当名は1〜100文字で、重複しないようにしてください。");
    }
    public static void Validate(WorkItem task)
    {
        if (!Enum.IsDefined(task.CalendarColor)) throw new RuleException("案件の色を確認してください。");
        ValidateRoster(task.Responses.Select(r => r.TeamName).ToList());
        if (task.Responses.Select(r => r.Id).Distinct().Count() != task.Responses.Count)
            throw new RuleException("担当の記録番号が重複しています。");
        foreach (var r in task.Responses)
        {
            if (!Enum.IsDefined(r.Stage) || !Enum.IsDefined(r.Answer)) throw new RuleException("回答状態を確認してください。");
            if (r.Stage == ResponseStage.NotRequested && (r.RequestedOn is not null || r.AnsweredOn is not null || r.ReviewedOn is not null) ||
                r.Stage == ResponseStage.Requested && (r.RequestedOn is null || r.AnsweredOn is not null || r.ReviewedOn is not null) ||
                r.Stage >= ResponseStage.Received && (r.AnsweredOn is null || r.Answer == AnswerKind.Unrecorded) ||
                r.Stage < ResponseStage.Received && (r.Answer != AnswerKind.Unrecorded || r.AnswerText.Length > 0) ||
                r.Stage == ResponseStage.Received && r.ReviewedOn is not null ||
                r.Stage == ResponseStage.Reviewed && r.ReviewedOn is null)
                throw new RuleException("状態と依頼日・回答日・確認日・回答内容の組合せを確認してください。");
            if (r.RequestedOn > r.AnsweredOn || r.AnsweredOn > r.ReviewedOn)
                throw new RuleException("依頼日→回答日→確認日の順になるように日付を確認してください。");
            if (r.Answer == AnswerKind.FreeText && string.IsNullOrWhiteSpace(r.AnswerText))
                throw new RuleException("自由記述では回答内容を入力してください。後で記録する場合は「内容は後で」を選べます。");
        }
    }
    public static string Summary(WorkItem task)
    {
        var rows = task.Responses;
        return rows.Count == 0 ? "回答を集める担当は未設定" :
            $"回答 {rows.Count(r => r.Stage >= ResponseStage.Received)} / {rows.Count}担当  ・ 確認済み {rows.Count(r => r.Stage == ResponseStage.Reviewed)}  ・ 回答待ち {rows.Count(r => r.Stage == ResponseStage.Requested)}  ・ 未依頼 {rows.Count(r => r.Stage == ResponseStage.NotRequested)}";
    }
    public static string RecordText(TeamResponse r) =>
        $"{r.TeamName}：{r.StageText}／依頼 {Date(r.RequestedOn)}／回答 {Date(r.AnsweredOn)}／確認 {Date(r.ReviewedOn)}\n{r.AnswerLabel}" +
        (r.AnswerText.Length > 0 ? "\n回答内容：" + r.AnswerText : "") + (r.Note.Length > 0 ? "\n補足：" + r.Note : "");
    public static string Date(DateOnly? date) => date?.ToString("yyyy/M/d") ?? "未記録";
    public static IEnumerable<string> Changes(WorkItem? before, WorkItem after)
    {
        if (before is not null && before.ReceivedOn != after.ReceivedOn)
            yield return $"依頼を受けた日：{Date(before.ReceivedOn)} → {Date(after.ReceivedOn)}";
        foreach (var row in after.Responses)
        {
            var previous = before?.Responses.SingleOrDefault(r => r.Id == row.Id);
            if (previous is null) yield return "担当を追加：" + RecordText(row);
            else if (Copy.Json(previous) != Copy.Json(row)) yield return "変更前：" + RecordText(previous) + "\n変更後：" + RecordText(row);
        }
    }
}

public static class CalendarPolicy
{
    // Unknown starts remain unknown; do not turn registration time into a receipt date.
    public static bool Covers(WorkItem task, DateOnly day)
    {
        var start = task.ReceivedOn; var end = task.Deadline.Day;
        if (start is {} a && end is {} b && a <= b) return a <= day && day <= b;
        return start == day || end == day;
    }
    public static List<DateOnly> MonthDays(DateOnly month) => Enumerable.Range(0, DateTime.DaysInMonth(month.Year, month.Month))
        .Select(i => new DateOnly(month.Year, month.Month, i + 1)).ToList();
    public static string Mark(WorkItem task, DateOnly day) => task.ReceivedOn == day && task.Deadline.Day == day ? "受・〆 " :
        task.Deadline.Day == day ? "〆 " : task.ReceivedOn == day ? "受 " : "― ";
    public static string Period(WorkItem task) => $"受付 {ResponsePolicy.Date(task.ReceivedOn)} → 回答〆 " +
        (task.Deadline.Day is {} end ? end.ToString("yyyy/M/d") : task.Deadline.Kind == DeadlineKind.None ? "期限なし" : "未確認");
    public static string Warning(WorkItem task) => task.ReceivedOn > task.Deadline.Day ? "締切が受付日より前です。日付は動かさず、両日を表示しています。" : "";
}
