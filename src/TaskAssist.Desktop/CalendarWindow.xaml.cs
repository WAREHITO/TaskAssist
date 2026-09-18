using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using TaskAssist.Core;

namespace TaskAssist.Desktop;

public partial class CalendarWindow : Window
{
    private readonly TaskService service;
    private readonly IClock clock;
    private Snapshot state = new();
    private DateOnly month, selectedDay;
    private string? selectedId, selectedResponseId;
    private bool busy, includeClosed;
    private bool compactDetails;
    private int listMode, listPage, responseFilter; // selected date / all / undated / week
    private DateOnly weekFirst, weekLast;
    private readonly System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private TextBlock? liveDeadline;
    private TextBlock? caseListHeading;
    private WorkItem? selectedTask;
    private double Scale => FontSize / 15;

    public CalendarWindow(TaskService service, IClock clock)
    {
        InitializeComponent(); this.service = service; this.clock = clock;
        Title = "カレンダー・回答集め — " + (service.Read().IsDemo ? "架空画面試験" : "ローカル版 0.4.0");
        selectedDay = Japan.Day(clock.Now); month = new DateOnly(selectedDay.Year, selectedDay.Month, 1);
        MaxHeight = Math.Max(620, SystemParameters.WorkArea.Height - 35);
        Loaded += (_, _) => { Refresh(); UpdatePaneLayout(); };
        SizeChanged += (_, _) => UpdatePaneLayout();
        PaneSwitch.Children.Add(Cmd("カレンダーを見る", () => { compactDetails=false; UpdatePaneLayout(); }));
        PaneSwitch.Children.Add(Cmd("案件・回答を見る", () => { compactDetails=true; UpdatePaneLayout(); }));
        Toolbar.Children.Add(Cmd("＋ 案件を登録", () => EditCase(null)));
        if (service.Read().IsDemo) Toolbar.Children.Add(Cmd("架空の例：9/21 → 10/31", () =>
        {
            string id = "";
            Run(() => id = service.AddCalendarExample(), () => { selectedId = id; selectedDay = new(2026,9,21); month = new(2026,9,1); listMode = 0; listPage = 0; ShowCasePane(); });
        }));
        Toolbar.Children.Add(Cmd("保存した情報を再表示", Refresh));
        var closed = new CheckBox { Content = "完了・取りやめも見る", Margin = new(8), VerticalAlignment = VerticalAlignment.Center };
        closed.Checked += (_, _) => { includeClosed = true; DrawCalendar(); };
        closed.Unchecked += (_, _) => { includeClosed = false; DrawCalendar(); };
        Toolbar.Children.Add(closed);
        timer.Tick += (_, _) => UpdateDeadline(); timer.Start();
        Activated += (_, _) => UpdateDeadline(); Closed += (_, _) => timer.Stop();
        Closing += (_, e) => { if (busy) e.Cancel = true; };
    }
    private TextBlock Txt(string text, bool bold = false, double size = 0) => new()
    { Text = text, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, FontSize = size > 0 ? size : FontSize };
    private Button Cmd(string label, Action action)
    {
        var button = new Button { Content = new TextBlock { Text=label, TextWrapping=TextWrapping.Wrap, Margin=new(0) }, HorizontalContentAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(button,label);
        button.Click += (_, _) => action(); return button;
    }
    private Border Card(UIElement content, CaseColor? color = null) => new()
    {
        Child = content, Padding = new(10), Margin = new(0,5,6,8), CornerRadius = new(6),
        BorderThickness = color is null ? new(1) : new(5,1,1,1), BorderBrush = color is {} c ? Paint(c).Ink : SystemColors.ActiveBorderBrush
    };
    private static (Brush Ink, Brush Fill) Paint(CaseColor color)
    {
        if (SystemParameters.HighContrast) return (SystemColors.WindowTextBrush, SystemColors.WindowBrush);
        return color switch
        {
            CaseColor.Teal => (new SolidColorBrush(Color.FromRgb(12,105,102)), new SolidColorBrush(Color.FromRgb(223,244,239))),
            CaseColor.Violet => (new SolidColorBrush(Color.FromRgb(105,62,154)), new SolidColorBrush(Color.FromRgb(239,232,248))),
            CaseColor.Amber => (new SolidColorBrush(Color.FromRgb(137,81,4)), new SolidColorBrush(Color.FromRgb(255,242,212))),
            CaseColor.Rose => (new SolidColorBrush(Color.FromRgb(151,52,86)), new SolidColorBrush(Color.FromRgb(252,231,239))),
            _ => (new SolidColorBrush(Color.FromRgb(36,88,164)), new SolidColorBrush(Color.FromRgb(230,239,253)))
        };
    }
    private void Refresh()
    {
        state = service.Read(); DrawCalendar(); DrawDetails();
        SaveMessage.Text = "ローカル保存済み：" + (state.SavedAt?.ToOffset(Japan.Offset).ToString("yyyy/M/d HH:mm:ss") ?? "まだ操作なし");
    }
    private async void Run(Action action, Action? success = null)
    {
        if (busy) return;
        busy = true; Toolbar.IsEnabled = CalendarPanel.IsEnabled = DetailsPanel.IsEnabled = false; SaveMessage.Text = "保存しています…";
        var offset = DetailsScroll.VerticalOffset;
        try { await Task.Run(action); success?.Invoke(); Refresh(); DetailsScroll.ScrollToVerticalOffset(offset); }
        catch (Exception ex)
        {
            SaveMessage.Text = "保存できませんでした。成功扱いにはしていません。";
            MessageBox.Show(this, ex is RuleException ? ex.Message : "保存先の空き容量・アクセス権を確認してください。（CAL-SAVE）", "記録は未確定");
        }
        finally { busy = false; Toolbar.IsEnabled = CalendarPanel.IsEnabled = DetailsPanel.IsEnabled = true; }
    }
    private List<WorkItem> VisibleCases() => state.Tasks.Where(t => includeClosed || !t.Closed).OrderBy(t => t.Deadline.Day ?? DateOnly.MaxValue)
        .ThenBy(t => t.CreatedAt).ThenBy(t => t.Id).ToList();
    private void SelectDay(DateOnly day)
    {
        selectedDay=day; month=new(day.Year,day.Month,1); listMode=0; listPage=0;
        var cases=VisibleCases().Where(t=>CalendarPresentation.AppearsOn(t,day)).ToList();
        selectedId=cases.Count==1?cases[0].Id:cases.Any(t=>t.Id==selectedId)?selectedId:null;
        DrawCalendar(); DrawDetails(); DetailsScroll.ScrollToTop();
        if(cases.Count == 1) ShowCasePane();
        else ShowCaseList();
    }
    private void DrawCalendar()
    {
        CalendarPanel.Children.Clear();
        var navigation = new WrapPanel();
        var previous = Cmd("‹ 前月", () => { month = month.AddMonths(-1); DrawCalendar(); }); previous.IsEnabled = month.Year != 1 || month.Month != 1;
        navigation.Children.Add(previous); navigation.Children.Add(Txt(month.ToString("yyyy年 M月"), true, FontSize + 6));
        var next = Cmd("翌月 ›", () => { month = month.AddMonths(1); DrawCalendar(); }); next.IsEnabled = month.Year != 9999 || month.Month != 12;
        navigation.Children.Add(next); navigation.Children.Add(Cmd("今日", () => SelectDay(Japan.Day(clock.Now))));
        CalendarPanel.Children.Add(navigation);
        CalendarPanel.Children.Add(Txt("帯＝受付から回答〆まで　受＝受付　〆＝締切\n依＝課内へ依頼した担当数　答＝回答が来た担当数"));
        var weekdays = new UniformGrid { Columns = 7, Rows = 1 };
        foreach (var name in new[] { "月", "火", "水", "木", "金", "土", "日" })
            weekdays.Children.Add(new TextBlock { Text = name, HorizontalAlignment = HorizontalAlignment.Center, FontSize = FontSize });
        CalendarPanel.Children.Add(weekdays);
        var cases = VisibleCases();
        foreach (var week in CalendarPresentation.Weeks(month, cases)) DrawWeek(week, cases);
        var switches = new WrapPanel();
        switches.Children.Add(Cmd("選んだ日の案件", () => { listMode = 0; listPage = 0; DrawCalendar(); }));
        switches.Children.Add(Cmd($"全案件 {cases.Count}件", () => { listMode = 1; listPage = 0; DrawCalendar(); }));
        var undated = cases.Where(t => t.ReceivedOn is null && t.Deadline.Day is null).ToList();
        switches.Children.Add(Cmd($"日付未設定 {undated.Count}件", () => { listMode = 2; listPage = 0; DrawCalendar(); }));
        CalendarPanel.Children.Add(switches);
        var list = listMode == 1 ? cases : listMode == 2 ? undated : listMode == 3
            ? cases.Where(t => Enumerable.Range(weekFirst.DayNumber, weekLast.DayNumber-weekFirst.DayNumber+1).Any(n => CalendarPresentation.AppearsOn(t,DateOnly.FromDayNumber(n)))).ToList()
            : cases.Where(t => CalendarPresentation.AppearsOn(t,selectedDay)).ToList();
        caseListHeading=Txt((listMode == 0 ? selectedDay.ToString("yyyy/M/d") : listMode == 1 ? "全案件" : listMode == 2 ? "日付未設定" : $"{weekFirst:M/d}〜{weekLast:M/d}") + $"：{list.Count}件", true);
        CalendarPanel.Children.Add(caseListHeading);
        listPage = Math.Clamp(listPage,0,Math.Max(0,(list.Count-1)/10));
        foreach (var task in list.Skip(listPage * 10).Take(10))
        {
            var box = new StackPanel(); box.Children.Add(Txt(task.Title, true)); box.Children.Add(Txt(CalendarPolicy.Period(task))); box.Children.Add(Txt(ResponsePolicy.Summary(task)));
            if (listMode == 0)
                foreach (var mark in CalendarPresentation.Milestones(task,selectedDay)) box.Children.Add(Txt(mark.Detail));
            var button = Cmd("", () =>
            {
                var offset=CalendarScroll.VerticalOffset;
                selectedId=task.Id; DrawCalendar(); DrawDetails();
                CalendarScroll.ScrollToVerticalOffset(offset); DetailsScroll.ScrollToTop(); ShowCasePane();
            }); button.Content = box; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            System.Windows.Automation.AutomationProperties.SetName(button, task.Title + " の回答集めを開く");
            CalendarPanel.Children.Add(Card(button,task.CalendarColor));
        }
        if (list.Count == 0) CalendarPanel.Children.Add(Txt("この一覧に案件はありません。「全案件」や「日付未設定」からも選べます。"));
        if (list.Count > 10)
        {
            var row = new WrapPanel(); var back = Cmd("前の10件", () => { listPage--; DrawCalendar(); }); back.IsEnabled = listPage > 0;
            var more = Cmd("次の10件", () => { listPage++; DrawCalendar(); }); more.IsEnabled = (listPage+1)*10 < list.Count;
            row.Children.Add(back); row.Children.Add(Txt($"{listPage+1} / {(list.Count+9)/10}ページ")); row.Children.Add(more); CalendarPanel.Children.Add(row);
        }
    }
    private void UpdateDeadline()
    {
        if (liveDeadline is not null && selectedTask is not null) liveDeadline.Text = selectedTask.Deadline.Display(clock.Now);
    }
    private void DrawDetails()
    {
        DetailsPanel.Children.Clear(); liveDeadline = null;
        selectedTask = state.Tasks.SingleOrDefault(t => t.Id == selectedId);
        if (selectedTask is not {} task)
        {
            DetailsPanel.Children.Add(Txt("覚えておくことを、ここへ。", true, FontSize+5));
            DetailsPanel.Children.Add(Txt("左の日付と案件を選ぶと、いつ課内へ依頼し、どの担当から何が来たかを確認できます。"));
            DetailsPanel.Children.Add(Txt(state.IsDemo ? "まず「架空の例：9/21 → 10/31」で試せます。色帯は翌月にも続きます。" : "「＋ 案件を登録」から用件・受付日・締切を登録できます。色帯は翌月にも続きます。")); return;
        }
        var heading = new StackPanel(); heading.Children.Add(Txt(task.Title,true,FontSize+4));
        heading.Children.Add(Txt("本当の回答締切",true));
        liveDeadline = Txt(task.Deadline.Display(clock.Now),true); heading.Children.Add(liveDeadline);
        if (CalendarPolicy.Warning(task).Length > 0) heading.Children.Add(Txt(CalendarPolicy.Warning(task),true));
        heading.Children.Add(Txt("回答待ち：" + CalendarPresentation.Names(task,ResponseStage.Requested),true));
        heading.Children.Add(Txt("届いた回答の確認待ち：" + CalendarPresentation.Names(task,ResponseStage.Received)));
        heading.Children.Add(Txt("未依頼：" + CalendarPresentation.Names(task,ResponseStage.NotRequested)));
        heading.Children.Add(Txt("操作の目安：" + CalendarPresentation.Guide(task)));
        var last = CalendarPresentation.LastRecord(state,task.Id);
        heading.Children.Add(Txt("最後の記録：" + (last is null ? "履歴なし" : last.At.ToOffset(Japan.Offset).ToString("M/d HH:mm") + " " + last.Label + (last.Undone ? "（取消済み）" : ""))));
        var facts = new StackPanel(); facts.Children.Add(Txt(CalendarPolicy.Period(task)));
        facts.Children.Add(Txt("保存済みの次の一手：" + task.NextAction));
        facts.Children.Add(Txt("案件の状態：" + task.StatusText + "（担当の確認とは別）"));
        facts.Children.Add(Cmd("受付日・回答〆・色を編集", () => EditCase(task)));
        facts.Children.Add(Txt("「操作の目安」は担当の記録から作った案内です。保存済みの次の一手は元の画面で編集できます。"));
        heading.Children.Add(new Expander { Header=Txt("受付日・保存済みの次の一手・予定を編集"), Content=facts });
        DetailsPanel.Children.Add(Card(heading,task.CalendarColor));
        DetailsPanel.Children.Add(Txt("課内の回答集め",true,FontSize+3)); DetailsPanel.Children.Add(Txt(ResponsePolicy.Summary(task),true));
        var tools = new WrapPanel(); tools.Children.Add(Cmd("担当を選んで追加", () => AddTeams(task)));
        if (task.Responses.Any(r => r.Stage == ResponseStage.NotRequested)) tools.Children.Add(Cmd("未依頼の担当をまとめて依頼済みに", () => Run(() => service.MarkRequested(task.Id, task.Version))));
        DetailsPanel.Children.Add(tools);
        DrawResponses(task);
        var history = new StackPanel();
        foreach (var ev in state.Events.Where(e => e.After.ContainsKey(task.Id)).OrderByDescending(e => e.At).Take(8))
        {
            var box = new StackPanel(); box.Children.Add(Txt(ev.At.ToOffset(Japan.Offset).ToString("M/d HH:mm") + " " + ev.Label,true));
            var after = ev.After[task.Id]; ev.Before.TryGetValue(task.Id,out var before);
            foreach (var change in ResponsePolicy.Changes(before,after)) box.Children.Add(Txt(change));
            if (ev.Undone) box.Children.Add(Txt("取消済み"));
            else if (ev.Undoable && ev.Before.Count == ev.After.Count && ev.InboxBefore.Count == ev.InboxAfter.Count)
                box.Children.Add(Cmd("この変更を元に戻す", () => Run(() => service.Undo(ev.Id))));
            history.Children.Add(Card(box));
        }
        DetailsPanel.Children.Add(new Expander { Header=Txt("この案件の最近の履歴・取消（8件）"), Content=history, Margin=new(0,12,0,5) });
        DetailsPanel.Children.Add(Txt("すべての履歴は、元の画面の「履歴・取消」に残ります。回答が揃っても案件は自動完了しません。"));
    }
    private Window Form(string title, out StackPanel panel)
    {
        panel = new StackPanel { Margin=new(20) };
        return new Window { Title=title, Owner=this, Width=680, Height=760, MinWidth=450, MinHeight=400,
            MaxHeight=Math.Max(400,SystemParameters.WorkArea.Height-40), FontSize=FontSize,
            WindowStartupLocation=WindowStartupLocation.CenterOwner, Content=new ScrollViewer { Content=panel, VerticalScrollBarVisibility=ScrollBarVisibility.Auto } };
    }
    private TextBox Input(Panel parent,string label,string value,bool multiline=false)
    {
        parent.Children.Add(Txt(label)); var field = new TextBox { Text=value, AcceptsReturn=multiline, TextWrapping=TextWrapping.Wrap, MinHeight=multiline?75:32, FontSize=FontSize };
        System.Windows.Automation.AutomationProperties.SetName(field,label); parent.Children.Add(field); return field;
    }
    private CheckedDatePicker DateInput(Panel parent,string label,DateOnly? value)
    {
        parent.Children.Add(Txt(label)); var field = new CheckedDatePicker { SelectedDate=value?.ToDateTime(TimeOnly.MinValue), FontSize=FontSize, Margin=new(0,3,0,8) };
        System.Windows.Automation.AutomationProperties.SetName(field,label); parent.Children.Add(field);
        var error=Txt("",true); var retry=Cmd("日付を入力し直す",field.ClearRejectedInput);
        error.Visibility=retry.Visibility=Visibility.Collapsed;
        field.InputValidationChanged+=()=>
        {
            var invalid=field.RejectedInput is not null;
            error.Text=invalid?$"「{field.RejectedInput}」は日付として使えません。元の日付へ戻って見えても、保存は止めています。":"";
            error.Visibility=retry.Visibility=invalid?Visibility.Visible:Visibility.Collapsed;
        };
        parent.Children.Add(error); parent.Children.Add(retry); return field;
    }
    private static DateOnly? ReadDate(CheckedDatePicker field) => field.ReadValue();
    private void AddSave(Window dialog,StackPanel panel,Action save,Action? success=null)
    {
        var error = Txt(""); panel.Children.Add(error);
        var saving = false; dialog.Closing += (_,e) => { if (saving) e.Cancel=true; };
        var button = Cmd("保存する", async () =>
        {
            if (saving) return;
            // Read controls and save on the UI thread; dialog input stays intact on any failure.
            saving=true; panel.IsEnabled=false; error.Text="保存しています…";
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            try { save(); success?.Invoke(); saving=false; dialog.Close(); Refresh(); }
            catch(Exception ex) { error.Text=ex is RuleException?ex.Message:"保存できませんでした。入力は残しています。（CAL-FORM）"; }
            finally { saving=false; panel.IsEnabled=true; }
        });
        panel.Children.Add(button);
    }
    private void EditCase(WorkItem? task)
    {
        var dialog=Form(task is null?"カレンダーへ案件を登録":"案件の予定と色",out var panel);
        var title=Input(panel,"用件",task?.Title??"");
        var received=DateInput(panel,"依頼を受けた日（未確認なら空欄）",task is null?selectedDay:task.ReceivedOn);
        panel.Children.Add(Txt("本当の回答締切"));
        var kind=new ComboBox { ItemsSource=new[]{"未確認","確認済み期限なし","日付のみ","日時指定（日本時間）"},SelectedIndex=(int)(task?.Deadline.Kind??DeadlineKind.Date),FontSize=FontSize };
        System.Windows.Automation.AutomationProperties.SetName(kind,"回答締切の種類"); panel.Children.Add(kind);
        var deadline=DateInput(panel,"回答〆の日付",task?.Deadline.Day);
        var time=Input(panel,"時刻（日時指定のときだけ・例17:00）",task?.Deadline.At?.ToOffset(Japan.Offset).ToString("HH:mm")??"");
        void EnableDates(){deadline.IsEnabled=kind.SelectedIndex>=2;time.IsEnabled=kind.SelectedIndex==3;}
        kind.SelectionChanged+=(_,_)=>EnableDates();EnableDates();
        panel.Children.Add(Txt("案件の色（優先度の採点ではありません）"));
        var color=new ComboBox { ItemsSource=new[]{"青","青緑","紫","琥珀","桃"},SelectedIndex=(int)(task?.CalendarColor??CaseColor.Blue),FontSize=FontSize };panel.Children.Add(color);
        panel.Children.Add(Txt("受付日や課内への依頼日を変えても、本当の締切を自動では動かしません。"));
        string? newId=null;
        AddSave(dialog,panel,()=>
        {
            var k=(DeadlineKind)kind.SelectedIndex;var day=ReadDate(deadline);DateTimeOffset? at=null;
            if(k==DeadlineKind.DateTime)
            {
                if(day is null || !TimeOnly.TryParseExact(time.Text,"HH:mm",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var parsed))throw new RuleException("日時指定には日付と時刻HH:mmを入力してください。");
                at=new DateTimeOffset(day.Value.ToDateTime(parsed),Japan.Offset);
            }
            var value=new Deadline(k,k==DeadlineKind.Date?day:null,at,k==DeadlineKind.Unknown?"未確認":"本人がカレンダー画面で確認");
            if(task is not null && value.Kind==task.Deadline.Kind && value.Date==task.Deadline.Date && value.At==task.Deadline.At) value=task.Deadline;
            if(task is null)newId=service.AddCalendarCase(title.Text,ReadDate(received),value,(CaseColor)color.SelectedIndex);
            else service.SetCalendar(task.Id,task.Version,title.Text,ReadDate(received),value,(CaseColor)color.SelectedIndex);
        },()=>{selectedId=newId??task!.Id;selectedDay=ReadDate(received)??ReadDate(deadline)??selectedDay;month=new(selectedDay.Year,selectedDay.Month,1);listMode=0;listPage=0;ShowCasePane();});
        dialog.ShowDialog();
    }
    private void AddTeams(WorkItem task)
    {
        var dialog=Form("回答を集める担当を選ぶ",out var panel);
        panel.Children.Add(Txt("一度追加した担当は、次の案件でチェックするだけで使えます。",true));
        var choices=new List<(string Name,CheckBox Box)>();
        foreach(var name in state.TeamRoster.Concat(state.IsDemo ? new[]{"担当A","担当B","担当C"} : Array.Empty<string>()).DistinctBy(ResponsePolicy.Key))
        {
            var exists=task.Responses.Any(r=>ResponsePolicy.Key(r.TeamName)==ResponsePolicy.Key(name));
            var box=new CheckBox{Content=name+(exists?"（追加済み）":""),IsEnabled=!exists,IsChecked=exists,Margin=new(0,6,0,6),FontSize=FontSize};panel.Children.Add(box);choices.Add((name,box));
        }
        var names=Input(panel,"新しい担当名（任意・1行に1担当、初回だけ）","",true);
        AddSave(dialog,panel,()=>service.AddTeams(task.Id,task.Version,choices.Where(c=>c.Box.IsEnabled&&c.Box.IsChecked==true).Select(c=>c.Name)
            .Concat(names.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries))));dialog.ShowDialog();
    }
    private void EditResponse(WorkItem task,TeamResponse response)
    {
        var dialog=Form(response.TeamName+"の回答記録",out var panel);
        panel.Children.Add(Txt(response.TeamName,true,FontSize+4));
        var stage=new ComboBox{ItemsSource=new[]{"未依頼","依頼済み","回答あり","確認済み"},SelectedIndex=(int)response.Stage,FontSize=FontSize};panel.Children.Add(Txt("記録の状態"));panel.Children.Add(stage);
        var requested=DateInput(panel,"課内へ依頼した日",response.RequestedOn);var answered=DateInput(panel,"回答が来た日",response.AnsweredOn);var reviewed=DateInput(panel,"回答を確認した日",response.ReviewedOn);
        var values=Enum.GetValues<AnswerKind>();var answer=new ComboBox{ItemsSource=values.Select(ResponsePolicy.AnswerLabel).ToList(),SelectedIndex=(int)response.Answer,FontSize=FontSize};panel.Children.Add(Txt("どんな回答か"));panel.Children.Add(answer);
        var text=Input(panel,"回答内容（定型だけで足りる場合は空欄でOK）",response.AnswerText,true);var note=Input(panel,"補足・次に確認したいこと（任意）",response.Note,true);
        panel.Children.Add(Txt("状態を戻すと、その状態に不要な後続の日付・回答は現行記録から外れます。前の値は履歴に残ります。"));
        AddSave(dialog,panel,()=>
        {
            var status=(ResponseStage)stage.SelectedIndex;
            var draft=ResponsePolicy.PrepareEdit(response,response with{Stage=status,RequestedOn=ReadDate(requested),
                AnsweredOn=ReadDate(answered),ReviewedOn=ReadDate(reviewed),
                Answer=(AnswerKind)answer.SelectedIndex,AnswerText=text.Text,Note=note.Text});
            service.EditResponse(task.Id,task.Version,draft);
        });dialog.ShowDialog();
    }
}
