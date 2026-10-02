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
| `dialog-screenshot properties\|print\|password\|extract\|split\|replace\|create OUT.png` | ダイアログのスクリーンショット |
| `dialog-screenshot combine OUT.png FILE...` | ファイル結合ウィンドウ（ファイルを追加した状態） |
| `state` / `layout-info` | 状態・レイアウトの診断ログ |
| `wait MS` / `wait-idle` | 待機 |
| `icon-gallery OUT.png NAME...` | アイコン一覧（フォントにないアイコンの確認用） |
| `exit` | 終了 |
| `organize on\|off` / `grid-size W` | ページを整理（グリッド）の表示 / サムネールの幅 |
| `select-pages RANGE` | ページの選択（例: `2-4`） |
| `page-op rotate-left\|rotate-right\|delete\|duplicate\|insert-blank\|undo\|redo\|select-all\|select-odd` | ページ操作（結果のページ順をログへ） |
| `move-pages BEFORE` / `insert-file PATH BEFORE` | 選択ページの移動 / ファイルの挿入（ページ番号の前へ） |
| `grid-click N [ctrl] [shift] [double]` / `grid-drag N BEFORE` | グリッドでのクリック / ドラッグによる移動 |
| `grid-hover N` / `grid-hover gap BEFORE` / `grid-menu N OUT.png` | ホバー時の操作ボタン / 挿入ボタン / 右クリックメニュー（0 は余白） |
| `pages` | ページ順のログ（`P3` = 元ファイルの 3 ページ目、`S1p2` = 挿入ファイルの 2 ページ目、`@90` = 回転） |
| `save-as OUT.pdf` / `extract RANGE [FOLDER]` | 保存 / 抽出（フォルダー指定で 1 ページ 1 ファイル） |
| `split pages\|files\|ranges\|bookmarks VALUE FOLDER NAME` | 分割 |
| `combine bookmarks\|plain FILE...` | 結合 |
| `create-blank PAPER COUNT [landscape]` / `create-images FILE...` / `create-text FILE` | PDF の作成 |
| `autosave-now` / `crash` / `recover` | 自動保存 / 異常終了のシミュレーション / 復元（`phase2-recovery-1/2.txt`） |

## 注意点

- WPF プロジェクトでは暗黙の `using System.IO` が無効なので、必要なファイルで明示的に書く。
- コントロール内部から依存関係プロパティを変更するときは `SetCurrentValue` を使う（`Prop = x` だとバインディングが外れる）。
- WPF-UI 同梱のアイコンフォントに無いアイコンがある（例: `DocumentOnePageMultiple24`）。`icon-gallery` で確認する。
- 実プリンターで印刷テストをしない。`print-to-file` は「Microsoft Print to PDF」を使う。
- 自動確認スクリプトではクリップボードを使う操作（ページのコピー／貼り付け）を実行しない（作業中のクリップボードを上書きしてしまうため）。
- `ScrollViewer.ScrollToVerticalOffset` はレイアウトの最後に適用される。その途中で発生した `SizeChanged` は古いオフセットを見るので、
  ビューアはスクロール中のサイズ変更をスクロール適用後に処理する（ページを整理から戻ったときの位置ずれ対策）。
