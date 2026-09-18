# アプリアイコン

濃紺の角丸タイルに、水色のチェックマークとタスクカードを重ねたデザイン。
2026-09-19、Codex組込みの画像生成ツール（built-in image_gen）で新規生成した。CLI/APIモードは使用していない。

- 原画: `src/TaskAssist.Desktop/Assets/TaskAssist.png`
- Windows用: `src/TaskAssist.Desktop/Assets/TaskAssist.ico`
- ICOは16・24・32・48・64・128・256 pxの7サイズ。透過を保って縮小・形式変換している。
- EXEへの組込みは `ApplicationIcon`、ウィンドウ表示は共有スタイルの `Window.Icon` を使う。

原画からICOを作り直す場合、Windows上で `./tools/create-icon.ps1` を実行する。
通常のビルドは保存済みICOを使用するため、画像生成サービスへの接続は不要。

Microsoft公式資料: [アセンブリとウィンドウのアイコン](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.icon?view=windowsdesktop-10.0)。

## 使用した生成プロンプト

```text
Use case: logo-brand. Asset type: a finished Windows desktop app icon for a personal task management app called TaskAssist (仕事アシスト). Create ONE square, centered, polished app icon with a genuinely transparent background outside its silhouette. Design: a deep midnight navy rounded-square tile with subtle premium beveled depth, a large bold luminous ice-cyan check mark integrated with two neatly stacked task cards. Very restrained teal-to-ice-blue gradient highlights, crisp geometric silhouette, confident and calm, sophisticated productivity software. The mark should fill most of the tile and remain unmistakable at 32x32 and 16x16. Minimal detail, generous thick strokes, no tiny checklist lines, no glow outside the silhouette. Straight-on orthographic view, not a tilted mockup. Tile occupies about 90% of square canvas, transparent outer margin. No text, no letters, no numbers, no watermark, no desktop screenshot, no multiple variants. Deliver a 1024x1024 RGBA image with clean antialiased edges.
```

生成された原画の実寸は1254×1254 px。要求寸法と区別して記録する。
