using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using TaskAssist.Core;

namespace TaskAssist.Desktop;

public static class LocalSession
{
    public static void Start(Application app, ProfileStore profiles)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            if (profiles.Load().Profiles.Count == 0 && !CreateFirst(profiles)) { app.Shutdown(); return; }
            Open();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex is RuleException ? ex.Message : "保存記録を開けませんでした。元の記録を保持して停止します。空き容量・アクセス権・Windows利用者を確認してください。（START-01）", "起動できません");
            app.Shutdown(3);
        }
        void Open()
        {
            var window = new MainWindow(profiles.Folder(profiles.Load().ActiveId), profiles);
            var switching = false;
            window.Closed += (_, _) => { if (!switching) app.Shutdown(); };
            window.ProfileChanged += () =>
            {
                switching = true; window.Close();
                try { Open(); }
                catch { MessageBox.Show("新しい所属は保存済みですが画面を開けませんでした。アプリを開き直してください。旧記録は保持されています。（PROFILE-OPEN）"); app.Shutdown(3); }
            };
            app.MainWindow = window; window.Show();
        }
    }
    private static bool CreateFirst(ProfileStore profiles)
    {
        var panel = new StackPanel { Margin = new(26) };
        var window = new Window { Title = "仕事アシスト — はじめの所属設定", Width = 680, Height = 460, MinWidth = 440, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        panel.Children.Add(new TextBlock { Text = "あなたの仕事の記録を始めましょう", FontSize = 25, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "所属の表示名（局・課・担当など、分かる呼び方でOK）" });
        var name = new TextBox { MaxLength = 120 }; AutomationProperties.SetName(name, "所属の表示名"); panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "空の記録で始まります。架空データは入りません。\n異動するときは「設定・診断」から新しい所属を作れます。旧所属の記録は閲覧用に残ります。\n\nこの版は手動登録・カレンダー・回答管理を使うローカル版です。実メールの読取り・送信は未接続です。" });
        var error = new TextBlock(); panel.Children.Add(error);
        var button = new Button { Content = "この所属で始める" }; panel.Children.Add(button);
        button.Click += (_, _) =>
        {
            try { profiles.CreateFirst(name.Text); window.DialogResult = true; }
            catch (Exception ex) { error.Text = ex is RuleException ? ex.Message : "準備を保存できませんでした。入力を保持しています。空き容量・アクセス権を確認してください。"; }
        };
        return window.ShowDialog() == true;
    }
}

public partial class MainWindow
{
    private void DrawProfileSettings()
    {
        if (profiles is null) return;
        var catalog = profiles.Load();
        Heading(Settings, "所属と担当"); Settings.Children.Add(Body("現在：" + catalog.Active.Name, true));
        Settings.Children.Add(Button("所属の表示名を直す", () =>
        {
            var dialog = Dialog("所属名の訂正（同じ業務記録を使います）", out var panel);
            panel.Children.Add(Body("名称変更・誤字の訂正はこちら。異動して記録を分ける場合は「異動・新しい所属」を使ってください。"));
            var name = new TextBox { Text = catalog.Active.Name, MaxLength = 120 }; panel.Children.Add(name);
            panel.Children.Add(Button("表示名を保存", () => SaveDialog(dialog, () => profiles.RenameActive(name.Text)))); dialog.ShowDialog();
        }));
        Settings.Children.Add(Button("次回選ぶ担当の一覧を編集", () =>
        {
            var dialog = Dialog("担当の一覧", out var panel);
            panel.Children.Add(Body("1行に1担当。追加・名称変更・一覧からの除外ができます。登録済みの案件の担当名や回答は変わりません。"));
            var names = new TextBox { Text = string.Join(Environment.NewLine, state.TeamRoster), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160 };
            AutomationProperties.SetName(names, "次回選ぶ担当一覧"); panel.Children.Add(names);
            panel.Children.Add(Button("担当一覧を保存", () => SaveDialog(dialog, () => service.SetTeamRoster(names.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))))); dialog.ShowDialog();
        }));
        Settings.Children.Add(Button("異動・新しい所属を始める", TransferProfile));
        Settings.Children.Add(Body("異動先は空の記録で開始。未完了の案件は扱いを記録し、旧所属の記録を閲覧用に残します。文字サイズは引き継ぎます。"));
        foreach (var old in catalog.Profiles.Where(p => p.ArchivedAt is not null).OrderByDescending(p => p.ArchivedAt))
            Settings.Children.Add(Button("旧所属の記録を見る：" + old.Name, () =>
            {
                try
                {
                    using var repository = profiles.Open(old.Id);
                    new ArchiveWindow(old, repository.Load()) { Owner = this, FontSize = FontSize }.ShowDialog();
                }
                catch { MessageBox.Show(this, "旧所属の記録を開けませんでした。元の記録は保持しています。（ARCHIVE-01）", "読込みを停止"); }
            }));
    }
    private void TransferProfile()
    {
        if (profiles is null) return;
        if (!string.IsNullOrWhiteSpace(QuickTitle.Text)) { MessageBox.Show(this, "入力中の用件を登録するか、入力欄を空にしてから異動してください。"); return; }
        var snapshot = service.Read();
        var dialog = Dialog("異動の確認・未完了案件の扱い", out var panel);
        panel.Children.Add(Body("旧所属は閲覧専用になります。案件・担当一覧は自動で移しません。続ける業務は、新しい所属で必要な範囲を登録してください。", true));
        panel.Children.Add(Body("新しい所属の表示名")); var name = new TextBox { MaxLength = 120 }; AutomationProperties.SetName(name, "新しい所属の表示名"); panel.Children.Add(name);
        var entries = new List<(WorkItem Task, ComboBox Choice, TextBox Note)>();
        foreach (var task in snapshot.Tasks.Where(t => !t.Closed))
        {
            panel.Children.Add(Body(task.Title + " ／ " + task.StatusText, true));
            panel.Children.Add(Body(task.Deadline.Display(clock.Now)));
            var choice = new ComboBox { ItemsSource = new[] { "継続対応（必要な分を別途登録）", "引継ぎ", "取りやめ" }, SelectedIndex = -1 };
            AutomationProperties.SetName(choice, "異動後の扱い：" + task.Title); panel.Children.Add(choice);
            panel.Children.Add(Body("引継ぎ先・理由・次の対応（必須）")); var note = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 65, MaxLength = 2000 }; panel.Children.Add(note);
            entries.Add((task, choice, note));
        }
        if (entries.Count == 0) panel.Children.Add(Body("未完了の案件はありません。"));
        panel.Children.Add(Body("確定前に旧記録を退避します。新しい所属の画面へ切り替わります。メール送信・データ削除は行いません。"));
        var error = Body(""); panel.Children.Add(error); var committed = false;
        panel.Children.Add(Button("確認して新しい所属を開始", () =>
        {
            try
            {
                profiles.Transfer(profileId, snapshot.Revision, name.Text, entries.Select(e => new HandoverDecision(e.Task.Id, e.Task.Version, (HandoverChoice)e.Choice.SelectedIndex, e.Note.Text.Trim())));
                committed = true; dialog.Close();
            }
            catch (Exception ex) { error.Text = ex is RuleException ? ex.Message : "異動を確定できませんでした。旧所属のまま保持しています。空き容量・アクセス権を確認してください。（TRANSFER-01）"; }
        }));
        dialog.ShowDialog();
        if (committed) ProfileChanged?.Invoke();
    }
}

public sealed class ArchiveWindow : Window
{
    public ArchiveWindow(WorkProfile profile, Snapshot snapshot)
    {
        Title = "旧所属の記録（閲覧専用）— " + profile.Name; Width = 1080; Height = 800; MinWidth = 650; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new(20) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = profile.Name + " — 閲覧専用", FontSize = 24, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = "旧所属の案件・回答・履歴を保持しています。ここから作業を再開したり、現在の所属へ復元したりはできません。" });
        var search = new TextBox(); AutomationProperties.SetName(search, "旧所属の検索語"); top.Children.Add(search);
        var list = new ListBox { Width = 260, DisplayMemberPath = "Title" }; DockPanel.SetDock(list, Dock.Left); root.Children.Add(list);
        var detail = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(12,0,0,0) };
        AutomationProperties.SetName(detail, "旧所属の案件・回答・履歴"); root.Children.Add(detail);
        void Search() { list.ItemsSource = snapshot.Tasks.Where(t => (t.Title + t.Note + t.Material + string.Join(" ", t.Responses.Select(r => r.TeamName + r.AnswerText + r.Note))).Contains(search.Text, StringComparison.CurrentCultureIgnoreCase)).ToList(); if (list.Items.Count > 0) list.SelectedIndex = 0; else detail.Text = "該当する案件はありません。"; }
        search.TextChanged += (_, _) => Search();
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not WorkItem task) return;
            var decision = profile.Decisions.SingleOrDefault(d => d.TaskId == task.Id);
            var lines = new List<string> { Format(task), "", "異動時の判断", decision is null ? "異動時点で終了済み" : new[] { "継続対応（別途登録）", "引継ぎ", "取りやめ" }[(int)decision.Choice] + "：" + decision.Note, "", "変更履歴（古い順）" };
            foreach (var ev in snapshot.Events.Where(e => e.After.ContainsKey(task.Id)).OrderBy(e => e.At))
            {
                lines.Add($"\n{ev.At.ToOffset(Japan.Offset):yyyy/M/d HH:mm:ss} {ev.Label}" + (ev.Undone ? "（取消済み）" : ""));
                if (ev.Before.TryGetValue(task.Id, out var before)) lines.Add("変更前\n" + Format(before));
                lines.Add("変更後\n" + Format(ev.After[task.Id]));
            }
            detail.Text = string.Join(Environment.NewLine, lines); detail.ScrollToHome();
        };
        Search();
        string Format(WorkItem task) => string.Join(Environment.NewLine, new[] {
            task.Title, "状態：" + task.StatusText, "受付：" + (task.ReceivedOn?.ToString("yyyy/M/d") ?? "未確認"),
            "締切：" + task.Deadline.Display(DateTimeOffset.UtcNow), "期限の根拠：" + task.Deadline.Evidence,
            "次に見る日：" + task.ReviewOn, "作業候補の日：" + task.PlannedOn, "次の一手：" + task.NextAction,
            "完了条件：" + task.Completion, "完了時の確認：" + (task.ConfirmCompletion ? "必要" : "不要"), "再開メモ：" + task.Note,
            "完了済みの手順：" + task.DoneSteps, "資料：" + task.Material,
            "親の仕事：" + snapshot.Tasks.SingleOrDefault(t => t.Id == task.ParentId)?.Title,
            "前提作業：" + string.Join("、", snapshot.Tasks.Where(t => task.Prerequisites.Contains(t.Id)).Select(t => t.Title)),
            "登録：" + task.CreatedAt.ToOffset(Japan.Offset).ToString("yyyy/M/d HH:mm"), "完了：" + task.CompletedAt?.ToOffset(Japan.Offset).ToString("yyyy/M/d HH:mm"),
            "回答記録：", string.Join("\n\n", task.Responses.Select(r => $"{r.TeamName}：{r.StageText}\n依頼 {r.RequestedOn}／回答 {r.AnsweredOn}／確認 {r.ReviewedOn}\n{ResponsePolicy.AnswerLabel(r.Answer)}\n{r.AnswerText}\n補足：{r.Note}")) });
    }
}
