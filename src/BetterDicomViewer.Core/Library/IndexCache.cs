using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BetterDicomViewer.Core.Dicom;

namespace BetterDicomViewer.Core.Library;

public sealed class IndexCacheFile
{
    public int Version { get; set; } = IndexCache.FormatVersion;
    public string Root { get; set; } = "";
    public DateTime ScannedUtc { get; set; }
    public List<InstanceInfo> Instances { get; set; } = [];
    public List<string> NonDicomFiles { get; set; } = [];
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(IndexCacheFile))]
internal sealed partial class IndexCacheJsonContext : JsonSerializerContext;

/// <summary>Per-root header index stored under the local app-data folder (gzip JSON). Contains patient metadata; never stored in the source folder.</summary>
public static class IndexCache
{
    public const int FormatVersion = 3;

    public static string PathFor(string root)
    {
        var normalized = Path.GetFullPath(root).TrimEnd('\\', '/').ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..20];
        return Path.Combine(AppPaths.IndexDir, hash + ".json.gz");
    }

    public static IndexCacheFile? Load(string root)
    {
        var path = PathFor(root);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            var file = JsonSerializer.Deserialize(gz, IndexCacheJsonContext.Default.IndexCacheFile);
            return file is { Version: FormatVersion } ? file : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Save(string root, IndexCacheFile file)
    {
        var path = PathFor(root);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        {
            JsonSerializer.Serialize(gz, file, IndexCacheJsonContext.Default.IndexCacheFile);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static void Delete(string root)
    {
        var path = PathFor(root);
        if (File.Exists(path)) File.Delete(path);
    }
}
