namespace BetterDicomViewer.Tests;

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bdv-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
        BetterDicomViewer.Core.AppPaths.Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bdv-tests", "appdata");
    }

    public string Path { get; }

    public string Sub(string name)
    {
        var p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
