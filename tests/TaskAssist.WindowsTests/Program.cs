using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using TaskAssist.Core;
using TaskAssist.Desktop;

// Dedicated synthetic-data UI host, excluded from the user artifact. No UI automation hooks.
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TaskAssist","WindowTests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        if(args.Contains("--profiles") || args.Contains("--profile-review"))
        {
            var store = new ProfileStore(folder);
            if(args.Contains("--profile-review"))
            {
                var old = store.CreateFirst("【架空】旧所属"); string id; Snapshot snapshot;
                using(var r = store.Open(old.Id))
                {
                    var service = new TaskService(r,new SystemClock()); id=service.AddCalendarCase("【架空】旧所属の回答",new(2026,9,21),new(DeadlineKind.Date,new(2026,10,31),Evidence:"架空試験"),CaseColor.Teal);
                    service.AddTeams(id,1,["【架空】調査担当"]); var task=service.Read().Tasks.Single(); service.RecordAnswer(id,task.Version,task.Responses.Single().Id,AnswerKind.NoItems);snapshot=service.Read();
                }
                var current=store.Transfer(old.Id,snapshot.Revision,"【架空】現在の所属",[new(id,snapshot.Tasks.Single().Version,HandoverChoice.HandOver,"【架空】後任に説明済み")]);
                using(var r=store.Open(current.Id))new TaskService(r,new SystemClock()).AddCalendarCase("【架空】日付の入力確認",new(2026,9,21),new(DeadlineKind.Date,new(2026,10,31),Evidence:"架空試験"),CaseColor.Teal);
                if(args.Contains("--large-font"))PreferencesStore.Save(folder,new DisplayPreferences(2,true));
            }
            var localApp = new System.Windows.Application();
            localApp.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source=new Uri("pack://application:,,,/TaskAssist;component/Styles.xaml") });
            Console.WriteLine(JsonSerializer.Serialize(new { test="local-profile-ui-host", syntheticFolder=folder }));
            localApp.Startup += (_,_) => LocalSession.Start(localApp,store);
            localApp.Run(); return;
        }
        if(args.Contains("--calendar"))
        {
            using var repository=new SqliteRepository(Path.Combine(folder,"tasks.db"));
            var calendarService=new TaskService(repository,new SystemClock());
            if(args.Contains("--overview"))
            {
                var id=calendarService.AddCalendarExample();
                var task=calendarService.Read().Tasks.Single(t=>t.Id==id);
                calendarService.AddTeams(id,task.Version,Enumerable.Range(4,9).Select(i=>"架空の担当"+i));
                task=calendarService.Read().Tasks.Single(t=>t.Id==id); calendarService.MarkRequested(id,task.Version);
                task=calendarService.Read().Tasks.Single(t=>t.Id==id); calendarService.RecordAnswer(id,task.Version,task.Responses[0].Id,AnswerKind.NoItems);
                for(var i=0;i<5;i++) calendarService.AddCalendarCase("【架空】重なる調査"+(i+1),new(2026,9,21),new(DeadlineKind.Date,new(2026,10,31),Evidence:"架空画面試験"),(CaseColor)(i%5));
            }
            var calendarApp=new System.Windows.Application();
            calendarApp.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source=new Uri("pack://application:,,,/TaskAssist;component/Styles.xaml") });
            var calendar=new CalendarWindow(calendarService,new SystemClock())
            { Title="カレンダー・回答集め — 独立した架空画面試験", FontSize=args.Contains("--large-font")?30:15,
                Width=args.Contains("--compact")?900:1360, Height=args.Contains("--compact")?760:900 };
            Console.WriteLine(JsonSerializer.Serialize(new { test="calendar-ui-host", syntheticFolder=folder, fontSize=calendar.FontSize }));
            calendarApp.Run(calendar);
            return;
        }
        using(var r=new SqliteRepository(Path.Combine(folder,"tasks.db")))
        {
            var before=r.Load(); var after=Copy.Of(before); var now=DateTimeOffset.UtcNow;
            for(var i=0;i<10000;i++) after.Inbox.Add(new InboxItem { SourceKey="scale:"+i, Subject="架空の受付 "+i, Body="これは性能試験の架空原文です。", Sender="test@example.invalid", ReceivedAt=now.AddMinutes(-i), Status=IntakeStatus.NeedsReview });
            for(var i=0;i<500;i++) after.Tasks.Add(new WorkItem { Title="架空の仕事 "+i, CreatedAt=now.AddMinutes(-i), Status=i==0?WorkStatus.Working:WorkStatus.NotStarted, Deadline=i%2==0?new(DeadlineKind.Date,Japan.Day(now),Evidence:"架空試験"):new() });
            after.SavedAt=now; after.LastScan=now; r.Save(before,after);
            var service=new TaskService(r,new SystemClock()); var watch=Stopwatch.StartNew(); var id=service.Add("架空の性能測定用件"); var elapsed=watch.ElapsedMilliseconds;
            Console.WriteLine(JsonSerializer.Serialize(new { test="T028-core", inbox=10000, unfinishedBefore=500, operation="register one task with DPAPI and history", elapsedMs=elapsed, targetMs=1000, targetMet=elapsed<=1000 }));
        }
        var app=new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source=new Uri("pack://application:,,,/TaskAssist;component/Styles.xaml") });
        var watchUi=Stopwatch.StartNew(); var window=new MainWindow(folder) { Title="仕事アシスト — 大量架空データ画面試験" };
        window.ContentRendered+=(_,_)=>Console.WriteLine(JsonSerializer.Serialize(new {test="T028-window-render", elapsedMs=watchUi.ElapsedMilliseconds, scope="startup rendering, not normal input latency"}));
        app.Run(window);
    }
}
