using System.IO;

namespace PdfAcrobat.Tests;

internal static class TestFiles
{
    private static readonly Lazy<string> RepoRoot = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PdfAcrobat.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("リポジトリのルートが見つかりません。");
    });

    public static string Sample(string name) => Path.Combine(RepoRoot.Value, "samples", name);

    public static byte[] ReadSample(string name) => File.ReadAllBytes(Sample(name));
}
