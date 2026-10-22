namespace BetterDicomViewer.Core;

/// <summary>
/// All local state lives under %LOCALAPPDATA%\BetterDicomViewer (or BDV_APPDATA for tests). Source scan folders are never written.
/// </summary>
public static class AppPaths
{
    private static string? _root;

    public static string Root
    {
        get
        {
            if (_root is not null) return _root;
            var overridePath = Environment.GetEnvironmentVariable("BDV_APPDATA");
            _root = !string.IsNullOrWhiteSpace(overridePath)
                ? overridePath
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterDicomViewer");
            return _root;
        }
        set => _root = value;
    }

    public static string IndexDir => Ensure(Path.Combine(Root, "index"));
    public static string ThumbnailDir => Ensure(Path.Combine(Root, "cache", "thumbnails"));
    public static string AnnotationsDir => Ensure(Path.Combine(Root, "annotations"));
    public static string SegmentationsDir => Ensure(Path.Combine(Root, "segmentations"));
    public static string SettingsFile => Path.Combine(Ensure(Root), "settings.json");

    public static string CacheRoot => Path.Combine(Root, "cache");

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>True if <paramref name="candidate"/> is the same as or inside <paramref name="folder"/>.</summary>
    public static bool IsSameOrInside(string candidate, string folder)
    {
        var c = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var f = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return c.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }
}
