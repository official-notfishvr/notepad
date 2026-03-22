using System.Buffers;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FastNote.Core;

public sealed class LargeFileDocument : IDisposable
{
    private const int ScanBufferSize = 1024 * 1024;
    private const int MaxVisibleCharsPerLine = 2048;

    private readonly SafeFileHandle _handle;
    private readonly long[] _lineStarts;
    private readonly Encoding _encoding;
    private readonly object _cacheGate = new();

    private CachedBlock? _cache;

    private LargeFileDocument(
        string path,
        long fileSizeBytes,
        long[] lineStarts,
        Encoding encoding,
        TimeSpan indexDuration,
        SafeFileHandle handle)
    {
        Path = path;
        FileSizeBytes = fileSizeBytes;
        _lineStarts = lineStarts;
        _encoding = encoding;
        EncodingName = encoding.EncodingName;
        IndexDuration = indexDuration;
        _handle = handle;
    }

    public string Path { get; }

    public long FileSizeBytes { get; }

    public long LineCount => _lineStarts.Length;

    public string EncodingName { get; }

    public TimeSpan IndexDuration { get; }

    public static Task<LargeFileDocument> OpenAsync(
        string path,
        IProgress<FileLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => OpenInternal(path, progress, cancellationToken), cancellationToken);
    }

    public IReadOnlyList<string> ReadLines(long firstLine, int count)
    {
        if (count <= 0 || LineCount == 0)
        {
            return Array.Empty<string>();
        }

        firstLine = Math.Clamp(firstLine, 0, LineCount - 1);
        count = (int)Math.Min(count, LineCount - firstLine);

        lock (_cacheGate)
        {
            if (_cache is { } cache &&
                firstLine >= cache.StartLine &&
                firstLine + count <= cache.StartLine + cache.Lines.Length)
            {
                return cache.Slice(firstLine, count);
            }
        }

        var lines = new string[count];
        for (var i = 0; i < count; i++)
        {
            lines[i] = ReadSingleLine(firstLine + i);
        }

        lock (_cacheGate)
        {
            _cache = new CachedBlock(firstLine, lines);
        }

        return lines;
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    private static LargeFileDocument OpenInternal(
        string path,
        IProgress<FileLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The file does not exist.", path);
        }

        var stopwatch = Stopwatch.StartNew();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            ScanBufferSize,
            FileOptions.SequentialScan);

        var fileLength = stream.Length;
        var encoding = DetectEncoding(stream, out var startOffset);
        var lineStarts = BuildLineIndex(path, stream, fileLength, startOffset, progress, cancellationToken);
        stopwatch.Stop();

        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new LargeFileDocument(path, fileLength, lineStarts, encoding, stopwatch.Elapsed, handle);
    }

    private static long[] BuildLineIndex(
        string path,
        FileStream stream,
        long fileLength,
        long startOffset,
        IProgress<FileLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var lineStarts = new List<long>(EstimateLineCapacity(fileLength));
        lineStarts.Add(startOffset);

        var buffer = ArrayPool<byte>.Shared.Rent(ScanBufferSize);
        try
        {
            stream.Position = startOffset;
            long absolutePosition = startOffset;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] == (byte)'\n' && absolutePosition + i + 1 < fileLength)
                    {
                        lineStarts.Add(absolutePosition + i + 1);
                    }
                }

                absolutePosition += read;
                progress?.Report(new FileLoadProgress(path, absolutePosition, fileLength, lineStarts.Count));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        progress?.Report(new FileLoadProgress(path, fileLength, fileLength, lineStarts.Count));
        return lineStarts.ToArray();
    }

    private static int EstimateLineCapacity(long fileLength)
    {
        var estimate = fileLength / 48;
        estimate = Math.Clamp(estimate, 4_096, 8_000_000);
        return (int)estimate;
    }

    private static Encoding DetectEncoding(FileStream stream, out long startOffset)
    {
        Span<byte> bom = stackalloc byte[4];
        var read = stream.Read(bom);
        stream.Position = 0;

        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
        {
            startOffset = 3;
            return new UTF8Encoding(false, false);
        }

        startOffset = 0;
        return new UTF8Encoding(false, false);
    }

    private string ReadSingleLine(long lineIndex)
    {
        var start = _lineStarts[lineIndex];
        var endExclusive = lineIndex + 1 < _lineStarts.Length ? _lineStarts[lineIndex + 1] : FileSizeBytes;
        var rawLength = Math.Max(0, endExclusive - start);
        if (rawLength == 0)
        {
            return string.Empty;
        }

        while (rawLength > 0)
        {
            var trailing = ReadByte(start + rawLength - 1);
            if (trailing is (byte)'\n' or (byte)'\r')
            {
                rawLength--;
                continue;
            }

            break;
        }

        var bytesToRead = (int)Math.Min(rawLength, 64 * 1024);
        if (bytesToRead == 0)
        {
            return string.Empty;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(bytesToRead);
        try
        {
            var totalRead = 0;
            while (totalRead < bytesToRead)
            {
                totalRead += RandomAccess.Read(_handle, buffer.AsSpan(totalRead, bytesToRead - totalRead), start + totalRead);
            }

            var line = DecodeLine(buffer.AsSpan(0, bytesToRead));
            if (rawLength > bytesToRead || line.Length > MaxVisibleCharsPerLine)
            {
                line = line[..Math.Min(line.Length, MaxVisibleCharsPerLine)] + " ...";
            }

            return line;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private string DecodeLine(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return _encoding.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private byte ReadByte(long offset)
    {
        Span<byte> value = stackalloc byte[1];
        RandomAccess.Read(_handle, value, offset);
        return value[0];
    }

    private sealed record CachedBlock(long StartLine, string[] Lines)
    {
        public IReadOnlyList<string> Slice(long requestedStartLine, int count)
        {
            var offset = (int)(requestedStartLine - StartLine);
            var output = new string[count];
            Array.Copy(Lines, offset, output, 0, count);
            return output;
        }
    }
}
