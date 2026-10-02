namespace PdfAcrobat.Pdfium;

public enum PdfiumErrorCode : uint
{
    Success = 0,
    Unknown = 1,
    File = 2,
    Format = 3,
    Password = 4,
    Security = 5,
    Page = 6,
    XfaLoad = 7,
    XfaLayout = 8,
}

public class PdfiumException : Exception
{
    public PdfiumException(PdfiumErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public PdfiumErrorCode Code { get; }

    internal static PdfiumException FromCode(PdfiumErrorCode code) => code switch
    {
        PdfiumErrorCode.Password => new PdfPasswordException(),
        PdfiumErrorCode.File => new PdfiumException(code, "ファイルを開けませんでした。"),
        PdfiumErrorCode.Format => new PdfiumException(code, "PDF ファイルではないか、ファイルが壊れています。"),
        PdfiumErrorCode.Security => new PdfiumException(code, "サポートされていないセキュリティ方式です。"),
        PdfiumErrorCode.Page => new PdfiumException(code, "ページが見つからないか、内容にエラーがあります。"),
        _ => new PdfiumException(code, "PDF の処理中に不明なエラーが発生しました。"),
    };
}

/// <summary>Thrown when a document requires a password or the given password is wrong.</summary>
public sealed class PdfPasswordException : PdfiumException
{
    public PdfPasswordException()
        : base(PdfiumErrorCode.Password, "パスワードが必要か、パスワードが正しくありません。")
    {
    }
}
