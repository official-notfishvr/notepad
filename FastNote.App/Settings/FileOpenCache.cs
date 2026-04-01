using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FastNote.App.Settings;

public sealed class CachedFileSnapshot
{
    public required string Text { get; init; }
    public required string EncodingKey { get; init; }
    public required string LineEndingKey { get; init; }
    public long CharacterCount { get; init; }
    public long LineCount { get; init; }
}

public static class FileOpenCache
{
    private const int MaxCacheEntries = 48;
    private static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(21);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static string CacheDirectoryPath => Path.Combine(AppSettingsStore.SettingsDirectoryPath, "FileCache");

    public static bool TryLoad(string path, out CachedFileSnapshot? snapshot)
    {
        snapshot = null;

        try
        {
            var normalizedPath = NormalizePath(path);
            var fileInfo = new FileInfo(normalizedPath);
            if (!fileInfo.Exists)
            {
                return false;
            }

            var (metadataPath, contentPath) = GetEntryPaths(normalizedPath);
            if (!File.Exists(metadataPath) || !File.Exists(contentPath))
            {
                return false;
            }

            var metadata = JsonSerializer.Deserialize<CachedFileMetadata>(File.ReadAllText(metadataPath), JsonOptions);
            if (metadata is null || !IsMetadataMatch(metadata, normalizedPath, fileInfo))
            {
                return false;
            }

            using var fileStream = new FileStream(contentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzipStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            var text = reader.ReadToEnd();

            snapshot = new CachedFileSnapshot
            {
                Text = text,
                EncodingKey = metadata.EncodingKey,
                LineEndingKey = metadata.LineEndingKey,
                CharacterCount = metadata.CharacterCount,
                LineCount = metadata.LineCount,
            };

            return true;
        }
        catch
        {
            snapshot = null;
            return false;
        }
    }

    public static async Task StoreAsync(string path, string text, string encodingKey, string lineEndingKey, long lineCount)
    {
        try
        {
            var normalizedPath = NormalizePath(path);
            var fileInfo = new FileInfo(normalizedPath);
            if (!fileInfo.Exists)
            {
                Delete(normalizedPath);
                return;
            }

            Directory.CreateDirectory(CacheDirectoryPath);
            var (metadataPath, contentPath) = GetEntryPaths(normalizedPath);
            var metadata = new CachedFileMetadata
            {
                Path = normalizedPath,
                FileLength = fileInfo.Length,
                LastWriteUtcTicks = fileInfo.LastWriteTimeUtc.Ticks,
                EncodingKey = string.IsNullOrWhiteSpace(encodingKey) ? "utf-8" : encodingKey,
                LineEndingKey = string.IsNullOrWhiteSpace(lineEndingKey) ? "crlf" : lineEndingKey,
                CharacterCount = text.Length,
                LineCount = Math.Max(1, lineCount),
            };

            await using (var fileStream = new FileStream(contentPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            await using (var gzipStream = new GZipStream(fileStream, CompressionLevel.Fastest))
            await using (var writer = new StreamWriter(gzipStream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(text);
            }

            await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions), Encoding.UTF8);
            TrimCacheDirectory();
        }
        catch { }
    }

    public static void Delete(string path)
    {
        try
        {
            var normalizedPath = NormalizePath(path);
            var (metadataPath, contentPath) = GetEntryPaths(normalizedPath);
            TryDeleteFile(metadataPath);
            TryDeleteFile(contentPath);
        }
        catch { }
    }

    private static bool IsMetadataMatch(CachedFileMetadata metadata, string normalizedPath, FileInfo fileInfo)
    {
        if (!string.Equals(metadata.Path, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (metadata.FileLength != fileInfo.Length || metadata.LastWriteUtcTicks != fileInfo.LastWriteTimeUtc.Ticks)
        {
            return false;
        }

        return true;
    }

    private static (string MetadataPath, string ContentPath) GetEntryPaths(string normalizedPath)
    {
        var keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        var key = Convert.ToHexString(keyBytes);
        return (Path.Combine(CacheDirectoryPath, $"{key}.json"), Path.Combine(CacheDirectoryPath, $"{key}.gz"));
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).Trim();
    }

    private static void TrimCacheDirectory()
    {
        if (!Directory.Exists(CacheDirectoryPath))
        {
            return;
        }

        var metadataFiles = new DirectoryInfo(CacheDirectoryPath).EnumerateFiles("*.json", SearchOption.TopDirectoryOnly).OrderByDescending(file => file.LastWriteTimeUtc).ToList();

        foreach (var staleFile in metadataFiles.Where(file => DateTime.UtcNow - file.LastWriteTimeUtc > MaxCacheAge))
        {
            DeleteEntryPair(staleFile);
        }

        metadataFiles = new DirectoryInfo(CacheDirectoryPath).EnumerateFiles("*.json", SearchOption.TopDirectoryOnly).OrderByDescending(file => file.LastWriteTimeUtc).ToList();

        foreach (var overflowFile in metadataFiles.Skip(MaxCacheEntries))
        {
            DeleteEntryPair(overflowFile);
        }
    }

    private static void DeleteEntryPair(FileInfo metadataFile)
    {
        TryDeleteFile(metadataFile.FullName);
        TryDeleteFile(Path.ChangeExtension(metadataFile.FullName, ".gz"));
    }

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch { }
    }

    private sealed class CachedFileMetadata
    {
        public required string Path { get; init; }
        public long FileLength { get; init; }
        public long LastWriteUtcTicks { get; init; }
        public string EncodingKey { get; init; } = "utf-8";
        public string LineEndingKey { get; init; } = "crlf";
        public long CharacterCount { get; init; }
        public long LineCount { get; init; }
    }
}
