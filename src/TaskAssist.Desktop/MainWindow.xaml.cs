using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TaskAssist.Core;

namespace TaskAssist.Desktop;

public partial class MainWindow : Window
{
    private readonly string folder;
    private readonly ProfileStore? profiles;
    private readonly string profileId;
    private readonly string preferencesFolder;
    public event Action? ProfileChanged;
    private SqliteRepository repository;
    private TaskService service;
    private Snapshot state = new();
    private readonly IClock clock = new SystemClock();
    private int reviewIndex, alertPage, taskPage, inboxPage, historyPage;
    private string search = "";
    private bool beginner = true, busy;
    private double scale = 1;
    private Button? pendingDatesAttention;
    private readonly DispatcherTimer attentionTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly List<WeakReference<TextBlock>> timedLabels = [];
    public MainWindow(string folder, ProfileStore? profiles = null)
    {
        InitializeComponent(); this.folder = folder; this.profiles = profiles;
        profileId = profiles?.Load().ActiveId ?? "demo";
        preferencesFolder = profiles?.Root ?? folder;
        var active = Path.Combine(folder, "active-data.txt");
        var filename = File.Exists(active) ? File.ReadAllText(active).Trim() : "tasks.db";
        if (Path.GetFileName(filename) != filename || !filename.EndsWith(".db", StringComparison.Ordinal)) throw new IOException();
        repository = profiles is null ? new SqliteRepository(Path.Combine(folder, filename)) : profiles.Open(profileId); service = new TaskService(repository, clock);
        var prefs = Path.Combine(preferencesFolder, "preferences.json");
        if (File.Exists(prefs))
        {
            try { var p = JsonSerializer.Deserialize<DisplayPreferences>(File.ReadAllText(prefs)); scale = p?.Scale is 1.5 or 2 ? p.Scale : 1; beginner = p?.Beginner ?? true; }
            catch (JsonException) { SaveStatus.Text = "表示設定を読めないため初期表示で起動しました。"; }
        }
        FontSize = 15 * scale;
        InitializeIntegration();
        Refresh();
        attentionTimer.Tick += (_, _) => UpdateTimeAttention(); attentionTimer.Start();
        Activated += (_, _) => UpdateTimeAttention();
        Closed += (_, _) => { attentionTimer.Stop(); repository.Dispose(); };
        Closing += (_, e) => { if (busy || integrationBusy) { CancelIntegration(); e.Cancel = true; return; } if (!string.IsNullOrWhiteSpace(QuickTitle.Text)) e.Cancel = MessageBox.Show(this, "入力中の用件はまだ登録されていません。閉じますか？", "入力中の用件", MessageBoxButton.YesNo) != MessageBoxResult.Yes; };
    }
    private static TextBlock Text(string value, double size = 0, bool bold = false) => new()
    { Text = value, FontSize = size > 0 ? size : 15, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
    private TextBlock Body(string value, bool bold = false) => Text(value, FontSize, bold);
    private void Heading(Panel parent, string title) => parent.Children.Add(Text(title, FontSize + 5, true));
    private Button Button(string text, Action action)
    {
        var button = new Button { Content = text }; button.Click += (_, _) => action(); return button;
    }
    private Border Card(UIElement content) => new() { Child = content, Padding = new Thickness(14), Margin = new Thickness(0,5,0,12),
        BorderThickness = new Thickness(1), BorderBrush = SystemColors.ActiveBorderBrush, CornerRadius = new CornerRadius(6) };
    private async void Run(Action action, Action? success = null)
    {
        if (busy) return;
        busy = true; Tabs.IsEnabled = false; QuickTitle.IsEnabled = false; SaveStatus.Text = "保存処理中…";
        try { await Task.Run(action); Refresh(); success?.Invoke(); }
        catch (Exception ex)
        {
            SaveStatus.Text = "保存できませんでした。操作は確定していません。入力を保持しています。";
            MessageBox.Show(this, ex is RuleException ? ex.Message : "保存または読込みに失敗しました。空き容量・アクセス権を確認してください。保存済みの記録は保持しています。（SAVE-01）", "操作を完了できませんでした");
        }
        finally { busy = false; Tabs.IsEnabled = true; QuickTitle.IsEnabled = true; }
    }
    private void AddClicked(object sender, RoutedEventArgs e)
    {
        var title = QuickTitle.Text;
        if (string.IsNullOrWhiteSpace(title)) { QuickTitle.Focus(); return; }
        Run(() => service.Add(title), () => { QuickTitle.Clear(); QuickTitle.Focus(); });
    }
    private void CalendarClicked(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var calendar = new CalendarWindow(service, clock) { Owner = this, FontSize = FontSize };
        calendar.ShowDialog(); Refresh();
    }
    private void Refresh()
    {
        state = service.Read();
        timedLabels.Clear();
        Title = "仕事アシスト — " + (state.IsDemo ? "架空画面試験" : AppRelease.Version + " 接続検証版");
        UpdateConnectionDisplay();
        SaveStatus.Text = "ローカル保存済み：" + (state.SavedAt?.ToOffset(Japan.Offset).ToString("yyyy/MM/dd HH:mm:ss") ?? "まだ操作なし");
        DrawHome(); DrawTasks(); DrawInbox(); DrawHistory(); DrawSettings();
        UpdateTimeAttention();
    }
    private string HealthText(Snapshot snapshot) => snapshot.IsDemo ? snapshot.IntakeText : "所属：" + profiles!.Load().Active.Name + "\n" + ConnectorHealth(snapshot);
    private void UpdateConnectionDisplay()
    {
        Health.Text = HealthText(state);
        var connector = state.Automation.Connector;
        ConnectionSummary.Text = state.IsDemo ? "架空受付の状態・保存の詳細" : $"受付：{(connector.Enabled ? connector.Health : "読取り停止中") }・状態と保存の詳細";
    }
    private void UpdateTimeAttention()
    {
        if (busy) return;
        var now = clock.Now;
        var alerts = Policy.Alerts(state, now).Count;
        var dueReview = Policy.Reviews(state,now).Count;
        var pendingDates = PendingDateAttention().Count;
        TimeAttention.Text = $"期限の注意 {alerts}件 ／ 期限・次に見る日の確認 {dueReview}件\n未確定の日付候補の注意 {pendingDates}件・受付の確認待ち {state.Inbox.Count(m => m.Pending)}件\n日本時間 {now.ToOffset(Japan.Offset):M/d HH:mm}・一覧は「最新の注意を開く」";
        if (pendingDatesAttention is not null)
        {
            pendingDatesAttention.Content = $"未確定の日付候補の注意 {pendingDates}件（確定締切とは別）";
            pendingDatesAttention.IsEnabled = pendingDates > 0;
        }
        timedLabels.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in timedLabels)
            if (reference.TryGetTarget(out var label) && label.Tag is Func<string> value) label.Text = value();
        // Refresh only text. Never remove a focused button, expand/collapse source, or change current work.
    }
    private TextBlock Timed(Func<string> value)
    {
        var label = Body(value()); label.Tag = value; timedLabels.Add(new(label)); return label;
    }
    private void TimeAttentionClicked(object sender, RoutedEventArgs e)
    {
        var dialog = Dialog("最新の注意・次に見る日", out var panel); var page = 0;
        void Draw()
        {
            panel.Children.Clear();
            if (PendingDateAttention().Count > 0) panel.Children.Add(Button("未確定の日付候補を確認する",() => { dialog.Close(); ShowPendingDates(); }));
            var tasks = Policy.Alerts(state, clock.Now).Concat(Policy.Reviews(state, clock.Now)).DistinctBy(t => t.Id).ToList();
            panel.Children.Add(Body($"期限の注意・確認対象：全{tasks.Count}件（期限未確認も含む）"));
            foreach (var task in tasks.Skip(page*10).Take(10)) panel.Children.Add(Card(TaskCard(task, task.ReviewOn <= Japan.Day(clock.Now) ? "次に見る日が来ています。" : null, () => dialog.Close())));
            if (page > 0) panel.Children.Add(Button("前の10件", () => { page--; Draw(); }));
            if ((page+1)*10 < tasks.Count) panel.Children.Add(Button("次の10件", () => { page++; Draw(); }));
        }
        Draw(); dialog.ShowDialog();
    }
    private StackPanel TaskCard(WorkItem task, string? reason = null, Action? beforeAction = null)
    {
        Button Command(string label, Action action) => Button(label, () => { beforeAction?.Invoke(); action(); });
        var box = new StackPanel(); box.Children.Add(Body(task.Title, true));
        box.Children.Add(Timed(() => task.StatusText + "  ／  " + task.Deadline.Display(clock.Now)));
        if (reason is not null) box.Children.Add(Body(reason));
        if (task.Responses.Count > 0) box.Children.Add(Body(ResponsePolicy.Summary(task)));
        if (task.Status == WorkStatus.Working)
        {
            box.Children.Add(Body("次の一手：" + task.NextAction)); box.Children.Add(Body("完了条件：" + task.Completion));
            if (task.Note.Length > 0) box.Children.Add(Body("再開メモ：" + task.Note));
        }
        var row = new WrapPanel();
        if (Policy.Ready(task, state, clock.Now)) row.Children.Add(Command("これを進める", () => Run(() => service.Start(task.Id, task.Version))));
        if (!task.Closed)
        {
            row.Children.Add(Command("完了", () => Complete(task)));
            if (task.Status == WorkStatus.Working) row.Children.Add(Command("中断", () => Pause(task)));
            if (task.Status is WorkStatus.Waiting or WorkStatus.Decision)
                row.Children.Add(Command("待ちが解消した", () => Run(() => service.Transition(task.Id, task.Version, WorkStatus.NotStarted))));
            else row.Children.Add(Command("待つ", () => Wait(task)));
        }
        row.Children.Add(Command("詳細・原文", () => Edit(task))); box.Children.Add(row);
        if (task.Prerequisites.Any(id => state.Tasks.Any(t => t.Id == id && t.Status != WorkStatus.Completed))) box.Children.Add(Body("前提作業が未完了のため、着手候補に入りません。"));
        return box;
    }
    private void DrawHome()
    {
        Home.Children.Clear(); Heading(Home, "今の1件");
        var current = state.Tasks.SingleOrDefault(t => t.Status == WorkStatus.Working);
        if (current is not null) Home.Children.Add(Card(TaskCard(current)));
        else Home.Children.Add(Card(Body("今の仕事を選んでください。「これを進める」で、ここに固定されます。")));
        Heading(Home, "確認が必要");
        pendingDatesAttention = Button("未確定の日付候補の注意",ShowPendingDates);
        Home.Children.Add(pendingDatesAttention);
        foreach (var conflict in AutomationPolicy.Conflicts(state,clock.Now)) Home.Children.Add(Card(Body(conflict,true)));
        foreach (var recurrence in state.Automation.Recurrences.Where(r => r.Enabled && (AutomationPolicy.PendingCount(r,Japan.Day(clock.Now)) < 0 || r.CatchUp == CatchUpChoice.Ask && AutomationPolicy.PendingCount(r,Japan.Day(clock.Now)) > 1)))
            Home.Children.Add(Card(Body("繰り返しの発生分を確認してください：" + recurrence.Title + "（設定・繰り返し業務）")));
        DrawHomeReviews();
        UpdateTimeAttention();
    }
    private List<InboxItem> PendingDateAttention() => AutomationPolicy.PendingDeadlineSources(state,clock.Now);
    private void ShowPendingDates()
        {
            var dialog = Dialog("未確定の日付候補を原文で確認",out var panel);
            foreach (var mail in PendingDateAttention())
            {
                var card = InboxSource(mail);
                foreach (var hint in mail.DeadlineHints) card.Children.Add(Body($"未確定：{hint.Text} → {hint.Day:yyyy/MM/dd}{(hint.EndDay is {} end ? "〜" + end.ToString("yyyy/MM/dd") : "")}\n{hint.Warning}\n原文抜粋：{hint.Excerpt}"));
                card.Children.Add(Button("受付で確認・判定する",() =>
                {
                    inboxPage = Math.Max(0,state.Inbox.OrderBy(m => !m.Pending).ThenBy(m => m.ReceivedAt).ThenBy(m => m.SourceKey).ToList().FindIndex(m => m.Id == mail.Id)) / 15;
                    dialog.Close(); DrawInbox(); Tabs.SelectedIndex = 2;
                }));
                panel.Children.Add(Card(card));
            }
            dialog.ShowDialog();
        }
    private void DrawHomeReviews()
    {
        var reviews = state.Inbox.Where(m => m.Pending).OrderBy(m => m.ReviewOn > Japan.Day(clock.Now)).ThenBy(m => m.ReceivedAt).ThenBy(m => m.Id).ToList();
        var unknown = Policy.Reviews(state, clock.Now);
        var total = reviews.Count + unknown.Count;
        Home.Children.Add(Body($"受付 {reviews.Count}件 ／ 期限・次に見る日の確認 {unknown.Count}件（後回しも含む）"));
        if (total > 0)
        {
            reviewIndex %= total;
            if (reviewIndex < reviews.Count) Home.Children.Add(Card(InboxCard(reviews[reviewIndex])));
            else
            {
                var task = unknown[reviewIndex-reviews.Count];
                Home.Children.Add(Card(TaskCard(task, task.ReviewOn <= Japan.Day(clock.Now) ? "次に見る日が来ています。待ちが解消したか確認してください。" : "期限を確認してください。未確認のまま保持されています。")));
            }
            var row = new WrapPanel(); row.Children.Add(Body($"{reviewIndex+1} / {total} 件"));
            row.Children.Add(Button("次の確認へ", () => { reviewIndex = (reviewIndex + 1) % total; DrawHome(); })); Home.Children.Add(row);
        }
        else Home.Children.Add(Body("現在、確認待ちはありません。"));
        Heading(Home, "期限の注意"); var alerts = Policy.Alerts(state, clock.Now);
        Home.Children.Add(Body($"注意 {alerts.Count}件。相手待ち・判断待ちの締切も表示します。"));
        Page(Home, alerts, ref alertPage, t => TaskCard(t, t.ReviewOn > t.Deadline.Day ? "注意：次に見る日が本当の締切より後です。" : null), DrawHome, 3);
        Heading(Home, "次の候補"); var candidates = Policy.Candidates(state, clock.Now);
        foreach (var task in candidates.Take(3)) Home.Children.Add(Card(TaskCard(task, Policy.Reason(task, clock.Now))));
        if (candidates.Count == 0) Home.Children.Add(Body("今、着手できる候補はありません。"));
        if (candidates.Count > 3) Home.Children.Add(Button($"残り {candidates.Count-3} 件を「すべて・検索」で見る", () => Tabs.SelectedIndex = 1));
        if (beginner) Home.Children.Add(Body("新着や順序の変化で「今の1件」は替わりません。完了を戻すときは「履歴・取消」を開きます。"));
    }
    private void Page<T>(Panel panel, List<T> items, ref int page, Func<T,UIElement> create, Action redraw, int count = 15)
    {
        page = Math.Clamp(page, 0, Math.Max(0, (items.Count-1)/count)); var pageNow = page;
        foreach (var item in items.Skip(page*count).Take(count)) panel.Children.Add(Card(create(item)));
        if (items.Count <= count) return;
        var row = new WrapPanel(); row.Children.Add(Body($"{page+1} / {(items.Count+count-1)/count} ページ・全{items.Count}件"));
        // Map the owning panel to its page cursor, retaining all records beyond the visible page.
        void Set(int value) { if (panel == Home) alertPage = value; else if (panel == All) taskPage = value; else if (panel == Inbox) inboxPage = value; else historyPage = value; redraw(); }
        var prev = Button("前のページ", () => Set(pageNow-1)); prev.IsEnabled = pageNow > 0;
        var next = Button("次のページ", () => Set(pageNow+1)); next.IsEnabled = (pageNow+1)*count < items.Count;
        row.Children.Add(prev); row.Children.Add(next); panel.Children.Add(row);
    }
    private void DrawTasks()
    {
        All.Children.Clear(); Heading(All, "すべての仕事・完了した仕事も検索");
        var field = new TextBox { Text = search }; System.Windows.Automation.AutomationProperties.SetName(field, "仕事の検索語");
        All.Children.Add(field); All.Children.Add(Button("検索", () => { search = field.Text; taskPage = 0; DrawTasks(); }));
        var items = state.Tasks.Where(t => (t.Title + t.Note + t.Material).Contains(search, StringComparison.CurrentCultureIgnoreCase)).OrderBy(t => t.Closed).ThenByDescending(t => t.CreatedAt).ToList();
        All.Children.Add(Body($"{items.Count}件。終了した仕事はここで確認できます。"));
        Page(All, items, ref taskPage, t => TaskCard(t), DrawTasks);
    }
    private StackPanel InboxCard(InboxItem mail)
    {
        var box = new StackPanel(); box.Children.Add(Body(mail.Subject, true));
        box.Children.Add(Body("依頼者：" + mail.Sender + " ／ " + mail.ReceivedAt.ToOffset(Japan.Offset).ToString("yyyy/MM/dd HH:mm")));
        box.Children.Add(Body(mail.Reason));
        if (mail.Status == IntakeStatus.ReadBlocked) box.Children.Add(Body("本文を確認できません。締切は推測していません。"));
        if (mail.ReviewOn is {} day) box.Children.Add(Body($"次に確認：{day:yyyy/MM/dd}（未確認のまま保持）"));
        box.Children.Add(new Expander { Header = "原文を読む（文字のみ）", Content = new TextBox { Text = mail.Body, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var row = new WrapPanel();
        if (mail.Pending) foreach (var choice in new[] { "仕事にする", "対応不要", "後で確認" }) row.Children.Add(Button(choice, () => Run(() => service.ReviewInbox(mail.Id, choice))));
        if (mail.Status == IntakeStatus.Ignored) row.Children.Add(Button("判定を戻す", () => Run(() => service.RestoreInboxReview(mail.Id))));
        if (mail.TaskId is {} id) row.Children.Add(Button("登録した仕事を開く", () => Edit(state.Tasks.Single(t => t.Id == id))));
        box.Children.Add(row); AddMailActions(box,mail); return box;
    }
    private void DrawInbox()
    {
        Inbox.Children.Clear(); Heading(Inbox, state.IsDemo ? "架空メールの受付記録" : "Outlookから保存した受付記録");
        if (state.IsDemo) {
        var controls = new WrapPanel(); controls.Children.Add(Button("同梱の架空メールを再照合", () => Run(() => service.ImportDemo(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "mail_scenarios.json"))))));
        controls.Children.Add(Button(state.DemoConnected ? "架空受付を停止" : "架空受付を再開", () => Run(() => service.SetDemoConnection(!state.DemoConnected)))); Inbox.Children.Add(controls);
        } else { Inbox.Children.Add(Body(ConnectorHealth(state))); Inbox.Children.Add(Button("保存済み受付の表示を更新",Refresh)); }
        Inbox.Children.Add(Body($"全{state.Inbox.Count}件。対応不要も消さずに保持します。自然文から期限・優先度を確定する機能は未実装です。"));
        var items = state.Inbox.OrderBy(m => !m.Pending).ThenBy(m => m.ReceivedAt).ThenBy(m => m.SourceKey).ToList();
        Page(Inbox, items, ref inboxPage, InboxCard, DrawInbox);
    }
    private void DrawHistory()
    {
        History.Children.Clear(); Heading(History, "保存した変更と取消");
        History.Children.Add(Body("取消は対象の仕事だけに適用します。その後に同じ仕事が変更された場合は取消を止めます。"));
        var events = state.Events.OrderByDescending(e => e.At).ToList();
        Page(History, events, ref historyPage, ev =>
        {
            var panel = new StackPanel(); panel.Children.Add(Body(ev.At.ToOffset(Japan.Offset).ToString("MM/dd HH:mm:ss") + "  " + ev.Label, true));
            if (ev.ConfigurationAfter.Length > 0) panel.Children.Add(Button("設定の変更前・変更後を見る",() => { var dialog = Dialog("設定変更の記録",out var contents); contents.Children.Add(Body("変更前\n" + ev.ConfigurationBefore + "\n変更後\n" + ev.ConfigurationAfter)); dialog.ShowDialog(); }));
            foreach (var task in ev.After.Values)
            {
                ev.Before.TryGetValue(task.Id, out var previous);
                panel.Children.Add(Body(task.Title + "：" + (previous?.StatusText ?? "登録") + " → " + task.StatusText));
                if (previous is not null && previous.Deadline != task.Deadline) panel.Children.Add(Body("締切：" + previous.Deadline.Display(clock.Now) + " → " + task.Deadline.Display(clock.Now) + "\n根拠：" + task.Deadline.Evidence));
                foreach (var change in ResponsePolicy.Changes(previous, task)) panel.Children.Add(Body(change));
            }
            if (ev.Undone) panel.Children.Add(Body("取消済み"));
            else if (ev.Undoable && ev.Before.Count == ev.After.Count && ev.InboxBefore.Count == ev.InboxAfter.Count)
                panel.Children.Add(Button("元に戻す", () => Run(() => service.Undo(ev.Id))));
            return panel;
        }, DrawHistory);
    }
    private void Complete(WorkItem task)
    {
        if (task.ConfirmCompletion && MessageBox.Show(this, "完了条件を満たしましたか？\n" + task.Completion, "完了条件の確認", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        Run(() => service.Transition(task.Id, task.Version, WorkStatus.Completed, confirmed: task.ConfirmCompletion));
    }
    private void Pause(WorkItem task)
    {
        var dialog = Dialog("中断・再開メモ（任意）", out var panel);
        panel.Children.Add(Body(task.Title)); var note = new TextBox { Text = task.Note, AcceptsReturn = true, Height = 100, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(note); panel.Children.Add(Button("中断して保存", () => SaveDialog(dialog, () => service.Transition(task.Id, task.Version, WorkStatus.Paused, note.Text)))); dialog.ShowDialog();
    }
    private void Wait(WorkItem task)
    {
        var dialog = Dialog("待つ・次に見る日", out var panel); panel.Children.Add(Body("本当の締切：" + task.Deadline.Display(clock.Now)));
        panel.Children.Add(Body("規則未設定のため、翌日を暫定提案します。変更できます。"));
        var date = CheckedDate(panel, "次に見る日", Japan.Day(clock.Now).AddDays(1));
        var warning = Body(""); panel.Children.Add(warning);
        void Warn() => warning.Text = date.SelectedDate is {} d && task.Deadline.Day is {} deadline && DateOnly.FromDateTime(d) > deadline ? "注意：次に見る日が締切より後です。本当の締切は変わりません。" : "本当の締切は変わりません。";
        date.SelectedDateChanged += (_, _) => Warn(); Warn();
        var note = new TextBox { Text = task.Note }; panel.Children.Add(Body("必要な判断・メモ（任意）")); panel.Children.Add(note);
        foreach (var choice in new[] { ("相手を待つ", WorkStatus.Waiting), ("判断を待つ", WorkStatus.Decision) })
            panel.Children.Add(Button(choice.Item1, () => SaveDialog(dialog, () => service.Transition(task.Id, task.Version, choice.Item2, note.Text,
                date.ReadValue() ?? throw new RuleException("次に見る日を入力してください。")))));
        dialog.ShowDialog();
    }
    private Window Dialog(string title, out StackPanel panel)
    {
        panel = new StackPanel { Margin = new Thickness(22) };
        return new Window { Title = title, Owner = this, Width = 720, Height = 690, MinWidth = 500, MinHeight = 400, FontSize = FontSize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
    }
    private void SaveDialog(Window dialog, Action action)
    {
        try { action(); dialog.Close(); Refresh(); }
        catch (Exception ex) { MessageBox.Show(dialog, ex is RuleException ? ex.Message : "保存できませんでした。入力はこの画面に残しています。（SAVE-02）", "保存されていません"); }
    }
    private CheckedDatePicker CheckedDate(Panel panel, string label, DateOnly? value)
    {
        panel.Children.Add(Body(label));
        var field = new CheckedDatePicker { SelectedDate = value?.ToDateTime(TimeOnly.MinValue), Margin = new Thickness(0,4,0,10) };
        System.Windows.Automation.AutomationProperties.SetName(field, label); panel.Children.Add(field);
        var error = Body(""); var retry = Button("日付を入力し直す", field.ClearRejectedInput);
        error.Visibility = retry.Visibility = Visibility.Collapsed;
        field.InputValidationChanged += () =>
        {
            var invalid = field.RejectedInput is not null;
            error.Text = invalid ? $"「{field.RejectedInput}」は日付として使えません。元の日付へ戻って見えても保存を止めています。" : "";
            error.Visibility = retry.Visibility = invalid ? Visibility.Visible : Visibility.Collapsed;
        };
        panel.Children.Add(error); panel.Children.Add(retry); return field;
    }
    private void Edit(WorkItem task)
    {
        var draft = Copy.Of(task); var dialog = Dialog("仕事の詳細・原文", out var panel);
        TextBox Field(string label, string value, bool multi = false)
        {
            panel.Children.Add(Body(label)); var field = new TextBox { Text = value, AcceptsReturn = multi, TextWrapping = TextWrapping.Wrap, MinHeight = multi ? 65 : 32 };
            System.Windows.Automation.AutomationProperties.SetName(field, label); panel.Children.Add(field); return field;
        }
        panel.Children.Add(Body("状態：" + task.StatusText + " ／ 変更は「内容を保存」で確定します。"));
        var title = Field("用件", task.Title); var next = Field("次の一手", task.NextAction); var completion = Field("完了条件", task.Completion);
        var required = new CheckBox { Content = "完了時に条件の確認が必要", IsChecked = task.ConfirmCompletion, Margin = new Thickness(0,6,0,10) }; panel.Children.Add(required);
        panel.Children.Add(Body("本当の締切")); var kind = new ComboBox { ItemsSource = new[] { "未確認", "確認済み期限なし", "日付のみ", "日時指定（日本時間）" }, SelectedIndex = (int)task.Deadline.Kind }; panel.Children.Add(kind);
        var date = CheckedDate(panel, "締切の日付", task.Deadline.Day);
        var time = Field("時刻（日時指定のときだけ・例 17:00）", task.Deadline.At?.ToOffset(Japan.Offset).ToString("HH:mm") ?? "");
        var basis = Field("期限確認の根拠（期限を確定・変更するとき）", task.Deadline.Kind == DeadlineKind.Unknown ? "本人が画面で確認" : task.Deadline.Evidence, true);
        void DeadlineFields() { date.IsEnabled = kind.SelectedIndex >= 2; time.IsEnabled = kind.SelectedIndex == 3; basis.IsEnabled = kind.SelectedIndex != 0; }
        kind.SelectionChanged += (_, _) => DeadlineFields(); DeadlineFields();
        var review = CheckedDate(panel, "次に見る日（締切とは別）", task.ReviewOn);
        var planned = CheckedDate(panel, "作業候補の日（締切とは別）", task.PlannedOn);
        var note = Field("再開メモ・必要な判断（任意）", task.Note, true); var steps = Field("完了済みの手順（工程だけの記録）", task.DoneSteps, true);
        var material = Field("資料の参照メモ（任意・自動で開きません）", task.Material, true);
        var estimate = Field("所要時間（任意・分。空欄は不明）",task.EstimatedMinutes?.ToString() ?? "");
        var urgent = new CheckBox { Content = "本人が緊急の調整対象と確認", IsChecked = task.UrgentConfirmed }; panel.Children.Add(urgent);
        panel.Children.Add(Body("前提作業（任意・Ctrlで複数選択）"));
        var dependencies = new ListBox { ItemsSource = state.Tasks.Where(t => t.Id != task.Id).ToList(), DisplayMemberPath = "Title", SelectionMode = SelectionMode.Multiple, MaxHeight = 140 };
        foreach (WorkItem item in dependencies.Items) if (task.Prerequisites.Contains(item.Id)) dependencies.SelectedItems.Add(item);
        panel.Children.Add(dependencies);
        panel.Children.Add(Body("親の仕事（任意）")); var parents = new List<WorkItem> { new() { Id = "", Title = "親なし" } }; parents.AddRange(state.Tasks.Where(t => t.Id != task.Id));
        var parent = new ComboBox { ItemsSource = parents, DisplayMemberPath = "Title", SelectedValuePath = "Id", SelectedValue = task.ParentId ?? "" }; panel.Children.Add(parent);
        foreach (var id in task.SourceIds) if (state.Inbox.SingleOrDefault(m => m.Id == id) is {} source) panel.Children.Add(Card(InboxSource(source)));
        panel.Children.Add(Button("内容を保存", () => SaveDialog(dialog, () =>
        {
            var k = (DeadlineKind)kind.SelectedIndex;
            DateOnly? day = date.ReadValue();
            DateTimeOffset? at = null;
            if (k == DeadlineKind.DateTime)
            {
                if (day is null || !TimeOnly.TryParseExact(time.Text, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)) throw new RuleException("日付と時刻（HH:mm）を入力してください。");
                at = new DateTimeOffset(day.Value.ToDateTime(parsed), Japan.Offset);
            }
            draft.Title = title.Text; draft.NextAction = next.Text; draft.Completion = completion.Text; draft.ConfirmCompletion = required.IsChecked == true;
            draft.Deadline = new Deadline(k, k == DeadlineKind.Date ? day : null, at, k == DeadlineKind.Unknown ? "未確認" : basis.Text,
                basis.Text == task.Deadline.Evidence ? task.Deadline.SourceId : "");
            draft.ReviewOn = review.ReadValue(); draft.PlannedOn = planned.ReadValue();
            draft.Note = note.Text; draft.DoneSteps = steps.Text; draft.Material = material.Text;
            draft.EstimatedMinutes = string.IsNullOrWhiteSpace(estimate.Text) ? null : int.Parse(estimate.Text,System.Globalization.CultureInfo.InvariantCulture); draft.UrgentConfirmed = urgent.IsChecked == true;
            draft.Prerequisites = dependencies.SelectedItems.Cast<WorkItem>().Select(t => t.Id).ToList(); draft.ParentId = parent.SelectedValue as string; if (draft.ParentId == "") draft.ParentId = null;
            service.Edit(draft, task.Version);
        })));
        if (!task.Closed) panel.Children.Add(Button("この仕事を取りやめる", () => { if (MessageBox.Show(dialog, "仕事を取りやめ状態にします。記録は残り、履歴から戻せます。", "取りやめ", MessageBoxButton.YesNo) == MessageBoxResult.Yes) SaveDialog(dialog, () => service.Transition(task.Id, task.Version, WorkStatus.Cancelled)); }));
        if (!task.Closed) panel.Children.Add(Button("引継ぎ済みとして記録する",() => { if (Confirm("この仕事を引継ぎ済みとして記録します。外部への送信は行いません。引継ぎを確認しましたか？")) SaveDialog(dialog,() => service.Transition(task.Id,task.Version,WorkStatus.HandedOver,note.Text)); }));
        dialog.ShowDialog();
    }
    private StackPanel InboxSource(InboxItem mail)
    {
        var box = new StackPanel(); box.Children.Add(Body("原文：" + mail.Subject, true)); box.Children.Add(Body(mail.Reason));
        box.Children.Add(new TextBox { Text = mail.Body, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); return box;
    }
    private void SavePreferences(double proposedScale, bool proposedBeginner)
    {
        try
        {
            PreferencesStore.Save(preferencesFolder, new DisplayPreferences(proposedScale, proposedBeginner));
            scale = proposedScale; beginner = proposedBeginner;
            FontSize = 15*scale; Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "表示設定を保存できませんでした。アプリは引き続き使えます。空き容量・アクセス権を確認してください。（PREF-01）", "表示設定は未保存");
        }
    }
    private void DrawSettings()
    {
        Settings.Children.Clear(); Heading(Settings, "表示と保存の状態");
        DrawProfileSettings();
        DrawIntegrationSettings();
        Settings.Children.Add(Body("文字の大きさ")); var row = new WrapPanel();
        foreach (var s in new[] { 1d, 1.5, 2d }) row.Children.Add(Button($"{s*100:0}%", () => SavePreferences(s, beginner))); Settings.Children.Add(row);
        Settings.Children.Add(Button(beginner ? "通常表示にする" : "説明を多く表示する", () => SavePreferences(scale, !beginner)));
        Settings.Children.Add(Body("保存先：" + folder)); Settings.Children.Add(Body("文字の内容はWindows利用者単位で保護しています。データベース全体の暗号化ではありません。"));
        Settings.Children.Add(Body($"仕事 {state.Tasks.Count}件／受付 {state.Inbox.Count}件／履歴 {state.Events.Count}件／記録形式 {AppRelease.Schema}"));
        Settings.Children.Add(Body("バックアップは同じ端末内の退避です。端末故障や別利用者への移行対策ではありません。"));
        Settings.Children.Add(Button("整合バックアップを作成", () => Run(() => repository.Backup(Path.Combine(folder, "backups")), () => SaveStatus.Text = "バックアップ作成・整合性確認済み")));
        var backups = Directory.Exists(Path.Combine(folder, "backups")) ? Directory.GetFiles(Path.Combine(folder, "backups"), "backup-*.db",SearchOption.AllDirectories).OrderDescending().ToList() : [];
        var backup = new ComboBox { ItemsSource = backups.Select(Path.GetFileName).ToList(), SelectedIndex = backups.Count > 0 ? 0 : -1 }; Settings.Children.Add(backup);
        Settings.Children.Add(Button("選んだ退避から復元", () =>
        {
            if (integrationBusy) { MessageBox.Show(this,"外部処理中です。停止してから復元してください。"); return; }
            if (backup.SelectedIndex < 0) return;
            var source = backups[backup.SelectedIndex];
            if (MessageBox.Show(this, "選んだ退避以後の変更は復元先に含まれません。現在の記録も退避して保持します。実メールの自動処理は無効のままです。復元しますか？", "復元内容の確認", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            Run(() =>
            {
                repository.Backup(Path.Combine(folder, "backups"));
                var filename = "restored-" + Guid.NewGuid().ToString("N") + ".db";
                var destination = Path.Combine(folder, filename); SqliteRepository.RestoreToNew(source, destination, profileId);
                var nextRepo = new SqliteRepository(destination, profileId);
                try
                {
                    _ = nextRepo.Load();
                    var pointer = Path.Combine(folder, "active-data.txt"); File.WriteAllText(pointer + ".new", filename); File.Move(pointer + ".new", pointer, true);
                }
                catch { nextRepo.Dispose(); throw; }
                repository.Dispose(); repository = nextRepo; service = new TaskService(repository, clock);
            });
        }));
        Settings.Children.Add(Button("診断をローカル保存", () => Run(() => File.WriteAllText(Path.Combine(folder, "diagnostics.json"), JsonSerializer.Serialize(new
        { version = AppRelease.Version, schema = AppRelease.Schema, os = Environment.OSVersion.VersionString, runtime = Environment.Version.ToString(), outlookEnabled = state.Automation.Connector.Enabled, pendingExternal = state.Automation.Jobs.Count(j => j.State == JobState.OutcomeUnknown), revision = state.Revision })), () => SaveStatus.Text = "診断を保存しました。件名・本文・宛先・実パスは含めていません。")));
        Settings.Children.Add(Button("選んだ更新前退避で旧版0.4へ戻す準備",() =>
        {
            if (integrationBusy || backup.SelectedIndex < 0) return;
            var source = backups[backup.SelectedIndex];
            if (!Confirm("更新前（記録形式3）の退避を別ファイルへ検証復元し、この版を終了します。更新後の記録は退避して保持しますが、旧版では表示されません。終了後は保管してある旧版0.4を開いてください。この版を再び開くと新形式へ戻ります。実行しますか？")) return;
            integrationTimer.Stop();
            Run(() =>
            {
                service.SuspendAutomation(); repository.Backup(Path.Combine(folder,"backups"));
                var filename = "legacy-restored-" + Guid.NewGuid().ToString("N") + ".db";
                SqliteRepository.RestoreToNew(source,Path.Combine(folder,filename),profileId,forLegacyApplication:true);
                var pointer = Path.Combine(folder,"active-data.txt"); File.WriteAllText(pointer + ".new",filename); File.Move(pointer + ".new",pointer,true);
            },() => Dispatcher.BeginInvoke(Close));
        }));
        Settings.Children.Add(Body("別PC・別Windows利用者への保護された記録の移行は未対応です。表示設定だけは書き出せます。外部AI解析・無断更新は行いません。"));
    }
}
