using Wpf.Ui.Controls;

namespace PdfAcrobat.App.ViewModels;

/// <summary>
/// The tool list shown on the home screen and in the "all tools" pane. Tools that are not implemented
/// yet stay visible (disabled) with the roadmap phase from docs/FEATURES.md.
/// </summary>
public static class ToolCatalog
{
    public const string Open = "open";
    public const string Combine = "combine";
    public const string Organize = "organize";
    public const string Comment = "comment";
    public const string FillSign = "fill-sign";
    public const string Edit = "edit";
    public const string Export = "export";
    public const string Compress = "compress";
    public const string Protect = "protect";
    public const string Redact = "redact";
    public const string Ocr = "ocr";
    public const string Compare = "compare";
    public const string PrepareForm = "prepare-form";
    public const string Certificate = "certificate";
    public const string ReadAloud = "read-aloud";
    public const string Measure = "measure";
    public const string AiAssistant = "ai";

    public static IReadOnlyList<ToolCard> All { get; } =
    [
        new(Edit, "PDF を編集", "テキストや画像を追加・編集", SymbolRegular.DocumentEdit24, false, "Phase 5", "#E34877"),
        new(Export, "PDF を書き出し", "Word・Excel・画像などに変換", SymbolRegular.ArrowExportUp24, false, "Phase 6", "#1473E6"),
        new(FillSign, "入力と署名", "フォームに入力して署名", SymbolRegular.Signature24, false, "Phase 4", "#7E4CE6"),
        new(Comment, "注釈", "ハイライト・ノート・描画", SymbolRegular.Comment24, false, "Phase 3", "#E8A600"),
        new(Organize, "ページを整理", "回転・削除・並べ替え・抽出", SymbolRegular.DocumentMultiple24, false, "Phase 2", "#12A35A"),
        new(Combine, "ファイルを結合", "複数のファイルを 1 つの PDF に", SymbolRegular.Merge24, false, "Phase 2", "#0E9AA7"),
        new(Compress, "PDF を圧縮", "ファイルサイズを小さくする", SymbolRegular.ArrowMinimize24, false, "Phase 6", "#D7373F"),
        new(Ocr, "スキャンと OCR", "画像の文字を検索可能に", SymbolRegular.ScanText24, false, "Phase 7", "#2D9D78"),
        new(Protect, "PDF を保護", "パスワードと権限を設定", SymbolRegular.LockClosed24, false, "Phase 8", "#5258E4"),
        new(Redact, "墨消し", "機密情報を完全に削除", SymbolRegular.EyeOff24, false, "Phase 8", "#C9252D"),
        new(Compare, "ファイルを比較", "2 つの PDF の違いを表示", SymbolRegular.SplitVertical24, false, "Phase 9", "#E68619"),
        new(PrepareForm, "フォームを準備", "入力フィールドを作成", SymbolRegular.Form24, false, "Phase 10", "#8F5BD6"),
        new(Certificate, "証明書を使用", "デジタル署名と検証", SymbolRegular.ShieldCheckmark24, false, "Phase 11", "#3B8C3B"),
        new(ReadAloud, "読み上げ", "文書を音声で読み上げ", SymbolRegular.Speaker224, false, "Phase 12", "#0D66D0"),
        new(Measure, "オブジェクトを測定", "距離・面積の計測", SymbolRegular.Ruler24, false, "Phase 12", "#6E6E6E"),
        new(AiAssistant, "AI アシスタント", "要約と質問応答", SymbolRegular.Sparkle24, false, "Phase 13", "#B04CE6"),
    ];

    public static IReadOnlyList<ToolCard> HomeTools { get; } = All.Take(8).ToList();
}
