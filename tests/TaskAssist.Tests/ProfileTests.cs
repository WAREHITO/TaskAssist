using System.Security.Cryptography;
using System.Text;
using TaskAssist.Core;

internal static class ProfileTests
{
    public static void Run(Action<string,string,Action> test, string root, string fixture)
    {
        void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
        void Reject(Action action) { try { action(); } catch (RuleException) { return; } throw new Exception("Expected RuleException"); }
        ProfileStore Store(string label) => new(Path.Combine(root, "profiles-" + label));
        test("LOCAL-01", "First run starts empty, profile identity and manual task persist", () =>
        {
            var store = Store("first"); var p = store.CreateFirst("【架空】第一局・企画課");
            using (var repo = store.Open(p.Id))
            {
                var before = repo.Load(); Check(!before.IsDemo && before.Tasks.Count == 0 && before.Inbox.Count == 0 && before.TeamRoster.Count == 0 && !before.DemoConnected, "Fixture contamination");
                new TaskService(repo, new FakeClock()).Add("【架空】用件");
            }
            using var reopened = new ProfileStore(store.Root).Open(p.Id);
            Check(reopened.Load().Tasks.Single().Profile == p.Id, "Profile lost on reopen");
        });
        test("LOCAL-02", "Production rejects all demo entry points without changes", () =>
        {
            var store = Store("guards"); var p = store.CreateFirst("架空所属"); using var repo = store.Open(p.Id); var s = new TaskService(repo, new FakeClock());
            Reject(() => s.ImportDemo(fixture)); Reject(() => s.AddCalendarExample()); Reject(() => s.SetDemoConnection(true));
            Check(s.Read().Revision == 0 && s.Read().Events.Count == 0, "Rejected changes persisted");
        });
        test("LOCAL-03", "Transfer isolates tasks and teams, preserves full old answers read-only", () =>
        {
            var store = Store("transfer"); var a = store.CreateFirst("【架空】旧所属"); string id; Snapshot old;
            using(var repo = store.Open(a.Id))
            {
                var s = new TaskService(repo,new FakeClock()); id = s.AddCalendarCase("【架空】調査",new(2026,9,21),new(DeadlineKind.Date,new(2026,10,31),Evidence:"架空"),CaseColor.Teal);
                s.AddTeams(id,1,["【架空】担当A"]); var task=s.Read().Tasks.Single(); s.RecordAnswer(id,task.Version,task.Responses.Single().Id,AnswerKind.NoItems); old=s.Read();
            }
            var b=store.Transfer(a.Id,old.Revision,"【架空】新所属",[new(id,old.Tasks.Single().Version,HandoverChoice.HandOver,"【架空】後任へ引継ぎ")]);
            using(var next=store.Open(b.Id)) Check(next.Load().Tasks.Count==0&&next.Load().TeamRoster.Count==0&&next.Load().Events.Count==0,"Old work carried over");
            using var archive=store.Open(a.Id); Check(Copy.Json(archive.Load())==Copy.Json(old),"Old data changed");
            Reject(()=>new TaskService(archive,new FakeClock()).Add("must reject"));
            Check(store.Load().Profiles.Single(p=>p.Id==a.Id).Decisions.Single().Note.Contains("後任"),"Handover missing");
        });
        test("LOCAL-04", "Every unfinished task requires explicit valid decision and note", () =>
        {
            var store=Store("decisions");var a=store.CreateFirst("旧");using var repo=store.Open(a.Id);var s=new TaskService(repo,new FakeClock());var id=s.Add("架空");var revision=s.Read().Revision;
            Reject(()=>store.Transfer(a.Id,revision,"新",[]));
            Reject(()=>store.Transfer(a.Id,revision,"新",[new(id,1,(HandoverChoice)(-1),"理由")]));
            Reject(()=>store.Transfer(a.Id,revision,"新",[new(id,1,HandoverChoice.Cancel,"")]));
            Reject(()=>store.Transfer(a.Id,revision,"新",[new(id,9,HandoverChoice.Cancel,"理由")]));
            Check(store.Load().ActiveId==a.Id,"Invalid transfer committed");
        });
        test("LOCAL-05", "Catalog publish failure keeps active profile, old data, and backup", () =>
        {
            var store=Store("failure");var a=store.CreateFirst("旧");using var repo=store.Open(a.Id);var s=new TaskService(repo,new FakeClock());var id=s.Add("架空");var before=Copy.Json(s.Read());
            store.BeforePublish=()=>throw new IOException("synthetic failure");
            try { store.Transfer(a.Id,s.Read().Revision,"新",[new(id,1,HandoverChoice.ContinueSeparately,"別途対応")]);throw new Exception("No failure"); }catch(IOException){}
            Check(store.Load().ActiveId==a.Id&&Copy.Json(s.Read())==before,"Partial transfer");
            Check(Directory.GetFiles(Path.Combine(store.Folder(a.Id),"backups"),"*.db").Length==1,"No old backup");
            store.BeforePublish=null;store.RenameActive("旧の訂正"); Check(store.Load().Active.Name=="旧の訂正","Old profile no longer editable");
        });
        test("LOCAL-06", "Wrong-profile open and restore reject before destination creation", () =>
        {
            var store=Store("identity");var a=store.CreateFirst("旧");string backup;
            using(var repo=store.Open(a.Id)){new TaskService(repo,new FakeClock()).Add("架空");backup=repo.Backup(Path.Combine(store.Root,"backup"));}
            var other=Guid.NewGuid().ToString("N");var dest=Path.Combine(store.Root,"wrong.db");
            Reject(()=>{using var wrong=new SqliteRepository(store.DatabasePath(a.Id),other);});
            Reject(()=>SqliteRepository.RestoreToNew(backup,dest,other));Check(!File.Exists(dest),"Wrong destination created");
            Reject(()=>SqliteRepository.RestoreToNew(backup,dest));
        });
        test("LOCAL-07", "Same profile backup restores encrypted payload and identity", () =>
        {
            var store=Store("restore");var a=store.CreateFirst("架空");using var repo=store.Open(a.Id);var s=new TaskService(repo,new FakeClock());s.Add("架空の復旧");
            var backup=repo.Backup(Path.Combine(store.Root,"backup"));var dest=Path.Combine(store.Root,"restored.db");SqliteRepository.RestoreToNew(backup,dest,a.Id);
            using var restored=new SqliteRepository(dest,a.Id);var actual=restored.Load();var original=repo.Load();
            Check(actual.ProfileId==original.ProfileId && Copy.Json(actual.Tasks)==Copy.Json(original.Tasks) && Copy.Json(actual.Inbox)==Copy.Json(original.Inbox) && Copy.Json(actual.Events)==Copy.Json(original.Events) && Copy.Json(actual.TeamRoster)==Copy.Json(original.TeamRoster),"Restored business data mismatch");
            Check(actual.Automation.RestoreGeneration==original.Automation.RestoreGeneration+1 && !actual.Automation.Connector.Enabled && !actual.Automation.DigestEnabled,"Restore must suspend external processing");
        });
        test("LOCAL-08", "Roster changes preserve previous team names, answers and history", () =>
        {
            var store=Store("roster");var a=store.CreateFirst("架空");using var repo=store.Open(a.Id);var s=new TaskService(repo,new FakeClock());var id=s.Add("架空");s.AddTeams(id,1,["旧担当"]);
            var task=s.Read().Tasks.Single();s.RecordAnswer(id,task.Version,task.Responses.Single().Id,AnswerKind.NoIssues);var before=Copy.Json(s.Read().Tasks);
            s.SetTeamRoster(["新担当"]);Check(Copy.Json(s.Read().Tasks)==before&&s.Read().TeamRoster.SequenceEqual(new[]{"新担当"}),"Historical answer renamed");
            Reject(()=>s.SetTeamRoster(["同じ","同じ"]));
        });
        test("LOCAL-09", "Encrypted catalog does not store organizational names in plaintext", () =>
        {
            var store=Store("privacy");const string name="SYNTHETIC_PRIVATE_ORG_9f83";var a=store.CreateFirst(name);store.RenameActive(name+"_2");
            foreach(var path in Directory.GetFiles(store.Root,"profiles.dat*")) Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(name),"Plaintext org name");
            Check(Path.GetFileName(store.Folder(a.Id))==a.Id,"Org name in directory");
        });
        test("LOCAL-10", "Corrupted catalog does not reset, and previous copy is kept", () =>
        {
            var store=Store("corrupt");store.CreateFirst("架空");store.RenameActive("架空2");var path=Path.Combine(store.Root,"profiles.dat");File.WriteAllBytes(path,[1,2,3,4]);
            try {store.Load();throw new Exception("Unexpected reset");}catch(CryptographicException){}
            Check(File.Exists(path+".previous")&&File.ReadAllBytes(path).Length==4,"Corrupt record overwritten");
        });
        test("LOCAL-11", "Missing catalog or database blocks rather than replacing with empty data", () =>
        {
            var store=Store("missing");var a=store.CreateFirst("架空");var db=store.DatabasePath(a.Id);File.Move(db,db+".held");Reject(()=>{using var r=store.Open(a.Id);});
            var catalog=Path.Combine(store.Root,"profiles.dat");File.Move(catalog,catalog+".held");Reject(()=>store.Load());Check(!File.Exists(db)&&!File.Exists(catalog),"Silent recreation");
        });
        test("LOCAL-12", "Display preferences shared while organizational records stay separate", () =>
        {
            var store=Store("prefs");var a=store.CreateFirst("旧");PreferencesStore.Save(store.Root,new(2,false));var before=File.ReadAllText(Path.Combine(store.Root,"preferences.json"));
            var b=store.Transfer(a.Id,0,"新",[]);Check(File.ReadAllText(Path.Combine(store.Root,"preferences.json"))==before,"Preferences lost");
            Check(!File.Exists(Path.Combine(store.Folder(b.Id),"preferences.json")),"Preferences mixed with org");
        });
        test("LOCAL-13", "Stale transfer revision cannot archive newly changed work", () =>
        {
            var store=Store("stale");var a=store.CreateFirst("旧");using var repo=store.Open(a.Id);new TaskService(repo,new FakeClock()).Add("架空");
            Reject(()=>store.Transfer(a.Id,0,"新",[]));Check(store.Load().ActiveId==a.Id,"Stale transfer committed");
        });
        test("LOCAL-14", "Cross-profile rows and historical payloads are rejected", () =>
        {
            var store=Store("rows");var a=store.CreateFirst("架空");using var repo=store.Open(a.Id);var before=repo.Load();var bad=Copy.Of(before);bad.Tasks.Add(new WorkItem{Title="架空"});Reject(()=>repo.Save(before,bad));
            bad=Copy.Of(before);bad.Events.Add(new ChangeEvent{Before=new(){["x"]=new WorkItem{Id="x",Title="架空"}}});Reject(()=>repo.Save(before,bad));
        });
        test("LOCAL-15", "Failed first catalog publish retries in same session and after restart", () =>
        {
            foreach (var restart in new[] { false, true })
            {
                var store=Store("first-retry-"+restart);store.BeforePublish=()=>throw new IOException("synthetic failure");
                try {store.CreateFirst("架空の初回");throw new Exception("No failure");}catch(IOException){}
                var prepared=Directory.GetDirectories(Path.Combine(store.Root,"profiles")).Single();
                store.BeforePublish=null;if(restart)store=new ProfileStore(store.Root);
                Check(store.Load().Profiles.Count==0,"Cannot resume first setup");var p=store.CreateFirst("架空の再試行");
                Check(store.Folder(p.Id)==prepared&&store.Load().Active.Name=="架空の再試行","Retry created unrelated profile");
            }
        });
        test("LOCAL-16", "First-setup recovery refuses a prepared database containing work", () =>
        {
            var store=Store("first-nonempty");var p=store.CreateFirst("架空");using(var repo=store.Open(p.Id))new TaskService(repo,new FakeClock()).Add("保存済み架空案件");
            var catalog=Path.Combine(store.Root,"profiles.dat");File.Move(catalog,catalog+".held");Reject(()=>store.Load());Reject(()=>store.CreateFirst("置換不可"));
        });
        test("LOCAL-17", "Missing catalog never resets a restored profile whose original DB is empty", () =>
        {
            var store=Store("first-restored");var p=store.CreateFirst("架空");string backup;
            using(var repo=store.Open(p.Id))backup=repo.Backup(Path.Combine(store.Folder(p.Id),"backups"));
            var restored=Path.Combine(store.Folder(p.Id),"restored-test.db");SqliteRepository.RestoreToNew(backup,restored,p.Id);
            File.WriteAllText(Path.Combine(store.Folder(p.Id),"active-data.txt"),"restored-test.db");
            using(var repo=store.Open(p.Id))new TaskService(repo,new FakeClock()).Add("復元後の架空案件");
            var catalog=Path.Combine(store.Root,"profiles.dat");File.Move(catalog,catalog+".held");Reject(()=>store.Load());Reject(()=>store.CreateFirst("置換不可"));
        });
    }
}
