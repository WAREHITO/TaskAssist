using System.Text.Json;
using TaskAssist.Core;

internal static class IntegrationRegressionTests
{
    private sealed class Clock : IClock { public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-18T09:00:00+09:00"); }
    private static void Check(bool ok,string message) { if (!ok) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch(RuleException) { return; } throw new Exception("Expected rejection"); }
    public static void Run(Action<string,string,Action> test,string root)
    {
        var clock = new Clock();
        var scope = new MailFolder("synthetic@example.invalid","store","folder","Synthetic");
        IncomingMail Mail(int index) => new() { Location = new(scope.Account,scope.StoreId,scope.EntryId,"mail-"+index), Readable = true, ReceivedAt = clock.Now.AddMinutes(-index), LastModifiedAt = clock.Now, Subject = "SYNTHETIC", Sender = "sender@example.invalid", Body = "架空の原文" };
        test("DR-01","Long Japanese and emoji pages split by actual wire size without losing mail",() =>
        {
            var cursor = new ScanCursor(); var saved = new List<string>(); var pages = 0;
            while (saved.Count < 55)
            {
                var page = MailPager.Read(cursor,55,i => new("mail-"+i,Mail(i) with { Body = new string('あ',62000) + "😀🧑‍💻" }));
                Check(JsonSerializer.SerializeToUtf8Bytes(page).Length <= PipeProtocol.MaxBytes,"Oversize wire page");
                Check(page.Cursor.Offset > cursor.Offset,"No progress"); saved.AddRange(page.Messages.Select(m=>m.Location.EntryId)); cursor = page.Cursor; pages++;
                Check(pages < 10,"Unbounded paging");
            }
            Check(saved.Distinct().Count()==55 && cursor.Remaining==0 && pages>1,"Lost or duplicate mail");
        });
        test("DR-02","Item/header failures at start, middle, end persist while later pages advance",() =>
        {
            var failed = new[]{1,2,25,50}; var page=MailPager.Read(new(),55,i => failed.Contains(i) ? throw new IOException("synthetic unreadable item") : new("mail-"+i,Mail(i)));
            Check(page.Cursor.Offset==50 && page.Cursor.Errors==4 && page.Messages.Count==46 && page.Cursor.FailedPositions.SequenceEqual(failed),"Failure isolation missing");
            var last=MailPager.Read(page.Cursor,55,i=>new("mail-"+i,Mail(i))); Check(last.Complete && last.Messages.Count==5 && last.Cursor.Errors==4,"Incomplete scan hidden");
            var recovered=MailPager.Read(new(),55,i=>new("mail-"+i,Mail(i))); Check(recovered.Cursor.Errors==0 && recovered.Messages.Count==50,"Full rescan failed to recover");
        });
        test("DR-03","Unusable recurrence start is rejected before save; old invalid rule count is safe",() =>
        {
            using var r=new SqliteRepository(Path.Combine(root,"regression-recurrence","tasks.db")); var s=new TaskService(r,clock);
            var bad=new RecurrenceRule{Title="synthetic",StartOn=new(2000,1,1),Enabled=true}; Reject(()=>s.SaveRecurrence(bad));
            Check(s.Read().Automation.Recurrences.Count==0 && AutomationPolicy.PendingCount(bad,Japan.Day(clock.Now))==-1,"Bad persisted or display throws");
            s.SaveRecurrence(bad with{StartOn=Japan.Day(clock.Now).AddYears(-10)}); Check(s.Read().Automation.Recurrences.Count==1,"Boundary rejected");
        });
        test("DR-04","Retention removes automatically copied body from current and historical deadline evidence",() =>
        {
            var profile=Guid.NewGuid().ToString("N"); var path=Path.Combine(root,"regression-retention","tasks.db");
            using(var r=new SqliteRepository(path,profile))
            {
                var s=new TaskService(r,clock); s.ConfigureConnector([scope],clock.Now.AddDays(-1));
                s.SaveRule(new(){Name="synthetic",Sender="sender@example.invalid",SubjectPrefix="SYNTHETIC",FolderKey=scope.Key,Enabled=true,EffectiveFrom=clock.Now.AddDays(-1),ExactBodyTemplate="RAW-SYNTHETIC:{date}"});
                var mail=Mail(1) with{Body="RAW-SYNTHETIC:2026-10-31"};
                s.SaveMailPage(scope,s.Read().Automation.Connector.Generation,0,new(){Success=true,Messages=[mail],Complete=true,Cursor=new(){Offset=1}});
                var task=s.Read().Tasks.Single(); task.Note="user note must remain"; s.Edit(task,task.Version);
                s.PurgeMailBefore(clock.Now); var state=s.Read();
                Check(!Copy.Json(state).Contains("RAW-SYNTHETIC:2026-10-31") && state.Tasks.Single().Note=="user note must remain" && state.Tasks.Single().Deadline.Date==new DateOnly(2026,10,31),"Raw body retained or user work lost");
            }
            using var reopened=new SqliteRepository(path,profile); Check(!Copy.Json(reopened.Load()).Contains("RAW-SYNTHETIC:2026-10-31"),"Raw evidence restored on reopen");
        });
        test("DR-07","Attachment approval rejects metadata changes and same-name same-size later modification",() =>
        {
            var job=new ExternalJob{AttachmentName="approved.docm",AttachmentBytes=10,ExpectedModifiedAt=clock.Now};
            Check(SafeFiles.ApprovedAttachmentMatches(job,"approved.docm",10,clock.Now),"Unchanged attachment rejected");
            Check(!SafeFiles.ApprovedAttachmentMatches(job,"other.docm",10,clock.Now) && !SafeFiles.ApprovedAttachmentMatches(job,"approved.docm",11,clock.Now) && !SafeFiles.ApprovedAttachmentMatches(job,"approved.docm",10,clock.Now.AddSeconds(1)),"Changed attachment accepted");
        });
        test("DR-08","Read scope and notification permission history contains old and new values after reopen",() =>
        {
            var path=Path.Combine(root,"regression-permission","tasks.db");
            using(var r=new SqliteRepository(path)) { var s=new TaskService(r,clock); s.ConfigureConnector([scope],clock.Now.AddDays(-1)); s.ConfigureDigest(true,"self@example.invalid","self@example.invalid",new(16,0)); s.ConfigureDigest(false,"","",new(17,0)); }
            using var reopened=new SqliteRepository(path); var changes=reopened.Load().Events.Where(e=>e.ConfigurationAfter.Length>0).ToList();
            Check(changes.Count==3 && changes.Last().ConfigurationBefore.Contains("self@example.invalid") && changes.Last().ConfigurationAfter.Contains("17:00"),"Permission history incomplete");
        });
        test("M2-HINTS","Relative dates use received Japanese date; yearless and urgent phrases stay unspecified",() =>
        {
            var hints=DeadlineHints.Extract("9月21日、明日、至急。引用:来週、来月末",DateTimeOffset.Parse("2026-09-18T16:30:00Z"));
            Check(hints.Single(h=>h.Text=="明日").Day==new DateOnly(2026,9,20),"Wrong received timezone");
            Check(hints.Single(h=>h.Text=="9月21日").Day is null && hints.Single(h=>h.Text=="至急").Day is null && hints.Single(h=>h.Text=="来週").EndDay is not null,"Invented exact deadline");
        });
        test("M4-ROLLBACK","Legacy rollback verifies a schema 3 backup without upgrading the replacement",() =>
        {
            var path=Path.Combine(root,"legacy-rollback","tasks.db"); string backup;
            using(var r=new SqliteRepository(path)) { new TaskService(r,clock).Add("synthetic old task"); backup=r.Backup(Path.Combine(root,"legacy-rollback","backup")); }
            using(var c=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+backup)) { c.Open(); using var q=c.CreateCommand(); q.CommandText="PRAGMA user_version=3"; q.ExecuteNonQuery(); }
            var target=Path.Combine(root,"legacy-rollback","restored.db"); SqliteRepository.RestoreToNew(backup,target,forLegacyApplication:true);
            using var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+target); connection.Open(); using var query=connection.CreateCommand(); query.CommandText="PRAGMA user_version";
            Check(Convert.ToInt32(query.ExecuteScalar())==3,"Rollback immediately migrated forward");
            using var restored=new SqliteRepository(target,readOnly:true); Check(restored.Load().Tasks.Single().Title=="synthetic old task","Rollback lost work");
        });
        test("M3-RECEIPT","Lost attachment response can verify own result and refuses modified content",() =>
        {
            var dir=Path.Combine(root,"receipt"); Directory.CreateDirectory(dir); var id=Guid.NewGuid().ToString("N"); var path=SafeFiles.AttachmentPath(dir,"synthetic.docm",id);
            var job=new ExternalJob{Id=id,Destination=path,Kind=JobKind.Attachment}; File.WriteAllText(path,"synthetic saved bytes");
            var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))); SafeFiles.WriteReceipt(job,hash);
            Check(SafeFiles.VerifyReceipt(job)==hash,"Saved result not verified"); File.WriteAllText(path,"modified"); Reject(()=>SafeFiles.VerifyReceipt(job));
        });
        test("M2-HINT-CONFIRM","Candidate confirmation creates one task and preserves source and actor evidence",() =>
        {
            using var r=new SqliteRepository(Path.Combine(root,"regression-hint-confirm","tasks.db"),Guid.NewGuid().ToString("N")); var s=new TaskService(r,clock); s.ConfigureConnector([scope],clock.Now.AddDays(-1));
            s.SaveMailPage(scope,s.Read().Automation.Connector.Generation,0,new(){Success=true,Messages=[Mail(1) with{Body="明日が期限"}],Cursor=new(){Offset=1},Complete=true});
            var mail=s.Read().Inbox.Single(); var hint=mail.DeadlineHints.Single(); Check(s.Read().Tasks.Count==0,"Candidate auto accepted");
            s.ConfirmDeadlineHint(mail.Id,hint.Id); s.ConfirmDeadlineHint(mail.Id,hint.Id);
            Check(s.Read().Tasks.Count==1 && s.Read().Tasks.Single().Deadline.SourceId==mail.Id && s.Read().Tasks.Single().Deadline.Evidence.Contains("本人確認"),"Confirmation lost evidence or duplicated");
        });
        test("R13-PENDING-DATE","Accepting mail as work keeps an unresolved today hint until deadline confirmation or closure",() =>
        {
            using var r=new SqliteRepository(Path.Combine(root,"today-attention","tasks.db"),Guid.NewGuid().ToString("N")); var s=new TaskService(r,clock); s.ConfigureConnector([scope],clock.Now.AddDays(-1));
            s.SaveMailPage(scope,s.Read().Automation.Connector.Generation,0,new(){Success=true,Messages=[Mail(1) with{Body="今日が期限という引用です"}],Cursor=new(){Offset=1},Complete=true});
            var mail=s.Read().Inbox.Single(); Check(AutomationPolicy.PendingDeadlineSources(s.Read(),clock.Now).Count==1,"Initial hint absent");
            s.ReviewInbox(mail.Id,"仕事にする"); Check(AutomationPolicy.PendingDeadlineSources(s.Read(),clock.Now).Count==1,"Classification hid unresolved deadline");
            var task=s.Read().Tasks.Single(); task.Deadline=new(DeadlineKind.None,Evidence:"本人が期限なしと確認"); s.Edit(task,task.Version);
            Check(AutomationPolicy.PendingDeadlineSources(s.Read(),clock.Now).Count==0,"Confirmed deadline still pending");
            task=s.Read().Tasks.Single(); task.Deadline=new(); s.Edit(task,task.Version); task=s.Read().Tasks.Single(); s.Transition(task.Id,task.Version,WorkStatus.Completed);
            Check(AutomationPolicy.PendingDeadlineSources(s.Read(),clock.Now).Count==0,"Completed work still pending");
        });
        test("R22-STOP-HEALTH","Stopping automation persists an explicit unconfirmed-since-stop health message",() =>
        {
            var path=Path.Combine(root,"stop-health","tasks.db");
            using(var r=new SqliteRepository(path)) { var s=new TaskService(r,clock); s.ConfigureConnector([scope],clock.Now.AddDays(-1)); s.SuspendAutomation(); }
            using var reopened=new SqliteRepository(path); var c=reopened.Load().Automation.Connector;
            Check(!c.Enabled && c.Health.Contains("停止") && c.Health.Contains("未確認"),"Stale running health remains");
        });
    }
}
