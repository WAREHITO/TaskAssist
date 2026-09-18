namespace TaskAssist.Core;

public sealed record CalendarSpan(WorkItem Task, int Column, int Length, int Lane, DateOnly First, DateOnly Last);
public sealed record CalendarWeek(IReadOnlyList<DateOnly?> Days, IReadOnlyList<CalendarSpan> Spans);
public sealed record CalendarMilestone(string Label, string Detail);

// Display projections only: never change dates, stored next actions, or completion.
public static class CalendarPresentation
{
    public static IReadOnlyList<CalendarMilestone> Milestones(WorkItem task, DateOnly day)
    {
        var result = new List<CalendarMilestone>();
        if (task.ReceivedOn == day) result.Add(new("受", "依頼を受けた日"));
        if (task.Deadline.Day == day) result.Add(new("〆", "本当の回答締切"));
        var requested = task.Responses.Where(r => r.RequestedOn == day).Select(r => r.TeamName).ToList();
        var answered = task.Responses.Where(r => r.AnsweredOn == day).Select(r => r.TeamName).ToList();
        if (requested.Count > 0) result.Add(new($"依{requested.Count}", "課内へ依頼：" + string.Join("、", requested)));
        if (answered.Count > 0) result.Add(new($"答{answered.Count}", "回答あり：" + string.Join("、", answered)));
        return result;
    }

    public static bool AppearsOn(WorkItem task, DateOnly day) => CalendarPolicy.Covers(task, day) || Milestones(task, day).Count > 0;

    public static IReadOnlyList<CalendarWeek> Weeks(DateOnly month, IEnumerable<WorkItem> source)
    {
        month = new(month.Year, month.Month, 1);
        var days = CalendarPolicy.MonthDays(month);
        var offset = ((int)month.DayOfWeek + 6) % 7;
        var tasks = source.ToList();
        var weeks = new List<CalendarWeek>();
        for (var w = 0; w < (offset + days.Count + 6) / 7; w++)
        {
            var cells = Enumerable.Range(0, 7).Select(c => w * 7 + c - offset)
                .Select(i => i >= 0 && i < days.Count ? (DateOnly?)days[i] : null).ToArray();
            var segments = new List<(WorkItem Task, int Start, int End)>();
            foreach (var task in tasks)
            {
                for (var c = 0; c < 7; c++)
                {
                    if (cells[c] is not {} day || !CalendarPolicy.Covers(task, day)) continue;
                    var start = c;
                    while (c < 6 && cells[c + 1] is {} next && CalendarPolicy.Covers(task, next)) c++;
                    segments.Add((task, start, c));
                }
            }
            var laneEnds = new List<int>();
            var spans = new List<CalendarSpan>();
            foreach (var segment in segments.OrderBy(s => s.Start).ThenByDescending(s => s.End - s.Start).ThenBy(s => s.Task.Id, StringComparer.Ordinal))
            {
                var lane = laneEnds.FindIndex(end => end < segment.Start);
                if (lane < 0) { lane = laneEnds.Count; laneEnds.Add(-1); }
                laneEnds[lane] = segment.End;
                spans.Add(new(segment.Task, segment.Start, segment.End - segment.Start + 1, lane, cells[segment.Start]!.Value, cells[segment.End]!.Value));
            }
            weeks.Add(new(cells, spans));
        }
        return weeks;
    }

    public static string Guide(WorkItem task)
    {
        if (task.Closed) return "この案件は終了状態です。必要なら履歴を確認できます。";
        if (task.Responses.Count == 0) return "回答を集める担当を追加する。";
        var steps = new List<string>();
        if (task.Responses.Any(r => r.Stage == ResponseStage.NotRequested)) steps.Add("未依頼の担当へ依頼し、依頼済みを記録する");
        if (task.Responses.Any(r => r.Stage == ResponseStage.Requested)) steps.Add("回答を受け取った担当の記録を更新する");
        if (task.Responses.Any(r => r.Stage == ResponseStage.Received)) steps.Add("届いた回答を確認する");
        if (task.Responses.Any(r => r.Answer == AnswerKind.Later)) steps.Add("後回しにした回答内容を補う");
        return steps.Count == 0 ? "回答の集約・正式回答が済んだか確認する。案件は自動完了しません。" : string.Join("／", steps) + "。";
    }

    public static string Names(WorkItem task, ResponseStage stage)
    {
        var names = task.Responses.Where(r => r.Stage == stage).Select(r => r.TeamName).ToList();
        return names.Count == 0 ? "なし" : string.Join("、", names.Take(5)) + (names.Count > 5 ? $" ほか{names.Count - 5}担当（一覧で確認）" : "");
    }

    public static ChangeEvent? LastRecord(Snapshot state, string taskId) => state.Events.AsEnumerable().Reverse()
        .Where(e => e.After.ContainsKey(taskId)).OrderByDescending(e => e.At).FirstOrDefault();
}
