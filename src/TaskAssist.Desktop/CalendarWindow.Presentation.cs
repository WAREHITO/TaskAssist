using System.Windows;
using System.Windows.Controls;
using TaskAssist.Core;

namespace TaskAssist.Desktop;

public partial class CalendarWindow
{
    private void ShowCasePane() { compactDetails=true; UpdatePaneLayout(); }

    private void ShowCaseList()
    {
        CalendarPanel.UpdateLayout();
        if(caseListHeading is not null)
            CalendarScroll.ScrollToVerticalOffset(caseListHeading.TranslatePoint(new Point(0,0),CalendarPanel).Y);
    }

    private void UpdatePaneLayout()
    {
        if(PaneSwitch is null || CalendarScroll is null || DetailsFrame is null) return;
        var compact=ActualWidth<1050 || Scale>=1.5;
        PaneSwitch.Visibility=compact?Visibility.Visible:Visibility.Collapsed;
        Grid.SetColumnSpan(CalendarScroll,compact?3:1);
        Grid.SetColumn(DetailsFrame,compact?0:2); Grid.SetColumnSpan(DetailsFrame,compact?3:1);
        CalendarScroll.Visibility=compact&&compactDetails?Visibility.Collapsed:Visibility.Visible;
        DetailsFrame.Visibility=compact&&!compactDetails?Visibility.Collapsed:Visibility.Visible;
        if(PaneSwitch.Children.Count==2)
        {
            PaneSwitch.Children[0].IsEnabled=compactDetails;
            PaneSwitch.Children[1].IsEnabled=!compactDetails;
        }
    }

    private void DrawWeek(CalendarWeek week, List<WorkItem> cases)
    {
        var grid = new Grid { Margin = new(0,0,0,7) };
        for (var c = 0; c < 7; c++) grid.ColumnDefinitions.Add(new() { Width = new(1,GridUnitType.Star) });
        for (var r = 0; r < 4; r++) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (var c = 0; c < 7; c++)
        {
            if (week.Days[c] is not {} day) continue;
            var onDay = cases.Where(t => CalendarPresentation.AppearsOn(t,day)).ToList();
            var label = day.Day + (day == Japan.Day(clock.Now) ? " 今日" : "");
            var received = onDay.Count(t => t.ReceivedOn == day);
            var due = onDay.Count(t => t.Deadline.Day == day);
            var requested = onDay.Sum(t => t.Responses.Count(r => r.RequestedOn == day));
            var answered = onDay.Sum(t => t.Responses.Count(r => r.AnsweredOn == day));
            var marks = new List<string>();
            if (received > 0) marks.Add($"受{received}");
            if (due > 0) marks.Add($"〆{due}");
            if (requested > 0) marks.Add($"依{requested}");
            if (answered > 0) marks.Add($"答{answered}");
            var content = new StackPanel(); content.Children.Add(Txt(label,day == selectedDay));
            if (marks.Count > 0) content.Children.Add(Txt(string.Join(" ",marks),false,13*Scale));
            var button = Cmd("",() => SelectDay(day)); button.Content = content;
            button.Padding = new(3); button.Margin = new(1); button.MinWidth = 0; button.MinHeight = 40*Scale;
            button.VerticalContentAlignment = VerticalAlignment.Top; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.BorderThickness = new(day == selectedDay ? 2 : 1);
            button.BorderBrush = day == selectedDay ? SystemColors.HighlightBrush : SystemColors.ActiveBorderBrush;
            button.ToolTip = day.ToString("yyyy/M/d") + $"・{onDay.Count}件。クリックで案件と当日の記録を見る。";
            System.Windows.Automation.AutomationProperties.SetName(button,day.ToString("yyyy年M月d日")+$"、{onDay.Count}件、"+string.Join(" ",marks));
            Grid.SetColumn(button,c); grid.Children.Add(button);
        }
        foreach (var span in week.Spans.Where(s => s.Lane < 3))
        {
            var paint = Paint(span.Task.CalendarColor);
            var label = (span.Task.ReceivedOn == span.First ? "受 " : "") + span.Task.Title + (span.Task.Deadline.Day == span.Last ? " 〆" : "");
            var button = Cmd("",() =>
            {
                selectedId = span.Task.Id; selectedDay = span.First; listMode = 0; listPage = 0;
                DrawCalendar(); DrawDetails(); DetailsScroll.ScrollToTop(); ShowCasePane();
            });
            button.Content = new TextBlock { Text=label, TextTrimming=TextTrimming.CharacterEllipsis, TextWrapping=TextWrapping.NoWrap,
                Foreground=paint.Ink, Margin=new(0), FontSize=13*Scale };
            button.Background = paint.Fill; button.BorderBrush = selectedId == span.Task.Id ? SystemColors.HighlightBrush : paint.Ink;
            button.BorderThickness = new(selectedId == span.Task.Id ? 2 : 1); button.Padding=new(5,3,5,3); button.Margin=new(1);
            button.MinHeight=28*Scale; button.MinWidth=0; button.HorizontalContentAlignment=HorizontalAlignment.Stretch;
            button.ToolTip=span.Task.Title+"\n"+CalendarPolicy.Period(span.Task);
            System.Windows.Automation.AutomationProperties.SetName(button,$"期間帯 {span.Task.Title} {span.First:M/d}〜{span.Last:M/d}");
            Grid.SetRow(button,span.Lane+1); Grid.SetColumn(button,span.Column); Grid.SetColumnSpan(button,span.Length); grid.Children.Add(button);
        }
        CalendarPanel.Children.Add(grid);
        var hidden = week.Spans.Where(s => s.Lane >= 3).Select(s => s.Task.Id).Distinct().Count();
        if (hidden > 0)
        {
            var first = week.Days.First(d => d is not null)!.Value;
            var last = week.Days.Last(d => d is not null)!.Value;
            CalendarPanel.Children.Add(Cmd($"{first:M/d}〜{last:M/d}：ほか{hidden}件の帯・この週の全案件を見る",() =>
            {
                weekFirst=first; weekLast=last; listMode=3; listPage=0; DrawCalendar();
                ShowCaseList();
            }));
        }
    }

    private Grid ResponseColumns(string team, string stage, string answer, bool bold = false)
    {
        var grid = new Grid();
        foreach (var width in new[] { 1.1, 1.0, 1.7 }) grid.ColumnDefinitions.Add(new() { Width = new(width,GridUnitType.Star) });
        var texts = new[] { team, stage, answer };
        for (var i=0;i<3;i++)
        {
            var text = Txt(texts[i],bold); text.Margin=new(5,4,5,4); Grid.SetColumn(text,i); grid.Children.Add(text);
        }
        return grid;
    }

    private void DrawResponses(WorkItem task)
    {
        if (task.Responses.Count == 0) { DetailsPanel.Children.Add(Txt("「担当を選んで追加」から始められます。")); return; }
        var counts = Enum.GetValues<ResponseStage>().Select(s => task.Responses.Count(r => r.Stage==s)).ToArray();
        var filter = new ComboBox { ItemsSource=new[] { $"すべて {task.Responses.Count}担当", $"回答待ち {counts[1]}担当", $"回答あり・未確認 {counts[2]}担当", $"未依頼 {counts[0]}担当", $"確認済み {counts[3]}担当" }, SelectedIndex=responseFilter,
            Margin=new(0,4,6,6), FontSize=FontSize };
        System.Windows.Automation.AutomationProperties.SetName(filter,"担当一覧の絞り込み");
        filter.SelectionChanged+=(_,_)=>{responseFilter=filter.SelectedIndex;DrawDetails();};
        DetailsPanel.Children.Add(filter);
        var wanted = responseFilter switch {1=>ResponseStage.Requested,2=>ResponseStage.Received,3=>ResponseStage.NotRequested,_=>ResponseStage.Reviewed};
        var rows = task.Responses.Where(r=>responseFilter==0 || r.Stage==wanted).ToList();
        DetailsPanel.Children.Add(ResponseColumns("担当","状態","回答 / 記録日",true));
        var list = new ListBox { MaxHeight=280*Scale, HorizontalContentAlignment=HorizontalAlignment.Stretch, FontSize=FontSize };
        ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list,ScrollBarVisibility.Auto);
        System.Windows.Automation.AutomationProperties.SetName(list,"担当の状況一覧。選ぶと下に操作が出ます");
        var selectedPanel = new StackPanel();
        foreach (var response in rows)
        {
            var answer = response.Stage >= ResponseStage.Received ? response.AnswerLabel + "\n回答 " + ResponsePolicy.Date(response.AnsweredOn) :
                response.Stage == ResponseStage.Requested ? "依頼 " + ResponsePolicy.Date(response.RequestedOn) : "—";
            var item = new ListBoxItem { Content=ResponseColumns(response.TeamName,response.StageText,answer), Tag=response.Id,
                HorizontalContentAlignment=HorizontalAlignment.Stretch, Padding=new(0), IsSelected=response.Id==selectedResponseId };
            System.Windows.Automation.AutomationProperties.SetName(item,response.TeamName+"、"+response.StageText+"、"+answer.Replace('\n',' '));
            list.Items.Add(item);
        }
        if (!rows.Any(r=>r.Id==selectedResponseId)) selectedResponseId=null;
        void ShowSelected()
        {
            selectedPanel.Children.Clear();
            var response=rows.SingleOrDefault(r=>r.Id==selectedResponseId);
            if(response is null) { selectedPanel.Children.Add(Txt(rows.Count==0?"該当する担当はいません。":"一覧で担当を選ぶと、ここに記録ボタンが出ます。")); return; }
            selectedPanel.Children.Add(Txt("操作する担当："+response.TeamName+"（"+response.StageText+"）",true));
            var actions=new WrapPanel();
            if(response.Stage==ResponseStage.NotRequested) actions.Children.Add(Cmd("今日依頼した",()=>Run(()=>service.MarkRequested(task.Id,task.Version,response.Id))));
            if(response.Stage<ResponseStage.Received)
                foreach(var kind in new[]{AnswerKind.Later,AnswerKind.NoItems,AnswerKind.NoIssues,AnswerKind.HasItems,AnswerKind.NeedsReview})
                    actions.Children.Add(Cmd(kind==AnswerKind.Later?"回答あり・内容は後で":ResponsePolicy.AnswerLabel(kind),()=>Run(()=>service.RecordAnswer(task.Id,task.Version,response.Id,kind))));
            if(response.Stage==ResponseStage.Received) actions.Children.Add(Cmd("確認済みにする",()=>Run(()=>service.MarkReviewed(task.Id,task.Version,response.Id))));
            actions.Children.Add(Cmd("記録を直す・詳しい回答",()=>EditResponse(task,response)));
            selectedPanel.Children.Add(actions);
            selectedPanel.Children.Add(Txt("日付は今日を記録。過去分は「記録を直す」で選べます。"));
            selectedPanel.Children.Add(new Expander { Header=Txt("この担当の全記録・回答内容・補足"), Content=Txt(ResponsePolicy.RecordText(response)) });
        }
        list.SelectionChanged+=(_,_)=>
        {
            selectedResponseId=(list.SelectedItem as ListBoxItem)?.Tag as string; ShowSelected();
            selectedPanel.UpdateLayout(); selectedPanel.BringIntoView();
        };
        DetailsPanel.Children.Add(list); DetailsPanel.Children.Add(Card(selectedPanel)); ShowSelected();
    }
}
