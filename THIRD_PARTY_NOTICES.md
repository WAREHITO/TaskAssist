# 第三者ライブラリ

以下は、このソースが参照するNuGetパッケージです。版は `packages.lock.json`、ライセンス表記は取得済みパッケージの `.nuspec` に記載された式で確認しています。

| パッケージ | 版 | パッケージのライセンス表記 |
|---|---|---|
| [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.12) | 10.0.12 | MIT |
| [Microsoft.Data.Sqlite.Core](https://www.nuget.org/packages/Microsoft.Data.Sqlite.Core/10.0.12) | 10.0.12 | MIT |
| [System.Security.Cryptography.ProtectedData](https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData/10.0.12) | 10.0.12 | MIT |
| [SQLitePCLRaw.bundle_e_sqlite3](https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3/2.1.12) | 2.1.12 | Apache-2.0 |
| [SQLitePCLRaw.core](https://www.nuget.org/packages/SQLitePCLRaw.core/2.1.12) | 2.1.12 | Apache-2.0 |
| [SQLitePCLRaw.lib.e_sqlite3](https://www.nuget.org/packages/SQLitePCLRaw.lib.e_sqlite3/2.1.12) | 2.1.12 | Apache-2.0 |
| [SQLitePCLRaw.provider.e_sqlite3](https://www.nuget.org/packages/SQLitePCLRaw.provider.e_sqlite3/2.1.12) | 2.1.12 | Apache-2.0 |

NuGetパッケージやそのバイナリ自体は、このソースZIPに含めません。復元時に取得します。パッケージのライセンスは、このプロジェクト独自のコード全体へライセンスを付けるものではありません。

上流: [Microsoft .NET](https://github.com/dotnet/dotnet)、[SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw)。SQLitePCLRawのパッケージの表記と、その中で使う [SQLite本体の扱い](https://www.sqlite.org/copyright.html) は区別します。

自己完結形式の実行ファイルには.NETとWindows Desktopランタイム10.0.12、ネイティブSQLiteなどのバイナリも含まれます。`tools/package-runtime.ps1` は原文のLICENSE・NOTICE・THIRD-PARTY-NOTICESを `licenses` へ同梱します。[取得元](licenses/README.md)を参照してください。この一覧だけで配布時の表示を代替しません。

CIで参照するGitHub Actionsはリポジトリ内に複製せず、ワークフローから参照します。[checkout](https://github.com/actions/checkout) と [setup-dotnet](https://github.com/actions/setup-dotnet) のライセンス・通知は各公式リポジトリを参照してください。
