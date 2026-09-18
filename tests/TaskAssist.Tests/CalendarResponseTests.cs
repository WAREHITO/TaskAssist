using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TaskAssist.Core;

internal static class CalendarResponseTests
{
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    private static void Reject(Action action){try{action();}catch(RuleException){return;}throw new Exception("Expected rule rejection");}
    private static readonly DateOnly Start=new(2026,9,21), Due=new(2026,10,31);
    private static Deadline Deadline()=>new(DeadlineKind.Date,Due,Evidence:"synthetic calendar date");
    private static WorkItem Task(TaskService s,string id)=>s.Read().Tasks.Single(t=>t.Id==id);
    private static string Add(TaskService s)=>s.AddCalendarCase("架空の調査",Start,Deadline(),CaseColor.Teal);
    public static void Run(Action<string,string,Action> Test,string root)
    {
        SqliteRepository Repo(string name)=>new(Path.Combine(root,"calendar-"+name,"tasks.db"));
        Test("CAL-01","September 21 to October 31 covers both months and exact endpoints",()=>
        {
            using var r=Repo("span");var s=new TaskService(r,new FakeClock());var id=Add(s);var t=Task(s,id);
            Check(CalendarPolicy.MonthDays(Start).Count(d=>CalendarPolicy.Covers(t,d))==10,"September range");
            Check(CalendarPolicy.MonthDays(Due).Count(d=>CalendarPolicy.Covers(t,d))==31,"October range");
            Check(!CalendarPolicy.Covers(t,Start.AddDays(-1))&&!CalendarPolicy.Covers(t,Due.AddDays(1)),"Leaked endpoints");
            Check(CalendarPolicy.Mark(t,Start)=="受 "&&CalendarPolicy.Mark(t,Due)=="〆 ","Milestones missing");
        });
        Test("CAL-02","Year boundary, leap day and maximum dates remain valid",()=>
        {
            var t=new WorkItem{ReceivedOn=new(2027,12,31),Deadline=new(DeadlineKind.Date,new(2028,1,2),Evidence:"test")};
            Check(CalendarPolicy.Covers(t,new(2028,1,1)),"Year boundary");
            Check(CalendarPolicy.MonthDays(new(2028,2,1)).Count==29&&CalendarPolicy.MonthDays(new(2027,2,1)).Count==28,"Leap calendar");
            Check(CalendarPolicy.MonthDays(new(9999,12,1)).Last()==DateOnly.MaxValue,"Max calendar overflow");
        });
        Test("CAL-03","Unknown receipt is not inferred; date-only and Japan datetime differ correctly",()=>
        {
            var t=new WorkItem{CreatedAt=DateTimeOffset.Parse("2026-09-01T00:00Z"),Deadline=Deadline()};
            Check(!CalendarPolicy.Covers(t,new(2026,9,1))&&CalendarPolicy.Covers(t,Due),"Inferred receipt");
            t.Deadline=new();Check(!CalendarPolicy.Covers(t,Due),"Unknown painted as deadline");
            t.Deadline=new(DeadlineKind.DateTime,At:DateTimeOffset.Parse("2026-10-30T16:00Z"),Evidence:"test");
            Check(CalendarPolicy.Covers(t,Due)&&!CalendarPolicy.Covers(t,Due.AddDays(-1)),"Japan date shift");
        });
        Test("CAL-04","An already expired incoming request does not invent an inverted span",()=>
        {
            var t=new WorkItem{ReceivedOn=Due.AddDays(3),Deadline=Deadline()};
            Check(CalendarPolicy.Warning(t).Length>0&&CalendarPolicy.Covers(t,Due)&&CalendarPolicy.Covers(t,Due.AddDays(3))&&!CalendarPolicy.Covers(t,Due.AddDays(1)),"Inverted span");
        });
        Test("RESP-01","One click stores Japan request/answer/review dates without completing work",()=>
        {
            using var r=Repo("lifecycle");var clock=new FakeClock{Now=DateTimeOffset.Parse("2026-09-21T15:10Z")};var s=new TaskService(r,clock);var id=Add(s);
            s.AddTeams(id,1,["担当A"]);var t=Task(s,id);var row=t.Responses.Single();s.MarkRequested(id,t.Version,row.Id);t=Task(s,id);
            Check(t.Responses[0].RequestedOn==new DateOnly(2026,9,22),"Not Japan day");
            s.RecordAnswer(id,t.Version,row.Id,AnswerKind.NoItems);t=Task(s,id);s.MarkReviewed(id,t.Version,row.Id);t=Task(s,id);
            Check(t.Responses[0].Stage==ResponseStage.Reviewed&&t.Responses[0].AnsweredOn==new DateOnly(2026,9,22)&&t.Responses[0].ReviewedOn==new DateOnly(2026,9,22),"Dates missing");
            Check(t.Status==WorkStatus.NotStarted&&t.Deadline==Deadline(),"Work auto-completed or deadline moved");
        });
        Test("RESP-02","Roster reuse and normalized duplicate names preserve each case separately",()=>
        {
            using var r=Repo("roster");var s=new TaskService(r,new FakeClock());var a=Add(s);var b=Add(s);
            s.AddTeams(a,1,[" 担当Ａ ","担当A","担当B"]);s.AddTeams(b,1,["担当A"]);
            Check(Task(s,a).Responses.Count==2&&Task(s,b).Responses.Count==1&&s.Read().TeamRoster.Count==2,"Roster duplicate or cross-case link");
            s.MarkRequested(a,Task(s,a).Version);Check(Task(s,b).Responses[0].Stage==ResponseStage.NotRequested,"Other case changed");
        });
        Test("RESP-03","Batch request does not downgrade answers; repeated clicks are idempotent",()=>
        {
            using var r=Repo("batch");var s=new TaskService(r,new FakeClock());var id=Add(s);s.AddTeams(id,1,["A","B"]);var t=Task(s,id);
            s.RecordAnswer(id,t.Version,t.Responses[0].Id,AnswerKind.NoIssues);t=Task(s,id);s.MarkRequested(id,t.Version);t=Task(s,id);var count=s.Read().Events.Count;
            s.MarkRequested(id,1);Check(s.Read().Events.Count==count&&Task(s,id).Responses[0].Answer==AnswerKind.NoIssues,"Repeated batch changed answer");
            Check(Task(s,id).Responses[0].RequestedOn is null,"Unknown dispatch date invented");
        });
        Test("RESP-04","Detailed answer, note, corrected dates, color and roster survive reopening",()=>
        {
            var path=Path.Combine(root,"response-reopen","tasks.db");string id;
            using(var r=new SqliteRepository(path))
            {
                var s=new TaskService(r,new FakeClock());id=Add(s);s.AddTeams(id,1,["回答担当"]);var t=Task(s,id);var row=t.Responses[0] with
                {Stage=ResponseStage.Received,RequestedOn=new(2026,9,22),AnsweredOn=new(2026,10,1),Answer=AnswerKind.FreeText,AnswerText="架空の回答：対象は3件",Note="架空の別紙2頁を確認"};s.EditResponse(id,t.Version,row);
            }
            using(var r=new SqliteRepository(path))
            {
                var s=new TaskService(r,new FakeClock());var t=Task(s,id);Check(t.CalendarColor==CaseColor.Teal&&t.ReceivedOn==Start&&t.Deadline==Deadline(),"Calendar lost");
                Check(t.Responses[0].AnswerText=="架空の回答：対象は3件"&&t.Responses[0].Note=="架空の別紙2頁を確認"&&s.Read().TeamRoster.Single()=="回答担当","Response lost");
            }
        });
        Test("RESP-05","Invalid chronology and empty free text are rejected without partial save",()=>
        {
            using var r=Repo("invalid");var s=new TaskService(r,new FakeClock());var id=Add(s);s.AddTeams(id,1,["A"]);var t=Task(s,id);var revision=s.Read().Revision;
            var row=t.Responses[0] with {Stage=ResponseStage.Received,RequestedOn=new(2026,10,3),AnsweredOn=new(2026,10,1),Answer=AnswerKind.NoItems};Reject(()=>s.EditResponse(id,t.Version,row));
            row=row with{RequestedOn=new(2026,9,22),Answer=AnswerKind.FreeText};Reject(()=>s.EditResponse(id,t.Version,row));
            Reject(()=>s.MarkReviewed(id,t.Version,row.Id));Check(s.Read().Revision==revision,"Rejected edit wrote data");
        });
        Test("RESP-06","Write failure rolls back task rows and reusable roster together",()=>
        {
            using var r=Repo("failure");var s=new TaskService(r,new FakeClock());var id=Add(s);r.BeforeCommit=()=>throw new IOException("synthetic disk full");
            try{s.AddTeams(id,1,["新担当"]);throw new Exception("Failure missing");}catch(IOException){}
            Check(Task(s,id).Responses.Count==0&&s.Read().TeamRoster.Count==0,"Partial roster save");
        });
        Test("RESP-07","Answer correction is undoable without reverting another case",()=>
        {
            using var r=Repo("undo");var s=new TaskService(r,new FakeClock());var id=Add(s);s.AddTeams(id,1,["A"]);var t=Task(s,id);s.RecordAnswer(id,t.Version,t.Responses[0].Id,AnswerKind.HasItems);t=Task(s,id);
            var before=t.Responses[0];s.EditResponse(id,t.Version,before with{Answer=AnswerKind.FreeText,AnswerText="3件"});var ev=s.Read().Events.Last().Id;var other=s.Add("別件を保持");s.Undo(ev);
            Check(Task(s,id).Responses[0].Answer==AnswerKind.HasItems&&Task(s,id).Responses[0].AnswerText==""&&Task(s,other).Title=="別件を保持","Undo lost unrelated state");
            Check(s.Read().Events.Single(e=>e.Id==ev).After[id].Responses[0].AnswerText=="3件","Original answer history erased");
        });
        Test("RESP-08","Stale response edit cannot overwrite a newer answer",()=>
        {
            using var r=Repo("stale");var s=new TaskService(r,new FakeClock());var id=Add(s);s.AddTeams(id,1,["A"]);var old=Task(s,id);s.RecordAnswer(id,old.Version,old.Responses[0].Id,AnswerKind.NoItems);
            Reject(()=>s.EditResponse(id,old.Version,old.Responses[0] with{Note="stale"}));Check(Task(s,id).Responses[0].Answer==AnswerKind.NoItems,"New answer lost");
        });
        Test("RESP-09","Protected answers and roster restore through online backup",()=>
        {
            using var r=Repo("protected");var s=new TaskService(r,new FakeClock());var id=Add(s);s.AddTeams(id,1,["SYNTHETIC-TEAM-8172"]);var t=Task(s,id);
            s.EditResponse(id,t.Version,t.Responses[0] with{Stage=ResponseStage.Received,AnsweredOn=new(2026,9,22),Answer=AnswerKind.FreeText,AnswerText="SYNTHETIC-ANSWER-9813"});
            var backup=r.Backup(Path.Combine(root,"answer-backups"));var raw=Encoding.UTF8.GetString(File.ReadAllBytes(backup));Check(!raw.Contains("SYNTHETIC-TEAM-8172")&&!raw.Contains("SYNTHETIC-ANSWER-9813"),"Plaintext leak");
            var target=Path.Combine(root,"answer-restored","tasks.db");SqliteRepository.RestoreToNew(backup,target);using var restored=new SqliteRepository(target);var snapshot=restored.Load();
            Check(snapshot.Tasks.Single().Responses.Single().AnswerText=="SYNTHETIC-ANSWER-9813"&&snapshot.TeamRoster.Single()=="SYNTHETIC-TEAM-8172","Restore dropped response data");
        });
        Test("RESP-10","Legacy format is backed up before upgrade; new fields start empty",()=>
        {
            var path=Legacy(root,"legacy");using(var r=new SqliteRepository(path))
            {
                var state=r.Load();Check(state.Tasks.Single().Title=="旧形式の架空案件"&&state.Tasks[0].ReceivedOn is null&&state.Tasks[0].Responses.Count==0&&state.TeamRoster.Count==0,"Legacy values inferred or lost");
            }
            using var c=new SqliteConnection("Data Source="+path);c.Open();using var q=c.CreateCommand();q.CommandText="PRAGMA user_version";Check(Convert.ToInt32(q.ExecuteScalar())==AppRelease.Schema,"Old app not guarded");
            var backups=Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!,"backups"),"backup-*.db");Check(backups.Length==1,"Migration backup missing");
            using var b=new SqliteConnection("Data Source="+backups[0]);b.Open();using var bq=b.CreateCommand();bq.CommandText="PRAGMA user_version";Check(Convert.ToInt32(bq.ExecuteScalar())==1,"Backup was after migration");
        });
        Test("RESP-11","Failed upgrade backup leaves format 1 unchanged",()=>
        {
            var path=Legacy(root,"upgrade-failure");File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!,"backups"),"block path");var failed=false;
            try{using var r=new SqliteRepository(path);}catch(IOException){failed=true;}
            using var c=new SqliteConnection("Data Source="+path);c.Open();using var q=c.CreateCommand();q.CommandText="PRAGMA user_version";Check(failed&&Convert.ToInt32(q.ExecuteScalar())==1,"Failed backup modified format");
        });
        Test("RESP-12","Example insertion is idempotent and all existing tasks are retained",()=>
        {
            using var r=Repo("example");var s=new TaskService(r,new FakeClock());var old=s.Add("既存の架空案件");var id=s.AddCalendarExample();var revision=s.Read().Revision;Check(s.AddCalendarExample()==id&&s.Read().Revision==revision,"Duplicate example");
            Check(s.Read().Tasks.Count==2&&Task(s,old).Title=="既存の架空案件"&&Task(s,id).Responses.Count==3,"Existing data changed");
        });
        Test("RESP-13","Detail form never discards new answers or dates when stage was not selected",()=>
        {
            foreach(var stage in new[]{ResponseStage.NotRequested,ResponseStage.Requested})
            {
                var row=new TeamResponse{TeamName="A",Stage=stage,RequestedOn=stage==ResponseStage.Requested?Start:null};
                Reject(()=>ResponsePolicy.PrepareEdit(row,row with{AnswerText="入力した回答を残す"}));
                Reject(()=>ResponsePolicy.PrepareEdit(row,row with{Answer=AnswerKind.NoItems}));
                Reject(()=>ResponsePolicy.PrepareEdit(row,row with{AnsweredOn=Due}));
                Reject(()=>ResponsePolicy.PrepareEdit(row,row with{ReviewedOn=Due}));
            }
            var original=new TeamResponse{TeamName="A"};Reject(()=>ResponsePolicy.PrepareEdit(original,original with{RequestedOn=Start}));
            var correct=ResponsePolicy.PrepareEdit(original,original with{Stage=ResponseStage.Received,AnsweredOn=Due,Answer=AnswerKind.FreeText,AnswerText="架空回答",Note="補足"});
            Check(correct.AnswerText=="架空回答"&&correct.Note=="補足","Valid text lost");
        });
        Test("RESP-14","Explicit stage rollback retains old answer in history and is undoable",()=>
        {
            using var r=Repo("form-rollback");var s=new TaskService(r,new FakeClock());var id=Add(s);s.AddTeams(id,1,["A"]);var t=Task(s,id);
            var answered=t.Responses[0] with{Stage=ResponseStage.Received,RequestedOn=Start,AnsweredOn=Due,Answer=AnswerKind.FreeText,AnswerText="保持すべき旧回答"};s.EditResponse(id,t.Version,answered);t=Task(s,id);
            var rollback=ResponsePolicy.PrepareEdit(t.Responses[0],t.Responses[0] with{Stage=ResponseStage.Requested});s.EditResponse(id,t.Version,rollback);var ev=s.Read().Events.Last();
            Check(Task(s,id).Responses[0].AnswerText==""&&ev.Before[id].Responses[0].AnswerText=="保持すべき旧回答","Rollback history lost");s.Undo(ev.Id);Check(Task(s,id).Responses[0].AnswerText=="保持すべき旧回答","Undo missing");
        });
    }
    private static string Legacy(string root,string name)
    {
        var folder=Path.Combine(root,name);Directory.CreateDirectory(folder);var path=Path.Combine(folder,"tasks.db");
        using var c=new SqliteConnection("Data Source="+path);c.Open();using var q=c.CreateCommand();
        q.CommandText="CREATE TABLE metadata(id INTEGER PRIMARY KEY,revision INTEGER,payload BLOB);CREATE TABLE tasks(id TEXT PRIMARY KEY,profile TEXT,status INTEGER,payload BLOB);CREATE TABLE inbox(id TEXT PRIMARY KEY,source_key TEXT,payload BLOB);CREATE TABLE history(id TEXT PRIMARY KEY,payload BLOB);PRAGMA user_version=1";q.ExecuteNonQuery();
        static byte[] Protect(object value)=>ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value),Encoding.UTF8.GetBytes("TaskAssist-demo-schema-1"),DataProtectionScope.CurrentUser);
        q.CommandText="INSERT INTO metadata VALUES(1,1,$data)";q.Parameters.AddWithValue("$data",Protect(new{Revision=1,DemoConnected=true}));q.ExecuteNonQuery();q.Parameters.Clear();
        q.CommandText="INSERT INTO tasks VALUES('legacy','demo',0,$data)";q.Parameters.AddWithValue("$data",Protect(new{Id="legacy",Profile="demo",Version=1,Title="旧形式の架空案件",Status=0,CreatedAt=DateTimeOffset.UtcNow}));q.ExecuteNonQuery();return path;
    }
}
