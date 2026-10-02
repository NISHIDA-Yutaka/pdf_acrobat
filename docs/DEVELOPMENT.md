# 開発メモ

## 準備

- .NET 10 SDK（`global.json` で 10.0.401 以上を指定）
- Visual Studio 2026、または VS Code + C# Dev Kit、または `dotnet` CLI

## よく使うコマンド

ビルド:

```bash
dotnet build
```

単体テスト:

```bash
dotnet test
```

アプリの起動:

```bash
dotnet run --project src/PdfAcrobat.App
```

サンプル PDF の再生成（`--large` で 1,200 ページの large.pdf も作る）:

```bash
dotnet run --project tools/PdfAcrobat.SampleGen -- samples --large
```

配布用の単一フォルダー発行（自己完結型）:

```bash
dotnet publish src/PdfAcrobat.App -c Release -r win-x64 --self-contained -o publish
```

## UI の自動確認スクリプト

アプリは次のオプションで、スクリプトの操作を実行してから終了する。

```bash
src/PdfAcrobat.App/bin/Debug/net10.0-windows/PdfAcrobat.exe --script tests/ui/phase1-basic.txt --settings-folder artifacts/settings --offscreen
```

- `--offscreen`: ウィンドウを画面外に置く（作業中の画面を邪魔しない）
- `--settings-folder`: 設定ファイルの保存先を変える（普段の設定を汚さない）
- 実行ログはスクリプトと同じ場所に `.log` として出力される
- スクリプト中のパスはスクリプトのあるフォルダーからの相対パス
- 実行中はメッセージボックスを表示せず、ログに記録する（確認は「いいえ」扱い）

| コマンド | 説明 |
|---|---|
| `size W H` | ウィンドウサイズ |
| `theme light\|dark` | テーマ |
| `open PATH [PASSWORD]` | ファイルを開く |
| `home` / `tab N` / `close-tab` | タブ操作 |
| `page N` | ページ移動（1 始まり） |
| `zoom 150\|fit-width\|fit-page` | ズーム |
| `layout single\|continuous\|two\|two-continuous` | 表示モード |
| `rotate-view` | 表示を右に回転 |
| `panel thumbnails\|bookmarks\|search\|none` | 右パネル |
| `tools on\|off` | すべてのツール パネル |
| `find TEXT` / `find-next` | 検索 |
| `click X Y [COUNT]` / `drag X1 Y1 X2 Y2` | ビューア座標でのマウス操作 |
| `select-all` / `copy-text` | 全選択 / 選択テキストをログへ |
| `print-to-file OUT.pdf [PRINTER]` | 「Microsoft Print to PDF」でファイルに印刷 |
| `screenshot OUT.png` | ウィンドウのスクリーンショット |
| `dialog-screenshot properties\|print\|password OUT.png` | ダイアログのスクリーンショット |
| `state` / `layout-info` | 状態・レイアウトの診断ログ |
| `wait MS` / `wait-idle` | 待機 |
| `icon-gallery OUT.png NAME...` | アイコン一覧（フォントにないアイコンの確認用） |
| `exit` | 終了 |

## 注意点

- WPF プロジェクトでは暗黙の `using System.IO` が無効なので、必要なファイルで明示的に書く。
- コントロール内部から依存関係プロパティを変更するときは `SetCurrentValue` を使う（`Prop = x` だとバインディングが外れる）。
- WPF-UI 同梱のアイコンフォントに無いアイコンがある（例: `DocumentOnePageMultiple24`）。`icon-gallery` で確認する。
- 実プリンターで印刷テストをしない。`print-to-file` は「Microsoft Print to PDF」を使う。
