namespace FastNote.Core;

public readonly record struct FileLoadProgress(
    string Path,
    long BytesProcessed,
    long TotalBytes,
    long LinesDiscovered)
{
    public double PercentComplete => TotalBytes == 0 ? 100 : Math.Min(100, BytesProcessed * 100d / TotalBytes);
}
