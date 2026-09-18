using System.Globalization;
using System.Text.RegularExpressions;

namespace TaskAssist.Core;

public sealed partial class TaskService
{
    public void ConfigureConnector(IEnumerable<MailFolder> folders, DateTimeOffset startAt) => Change("Outlookの読取り範囲と開始時点を確認", s =>
    {
        var list = folders.DistinctBy(f => f.Key).ToList();
        if (list.Count is < 1 or > 100 || startAt > clock.Now || startAt < clock.Now.AddYears(-10))
            throw new RuleException("対象フォルダーと開始日時（過去10年以内・未来不可）を確認してください。");
        if (list.Any(f => string.IsNullOrWhiteSpace(f.Account) || string.IsNullOrWhiteSpace(f.StoreId) || string.IsNullOrWhiteSpace(f.EntryId)))
            throw new RuleException("Outlookから取得した対象を選んでください。");
        s.Automation.Connector = new() { Enabled = true, StartAt = startAt, Folders = Copy.Of(list), Generation = s.Automation.Connector.Generation + 1,
            ConsentAt = clock.Now, Health = "読取りを許可済み。まだ照合していません。対象外フォルダーは未監視です。" };
    }, false);
    public void StopConnector() => Change("Outlook読取りを停止", s => { s.Automation.Connector.Enabled = false; s.Automation.Connector.Generation++; s.Automation.Connector.Health = "読取り停止中。停止後の新着は未確認です。"; }, false);
    public void ConnectorFailure(int generation, string code) => Change("Outlook照合の状態を記録", s =>
    {
        if (s.Automation.Connector.Generation != generation) return;
        s.Automation.Connector.Health = code switch { "timeout" => "Outlookが応答しません。読取り結果は未確認です。Outlookの警告や起動状態を確認してください。", "changed" => "フォルダーの内容が変わりました。先頭から再照合します。", _ => "Outlookに接続できないか、対象を読み取れません。Outlookの状態を確認して再照合してください。" };
        s.Automation.Connector.LastAttempt = clock.Now;
    }, false);
    public void BeginScan(string key, int generation) => Change("対象フォルダーの照合開始", s =>
    {
        var c = CheckedScope(s, key, generation);
        if (!c.Cursors.TryGetValue(key, out var cursor) || cursor.CompletedAt is not null) c.Cursors[key] = new();
        c.LastAttempt = clock.Now; c.Health = "照合中。最後の完全照合以後は未確認です。";
    }, false);
    private static ConnectorSettings CheckedScope(Snapshot s, string key, int generation)
    {
        var c = s.Automation.Connector;
        if (!c.Enabled || c.Generation != generation || !c.Folders.Any(f => f.Key == key)) throw new RuleException("読取りの許可範囲が変更されました。取得結果は保存しません。");
        return c;
    }
    public void SaveMailPage(MailFolder folder, int generation, int expectedOffset, OutlookReply reply) => Change("Outlook受付と照合位置を保存", s =>
    {
        var c = CheckedScope(s, folder.Key, generation);
        if (!reply.Success || reply.Protocol != 1 || reply.Messages.Count > 50) throw new RuleException("読取り結果を確認できません。");
        var previous = c.Cursors.GetValueOrDefault(folder.Key) ?? new();
        if (previous.Offset != expectedOffset || reply.Cursor.Offset < expectedOffset) throw new RuleException("照合位置が変わりました。やり直してください。");
        foreach (var input in reply.Messages)
        {
            if (input.Location.Account != folder.Account || input.Location.StoreId != folder.StoreId || input.Location.FolderId != folder.EntryId || input.ReceivedAt < c.StartAt)
                throw new RuleException("許可範囲外の取得結果を拒否しました。");
            var physical = s.Inbox.FirstOrDefault(m => m.Locations.Any(p => p.Key == input.Location.Key));
            if (physical is not null)
            {
                if (physical.SourceReadable && physical.Status != IntakeStatus.ReadBlocked || !input.Readable || physical.Purged) continue;
                var fixedMail = physical with { Body = input.Body, Subject = input.Subject, Sender = input.Sender, Attachments = input.Attachments,
                    SourceReadable = true, InternetId = physical.InternetId.Length == 0 ? input.InternetId : physical.InternetId,
                    Status = physical.Status == IntakeStatus.ReadBlocked ? IntakeStatus.NeedsReview : physical.Status,
                    Reason = "本文を再取得しました。以前の本人判定・仕事・期限は保持しています。", Fingerprint = input.Fingerprint, LastModifiedAt = input.LastModifiedAt,
                    DeadlineCandidate = Candidate(input.Body), DeadlineHints = DeadlineHints.Extract(input.Body,input.ReceivedAt) };
                s.Inbox[s.Inbox.IndexOf(physical)] = fixedMail; continue;
            }
            // A move can be linked only when both identifier and exact content agree. Subjects never merge records.
            var logical = string.IsNullOrEmpty(input.InternetId) ? null : s.Inbox.FirstOrDefault(m => !m.Purged && input.Readable && m.InternetId == input.InternetId && m.Fingerprint == input.Fingerprint);
            if (logical is not null) { logical.Locations.Add(input.Location); continue; }
            var collision = input.InternetId.Length > 0 && s.Inbox.Any(m => m.InternetId == input.InternetId);
            var mail = new InboxItem { IsOutlook = true, SourceReadable = input.Readable, SourceKey = "outlook:" + input.Location.Key, Locations = [input.Location], InternetId = input.InternetId,
                Subject = input.Subject, Sender = input.Sender, Body = input.Body, ReceivedAt = input.ReceivedAt, Attachments = input.Attachments, Fingerprint = input.Fingerprint,
                Status = input.Readable ? IntakeStatus.NeedsReview : IntakeStatus.ReadBlocked,
                Reason = !input.Readable ? "本文未取得・保護・容量上限等のため内容未確認" : collision ? "同じ識別子で内容が異なります。統合せず確認へ回しました。" : "定型規則なし・本人確認が必要",
                DeadlineCandidate = Candidate(input.Body), DeadlineHints = DeadlineHints.Extract(input.Body,input.ReceivedAt), LastModifiedAt = input.LastModifiedAt };
            s.Inbox.Add(mail);
            if (input.AppJobId.Length > 0 && s.Automation.Jobs.Any(j => j.Id == input.AppJobId && j.Kind == JobKind.SelfDigest && j.Account == input.Location.Account && j.State is JobState.ConfirmedSuccess or JobState.OutcomeUnknown))
            { mail.Status = IntakeStatus.Ignored; mail.Reason = "自身の通知実行記録と識別子が一致"; continue; }
            if (!input.Readable || collision) continue;
            var rules = s.Automation.Rules.Where(r => RuleMatches(r, folder.Key, mail)).ToList();
            if (rules.Count == 1) ApplyRule(s, mail, rules[0]);
            else if (rules.Count > 1) mail.Reason = "複数の規則に一致したため本人確認が必要です。";
        }
        c.Cursors[folder.Key] = Copy.Of(reply.Cursor);
        c.Cursors[folder.Key].CompletedAt = reply.Complete ? clock.Now : null;
        c.LastAttempt = clock.Now;
        var all = c.Folders.All(f => c.Cursors.TryGetValue(f.Key, out var cursor) && cursor.CompletedAt is not null);
        var errors = c.Cursors.Values.Sum(cursor => cursor.Errors);
        if (all && errors == 0) s.LastScan = clock.Now;
        c.Health = all ? $"指定範囲の走査終了。取得不能 {errors}件／本文確認不能の受付 {s.Inbox.Count(m => !m.Purged && (!m.SourceReadable || m.Status == IntakeStatus.ReadBlocked))}件。取得不能分は次回の全件照合で再試行します。対象外・未同期領域は未確認です。"
            : "照合途中です。残りのページ・フォルダーは未確認です。";
    }, false);
    public void ResetScan(string key, int generation) => Change("順序変更のため照合位置を戻す", s => CheckedScope(s, key, generation).Cursors[key] = new(), false);
    public static string Candidate(string body)
    {
        var matches = Regex.Matches(body, @"\d{4}[年/-]\d{1,2}[月/-]\d{1,2}日?|\d{1,2}月\d{1,2}日|明日|明後日|来週|来月末|至急|なるべく早く", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return string.Join("、", matches.Cast<Match>().Take(8).Select(m => m.Value).Distinct()) is { Length: > 0 } text
            ? "期限の候補（引用・否定・年・曜日を含め本人確認が必要）：" + text : "";
    }
    public static bool RuleMatches(MailRule rule, string folderKey, InboxItem mail) => rule.Enabled && rule.ApprovedAt is not null && mail.ReceivedAt >= rule.EffectiveFrom &&
        rule.FolderKey == folderKey && string.Equals(rule.Sender.Trim(), mail.Sender.Trim(), StringComparison.OrdinalIgnoreCase) &&
        mail.Subject.StartsWith(rule.SubjectPrefix, StringComparison.Ordinal) && (rule.ExactBodyTemplate.Length == 0 || TemplateDeadline(rule, mail.Body).matched);
    private static (bool matched, Deadline deadline) TemplateDeadline(MailRule rule, string body)
    {
        var template = rule.ExactBodyTemplate;
        var marker = template.IndexOf("{date}", StringComparison.Ordinal);
        if (marker < 0) return (body == template, new());
        var suffix = template[(marker + 6)..]; var prefix = template[..marker];
        if (!body.StartsWith(prefix, StringComparison.Ordinal) || !body.EndsWith(suffix, StringComparison.Ordinal) || body.Length < prefix.Length + suffix.Length) return (false, new());
        var dateText = body.Substring(prefix.Length, body.Length - prefix.Length - suffix.Length);
        if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return (false, new());
        return (true, new(DeadlineKind.Date, Date: date, Evidence: $"承認規則 {rule.Name} v{rule.Version}／原文全体一致／{body}"));
    }
    private static void ApplyRule(Snapshot s, InboxItem mail, MailRule rule)
    {
        mail.Reason = $"承認規則 {rule.Name} v{rule.Version}／差出人・対象・件名条件一致";
        if (rule.Action == MailRuleAction.Ignore) { mail.Status = IntakeStatus.Ignored; return; }
        var task = new WorkItem { Profile = s.ProfileId, Title = mail.Subject.Length == 0 ? "件名なしの依頼" : mail.Subject[..Math.Min(500, mail.Subject.Length)],
            CreatedAt = mail.ReceivedAt, ReceivedOn = Japan.Day(mail.ReceivedAt), SourceIds = [mail.Id], NextAction = rule.NextAction, Completion = rule.Completion, UrgentConfirmed = rule.Urgent };
        if (rule.ExactBodyTemplate.Contains("{date}", StringComparison.Ordinal)) task.Deadline = TemplateDeadline(rule, mail.Body).deadline with { SourceId = mail.Id };
        s.Tasks.Add(task); mail.Status = IntakeStatus.LinkedTask; mail.TaskId = task.Id;
    }
    public int PreviewRule(MailRule proposed) => Read().Inbox.Count(m => !m.Purged && m.Locations.Any(l => RuleMatches(proposed with { Enabled = true, ApprovedAt = clock.Now }, new MailFolder(l.Account,l.StoreId,l.FolderId,"").Key, m)));
    public void SaveRule(MailRule proposed) => Change("定型規則の影響を確認して保存：" + proposed.Name, s =>
    {
        if (!s.Automation.Connector.Folders.Any(f => f.Key == proposed.FolderKey)) throw new RuleException("許可した対象フォルダーの規則だけを保存できます。");
        var old = s.Automation.Rules.SingleOrDefault(r => r.Id == proposed.Id);
        if (old is not null && old.Version != proposed.Version) throw new RuleException("規則が変更されています。");
        var rule = Copy.Of(proposed); rule.ApprovedAt = clock.Now; rule.Version = old is null ? 1 : old.Version + 1;
        if (old is not null) { s.Automation.RuleHistory.Add(Copy.Of(old)); s.Automation.Rules.Remove(old); }
        s.Automation.Rules.Add(rule);
    }, false);
    public void DisableRule(string id) => Change("定型規則を停止", s => { var rule = s.Automation.Rules.Single(r => r.Id == id); s.Automation.RuleHistory.Add(Copy.Of(rule)); rule.Enabled = false; rule.Version++; }, false);
    public void LinkSource(string mailId, string taskId) => Change("原文を既存の仕事へ関連付け", s =>
    {
        var mail = s.Inbox.Single(m => m.Id == mailId); var task = Find(s, taskId);
        if (!task.SourceIds.Contains(mailId)) task.SourceIds.Add(mailId);
        mail.TaskId = taskId; mail.Status = IntakeStatus.LinkedTask; mail.Reason = "本人が関連を確認。期限や完了状態は変更していません。";
    });
    public void AddAnotherTask(string mailId, string title) => Change("一通の別依頼を仕事として追加", s =>
    {
        var mail = s.Inbox.Single(m => m.Id == mailId);
        s.Tasks.Add(new() { Profile = s.ProfileId, Title = title.Trim(), SourceIds = [mailId], CreatedAt = clock.Now, ReceivedOn = Japan.Day(mail.ReceivedAt) });
    }, false);
}
