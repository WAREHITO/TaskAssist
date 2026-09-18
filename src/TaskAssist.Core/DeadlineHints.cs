using System.Globalization;
using System.Text.RegularExpressions;

namespace TaskAssist.Core;

public sealed record DeadlineHint(string Id, string Text, string Excerpt, DateOnly? Day, DateOnly? EndDay, DateTimeOffset ReceivedAt,
    string Method = "local-candidate-v1", string Warning = "引用・否定・曜日・別作業の期限かを原文で確認してください。未確定です。");
public static class DeadlineHints
{
    public static List<DeadlineHint> Extract(string body, DateTimeOffset received)
    {
        var result = new List<DeadlineHint>(); var day = Japan.Day(received);
        var matches = Regex.Matches(body,@"\d{4}[年/-]\d{1,2}[月/-]\d{1,2}日?|\d{1,2}月\d{1,2}日|明後日|明日|今日|来週|来月末|至急|なるべく早く",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        foreach (Match m in matches.Cast<Match>().Take(100))
        {
            DateOnly? proposed = null, end = null;
            if (m.Value == "明日") proposed = day.AddDays(1);
            else if (m.Value == "明後日") proposed = day.AddDays(2);
            else if (m.Value == "今日") proposed = day;
            else if (m.Value == "来週") { proposed = day.AddDays(7-((int)day.DayOfWeek+6)%7); end = proposed.Value.AddDays(6); }
            else if (m.Value == "来月末") { var next = day.AddMonths(1); proposed = new(next.Year,next.Month,DateTime.DaysInMonth(next.Year,next.Month)); }
            else if (DateOnly.TryParseExact(m.Value,["yyyy年M月d日","yyyy/M/d","yyyy-M-d"],CultureInfo.InvariantCulture,DateTimeStyles.None,out var explicitDay)) proposed = explicitDay;
            var from = Math.Max(0,m.Index-35); var excerpt = body.Substring(from,Math.Min(body.Length-from,m.Length+70));
            result.Add(new(MailIdentity.Hash(m.Index + ":" + m.Value),m.Value,excerpt,proposed,end,received));
        }
        return result;
    }
}

public sealed partial class TaskService
{
    public void ConfirmDeadlineHint(string mailId, string hintId) => Change("本人が原文と期限候補を確認",s =>
    {
        var mail = s.Inbox.Single(m => m.Id == mailId && !m.Purged);
        var hint = mail.DeadlineHints.Single(h => h.Id == hintId);
        if (hint.Day is null || hint.EndDay is not null) throw new RuleException("年のない日付・期間・緊急表現は日付を手入力して確認してください。");
        var task = mail.TaskId is {} id ? Find(s,id) : new WorkItem { Profile = s.ProfileId, Title = mail.Subject.Length == 0 ? "件名なしの依頼" : mail.Subject[..Math.Min(500,mail.Subject.Length)], CreatedAt = clock.Now, ReceivedOn = Japan.Day(mail.ReceivedAt), SourceIds = [mail.Id] };
        if (mail.TaskId is null) s.Tasks.Add(task);
        task.Deadline = new(DeadlineKind.Date,hint.Day,Evidence:$"本人確認 {clock.Now.ToOffset(Japan.Offset):O}／{hint.Method}／受信 {hint.ReceivedAt.ToOffset(Japan.Offset):O}／候補 {hint.Text}／原文 {hint.Excerpt}",SourceId:mail.Id);
        mail.TaskId = task.Id; mail.Status = IntakeStatus.LinkedTask; mail.Reason = "本人が原文を確認し期限候補を採用";
    });
}
