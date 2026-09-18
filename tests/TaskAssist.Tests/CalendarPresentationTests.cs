using TaskAssist.Core;

internal static class CalendarPresentationTests
{
    public static void Run(Action<string,string,Action> test)
    {
        static void Check(bool value,string message) { if(!value) throw new Exception(message); }
        static WorkItem Case(DateOnly? start,DateOnly? end) => new() { Title="架空案件",ReceivedOn=start,Deadline=end is {} date?new(DeadlineKind.Date,date,Evidence:"試験"):new() };
        test("UX-01","Month-spanning bars retain exact inclusive dates and correct weekday columns",()=>
        {
            var task=Case(new(2026,9,21),new(2026,10,31));
            var september=CalendarPresentation.Weeks(new(2026,9,1),[task]);
            var october=CalendarPresentation.Weeks(new(2026,10,1),[task]);
            var spans=september.Concat(october).SelectMany(w=>w.Spans).ToList();
            Check(spans.Sum(s=>s.Length)==41,"Lost or duplicated day");
            Check(spans[0].Column==0&&spans[0].Length==7&&spans[^1].Last==new DateOnly(2026,10,31),"Wrong weekday or end");
            Check(october[0].Spans.Single().Column==3,"October must start Thursday");
        });
        test("UX-02","Overlapping bars occupy separate lanes and hidden lanes remain in projection",()=>
        {
            var tasks=Enumerable.Range(0,7).Select(_=>Case(new(2026,9,21),new(2026,9,25))).ToList();
            var spans=CalendarPresentation.Weeks(new(2026,9,1),tasks).SelectMany(w=>w.Spans).ToList();
            Check(spans.Count==7&&spans.Select(s=>s.Lane).Distinct().Count()==7,"Collision or hidden case lost");
            var separate=Case(new(2026,9,26),new(2026,9,27));
            spans=CalendarPresentation.Weeks(new(2026,9,1),tasks.Append(separate)).SelectMany(w=>w.Spans).ToList();
            Check(spans.Single(s=>s.Task.Id==separate.Id).Lane==0,"Non-overlapping bar did not reuse space");
        });
        test("UX-03","Unknown or reversed dates never fabricate a continuous period",()=>
        {
            var reversed=Case(new(2026,9,25),new(2026,9,21));var dueOnly=Case(null,new(2026,9,22));
            var spans=CalendarPresentation.Weeks(new(2026,9,1),[reversed,dueOnly]).SelectMany(w=>w.Spans).ToList();
            Check(spans.Count==3&&spans.All(s=>s.Length==1),"Fabricated start or filled reversed interval");
            Check(!CalendarPresentation.AppearsOn(reversed,new(2026,9,23)),"Reversed interval selected");
        });
        test("UX-04","Milestones outside case period are selectable and retain actual team names",()=>
        {
            var task=Case(new(2026,9,21),new(2026,10,31));var day=new DateOnly(2026,9,18);
            task.Responses.Add(new(){TeamName="担当A",Stage=ResponseStage.Requested,RequestedOn=day});
            task.Responses.Add(new(){TeamName="担当B",Stage=ResponseStage.Received,AnsweredOn=day,Answer=AnswerKind.NoItems});
            var marks=CalendarPresentation.Milestones(task,day);
            Check(!CalendarPolicy.Covers(task,day)&&CalendarPresentation.AppearsOn(task,day),"Historical milestone unreachable");
            Check(marks.Count==2&&marks[0].Label=="依1"&&marks[0].Detail.Contains("担当A")&&marks[1].Detail.Contains("担当B"),"Milestone altered");
            Check(CalendarPresentation.Milestones(task,new(2026,9,19)).Count==0,"Unknown date inferred");
        });
        test("UX-05","Leap year and representable date limits do not overflow or lose days",()=>
        {
            foreach(var month in new[]{new DateOnly(1,1,1),new DateOnly(9999,12,1),new DateOnly(2028,2,1),new DateOnly(2026,12,1)})
            {
                var days=CalendarPolicy.MonthDays(month);var task=Case(days[0],days[^1]);var weeks=CalendarPresentation.Weeks(month,[task]);
                Check(weeks.SelectMany(w=>w.Days).Count(d=>d is not null)==days.Count&&weeks.SelectMany(w=>w.Spans).Sum(s=>s.Length)==days.Count,"Boundary month incomplete");
            }
        });
        test("UX-06","Guide distinguishes unrequested, waiting, unchecked and deferred answer content",()=>
        {
            var task=Case(null,null);task.Responses=[new(){TeamName="未依頼"},new(){TeamName="待ち",Stage=ResponseStage.Requested},new(){TeamName="未確認",Stage=ResponseStage.Received,Answer=AnswerKind.Later}];
            var guide=CalendarPresentation.Guide(task);
            Check(CalendarPresentation.Names(task,ResponseStage.Requested)=="待ち"&&guide.Contains("未依頼")&&guide.Contains("受け取った")&&guide.Contains("届いた")&&guide.Contains("後回し"),"Guide skipped state");
            task.Responses.ForEach(r=>{r.Stage=ResponseStage.Reviewed;r.Answer=AnswerKind.NoItems;});
            Check(CalendarPresentation.Guide(task).Contains("自動完了しません"),"Completion inferred");
            task.Status=WorkStatus.Cancelled;Check(CalendarPresentation.Guide(task).Contains("終了状態"),"Closed case prompted new work");
        });
        test("UX-07","Latest record handles equal timestamps, unrelated events, and cancellation explicitly",()=>
        {
            var task=Case(null,null);var state=new Snapshot();var at=DateTimeOffset.UtcNow;
            state.Events.Add(new(){At=at,Label="古い",After=new(){{task.Id,task}}});
            state.Events.Add(new(){At=at,Label="新しい",After=new(){{task.Id,task}},Undone=true});
            state.Events.Add(new(){At=at.AddMinutes(1),Label="別案件"});
            Check(CalendarPresentation.LastRecord(state,task.Id)?.Label=="新しい"&&CalendarPresentation.LastRecord(state,task.Id)!.Undone,"Wrong record selected");
            Check(CalendarPresentation.LastRecord(state,"missing")==null,"Invented history");
        });
        test("UX-08","Overview, lanes and milestones leave all stored state unchanged",()=>
        {
            var task=Case(new(2026,9,21),new(2026,10,31));task.NextAction="本人が保存した作業";
            task.Responses=Enumerable.Range(0,8).Select(i=>new TeamResponse{TeamName="担当"+i,Stage=ResponseStage.Requested,RequestedOn=new(2026,9,22)}).ToList();
            var state=new Snapshot{Tasks=[task]};var before=Copy.Json(state);
            _=CalendarPresentation.Guide(task);_=CalendarPresentation.Milestones(task,new(2026,9,22));_=CalendarPresentation.Weeks(new(2026,9,1),state.Tasks);_=CalendarPresentation.LastRecord(state,task.Id);
            Check(CalendarPresentation.Names(task,ResponseStage.Requested).Contains("ほか3担当"),"Remaining count hidden");
            Check(Copy.Json(state)==before,"Display mutated persisted facts");
        });
    }
}
