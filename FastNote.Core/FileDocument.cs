using System.Buffers;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FastNote.Core;

public sealed class FileDocument : IDisposable
{
    private const int ScanBufferSize = 1024 * 1024;
    private const int InitialScanBytes = 4 * 1024 * 1024;
    private const int InitialTargetLines = 10000;
    private const int MaxVisibleCharsPerLine = 4096;

    private readonly SafeFileHandle _handle;
    private readonly object _stateGate = new();
    private readonly List<long> _lineStarts;
    private readonly Encoding _encoding;
    private readonly CancellationTokenSource _scanCancellation = new();

    private CachedBlock? _cache;
    private long _scanPosition;
    private bool _isIndexComplete;
    private FileLoadProgress _latestProgress;

    private FileDocument(string path, long fileSizeBytes, Encoding encoding, SafeFileHandle handle, List<long> lineStarts, long scanPosition, bool isIndexComplete, TimeSpan initialOpenDuration, FileLoadProgress latestProgress)
    {
        Path = path;
        FileSizeBytes = fileSizeBytes;
        _encoding = encoding;
        _handle = handle;
        _lineStarts = lineStarts;
        _scanPosition = scanPosition;
        _isIndexComplete = isIndexComplete;
        InitialOpenDuration = initialOpenDuration;
        IndexDuration = initialOpenDuration;
        _latestProgress = latestProgress;
        EncodingName = encoding.EncodingName;
    }

    public event EventHandler<FileLoadProgress>? ProgressChanged;

    public string Path { get; }
    public long FileSizeBytes { get; }
    public string EncodingName { get; }
    public Encoding TextEncoding => _encoding;
    public TimeSpan InitialOpenDuration { get; }
    public TimeSpan IndexDuration { get; private set; }

    public long LineCount
    {
        get
        {
            lock (_stateGate)
            {
                return _lineStarts.Count;
            }
        }
    }

    public long IndexedLineCount => LineCount;

    public bool IsIndexComplete
    {
        get
        {
            lock (_stateGate)
            {
                return _isIndexComplete;
            }
        }
    }

    public long BytesIndexed
    {
        get
        {
            lock (_stateGate)
            {
                return _scanPosition;
            }
        }
    }

    public FileLoadProgress LatestProgress
    {
        get
        {
            lock (_stateGate)
            {
                return _latestProgress;
            }
        }
    }

    public static Task<FileDocument> OpenAsync(string path, IProgress<FileLoadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => OpenInternal(path, progress, cancellationToken), cancellationToken);
    }

    public IReadOnlyList<string> ReadLines(long firstLine, int count)
    {
        var indexedCount = IndexedLineCount;
        if (count <= 0 || indexedCount == 0)
        {
            return Array.Empty<string>();
        }

        firstLine = Math.Clamp(firstLine, 0, indexedCount - 1);
        count = (int)Math.Min(count, indexedCount - firstLine);

        lock (_stateGate)
        {
            if (_cache is { } cache && firstLine >= cache.StartLine && firstLine + count <= cache.StartLine + cache.Lines.Length)
            {
                return cache.Slice(firstLine, count);
            }
        }

        var lines = new string[count];
        for (var i = 0; i < count; i++)
        {
            lines[i] = ReadSingleLine(firstLine + i);
        }

        lock (_stateGate)
        {
            _cache = new CachedBlock(firstLine, lines);
        }

        return lines;
    }

    public long EstimateTotalLineCount()
    {
        lock (_stateGate)
        {
            if (_isIndexComplete || _scanPosition <= 0)
            {
                return _lineStarts.Count;
            }

            var rate = _lineStarts.Count / (double)Math.Max(1, _scanPosition);
            return Math.Max(_lineStarts.Count, (long)Math.Ceiling(rate * FileSizeBytes));
        }
    }

    public bool TryGetLineRangeOffsets(long startLine, int lineCount, out long startOffset, out long endOffset)
    {
        lock (_stateGate)
        {
            startOffset = 0;
            endOffset = 0;

            if (lineCount <= 0 || startLine < 0 || startLine >= _lineStarts.Count)
            {
                return false;
            }

            startOffset = _lineStarts[(int)startLine];
            var nextLineIndex = startLine + lineCount;
            if (nextLineIndex < _lineStarts.Count)
            {
                endOffset = _lineStarts[(int)nextLineIndex];
                return true;
            }

            if (_isIndexComplete && nextLineIndex == _lineStarts.Count)
            {
                endOffset = FileSizeBytes;
                return true;
            }

            return false;
        }
    }

    public void Dispose()
    {
        _scanCancellation.Cancel();
        _handle.Dispose();
        _scanCancellation.Dispose();
    }

    private static FileDocument OpenInternal(string path, IProgress<FileLoadProgress>? progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The file does not exist.", path);
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ScanBufferSize, FileOptions.SequentialScan);

        var fileLength = stream.Length;
        var encoding = DetectEncoding(stream, out var startOffset);
        var openStopwatch = Stopwatch.StartNew();
        var lineStarts = new List<long>(EstimateLineCapacity(fileLength)) { startOffset };
        var initialPosition = ScanChunk(path, stream, fileLength, startOffset, InitialScanBytes, InitialTargetLines, lineStarts, progress, cancellationToken);
        openStopwatch.Stop();

        var isComplete = initialPosition >= fileLength;
        var latestProgress = new FileLoadProgress(path, initialPosition, fileLength, lineStarts.Count);
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var document = new FileDocument(path, fileLength, encoding, handle, lineStarts, initialPosition, isComplete, openStopwatch.Elapsed, latestProgress);

        progress?.Report(latestProgress);

        if (!isComplete)
        {
            document.StartBackgroundScan(initialPosition);
        }

        return document;
    }

    private void StartBackgroundScan(long startPosition)
    {
        _ = Task.Run(() => BackgroundScanWorker(startPosition, _scanCancellation.Token));
    }

    private void BackgroundScanWorker(long startPosition, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ScanBufferSize, FileOptions.SequentialScan);

            stream.Position = startPosition;
            var buffer = ArrayPool<byte>.Shared.Rent(ScanBufferSize);
            try
            {
                long absolutePosition = startPosition;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    lock (_stateGate)
                    {
                        for (var i = 0; i < read; i++)
                        {
                            if (buffer[i] == (byte)'\n')
                            {
                                var nextPos = absolutePosition + i + 1;
                                if (nextPos < FileSizeBytes)
                                {
                                    _lineStarts.Add(nextPos);
                                }
                            }
                        }

                        _scanPosition = absolutePosition + read;
                        _latestProgress = new FileLoadProgress(Path, _scanPosition, FileSizeBytes, _lineStarts.Count);
                    }

                    absolutePosition += read;
                    RaiseProgressChanged();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            lock (_stateGate)
            {
                _scanPosition = FileSizeBytes;
                _isIndexComplete = true;
                _latestProgress = new FileLoadProgress(Path, FileSizeBytes, FileSizeBytes, _lineStarts.Count);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            stopwatch.Stop();
            lock (_stateGate)
            {
                IndexDuration = InitialOpenDuration + stopwatch.Elapsed;
            }

            RaiseProgressChanged();
        }
    }

    private void RaiseProgressChanged()
    {
        FileLoadProgress progressSnapshot;
        lock (_stateGate)
        {
            progressSnapshot = _latestProgress;
        }

        ProgressChanged?.Invoke(this, progressSnapshot);
    }

    private static long ScanChunk(string path, FileStream stream, long fileLength, long startOffset, int maxBytes, int targetLines, List<long> lineStarts, IProgress<FileLoadProgress>? progress, CancellationToken cancellationToken)
    {
        stream.Position = startOffset;
        var buffer = ArrayPool<byte>.Shared.Rent(ScanBufferSize);
        try
        {
            long absolutePosition = startOffset;
            var scannedBytes = 0;

            while (scannedBytes < maxBytes && lineStarts.Count < targetLines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = Math.Min(buffer.Length, maxBytes - scannedBytes);
                var read = stream.Read(buffer, 0, remaining);
                if (read <= 0)
                {
                    break;
                }

                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        var nextPos = absolutePosition + i + 1;
                        if (nextPos < fileLength)
                        {
                            lineStarts.Add(nextPos);
                        }
                    }
                }

                absolutePosition += read;
                scannedBytes += read;
                progress?.Report(new FileLoadProgress(path, absolutePosition, fileLength, lineStarts.Count));
            }

            return absolutePosition;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int EstimateLineCapacity(long fileLength)
    {
        var estimate = fileLength / 48;
        estimate = Math.Clamp(estimate, 4_096, 10_000_000);
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

        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
        {
            startOffset = 2;
            return new UnicodeEncoding(false, false);
        }

        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
        {
            startOffset = 2;
            return new UnicodeEncoding(true, false);
        }

        startOffset = 0;
        return new UTF8Encoding(false, false);
    }

    private string ReadSingleLine(long lineIndex)
    {
        long start;
        long endExclusive;

        lock (_stateGate)
        {
            start = _lineStarts[(int)lineIndex];
            endExclusive = lineIndex + 1 < _lineStarts.Count ? _lineStarts[(int)lineIndex + 1] : BytesIndexed;
            if (_isIndexComplete && lineIndex + 1 >= _lineStarts.Count)
            {
                endExclusive = FileSizeBytes;
            }
        }

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

        var bytesToRead = (int)Math.Min(rawLength, 128 * 1024);
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
                line = line[..Math.Min(line.Length, MaxVisibleCharsPerLine)] + " …";
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
