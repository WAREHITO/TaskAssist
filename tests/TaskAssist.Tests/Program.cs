using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TaskAssist.Core;

if (args.FirstOrDefault() == "lease")
{
    try { using var lease = new InstanceLease(args[1]); return 0; } catch (IOException) { return 23; }
}
var results = new List<object>(); var failed = 0;
var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskAssist", "Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "mail_scenarios.json"));
void Test(string id, string name, Action action)
{
    var watch = Stopwatch.StartNew();
    try { action(); results.Add(new { id, name, status = "passed", milliseconds = watch.ElapsedMilliseconds }); Console.WriteLine($"PASS {id} {name}"); }
    catch (Exception e) { failed++; results.Add(new { id, name, status = "failed", error = e.GetType().Name, detail = e.Message, milliseconds = watch.ElapsedMilliseconds }); Console.WriteLine($"FAIL {id} {name}: {e}"); }
}
static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
static void Reject(Action action) { try { action(); } catch (RuleException) { return; } throw new Exception("Expected rejection"); }
SqliteRepository Repo(string name) => new(Path.Combine(root, name, "tasks.db"));
var clock = new FakeClock();
WorkItem Get(TaskService service, string id) => service.Read().Tasks.Single(t => t.Id == id);
void Date(TaskService service, string id, Deadline deadline)
{
    var task = Get(service, id); task.Deadline = deadline; service.Edit(task, task.Version);
}
Test("T007", "Title-only registration preserves unknown deadline", () =>
{
    using var r = Repo("title"); var s = new TaskService(r, clock); var id = s.Add("電話の架空依頼");
    Check(Get(s,id).Deadline.Kind == DeadlineKind.Unknown && Get(s,id).Title == "電話の架空依頼", "One required field");
    Reject(() => s.Add(" ")); Check(s.Read().Tasks.Count == 1, "Invalid task persisted");
});
Test("T008", "Switch is atomic; a failed switch rolls back both tasks", () =>
{
    using var r = Repo("switch"); var s = new TaskService(r, clock); var a=s.Add("A"); var b=s.Add("B"); s.Start(a,1);
    r.BeforeCommit = () => throw new IOException("synthetic disk full");
    try { s.Start(b,1); throw new Exception("No failure"); } catch (IOException) { }
    Check(Get(s,a).Status == WorkStatus.Working && Get(s,b).Status == WorkStatus.NotStarted, "Partial switch");
    r.BeforeCommit=null; s.Start(b,1); Check(Get(s,a).Status == WorkStatus.Paused && Get(s,b).Status == WorkStatus.Working,"Switch failed");
});
Test("T009", "Completion survives reopen; undo restores previous state", () =>
{
    var path=Path.Combine(root,"complete","tasks.db"); string id,ev;
    using(var r=new SqliteRepository(path)) { var s=new TaskService(r,clock); id=s.Add("A"); s.Start(id,1); s.Transition(id,2,WorkStatus.Completed); ev=s.Read().Events.Last().Id; }
    using(var r=new SqliteRepository(path)) { var s=new TaskService(r,clock); Check(Get(s,id).CompletedAt == clock.Now,"CompletedAt missing"); s.Undo(ev); Check(Get(s,id).Status==WorkStatus.Working && Get(s,id).CompletedAt is null,"Undo failed"); }
});
Test("T010", "Duplicate completion creates exactly one history entry", () =>
{
    using var r=Repo("duplicate"); var s=new TaskService(r,clock); var id=s.Add("A"); s.Transition(id,1,WorkStatus.Completed); var count=s.Read().Events.Count;
    s.Transition(id,1,WorkStatus.Completed); Check(s.Read().Events.Count==count,"Duplicate history");
});
Test("T011", "Failed transaction preserves persisted data after reopen", () =>
{
    var path=Path.Combine(root,"failure","tasks.db"); string id;
    using(var r=new SqliteRepository(path)) { var s=new TaskService(r,clock); id=s.Add("保存済み"); r.BeforeCommit=()=>throw new IOException("synthetic disk full"); try { s.Transition(id,1,WorkStatus.Completed); } catch(IOException) { } Check(Get(s,id).Status==WorkStatus.NotStarted,"Cache mutated on failure"); }
    using(var r=new SqliteRepository(path)) Check(r.Load().Tasks.Single().Status==WorkStatus.NotStarted,"Disk mutated on failure");
});
Test("T012-auto", "A separate process cannot acquire the same data directory", () =>
{
    var folder=Path.Combine(root,"lease"); using var lease=new InstanceLease(folder);
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false, CreateNoWindow=true };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet",StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(FakeClock).Assembly.Location);
    start.ArgumentList.Add("lease"); start.ArgumentList.Add(folder);
    using var child=Process.Start(start)!; Check(child.WaitForExit(10000),"Lease process timed out"); Check(child.ExitCode==23,"Second instance acquired lock");
});
Test("T013", "Tomorrow review leaves today's real deadline and alert intact", () =>
{
    using var r=Repo("tomorrow"); var s=new TaskService(r,clock); var id=s.Add("A"); var day=Japan.Day(clock.Now); Date(s,id,new(DeadlineKind.Date,day,Evidence:"本人確認"));
    s.Transition(id,Get(s,id).Version,WorkStatus.Waiting,review:day.AddDays(1));
    Check(Get(s,id).Deadline.Date==day && Policy.Alerts(s.Read(),clock.Now).Count==1,"Deadline lost");
});
Test("T014", "All four deadline kinds survive disk roundtrip", () =>
{
    var path=Path.Combine(root,"dates","tasks.db");
    using(var r=new SqliteRepository(path)) { var s=new TaskService(r,clock); foreach(var deadline in new Deadline[]{new(),new(DeadlineKind.None,Evidence:"本人確認"),new(DeadlineKind.Date,new(2026,9,22),Evidence:"原文"),new(DeadlineKind.DateTime,At:clock.Now,Evidence:"原文")}) { var id=s.Add(deadline.Kind.ToString()); Date(s,id,deadline); } }
    using(var r=new SqliteRepository(path)) { var tasks=r.Load().Tasks; Check(tasks.Select(t=>t.Deadline.Kind).Distinct().Count()==4,"Kinds collapsed"); Check(tasks.Single(t=>t.Deadline.Kind==DeadlineKind.Date).Deadline.At is null,"Invented time"); }
});
Test("T015", "Japan midnight; date overdue differs from datetime overdue", () =>
{
    var date=new Deadline(DeadlineKind.Date,new(2026,9,18),Evidence:"原文");
    var before=DateTimeOffset.Parse("2026-09-18T14:59:59Z"); var after=before.AddSeconds(1);
    Check(date.Display(before).Contains("今日") && date.Display(after).Contains("期限日経過"),"Midnight boundary");
    Check(!date.Display(after).Contains("23:59") && new Deadline(DeadlineKind.DateTime,At:before,Evidence:"原文").Display(after).Contains("時刻超過"),"Invented time");
});
Test("T016", "A saved substep cannot complete the task; required confirmation enforced", () =>
{
    using var r=Repo("steps"); var s=new TaskService(r,clock); var id=s.Add("正式回答"); var t=Get(s,id); t.DoneSteps="添付保存済み"; t.Completion="正式回答"; t.ConfirmCompletion=true; s.Edit(t,t.Version);
    Check(!Get(s,id).Closed,"Step completed entire task"); Reject(()=>s.Transition(id,2,WorkStatus.Completed)); s.Transition(id,2,WorkStatus.Completed,confirmed:true); Check(Get(s,id).Closed,"Confirmed completion failed");
});
Test("T017", "Parent completion with open child is blocked; child not silently completed", () =>
{
    using var r=Repo("parent"); var s=new TaskService(r,clock); var parent=s.Add("親"); var child=s.Add("子"); var t=Get(s,child); t.ParentId=parent; s.Edit(t,t.Version);
    Reject(()=>s.Transition(parent,1,WorkStatus.Completed)); Check(!Get(s,child).Closed && !Get(s,parent).Closed,"Child auto completed");
});
Test("T018", "Pause note, steps and source links restored after reopen", () =>
{
    var path=Path.Combine(root,"pause","tasks.db"); string id;
    using(var r=new SqliteRepository(path)) { var s=new TaskService(r,clock); s.ImportDemo(fixture); id=s.Read().Tasks.First().Id; var t=Get(s,id); t.DoneSteps="資料確認済み"; t.Material="架空資料A"; s.Edit(t,t.Version); s.Start(id,Get(s,id).Version); s.Transition(id,Get(s,id).Version,WorkStatus.Paused,"表の2行目から再開"); }
    using(var r=new SqliteRepository(path)) { var t=r.Load().Tasks.Single(t=>t.Id==id); Check(t.Status==WorkStatus.Paused && t.Note=="表の2行目から再開" && t.DoneSteps=="資料確認済み" && t.Material=="架空資料A" && t.SourceIds.Count==1,"Paused state incomplete"); }
});
Test("T019", "Waiting excludes candidate but retains overdue alert", () =>
{
    using var r=Repo("waiting"); var s=new TaskService(r,clock); var id=s.Add("待つ"); Date(s,id,new(DeadlineKind.Date,Japan.Day(clock.Now).AddDays(-1),Evidence:"原文")); s.Transition(id,2,WorkStatus.Waiting);
    Check(Policy.Candidates(s.Read(),clock.Now).Count==0 && Policy.Alerts(s.Read(),clock.Now).Count==1,"Waiting deadline hidden");
});
Test("T020", "Adding an urgent dated task does not switch current work", () =>
{
    using var r=Repo("pin"); var s=new TaskService(r,clock); var current=s.Add("現在"); s.Start(current,1); var other=s.Add("期限"); Date(s,other,new(DeadlineKind.Date,Japan.Day(clock.Now),Evidence:"本人確認"));
    Check(Get(s,current).Status==WorkStatus.Working && Get(s,other).Status==WorkStatus.NotStarted,"Unrequested switch");
});
Test("T021", "Priority is stable and has reasons; unknown before confirmed none", () =>
{
    using var r=Repo("priority"); var s=new TaskService(r,clock); var unknown=s.Add("未確認"); var none=s.Add("期限なし"); Date(s,none,new(DeadlineKind.None,Evidence:"本人確認")); var due=s.Add("今日"); Date(s,due,new(DeadlineKind.Date,Japan.Day(clock.Now),Evidence:"本人確認"));
    var order=Policy.Candidates(s.Read(),clock.Now); Check(order.Select(t=>t.Id).SequenceEqual(new[]{due,unknown,none}),"Order incorrect");
    Check(Policy.Candidates(s.Read(),clock.Now).Select(t=>t.Id).SequenceEqual(order.Select(t=>t.Id)) && order.All(t=>Policy.Reason(t,clock.Now).Length>0),"Unstable or unexplained");
});
Test("T022", "Unknown deadlines remain accessible beyond three candidates", () =>
{
    using var r=Repo("unknown"); var s=new TaskService(r,clock); for(var i=0;i<8;i++) s.Add("期限未確認"+i);
    Check(s.Read().Tasks.Count(t=>t.Deadline.Kind==DeadlineKind.Unknown)==8 && Policy.Candidates(s.Read(),clock.Now).Count==8,"Records discarded");
});
Test("T023", "Dependency cycle rejection leaves prior dependencies intact", () =>
{
    using var r=Repo("cycle"); var s=new TaskService(r,clock); var a=s.Add("A"); var b=s.Add("B"); var c=s.Add("C");
    var t=Get(s,a); t.Prerequisites=[b]; s.Edit(t,1); t=Get(s,b); t.Prerequisites=[c]; s.Edit(t,1); t=Get(s,c); t.Prerequisites=[a]; Reject(()=>s.Edit(t,1)); Check(Get(s,c).Prerequisites.Count==0,"Cycle persisted");
});
Test("T024", "Prerequisite completion unlocks candidate without changing current", () =>
{
    using var r=Repo("ready"); var s=new TaskService(r,clock); var a=s.Add("前提"); var b=s.Add("後続"); var current=s.Add("現在"); var t=Get(s,b); t.Prerequisites=[a]; s.Edit(t,1); s.Start(current,1);
    Check(!Policy.Candidates(s.Read(),clock.Now).Any(t=>t.Id==b),"Blocked task eligible"); s.Transition(a,1,WorkStatus.Completed);
    Check(Policy.Candidates(s.Read(),clock.Now).Any(t=>t.Id==b) && Get(s,current).Status==WorkStatus.Working,"Dependency resolution switched current");
});
Test("T025", "DPAPI payload contains no plaintext subject body or sender in database/WAL", () =>
{
    using var r=Repo("protected"); var s=new TaskService(r,clock); s.ImportDemo(fixture); s.Add("SYNTHETIC-SECRET-9f8015");
    foreach(var file in Directory.GetFiles(Path.GetDirectoryName(r.FilePath)!,"tasks.db*"))
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); var text=Encoding.UTF8.GetString(buffer.ToArray());
        Check(!text.Contains("SYNTHETIC-SECRET-9f8015") && !text.Contains("requester@example.invalid") && !text.Contains("回答人数"),"Plaintext payload persisted");
    }
});
Test("T026", "Concurrent writer and online backup restore an internally consistent snapshot", () =>
{
    using var r=Repo("backup"); var s=new TaskService(r,clock); s.Add("最初");
    var writer=Task.Run(()=> { for(var i=0;i<12;i++) s.Add("同時書込"+i); });
    var backup=r.Backup(Path.Combine(root,"backups")); writer.GetAwaiter().GetResult();
    var restored=Path.Combine(root,"restored","tasks.db"); SqliteRepository.RestoreToNew(backup,restored);
    using var r2=new SqliteRepository(restored); var state=r2.Load(); Check(state.Tasks.Count>=1 && state.Tasks.Count<=13 && state.Events.Count==state.Tasks.Count,"Inconsistent backup");
    Reject(()=>SqliteRepository.RestoreToNew(backup,restored));
});
Test("T027", "Stopped intake shows unconfirmed and retains last reconciliation time", () =>
{
    using var r=Repo("health"); var s=new TaskService(r,clock); s.ImportDemo(fixture); var scan=s.Read().LastScan; s.SetDemoConnection(false);
    Check(s.Read().IntakeText.Contains("未確認") && s.Read().LastScan==scan,"Misleading intake state"); Reject(()=>s.ImportDemo(fixture));
});
Test("M1-fixture", "20 synthetic messages: 2 exact-rule tasks, 1 ignore, 1 blocked, no merging", () =>
{
    using var r=Repo("fixture"); var s=new TaskService(r,clock); s.ImportDemo(fixture); s.ImportDemo(fixture); var state=s.Read();
    Check(state.Inbox.Count==20 && state.Tasks.Count==2,"Missing or duplicate reception"); Check(state.Inbox.Count(m=>m.Status==IntakeStatus.ReadBlocked)==1 && state.Inbox.Count(m=>m.Status==IntakeStatus.Ignored)==1,"Classification mismatch");
    Check(state.Inbox.Count(m=>m.Subject=="同じ件名")==2,"Subject merge");
    var malicious=state.Inbox.Single(m=>m.SourceKey.EndsWith("F13")); Check(malicious.Status==IntakeStatus.NeedsReview,"Untrusted content acted upon");
});
Test("M1-review", "One-click acceptance, postpone and one-off ignore", () =>
{
    using var r=Repo("review"); var s=new TaskService(r,clock); s.ImportDemo(fixture); var mail=s.Read().Inbox.Single(m=>m.SourceKey.EndsWith("F02"));
    s.ReviewInbox(mail.Id,"後で確認"); Check(s.Read().Inbox.Single(m=>m.Id==mail.Id).Pending,"Postpone hid review");
    s.ReviewInbox(mail.Id,"仕事にする"); s.ReviewInbox(mail.Id,"仕事にする"); Check(s.Read().Tasks.Count==3,"Acceptance duplicated");
    var other=s.Read().Inbox.Single(m=>m.SourceKey.EndsWith("F03")); s.ReviewInbox(other.Id,"対応不要"); s.RestoreInboxReview(other.Id);
    Check(s.Read().Inbox.Single(m=>m.Id==other.Id).Pending,"Ignore cannot be corrected");
});
Test("T082", "Undo rejects newer same-task change and preserves unrelated task", () =>
{
    using var r=Repo("undo"); var s=new TaskService(r,clock); var a=s.Add("A"); var b=s.Add("B"); s.Transition(a,1,WorkStatus.Completed); var ev=s.Read().Events.Last().Id;
    var t=Get(s,b); t.Note="別仕事の新メモ"; s.Edit(t,1); s.Undo(ev); Check(Get(s,b).Note=="別仕事の新メモ","Unrelated change reverted");
    s.Transition(a,Get(s,a).Version,WorkStatus.Completed); ev=s.Read().Events.Last().Id; t=Get(s,a); t.Note="新変更"; s.Edit(t,t.Version); Reject(()=>s.Undo(ev)); Check(Get(s,a).Note=="新変更","New change erased");
});
Test("M1-concurrency", "Stale repository revision cannot overwrite newer saved state", () =>
{
    using var r=Repo("concurrent"); var s=new TaskService(r,clock); s.Add("A"); using var second=new SqliteRepository(r.FilePath); var old=second.Load(); s.Add("B"); var proposed=Copy.Of(old); proposed.DemoConnected=false;
    Reject(()=>second.Save(old,proposed)); Check(second.Load().Tasks.Count==2,"Lost concurrent change");
});
Test("T070-subset", "Corrupted encrypted payload prevents restore and does not create replacement", () =>
{
    using var r=Repo("corrupt"); var s=new TaskService(r,clock); s.Add("A"); var backup=r.Backup(Path.Combine(root,"corrupt-backup"));
    using(var c=new SqliteConnection("Data Source="+backup)) { c.Open(); using var q=c.CreateCommand(); q.CommandText="UPDATE tasks SET payload=x'010203'"; q.ExecuteNonQuery(); }
    var target=Path.Combine(root,"bad-restore","tasks.db"); var rejected=false;
    try { SqliteRepository.RestoreToNew(backup,target); } catch(System.Security.Cryptography.CryptographicException) { rejected=true; }
    Check(rejected && !File.Exists(target) && s.Read().Tasks.Count==1,"Failed restore damaged live data");
});
Test("REV-01", "Review date brings waiting/decision tasks with future or no deadline into review", () =>
{
    using var r=Repo("review-due"); var s=new TaskService(r,clock); var a=s.Add("相手の返答"); var b=s.Add("判断");
    Date(s,a,new(DeadlineKind.Date,Japan.Day(clock.Now).AddDays(10),Evidence:"本人確認")); Date(s,b,new(DeadlineKind.None,Evidence:"本人確認"));
    var tomorrow=Japan.Day(clock.Now).AddDays(1); s.Transition(a,2,WorkStatus.Waiting,review:tomorrow); s.Transition(b,2,WorkStatus.Decision,review:tomorrow);
    Check(Policy.Reviews(s.Read(),clock.Now).Count==0,"Future review premature");
    Check(Policy.Reviews(s.Read(),clock.Now.AddDays(1)).Count==2,"Due waiting work disappeared");
    Check(Policy.Candidates(s.Read(),clock.Now.AddDays(1)).Count==0,"Waiting became a startable task");
});
Test("REV-02", "Time-derived alert and review counts update across midnight without a database write", () =>
{
    using var r=Repo("time-attention"); var s=new TaskService(r,clock); var id=s.Add("日付境界"); var day=Japan.Day(clock.Now).AddDays(1);
    Date(s,id,new(DeadlineKind.Date,day,Evidence:"本人確認")); s.Transition(id,2,WorkStatus.Waiting,review:day); var snapshot=s.Read(); var revision=snapshot.Revision;
    Check(Policy.Alerts(snapshot,clock.Now).Count==0 && Policy.Reviews(snapshot,clock.Now).Count==0,"Premature alert");
    Check(Policy.Alerts(snapshot,clock.Now.AddDays(1)).Count==1 && Policy.Reviews(snapshot,clock.Now.AddDays(1)).Count==1,"Clock change not reflected");
    Check(s.Read().Revision==revision && Get(s,id).Status==WorkStatus.Waiting,"Time update changed saved work");
});
Test("REV-03", "Preference write failure retains previously saved preferences", () =>
{
    var folder=Path.Combine(root,"preferences"); Directory.CreateDirectory(folder);
    PreferencesStore.Save(folder,new DisplayPreferences(1.5,false)); var old=File.ReadAllText(Path.Combine(folder,"preferences.json"));
    Directory.CreateDirectory(Path.Combine(folder,"preferences.json.new")); var rejected=false;
    try { PreferencesStore.Save(folder,new DisplayPreferences(2,true)); } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { rejected=true; }
    Check(rejected && File.ReadAllText(Path.Combine(folder,"preferences.json"))==old,"Existing preferences damaged");
});
CalendarResponseTests.Run(Test, root);
CalendarPresentationTests.Run(Test);
ProfileTests.Run(Test, root, fixture);
var output=args.FirstOrDefault() ?? Path.Combine(root,"results.json");
File.WriteAllText(output,JsonSerializer.Serialize(new { version=AppRelease.Version, timestamp=DateTimeOffset.UtcNow, environment=Environment.OSVersion.VersionString, total=results.Count, failed, tests=results },new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"RESULT total={results.Count} failed={failed}");
return failed==0 ? 0 : 1;

sealed class FakeClock : IClock { public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-18T01:00:00Z"); }
