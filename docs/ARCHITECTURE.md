# アーキテクチャ

## 1. 技術スタック

| 層 | 技術 | 選定理由 |
|---|---|---|
| UI | WPF (.NET 10) + WPF-UI | Windows ネイティブ。Windows 10 でも Fluent デザインが使え、InkCanvas・印刷・UI オートメーションなど OS 機能と統合しやすい |
| MVVM | CommunityToolkit.Mvvm | ソースジェネレーターで ViewModel を簡潔に書ける |
| PDF 描画・解析 | PDFium（自前の P/Invoke） | Chrome の PDF エンジン。描画品質・速度・堅牢性が高く BSD ライセンス |
| PDF 書き込み | PDFsharp 6 (WPF 版) | 注釈の外観生成、日本語フォントのサブセット埋め込み、暗号化、電子署名 |
| OS 連携 | Windows API | 印刷（GDI）、OCR（Windows.Media.Ocr）、読み上げ、スキャナ（WIA）、証明書ストア |

## 2. プロジェクト構成と依存関係

```
PdfAcrobat.App ──► PdfAcrobat.Core ──► PdfAcrobat.Pdfium ──► pdfium.dll
       │                  │
       │                  └──► PDFsharp-WPF
       └──► WPF-UI, CommunityToolkit.Mvvm
```

- **PdfAcrobat.Pdfium**: PDFium の C API をそのまま `LibraryImport` で宣言し（`Interop/`）、その上に
  `PdfDocument` / `PdfPage` / `PdfTextPage` などの安全なラッパーを置く。UI に依存しない。
- **PdfAcrobat.Core**: 文書モデル、テキスト処理・検索、フォント解決など、UI 以外のロジック。
- **PdfAcrobat.App**: 画面、ビューアコントロール、各種サービス。

## 3. PDFium とスレッド

PDFium はスレッドセーフではないため、すべての呼び出しを `PdfiumLibrary.Acquire()` のグローバルロックで直列化する。

- 描画はプログレッシブ API（`FPDF_RenderPageBitmap_Start` / `_Continue`）を使い、約 25 ms ごとにロックを手放す。
  待っているスレッドがあれば即座に中断するので、テキストの当たり判定などの短い処理が描画に待たされない。
- 描画はキャンセル可能（スクロールで画面外に出たページの描画を止める）。
- ページはリース方式（`PdfDocument.OpenPage` → `Dispose`）。同じページは参照カウント付きでキャッシュし、
  解析済みの内容とテキスト情報を再利用する。

## 4. 文書モデル

```
DocumentSession（タブ 1 つ）
 ├─ Sources: PdfSource[]        読み込んだ PDF（変更しない）
 ├─ State: DocumentState        編集可能な状態（不変オブジェクト）
 │    └─ Pages: PageRef[]       各ページ = (SourceId, SourceIndex, 追加回転) または空白ページ
 └─ Undo / Redo スタック         DocumentState のスナップショット
```

- ページの並べ替え・削除・回転・他ファイルからの挿入は `PageRef` の並びを変えるだけで、PDF を書き換えない。
  そのため操作は即座に反映され、Undo/Redo はスナップショットの入れ替えで済む。
- 保存時（Phase 2）に、`DocumentState` から PDFium と PDFsharp で新しい PDF を組み立てる。
- 注釈など（Phase 3 以降）も `DocumentState` に不変オブジェクトとして持たせる。

## 5. 座標系

| 座標系 | 原点・向き | 単位 |
|---|---|---|
| PDF ユーザー空間 | 左下、y 上向き | ポイント (1/72 inch) |
| ページ表示空間（ビュー） | 左上、y 下向き | DIP (1/96 inch) |
| ビットマップ | 左上、y 下向き | デバイスピクセル |

`PdfPageGeometry.GetDisplayMatrix` が PDFium 内部（`CPDF_Page::GetDisplayMatrix`）と同じ計算で
ユーザー空間 → 表示空間の行列を作る。ページの `/Rotate`、`PageRef.Rotation`（保存される回転）、
表示の回転（保存されない）をすべてこの行列で扱うため、選択・検索結果・注釈は回転しても正しい位置に描かれる。

## 6. ビューア（`Controls/Viewer`）

- `ViewerLayout` が表示モード（単一／連続／見開き／見開き連続）とズームからページの配置を計算する。
- `PdfViewer` は `ScrollViewer` 内の `Canvas` に、画面付近のページだけ `PageView` を配置する（仮想化）。
- `PageView` は `PageRenderService` にビットマップを要求し、届くまでは別倍率のキャッシュを引き伸ばして表示する。
  1,000 万ピクセルを超える倍率では、ページ全体は縮小画像、見えている範囲だけ高解像度タイルで描く。
- ページのジオメトリ・リンク・テキストは `PageInfoCache` がバックグラウンドで読み込む。
- テキスト選択・検索は PDFium の文字ボックスを管理側（`PdfTextSnapshot`）にコピーして処理する。

## 7. 日本語フォント

PDFsharp は TrueType Collection（.ttc）を読めないが、游ゴシック・メイリオ・MS ゴシック・BIZ UD など
Windows の日本語フォントはほぼ .ttc で提供されている。`WindowsFontResolver` は WPF でフォントファイルと
書体番号を特定し、`TrueTypeCollection.ExtractFace` で単体の TTF に切り出して PDFsharp に渡す。
これにより日本語フォントがサブセット埋め込みされる（数 MB のフォントでも数十 KB）。

## 8. 印刷

GDI 印刷（`System.Drawing.Printing`）を使い、PDFium の `FPDF_RenderPage` で各ページをプリンターの
デバイスコンテキストに直接描画する。文字や図形はベクターのまま出力される。

## 9. UI の自動確認

`PdfAcrobat.exe --script steps.txt --offscreen` でスクリプトに書いた操作を実行し、スクリーンショットや
ログを保存できる（`Automation/AutomationRunner.cs`）。開発時の動作確認に使う。詳細は DEVELOPMENT.md。
