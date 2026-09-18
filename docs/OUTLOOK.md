# Outlookとの接続

起動中の**クラシックOutlook for Windows**を使用する。パスワードやMicrosoft GraphのアプリIDを仕事アシストへ入力する方式ではない。新しいOutlookには対応しない。職場でプログラムからのアクセスが制限されている場合、安全設定を変更して回避しない。

1. クラシックOutlookを通常どおり開き、同期が終わるのを待つ。
2. 仕事アシストの「設定・診断」→「Outlookの接続と読取り範囲」を開く。
3. 説明を読み「アカウント一覧の取得を許可して表示」を押す。この時点では本文を取得しない。
4. 対象アカウントを選び、フォルダー一覧を表示する。必要なフォルダーを個別に選ぶ。下位フォルダーや振分け先は自動で含まれない。
5. 取込み開始日時を日本時間で指定する。初期値は現在時刻。過去全期間を無断で取り込まない。
6. 最終確認のアカウント・フォルダー・開始時点を確認して有効化する。件名・差出人・受信日時・本文・添付の名前/サイズをローカル保存する。
7. 「受付」の候補を仕事にする、対応不要、後で確認から選ぶ。期限候補は引用・否定・年・別作業の期限でないことを確認して採用する。

通常の照合は原本の移動・削除・既読化・分類変更をしない。「Outlookで原本を開く」は本人操作であり、Outlookの表示設定により既読化される場合がある。HTMLや外部画像はアプリで読み込まない。

本文取得不能や未同期は画面に残る。1回のページが終わっても全取得成功とは限らない。残件、取得不能、最終完全照合を確認する。アプリ・Outlook停止、スリープ、ログオフ中は新着確認できない。再開後に照合する。取得前に完全削除された原本の復旧は保証しない。

## 添付・下書き・本人通知

- 添付は受付の「添付を保存」から保存先を指定して確認する。実行・展開しない。原本更新を検出したら保存を停止する。
- 「相談文を作る」でローカル下書きを保存できる。Outlookへ作る場合は送信元を入力し、別の確認を経て宛先なしの下書きを保存する。
- 「本人宛ての集約通知」で送信元と同じ本人アドレス1件・時刻・プレビューを確認して有効化する。初期値は無効。1日1回まで、仕事/受付が変わったときに件数を通知する。配送完了とは表示しない。
- タイムアウトや中断は「結果未確認」。自動再送しない。「外部処理の実行記録」でOutlookや保存先と照合して本人が結果を記録する。

## 接続できないとき

クラシックOutlookが起動しているか、Outlook側に警告があるか、実行フォルダー内に `outlook-worker` があるかを確認する。許可警告は職場の規則に従って本人が判断する。アプリは警告を無断承認しない。手動登録と既存記録の閲覧は接続なしで利用できる。

## 根拠・実機確認

APIはMicrosoft公式の [Items.Sort](https://learn.microsoft.com/en-us/office/vba/api/outlook.items.sort)、[GetStoreFromID](https://learn.microsoft.com/en-us/office/vba/api/outlook.namespace.getstorefromid)、[LastModificationTime](https://learn.microsoft.com/en-us/office/vba/api/outlook.mailitem.lastmodificationtime)、[SendUsingAccount](https://learn.microsoft.com/en-us/office/vba/api/outlook.mailitem.sendusingaccount)を参照した。保護されたプロパティへのアクセスには [Object Model Guardの警告](https://learn.microsoft.com/en-us/office/vba/outlook/how-to/security/protected-properties-and-methods)があり得る。

開発PCでは補助プロセスからOutlookのバージョン `16.0.0.20228` の取得に成功した。これはメールの取得・原本不変・送信の試験ではない。実メール・アカウント一覧・フォルダー一覧・送信は開発担当による実機試験未実施。
