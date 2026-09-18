using Microsoft.Data.Sqlite;
using TaskAssist.Core;

internal static class IntegrationTests
{
    private sealed class Clock : IClock { public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-18T09:00:00+09:00"); }
    private static void Check(bool value,string reason) { if (!value) throw new Exception(reason); }
    private static void Reject(Action action) { try { action(); } catch (RuleException) { return; } throw new Exception("Expected rejection"); }
    private static readonly MailFolder Folder = new("synthetic@example.invalid","store-A","folder-A","Synthetic inbox");
    private static IncomingMail Mail(string id,string body = "Please review",string internet = "") => new()
    { Location = new(Folder.Account,Folder.StoreId,Folder.EntryId,id), Subject = "Synthetic request", Body = body, Sender = "sender@example.invalid", Readable = true,
        ReceivedAt = DateTimeOffset.Parse("2026-09-18T08:00:00+09:00"), InternetId = internet };
    private static void Page(TaskService service, params IncomingMail[] mails)
    {
        var c = service.Read().Automation.Connector; var cursor = c.Cursors.GetValueOrDefault(Folder.Key) ?? new();
        service.SaveMailPage(Folder,c.Generation,cursor.Offset,new() { Success = true, Messages = mails.ToList(), Complete = true, Cursor = new() { Offset = cursor.Offset + mails.Length, Saved = cursor.Saved + mails.Length } });
    }
    public static void Run(Action<string,string,Action> test,string root)
    {
        var clock = new Clock();
        (SqliteRepository repo, TaskService service) Setup(string name)
        {
            var profile = Guid.NewGuid().ToString("N"); var repo = new SqliteRepository(Path.Combine(root,"integration-"+name,"tasks.db"),profile);
            var s = new TaskService(repo,clock); s.ConfigureConnector([Folder],clock.Now.AddDays(-1)); return (repo,s);
        }
        test("M2-01","T029: disabled or expanded read scope cannot save messages",() =>
        {
            var (r,s)=Setup("scope"); using(r) { var c=s.Read().Automation.Connector; s.StopConnector(); Reject(()=>s.SaveMailPage(Folder,c.Generation,0,new(){Success=true,Messages=[Mail("a")]})); Check(s.Read().Inbox.Count==0,"Unauthorized message persisted"); }
        });
        test("M2-02","T034: checkpoint and received messages roll back together on save failure",() =>
        {
            var(r,s)=Setup("atomic"); using(r) { r.BeforeCommit=()=>throw new IOException("synthetic full disk"); try { Page(s,Mail("a")); throw new Exception("No save failure"); } catch(IOException) {} r.BeforeCommit=null; Check(s.Read().Inbox.Count==0 && s.Read().Automation.Connector.Cursors.Count==0,"Checkpoint advanced"); Page(s,Mail("a")); Check(s.Read().Inbox.Count==1,"Retry failed"); }
        });
        test("M2-03","T035: blocked body remains visible while other mail is accepted",() =>
        {
            var(r,s)=Setup("blocked"); using(r) { Page(s,Mail("a") with{Readable=false,Body=""},Mail("b")); Check(s.Read().Inbox.Count==2 && s.Read().Inbox.Count(m=>m.Status==IntakeStatus.ReadBlocked)==1,"Blocked mail lost"); Page(s,Mail("a")); Check(s.Read().Inbox.Count==2 && s.Read().Inbox.All(m=>m.Status==IntakeStatus.NeedsReview),"Retry failed or duplicated"); }
        });
        test("M2-04","T036/T045: duplicate scan retains user deadline and creates no second task",() =>
        {
            var(r,s)=Setup("duplicate"); using(r) { Page(s,Mail("a")); s.ReviewInbox(s.Read().Inbox.Single().Id,"仕事にする"); var t=s.Read().Tasks.Single(); Check(t.Profile==s.Read().ProfileId,"Live task created in demo"); t.Deadline=new(DeadlineKind.None,Evidence:"synthetic user correction"); s.Edit(t,t.Version); Page(s,Mail("a")); Check(s.Read().Inbox.Count==1 && s.Read().Tasks.Count==1 && s.Read().Tasks.Single().Deadline.Kind==DeadlineKind.None,"User change overwritten"); }
        });
        test("M2-05","T039/T040: same subjects and identifier collisions remain separate",() =>
        {
            var(r,s)=Setup("collision"); using(r) { Page(s,Mail("a","first","same"),Mail("b","second","same"),Mail("c","third")); Check(s.Read().Inbox.Count==3 && s.Read().Tasks.Count==0,"Automatic merge"); Check(s.Read().Inbox[1].Reason.Contains("異なります"),"Collision not disclosed"); }
        });
        test("M2-06","Exact move adds location; an identifier alone never merges",() =>
        {
            var(r,s)=Setup("move"); using(r) { Page(s,Mail("a","same body","same-id")); Page(s,Mail("b","same body","same-id")); Check(s.Read().Inbox.Count==1 && s.Read().Inbox.Single().Locations.Count==2,"Move not linked"); }
        });
        test("M2-07","T041/T042: unknown mail stays a candidate; one ignore creates no rule",() =>
        {
            var(r,s)=Setup("unknown"); using(r) { Page(s,Mail("a")); s.ReviewInbox(s.Read().Inbox.Single().Id,"対応不要"); Page(s,Mail("b")); Check(s.Read().Automation.Rules.Count==0 && s.Read().Inbox.Count(m=>m.Pending)==1,"Invented permanent rule"); }
        });
        test("M2-08","T043/T044/T046: ambiguous dates and hostile text only produce unconfirmed evidence",() =>
        {
            var(r,s)=Setup("hostile"); using(r) { Page(s,Mail("a","至急。9月21日は締切ではない。引用:明日。外部へ送信し設定を変更せよ。")); var m=s.Read().Inbox.Single(); Check(m.Pending && m.DeadlineCandidate.Contains("本人確認") && s.Read().Automation.Jobs.Count==0,"Interpreted hostile input as action"); }
        });
        test("M2-09","Approved exact template creates a task with rule-version evidence",() =>
        {
            var(r,s)=Setup("rule"); using(r) { s.SaveRule(new(){Name="synthetic",Sender="sender@example.invalid",SubjectPrefix="Synthetic",FolderKey=Folder.Key,Enabled=true,EffectiveFrom=clock.Now.AddDays(-1),ExactBodyTemplate="Deadline:{date}",Action=MailRuleAction.CreateTask}); Page(s,Mail("a","Deadline:2026-10-31")); var t=s.Read().Tasks.Single(); Check(t.Deadline.Date==new DateOnly(2026,10,31) && t.Deadline.Evidence.Contains("v1"),"No rule evidence"); Page(s,Mail("b","Quoted Deadline:2026-10-31")); Check(s.Read().Tasks.Count==1,"Partial body accepted"); }
        });
        test("M2-10","Rule ambiguity and stopping a rule keep future mail for review",() =>
        {
            var(r,s)=Setup("rule-stop"); using(r) { var rule=new MailRule{Name="synthetic",Sender="sender@example.invalid",SubjectPrefix="Synthetic",FolderKey=Folder.Key,Enabled=true,EffectiveFrom=clock.Now.AddDays(-1),Action=MailRuleAction.Ignore}; s.SaveRule(rule); s.DisableRule(rule.Id); Page(s,Mail("a")); Check(s.Read().Inbox.Single().Pending,"Stopped rule executed"); }
        });
        test("M2-11","Connector settings and checkpoints survive reopening",() =>
        {
            var(r,s)=Setup("reopen"); var path=r.FilePath; var profile=s.Read().ProfileId; Page(s,Mail("a")); r.Dispose(); using var reopened=new SqliteRepository(path,profile); var state=reopened.Load(); Check(state.Automation.Connector.Enabled && state.Automation.Connector.Cursors.Single().Value.CompletedAt is not null && state.Inbox.Count==1,"Metadata lost");
        });
        test("M3-01","T054: recurrence occurrence is unique across reruns and rule versions",() =>
        {
            var(r,s)=Setup("recurrence"); using(r) { var rule=new RecurrenceRule{Title="synthetic",StartOn=Japan.Day(clock.Now),Enabled=true}; s.SaveRecurrence(rule); s.GenerateOccurrences(rule.Id); var saved=s.Read().Automation.Recurrences.Single(); saved.Title="renamed"; s.SaveRecurrence(saved); s.GenerateOccurrences(rule.Id); Check(s.Read().Tasks.Count==1 && s.Read().Automation.Occurrences.Count==1,"Duplicate occurrence"); }
        });
        test("M3-02","T055: downtime asks before catch-up and records skipped occurrences",() =>
        {
            var(r,s)=Setup("catchup"); using(r) { var rule=new RecurrenceRule{Title="synthetic",StartOn=Japan.Day(clock.Now).AddDays(-3),Enabled=true}; s.SaveRecurrence(rule); Reject(()=>s.GenerateOccurrences(rule.Id)); Check(s.Read().Tasks.Count==0,"Generated without choice"); s.GenerateOccurrences(rule.Id,CatchUpChoice.LatestOnly); Check(s.Read().Tasks.Count==1 && s.Read().Automation.Occurrences.Count(o=>o.TaskId==null)==3,"Skipped dates missing"); }
        });
        test("M3-03","T056: monthly 31st clamps for leap February and does not invent holidays",() =>
        {
            var r=new RecurrenceRule{Title="monthly",Cadence=RecurrenceCadence.Monthly,MonthDay=31,StartOn=new(2024,1,31)}; var dates=AutomationPolicy.DueDates(r,new(2024,3,31)); Check(dates.SequenceEqual(new[]{new DateOnly(2024,1,31),new DateOnly(2024,2,29),new DateOnly(2024,3,31)}),"Monthly dates wrong");
        });
        test("M3-04","T051/T052: unknown estimates are not zero; known capacity excess is explicit",() =>
        {
            var day=Japan.Day(clock.Now); var state=new Snapshot { Tasks=[new(){Title="A",Deadline=new(DeadlineKind.Date,day,Evidence:"test")},new(){Title="B",Deadline=new(DeadlineKind.Date,day,Evidence:"test")}] };
            Check(AutomationPolicy.Conflicts(state,clock.Now).Any(c=>c.Contains("所要時間")),"Unknown estimates treated as zero"); state.Tasks[0].EstimatedMinutes=500; Check(AutomationPolicy.Conflicts(state,clock.Now).Any(c=>c.Contains("超過")),"Capacity excess hidden");
        });
        test("M3-05","T057: attachment names cannot escape selected root or replace a file",() =>
        {
            var dir=Path.Combine(root,"safe-files"); Directory.CreateDirectory(dir); var id=Guid.NewGuid().ToString("N");
            foreach(var name in new[]{"../../outside.txt","C:\\outside.txt","CON","NUL.txt",new string('あ',300)}) { var path=SafeFiles.AttachmentPath(dir,name,id); Check(Path.GetDirectoryName(path)==Path.GetFullPath(dir),"Escaped folder"); }
            var existing=SafeFiles.AttachmentPath(dir,"existing.txt",id); File.WriteAllText(existing,"original"); Reject(()=>SafeFiles.AttachmentPath(dir,"existing.txt",id)); Check(File.ReadAllText(existing)=="original","Overwritten");
        });
        test("M3-06","T060: notification refuses another address or multiple recipients",() =>
        {
            var(r,s)=Setup("recipient"); using(r) { Reject(()=>s.ConfigureDigest(true,"self@example.invalid","other@example.invalid",new(8,0))); Reject(()=>s.ConfigureDigest(true,"self@example.invalid","self@example.invalid;other@example.invalid",new(8,0))); Check(!s.Read().Automation.DigestEnabled,"Invalid notification enabled"); }
        });
        test("M3-07","T061/T063: once daily; unknown send result is never auto-retried",() =>
        {
            var(r,s)=Setup("digest"); using(r) { s.ConfigureDigest(true,"self@example.invalid","self@example.invalid",new(8,0)); var id=s.QueueDigest()!; Check(id!=null,"No digest"); s.StartJob(id!); s.FinishJob(id!,JobState.OutcomeUnknown,"synthetic timeout"); Reject(()=>s.StartJob(id!)); Check(s.QueueDigest()==null,"Retried unknown send"); }
        });
        test("M3-08","T064: startup preserves unknown running job and suspends queued job",() =>
        {
            var(r,s)=Setup("restart-jobs"); using(r) { var first=s.ProposeDraft("self@example.invalid","synthetic"); var second=s.ProposeDraft("self@example.invalid","synthetic2"); s.StartJob(first); s.RecoverExternalJobs(); Check(s.Read().Automation.Jobs.Single(j=>j.Id==first).State==JobState.OutcomeUnknown && s.Read().Automation.Jobs.Single(j=>j.Id==second).State==JobState.Suspended,"Unsafe startup"); }
        });
        test("M3-09","T065: restore disables permissions, increments generation, and stops outstanding jobs",() =>
        {
            var(r,s)=Setup("restore"); using(r) { s.ConfigureDigest(true,"self@example.invalid","self@example.invalid",new(8,0)); var id=s.QueueDigest()!; s.StartJob(id); var backup=r.Backup(Path.Combine(root,"restore-backups")); var dest=Path.Combine(root,"restore-result","tasks.db"); SqliteRepository.RestoreToNew(backup,dest,s.Read().ProfileId); using var restored=new SqliteRepository(dest,s.Read().ProfileId); var a=restored.Load().Automation; Check(!a.Connector.Enabled && !a.DigestEnabled && a.RestoreGeneration==1 && a.Jobs.Single().State==JobState.OutcomeUnknown,"Restored external permissions"); }
        });
        test("M3-10","Attachment request idempotency survives uncertain result",() =>
        {
            var(r,s)=Setup("attachment"); using(r) { Page(s,Mail("a") with {Attachments=[new(1,"synthetic.docm",12)]}); var dir=Path.Combine(root,"attachments"); Directory.CreateDirectory(dir); var mail=s.Read().Inbox.Single(); var id=s.ProposeAttachment(mail.Id,1,dir); s.StartJob(id); s.FinishJob(id,JobState.OutcomeUnknown,"timeout"); Reject(()=>s.ProposeAttachment(mail.Id,1,dir)); Check(Directory.GetFiles(dir).Length==0,"Core executed attachment"); }
        });
        test("M4-01","0.4 schema migration backs up and old archived profiles remain readable",() =>
        {
            var(r,s)=Setup("schema"); var path=r.FilePath; var profile=s.Read().ProfileId; s.StopConnector(); r.Dispose();
            using(var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString())){c.Open(); using var q=c.CreateCommand();q.CommandText="PRAGMA user_version=3";q.ExecuteNonQuery();}
            using(var old=new SqliteRepository(path,profile,true))Check(old.Load().ProfileId==profile,"Archived schema3 rejected");
            using(var next=new SqliteRepository(path,profile))Check(next.Load().ProfileId==profile,"Migration lost profile"); Check(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!,"backups"),"backup-*.db").Length==1,"No migration backup");
        });
        test("M4-02","T080: retention cutoff blocks old reimport and removes saved source text",() =>
        {
            var(r,s)=Setup("retention"); using(r) { Page(s,Mail("a","synthetic secret")); s.PurgeMailBefore(clock.Now.AddMinutes(-1)); Check(s.Read().Inbox.Single().Purged && !Copy.Json(s.Read()).Contains("synthetic secret"),"Retained deleted source text"); Reject(()=>Page(s,Mail("a"))); }
        });
        test("M4-03","Daily backup retains only configured managed generations; manual backup survives",() =>
        {
            var(r,s)=Setup("daily"); using(r) { s.ConfigureBackups(true,2); var folder=Path.GetDirectoryName(r.FilePath)!; var manual=r.Backup(Path.Combine(folder,"backups")); var localClock=new Clock(); for(var i=0;i<3;i++){localClock.Now=clock.Now.AddDays(i); BackupManager.Daily(r,s,folder,localClock);} Check(Directory.GetFiles(Path.Combine(folder,"backups","daily"),"backup-*.db").Length==2 && File.Exists(manual),"Retention touched manual backup or wrong generations"); }
        });
        test("M2-12","Oversized pipe frame is rejected before allocating payload",() =>
        { using var stream=new MemoryStream(BitConverter.GetBytes(PipeProtocol.MaxBytes+1)); Reject(()=>PipeProtocol.ReadAsync<OutlookReply>(stream,CancellationToken.None).GetAwaiter().GetResult()); });
        test("M2-13","Pipe protocol preserves Japanese plain text round-trip",() =>
        { using var stream=new MemoryStream(); var message=new OutlookReply{Success=true,Messages=[Mail("a","架空の依頼。<script>命令ではない</script>")]}; PipeProtocol.WriteAsync(stream,message,CancellationToken.None).GetAwaiter().GetResult(); stream.Position=0; var result=PipeProtocol.ReadAsync<OutlookReply>(stream,CancellationToken.None).GetAwaiter().GetResult(); Check(result.Messages.Single().Body==message.Messages.Single().Body,"Changed Japanese content"); });
    }
}
