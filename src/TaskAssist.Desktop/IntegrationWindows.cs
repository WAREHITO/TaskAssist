using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using TaskAssist.Core;

namespace TaskAssist.Desktop;

public partial class MainWindow
{
    private readonly OutlookClient outlookClient = new();
    private readonly DispatcherTimer integrationTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private CancellationTokenSource integrationCancellation = new();
    private bool integrationBusy;
    private DateTimeOffset nextScan;
    private void ResetIntegrationCancellation()
    {
        if (!integrationCancellation.IsCancellationRequested) return;
        integrationCancellation.Dispose(); integrationCancellation = new();
    }
    private void CancelIntegration()
    { integrationCancellation.Cancel(); if (!integrationBusy) ResetIntegrationCancellation(); }
    private void InitializeIntegration()
    {
        if (profiles is null) return;
        service.RecoverExternalJobs();
        integrationTimer.Tick += async (_, _) => await IntegrationTick();
        integrationTimer.Start();
        Closed += (_, _) => { integrationTimer.Stop(); integrationCancellation.Cancel(); };
    }
    private string ConnectorHealth(Snapshot snapshot)
    {
        var c = snapshot.Automation.Connector;
        return c.Health + "\n残りの項目：" + c.Cursors.Values.Sum(p => p.Remaining) + "（未開始のフォルダーは件数未確認）\n最終完全照合：" + (snapshot.LastScan?.ToOffset(Japan.Offset).ToString("M/d HH:mm") ?? "未実施") +
            $" ／対象 {c.Folders.Count}フォルダー\nアプリ・Outlookの停止中や未同期領域は確認できません。";
    }
    private async Task IntegrationTick(bool manual = false)
    {
        if (busy || integrationBusy || profiles is null || OwnedWindows.Count > 0) return;
        integrationBusy = true;
        var currentService = service; var generation = -1;
        try
        {
            var snapshot = currentService.Read();
            foreach (var r in snapshot.Automation.Recurrences.Where(r => r.Enabled))
            {
                if (AutomationPolicy.PendingCount(r,Japan.Day(clock.Now)) < 0) continue;
                var dates = AutomationPolicy.DueDates(r,Japan.Day(clock.Now));
                if (dates.Count > 0 && (dates.Count == 1 || r.CatchUp != CatchUpChoice.Ask)) currentService.GenerateOccurrences(r.Id);
            }
            await Task.Run(() => BackupManager.Daily(repository,currentService,folder,clock));
            var digest = currentService.QueueDigest();
            if (digest is not null) await ExecuteJobCore(digest);
            snapshot = currentService.Read(); var c = snapshot.Automation.Connector; generation = c.Generation;
            if (!c.Enabled) return;
            var target = c.Folders.FirstOrDefault(f => !c.Cursors.TryGetValue(f.Key,out var cursor) || cursor.CompletedAt is null);
            if (target is null)
            {
                if (!manual && clock.Now < nextScan) return;
                foreach (var f in c.Folders) currentService.BeginScan(f.Key,generation);
                target = c.Folders.First();
            }
            currentService.BeginScan(target.Key,generation);
            var cursor = currentService.Read().Automation.Connector.Cursors[target.Key];
            var reply = await outlookClient.CallAsync(new() { Command = "page", Folder = target, StartAt = c.StartAt, Cursor = cursor },integrationCancellation.Token);
            if (!reply.Success)
            {
                if (reply.Code == "changed") currentService.ResetScan(target.Key,generation);
                currentService.ConnectorFailure(generation,reply.Code); nextScan = clock.Now.AddMinutes(5);
                // Retry unavailable connections at the normal cadence, not every timer tick.
                integrationTimer.Interval = TimeSpan.FromMinutes(5); return;
            }
            currentService.SaveMailPage(target,generation,cursor.Offset,reply);
            integrationTimer.Interval = TimeSpan.FromSeconds(5);
            if (currentService.Read().Automation.Connector.Folders.All(f => currentService.Read().Automation.Connector.Cursors.GetValueOrDefault(f.Key)?.CompletedAt is not null)) nextScan = clock.Now.AddMinutes(5);
        }
        catch (Exception)
        {
            SaveStatus.Text = "自動処理を完了できませんでした。保存済み記録は保持しています。設定・実行記録を確認してください。（AUTO-01）";
            integrationTimer.Interval = TimeSpan.FromMinutes(5);
        }
        finally
        {
            integrationBusy = false;
            ResetIntegrationCancellation();
            try { state = currentService.Read(); UpdateConnectionDisplay(); UpdateTimeAttention(); if (manual) Refresh(); } catch { }
        }
    }
    private void DrawIntegrationSettings()
    {
        if (profiles is null) return;
        Heading(Settings,"Outlook・定型業務");
        Settings.Children.Add(Body(ConnectorHealth(state)));
        Settings.Children.Add(Button("Outlookの接続と読取り範囲",ConfigureOutlook));
        Settings.Children.Add(Button("今すぐ1ページ照合",async () => await IntegrationTick(true)));
        Settings.Children.Add(Button("メール読取りを停止",() =>
        { CancelIntegration(); Run(() => service.StopConnector()); }));
        Settings.Children.Add(Button("定型メールの規則",RuleWindow));
        Settings.Children.Add(Button("繰り返し業務",RecurrenceWindow));
        Settings.Children.Add(Button("業務日・利用可能時間",WorkdayWindow));
        Settings.Children.Add(Button("本人宛ての集約通知",DigestWindow));
        Settings.Children.Add(Button("外部処理の実行記録・結果確認",JobsWindow));
        Settings.Children.Add(Button("相談文を作る・保存する",ConsultationWindow));
        Settings.Children.Add(Button("自動処理をすべて停止（異動前）",() => { CancelIntegration(); Run(() => service.SuspendAutomation()); }));
        Settings.Children.Add(Button(state.Automation.NotificationsPaused ? "通知の休止を解除" : "通知だけ休止（受付は続ける）",() => Run(() => service.PauseNotifications(!state.Automation.NotificationsPaused))));
        Settings.Children.Add(Button("日次バックアップ・保持期間",RetentionWindow));
        Settings.Children.Add(Button("表示設定を書き出す",ExportPreferences));
        Settings.Children.Add(Button("表示設定を読み込む",ImportPreferences));
        Settings.Children.Add(Button("仕事の記録を書き出す",ExportRecords));
    }
    private TextBox Field(Panel panel,string label,string value = "",bool multiline = false)
    {
        panel.Children.Add(Body(label));
        var text = new TextBox { Text = value, MaxLength = 64000, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap, MinHeight = multiline ? 80 : 26, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        System.Windows.Automation.AutomationProperties.SetName(text,label); panel.Children.Add(text); return text;
    }
    private ComboBox Choice(Panel panel,string label,string[] options,int selected = 0)
    { panel.Children.Add(Body(label)); var box = new ComboBox { ItemsSource = options, SelectedIndex = selected }; System.Windows.Automation.AutomationProperties.SetName(box,label); panel.Children.Add(box); return box; }
    private bool Confirm(string message) => MessageBox.Show(this,message,"範囲と作用の確認",MessageBoxButton.YesNo,MessageBoxImage.Question) == MessageBoxResult.Yes;
    private async void ConfigureOutlook()
    {
        if (integrationBusy) { MessageBox.Show(this,"照合中です。停止してから接続設定を変更してください。"); return; }
        var dialog = Dialog("Outlook接続 — 対象を選ぶまで本文は読みません",out var panel);
        panel.Children.Add(Body("起動中のクラシックOutlookを使用します。パスワードをこのアプリへ入力する必要はありません。\n最初の操作ではアカウント名・メールアドレス・追加/共有領域・フォルダー名をこの画面に表示します。外部へ送信しません。共有領域も初期選択せず個別に確認します。"));
        var status = Body(""); var accounts = new ComboBox(); var folders = new StackPanel(); panel.Children.Add(status); panel.Children.Add(accounts);
        var selections = new List<(MailFolder folder,CheckBox check)>();
        var selectionGeneration = 0; var closed = false;
        using var lookupCancellation = new CancellationTokenSource();
        dialog.Closed += (_,_) => { closed = true; selectionGeneration++; lookupCancellation.Cancel(); };
        accounts.SelectionChanged += (_,_) => { selectionGeneration++; selections.Clear(); folders.Children.Clear(); };
        panel.Children.Add(Button("アカウント一覧の取得を許可して表示",async () =>
        {
            status.Text = "接続中…";
            var version = ++selectionGeneration;
            var reply = await outlookClient.CallAsync(new() { Command = "accounts" },lookupCancellation.Token);
            if (closed || version != selectionGeneration) return;
            status.Text = reply.Success ? "アカウントを選び、次にフォルダー一覧を表示してください。" : "接続できません。クラシックOutlookの起動・警告と補助プログラムを確認してください。";
            accounts.ItemsSource = reply.Accounts; accounts.SelectedIndex = -1;
        }));
        panel.Children.Add(Button("選んだアカウントのフォルダーを表示",async () =>
        {
            if (accounts.SelectedItem is not MailAccount account) return;
            var version = ++selectionGeneration; selections.Clear(); folders.Children.Clear();
            status.Text = "フォルダーを確認中…";
            var reply = await outlookClient.CallAsync(new() { Command = "folders", Account = account },lookupCancellation.Token);
            if (closed || version != selectionGeneration || accounts.SelectedItem is not MailAccount selectedAccount || selectedAccount != account) return;
            if (!reply.Success) { status.Text = "フォルダーを取得できません。接続状態を確認してください。"; return; }
            selections.Clear(); folders.Children.Clear();
            foreach (var f in reply.Folders) { var check = new CheckBox { Content = f.ToString(), Margin = new Thickness(3), IsChecked = false }; selections.Add((f,check)); folders.Children.Add(check); }
            status.Text = "選択したフォルダーだけが対象です。下位フォルダーも個別選択です。未選択の振分け先は未監視です。";
        }));
        panel.Children.Add(folders);
        var start = Field(panel,"取込み開始日時（日本時間 yyyy-MM-dd HH:mm）",clock.Now.ToOffset(Japan.Offset).ToString("yyyy-MM-dd HH:mm"));
        panel.Children.Add(Body("件名・差出人・受信日時・本文・添付の名前とサイズを、このWindows利用者の記録へ保存します。添付の内容はこの操作では保存しません。原本の既読・所在・分類は変更しません。"));
        panel.Children.Add(Button("選んだ範囲の読取りとローカル保存を有効にする",() =>
        {
            try
            {
                var selected = selections.Where(x => x.check.IsChecked == true).Select(x => x.folder).ToList();
                if (accounts.SelectedItem is not MailAccount approvedAccount || selected.Count == 0 || selected.Any(f => f.Account != approvedAccount.Address || f.StoreId != approvedAccount.StoreId)) throw new RuleException("現在のアカウントのフォルダーを選び直してください。");
                if (!DateTime.TryParseExact(start.Text,"yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)) throw new RuleException("開始日時を確認してください。");
                if (!Confirm($"対象：{approvedAccount}\n{selected.Count}フォルダー\n開始：{start.Text} 日本時間\n" + string.Join("\n",selected.Select(f => f.Name)) + "\nこの範囲の読取り・ローカル保存を有効にしますか？")) return;
                ResetIntegrationCancellation(); integrationTimer.Interval = TimeSpan.FromSeconds(5); nextScan = default;
                SaveDialog(dialog,() => service.ConfigureConnector(selected,new DateTimeOffset(date,Japan.Offset)));
            }
            catch (RuleException e) { status.Text = e.Message; }
        }));
        dialog.ShowDialog(); await Task.CompletedTask;
    }
    private void RuleWindow()
    {
        var dialog = Dialog("定型メールの承認規則",out var panel);
        panel.Children.Add(Body("差出人・件名先頭・対象フォルダーが一致した新着だけに作用します。一度の対応不要判断から規則は作りません。"));
        foreach (var old in state.Automation.Rules) panel.Children.Add(Button(old.Name + (old.Enabled ? " — 停止する" : " — 停止済み"),() => SaveDialog(dialog,() => service.DisableRule(old.Id))));
        var name = Field(panel,"規則名"); var sender = Field(panel,"差出人（完全一致）"); var prefix = Field(panel,"件名の先頭（必須）");
        var folderBox = new ComboBox { ItemsSource = state.Automation.Connector.Folders, SelectedIndex = -1 }; panel.Children.Add(Body("対象フォルダー")); panel.Children.Add(folderBox);
        var action = Choice(panel,"作用",["仕事にする","対応不要（受付記録は保持）"]);
        var template = Field(panel,"本文全体の定型（任意）。日付の位置だけ {date} にでき、yyyy-MM-dd を確定します。", "",true);
        var next = Field(panel,"次の一手","内容を確認する"); var completion = Field(panel,"完了条件","対応を終える");
        var effective = Field(panel,"有効開始日（yyyy-MM-dd）",Japan.Day(clock.Now).ToString("yyyy-MM-dd"));
        var urgent = new CheckBox { Content = "一致した仕事を緊急の調整候補にする（勝手に着手しない）" }; panel.Children.Add(urgent);
        panel.Children.Add(Button("影響件数を確認して規則を有効にする",() =>
        {
            if (folderBox.SelectedItem is not MailFolder f || !DateOnly.TryParseExact(effective.Text,"yyyy-MM-dd",out var day)) { MessageBox.Show(dialog,"対象と有効開始日を選んでください。"); return; }
            var rule = new MailRule { Name = name.Text, Sender = sender.Text, SubjectPrefix = prefix.Text, FolderKey = f.Key, ExactBodyTemplate = template.Text,
                Action = (MailRuleAction)action.SelectedIndex, NextAction = next.Text, Completion = completion.Text, Enabled = true, Urgent = urgent.IsChecked == true, EffectiveFrom = new(day.ToDateTime(TimeOnly.MinValue),Japan.Offset) };
            var count = service.PreviewRule(rule);
            if (Confirm($"保存済み受付の一致例：{count}件。採用後に新規取得した一致メールへ適用します。\n過去の判定や本人の訂正は変えません。規則を有効にしますか？")) SaveDialog(dialog,() => service.SaveRule(rule));
        })); dialog.ShowDialog();
    }
    private void RecurrenceWindow()
    {
        var dialog = Dialog("繰り返し業務",out var panel);
        panel.Children.Add(Body("月の指定日がない場合は月末に短縮します。休日は登録した業務日だけを使い、祝日を推測しません。"));
        foreach (var old in state.Automation.Recurrences)
        {
            var due = AutomationPolicy.PendingCount(old,Japan.Day(clock.Now));
            panel.Children.Add(Body($"{old.Title}／{(due < 0 ? "開始期間の要確認（停止後に新しい開始日で登録）" : $"未処理 {due}回")}／{(old.Enabled ? "有効" : "停止")}"));
            if (old.Enabled)
            {
                panel.Children.Add(Button("各回を生成：" + old.Title,() => SaveDialog(dialog,() => service.GenerateOccurrences(old.Id,CatchUpChoice.EachOccurrence))));
                panel.Children.Add(Button("最新のみ生成：" + old.Title,() => SaveDialog(dialog,() => service.GenerateOccurrences(old.Id,CatchUpChoice.LatestOnly))));
                panel.Children.Add(Button("停止：" + old.Title,() => SaveDialog(dialog,() => service.DisableRecurrence(old.Id))));
            }
        }
        var title = Field(panel,"用件"); var completion = Field(panel,"完了条件","対応を終える");
        var cadence = Choice(panel,"周期",["毎日","毎週（開始日の曜日）","毎月（開始日の日、なければ月末）"]);
        var start = Field(panel,"開始日 yyyy-MM-dd",Japan.Day(clock.Now).ToString("yyyy-MM-dd"));
        var catchup = Choice(panel,"停止中の発生分",["まとめて確認してから生成","各回が独立して必要","最新の1回のみ必要"]);
        var workdays = new CheckBox { Content = "設定した業務日以外は省略して記録する" }; panel.Children.Add(workdays);
        panel.Children.Add(Button("繰り返しを保存して有効にする",() =>
        {
            if (!DateOnly.TryParseExact(start.Text,"yyyy-MM-dd",out var day)) { MessageBox.Show(dialog,"開始日を確認してください。"); return; }
            SaveDialog(dialog,() => service.SaveRecurrence(new() { Title = title.Text, Completion = completion.Text, StartOn = day, MonthDay = day.Day,
                Cadence = (RecurrenceCadence)cadence.SelectedIndex, CatchUp = (CatchUpChoice)catchup.SelectedIndex, Enabled = true, SkipNonWorkdays = workdays.IsChecked == true }));
        })); dialog.ShowDialog();
    }
    private void WorkdayWindow()
    {
        var dialog = Dialog("業務日と利用可能時間",out var panel); var boxes = new List<(DayOfWeek day,CheckBox box)>();
        foreach (var day in Enum.GetValues<DayOfWeek>()) { var box = new CheckBox { Content = new[] { "日","月","火","水","木","金","土" }[(int)day], IsChecked = state.Automation.Workdays.Contains(day) }; boxes.Add((day,box)); panel.Children.Add(box); }
        var daysOff = Field(panel,"休日・休暇（yyyy-MM-dd、1行1日）",string.Join("\n",state.Automation.DaysOff.Select(d => d.ToString("yyyy-MM-dd"))),true);
        var capacity = Field(panel,"1日の利用可能時間（分）",state.Automation.CapacityMinutes.ToString());
        panel.Children.Add(Button("業務日を確認して保存",() => SaveDialog(dialog,() =>
        {
            var dates = daysOff.Text.Split(new[] {'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(t => DateOnly.ParseExact(t.Trim(),"yyyy-MM-dd",CultureInfo.InvariantCulture));
            service.ConfigureWorkdays(boxes.Where(b => b.box.IsChecked == true).Select(b => b.day),dates,int.Parse(capacity.Text,CultureInfo.InvariantCulture));
        }))); dialog.ShowDialog();
    }
    private void DigestWindow()
    {
        var dialog = Dialog("本人宛て集約通知",out var panel);
        panel.Children.Add(Body("本人の送信元と同じメールアドレス1件だけへ、件数の集約を送ります。原文・添付は転送しません。1日1回まで、仕事・受付の変化がある場合だけです。アプリ停止中の過去通知は再送しません。"));
        var account = Field(panel,"Outlookの本人送信元アドレス",state.Automation.DigestAccount);
        var recipient = Field(panel,"本人通知先（同じアドレスを確認）",state.Automation.DigestRecipient);
        var time = Field(panel,"通知時刻（日本時間 HH:mm）",state.Automation.DigestTime.ToString("HH:mm"));
        panel.Children.Add(Body(TaskService.DigestText(state,clock.Now)));
        panel.Children.Add(Button("この本人宛て通知を有効にする",() =>
        {
            if (!TimeOnly.TryParseExact(time.Text,"HH:mm",out var at)) { MessageBox.Show(dialog,"時刻を確認してください。"); return; }
            if (Confirm($"送信元：{account.Text}\n通知先：{recipient.Text}\n{time.Text}以降、変化時に1日1回まで自動送信します。有効にしますか？")) SaveDialog(dialog,() => service.ConfigureDigest(true,account.Text,recipient.Text,at));
        }));
        panel.Children.Add(Button("本人通知を無効にする",() => SaveDialog(dialog,() => service.ConfigureDigest(false,"","",new(16,0))))); dialog.ShowDialog();
    }
    private async Task ExecuteJobCore(string id)
    {
        service.StartJob(id); var snapshot = service.Read(); var job = snapshot.Automation.Jobs.Single(j => j.Id == id);
        var mail = snapshot.Inbox.SingleOrDefault(m => m.Id == job.SourceId);
        var reply = await outlookClient.CallAsync(new() { Command = "job", Job = job, Location = mail?.Locations.LastOrDefault() },integrationCancellation.Token);
        service.FinishJob(id,reply.Success ? JobState.ConfirmedSuccess : JobState.OutcomeUnknown,reply.Success ? reply.Code : "outcome_unknown",reply.ResultHash);
    }
    private async void ExecuteJob(string id)
    {
        if (integrationBusy) return; integrationBusy = true;
        try { await ExecuteJobCore(id); Refresh(); }
        catch { MessageBox.Show(this,"結果を確認できません。自動再実行しません。実行記録とOutlook・保存先を確認してください。"); }
        finally { integrationBusy = false; ResetIntegrationCancellation(); }
    }
    private void JobsWindow()
    {
        var dialog = Dialog("外部処理の実行記録",out var panel);
        panel.Children.Add(Body("送信処理受付は配送確認ではありません。結果未確認を確認済みにする前に、Outlookや保存先を確認してください。確認操作で再実行はしません。"));
        foreach (var job in state.Automation.Jobs.OrderByDescending(j => j.CreatedAt))
        {
            var label = job.Kind switch { JobKind.Attachment => "添付保存", JobKind.SelfDigest => "本人通知", _ => "下書き" };
            panel.Children.Add(Body($"{job.CreatedAt.ToOffset(Japan.Offset):MM/dd HH:mm} {label}：{JobLabel(job)}"));
            if (job.Kind == JobKind.Attachment) panel.Children.Add(Body(job.Destination));
            if (job.Kind == JobKind.Attachment && job.State == JobState.OutcomeUnknown)
                panel.Children.Add(Button("保存済みのファイルと記録を検証",() => SaveDialog(dialog,() => service.VerifySavedAttachment(job.Id))));
            if (job.State is JobState.OutcomeUnknown or JobState.Suspended)
            {
                panel.Children.Add(Button("結果を確認：完了していた",() => SaveDialog(dialog,() => service.ConfirmJobResult(job.Id,true))));
                panel.Children.Add(Button("結果を確認：未実行として終了",() => SaveDialog(dialog,() => service.ConfirmJobResult(job.Id,false))));
            }
        }
        dialog.ShowDialog();
    }
    private static string JobLabel(ExternalJob job) => job.State switch
    {
        JobState.ConfirmedSuccess when job.Kind == JobKind.SelfDigest => job.ResultCode == "user-confirmed" ? "本人が結果確認済み" : "Outlookへ送信処理受付（配送は未確認）",
        JobState.ConfirmedSuccess => "保存確認済み", JobState.Running => "実行中・結果未確定", JobState.OutcomeUnknown => "結果未確認・再実行停止",
        JobState.Suspended => "停止・本人確認待ち", JobState.Cancelled => "終了・再実行しない", JobState.Authorized => "実行待ち", JobState.ConfirmedFailure => "失敗確認済み", _ => "提案のみ"
    };
    private void ConsultationWindow()
    {
        var dialog = Dialog("優先順位・期限・分担の相談",out var panel);
        var initial = state.Automation.Consultation;
        if (initial.Length == 0) initial = "次の案件について、着手順・期限・分担を確認させてください。\n" + string.Join("\n",AutomationPolicy.Conflicts(state,clock.Now)) + "\n" + string.Join("\n",state.Tasks.Where(t => !t.Closed && (t.Deadline.Day == Japan.Day(clock.Now) || t.UrgentConfirmed)).Select(t => t.Title + "／" + t.StatusText + "／" + t.Deadline.Display(clock.Now)));
        var text = Field(panel,"相談文（ローカル下書き。作成だけでは競合解消になりません）",initial,true);
        panel.Children.Add(Button("ローカルに保存",() => SaveDialog(dialog,() => service.SaveConsultation(text.Text))));
        var account = Field(panel,"Outlookへ下書きを作る場合の本人送信元");
        panel.Children.Add(Button("宛先なしでOutlookの下書きに保存",() =>
        { if (!Confirm("Outlookにこの相談文の下書きを作成します。宛先は空で、自動送信しません。作成しますか？")) return; try { var id = service.ProposeDraft(account.Text,text.Text); dialog.Close(); ExecuteJob(id); } catch (RuleException e) { MessageBox.Show(dialog,e.Message); } }));
        dialog.ShowDialog();
    }
    private void RetentionWindow()
    {
        var dialog = Dialog("バックアップと保存原文の保持",out var panel);
        panel.Children.Add(Body("日次バックアップは同じPC内の退避です。設定を有効にすると、この機能が作った古い日次退避のみ、指定世代を超えた分を削除します。手動退避・更新前退避は保持します。"));
        var generations = Field(panel,"日次バックアップの保持世代（1〜30）",state.Automation.BackupGenerations.ToString());
        panel.Children.Add(Button("日次バックアップと古い世代の整理を有効にする",() => SaveDialog(dialog,() => service.ConfigureBackups(true,int.Parse(generations.Text,CultureInfo.InvariantCulture)))));
        panel.Children.Add(Button("日次バックアップを停止",() => SaveDialog(dialog,() => service.ConfigureBackups(false,state.Automation.BackupGenerations))));
        panel.Children.Add(Body("以下は保存済みメール原文の削除です。案件・担当回答は残します。古いバックアップには原文が残るため、職場の取扱いに従って別途保持を管理してください。"));
        var cutoff = Field(panel,"この日より前のメール原文を削除（yyyy-MM-dd）");
        panel.Children.Add(Button("削除対象をプレビュー",() =>
        {
            if (!DateOnly.TryParseExact(cutoff.Text,"yyyy-MM-dd",out var day)) { MessageBox.Show(dialog,"日付を確認してください。"); return; }
            var at = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue),Japan.Offset);
            var count = state.Inbox.Count(m => m.ReceivedAt < at && !m.Purged);
            if (Confirm($"{count}件の保存原文・差出人・添付一覧を削除します。最小の処理済み識別子を保持し、取込み開始日も更新して復活を防ぎます。元メールは削除しません。実行しますか？")) SaveDialog(dialog,() => service.PurgeMailBefore(at));
        })); dialog.ShowDialog();
    }
    private void ExportPreferences()
    {
        var picker = new SaveFileDialog { Filter = "表示設定 (*.json)|*.json", FileName = "仕事アシスト-表示設定.json" };
        if (picker.ShowDialog(this) == true) Run(() => File.WriteAllText(picker.FileName,JsonSerializer.Serialize(new DisplayPreferences(scale,beginner))));
    }
    private void ImportPreferences()
    {
        var picker = new OpenFileDialog { Filter = "表示設定 (*.json)|*.json" };
        if (picker.ShowDialog(this) != true) return;
        try { if (new FileInfo(picker.FileName).Length > 4096) throw new RuleException("設定ファイルが大きすぎます。"); var p = JsonSerializer.Deserialize<DisplayPreferences>(File.ReadAllText(picker.FileName)) ?? throw new RuleException("表示設定を読めません。"); SavePreferences(p.Scale,p.Beginner); }
        catch { MessageBox.Show(this,"表示設定を取り込めませんでした。現在の設定は保持しています。"); }
    }
    private void ExportRecords()
    {
        if (!Confirm("仕事・回答・受付の記録を平文JSONとして選んだ場所へ書き出します。メール本文を含みます。保管先を確認して使ってください。書き出しますか？")) return;
        var picker = new SaveFileDialog { Filter = "記録 (*.json)|*.json", FileName = "仕事アシスト-記録.json" };
        if (picker.ShowDialog(this) == true) Run(() => File.WriteAllText(picker.FileName,JsonSerializer.Serialize(service.Read(),new JsonSerializerOptions { WriteIndented = true })));
    }
    private void AddMailActions(StackPanel panel,InboxItem mail)
    {
        if (mail.DeadlineCandidate.Length > 0) panel.Children.Add(Body(mail.DeadlineCandidate));
        if (!mail.IsOutlook || mail.Purged) return;
        foreach (var hint in mail.DeadlineHints)
        {
            panel.Children.Add(Body($"未確定：{hint.Text} → {(hint.Day?.ToString("yyyy/MM/dd") ?? "具体日付なし")}{(hint.EndDay is {} end ? "〜" + end.ToString("yyyy/MM/dd") : "")}\n受信日時を日本時間で解釈。{hint.Warning}\n原文抜粋：{hint.Excerpt}"));
            if (hint.Day is not null && hint.EndDay is null) panel.Children.Add(Button("原文を確認してこの日を締切にする",() =>
            { if (Confirm($"原文：{hint.Excerpt}\n{hint.Day:yyyy/MM/dd}をこの仕事の本当の締切として本人確認します。引用や否定、別作業の期限でないことを確認しましたか？")) Run(() => service.ConfirmDeadlineHint(mail.Id,hint.Id)); }));
        }
        var relatedIds = state.Inbox.Where(other => other.Id != mail.Id && other.Subject.Length > 0 && other.Subject == mail.Subject && other.TaskId is not null).Select(other => other.TaskId).ToHashSet();
        foreach (var related in state.Tasks.Where(t => relatedIds.Contains(t.Id)).Take(5))
            panel.Children.Add(Button("同じ件名の関連候補：" + related.Title,() => { if (Confirm("件名だけでは同じ依頼か判断できません。この仕事へ原文を関連付けますか？期限や状態は変えません。")) Run(() => service.LinkSource(mail.Id,related.Id)); }));
        panel.Children.Add(Button("別の依頼として追加",() =>
        { var dialog = Dialog("同じメールの別依頼",out var p); var title = Field(p,"用件",mail.Subject); p.Children.Add(Button("登録",() => SaveDialog(dialog,() => service.AddAnotherTask(mail.Id,title.Text)))); dialog.ShowDialog(); }));
        panel.Children.Add(Button("既存の仕事に関連付ける",() =>
        { var dialog = Dialog("関連する仕事を選択",out var p); var choices = state.Tasks.ToList(); var list = new ComboBox { ItemsSource = choices.Select(t => t.Title).ToList(), SelectedIndex = -1 }; p.Children.Add(list); p.Children.Add(Button("関連を保存（締切・状態は変えない）",() => { if (list.SelectedIndex >= 0) SaveDialog(dialog,() => service.LinkSource(mail.Id,choices[list.SelectedIndex].Id)); })); dialog.ShowDialog(); }));
        foreach (var attachment in mail.Attachments)
            panel.Children.Add(Button("添付を保存：" + attachment.Name,() =>
            {
                if (integrationBusy) return;
                var picker = new OpenFolderDialog { Title = "この添付の保存先を選ぶ" };
                if (picker.ShowDialog(this) != true || !Confirm($"添付：{attachment.Name}\n保存先：{picker.FolderName}\n自動実行・展開はしません。保存しますか？")) return;
                try { ExecuteJob(service.ProposeAttachment(mail.Id,attachment.Index,picker.FolderName)); } catch (RuleException e) { MessageBox.Show(this,e.Message); }
            }));
        if (mail.Locations.Count > 0) panel.Children.Add(Button("Outlookで原本を開く",async () =>
        {
            if (!Confirm("Outlookの画面で原本を開きます。Outlook側の設定により既読になる場合があります。開きますか？")) return;
            var reply = await outlookClient.CallAsync(new() { Command = "open", Location = mail.Locations.Last() });
            if (!reply.Success) MessageBox.Show(this,"原本を開けません。移動・削除・未同期の可能性があります。保存した仕事は保持しています。");
        }));
    }
}
