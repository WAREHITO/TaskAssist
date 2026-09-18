# ビルドと試験

## 必要なもの

- Windows 11 x64。WPFとDPAPIを使うため、この手順はWindows用。
- .NET SDK **10.0.401**。`global.json` で固定し、別版への自動切替を無効にしている。
- PowerShell。GitはソースのZIPからビルドするだけなら不要。

[Microsoftの.NETダウンロード](https://dotnet.microsoft.com/download/dotnet/10.0)からSDKを用意する。導入済みの環境では追加インストールは不要。端末の管理規則と実行ポリシーを変更しない。

## 通常の確認

リポジトリ直下で実行する。

```powershell
./tools/build.ps1
```

`dotnet` がPATHにない場合:

```powershell
# 実際に自分で配置したSDKの場所を指定する例
./tools/build.ps1 -DotnetPath 'D:/Tools/dotnet/dotnet.exe'
```

スクリプトはSDK版を確認し、固定済みの依存関係を `--locked-mode` で復元して、Desktop・OutlookWorker・自動試験・Windows画面試験用ホストをReleaseビルドする。続けて自動試験の実行ファイルを実行する。どれかが失敗したら停止する。

**自動試験は独自の実行プログラム。`dotnet test` ではこの試験群は実行されない。** 結果は `.artifacts/tests/<実行識別子>/automated-tests.json` に出る。ビルドログと結果JSONは個人の環境情報を含み得るので、そのまま公開しない。

## 実行ファイルを作る

```powershell
./tools/build.ps1 -Publish
```

試験に成功した後、Windows x64用の自己完結形式を `.artifacts/publish/` の新しいフォルダーに作る。OutlookWorkerも `outlook-worker` サブフォルダーへ発行する。SDKを追加指定する場合は `-DotnetPath` と併用できる。`TaskAssist.exe` だけを抜き出さず、出力フォルダー全体を保持する。

その出力先を渡すと、説明書・第三者ライセンス・ファイルハッシュを含む配布ZIPを作る。

```powershell
./tools/package-runtime.ps1 -PublishDirectory '<今回表示された発行先>'
```

`.artifacts/releases/` の新しいフォルダーへ保存する。PDB・記録DB・試験データ・リンクを含む出力は拒否する。作成物をそのまま自動公開する処理はない。

製品の起動は通常の保存先 `TaskAssist/Live` を使用する。既存の記録がある環境ではその記録を開く。ソースのビルドと自動試験は製品を自動起動しない。

## 架空データによる画面確認

ビルド成功後、以下を実行すると独立した試験用フォルダーを使う画面が開く。`dotnet` は上記と同じSDKを使う。

```powershell
dotnet run --project tests/TaskAssist.WindowsTests -c Release --no-build --no-restore -- --profiles
dotnet run --project tests/TaskAssist.WindowsTests -c Release --no-build --no-restore -- --profile-review
dotnet run --project tests/TaskAssist.WindowsTests -c Release --no-build --no-restore -- --calendar --overview
```

順に、空の所属設定、架空の異動前後の記録、架空のカレンダーを確認する。各画面を閉じてから次を起動する。実データを入力しない。このホストのビルド成功だけを、画面操作の合格とは扱わない。

## 保存領域

Windowsの `LocalApplicationData` 配下に用途別のフォルダーを使う。

| フォルダー | 用途 |
|---|---|
| `TaskAssist/Live` | 通常のアプリの所属・案件・設定 |
| `TaskAssist/Tests/<識別子>` | 自動試験の架空記録 |
| `TaskAssist/WindowTests/<識別子>` | 手動画面試験の架空記録 |

これらのフォルダーをソースにコピーしない。ビルドスクリプトは既存の記録を削除しない。

## 依存関係の更新

`Directory.Build.props` の `RuntimeIdentifiers` と各 `packages.lock.json` はWindows x64での発行も含めて整合させる。通常ビルド中にロックファイルを更新しない。更新が必要な場合だけ、パッケージ・SDKの公式情報を確認し、明示的に復元し直して差分をレビューする。

## GitHub Actions

`.github/workflows/ci.yml` はWindowsランナーで同じビルド・試験・ソースZIP作成を行う。ソースのpushとpull request、手動実行を対象にする。リリース作成、実行ファイルのアップロード、アプリデータの送信は行わない。

`actions/checkout` と `actions/setup-dotnet` は確認時のコミットに固定している。ローカルでの検証とGitHub上での実行結果は別。過去版の成功を最新変更の成功とは扱わず、該当コミットの結果を確認する。

公式資料: [checkout](https://github.com/actions/checkout)、[setup-dotnet](https://github.com/actions/setup-dotnet)。
