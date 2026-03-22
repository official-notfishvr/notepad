using FastNote.Core;
using System.Diagnostics;

if (args.Length == 0)
{
    Console.WriteLine("Usage: FastNote.Bench <path>");
    return;
}

var path = Path.GetFullPath(args[0]);
if (!File.Exists(path))
{
    Console.WriteLine($"Missing file: {path}");
    return;
}

var stopwatch = Stopwatch.StartNew();
using var document = await LargeFileDocument.OpenAsync(path);
stopwatch.Stop();

Console.WriteLine($"Path: {path}");
Console.WriteLine($"Lines: {document.LineCount:N0}");
Console.WriteLine($"Size: {document.FileSizeBytes:N0} bytes");
Console.WriteLine($"Encoding: {document.EncodingName}");
Console.WriteLine($"Engine: {document.IndexDuration.TotalMilliseconds:N0} ms");
Console.WriteLine($"Total: {stopwatch.Elapsed.TotalMilliseconds:N0} ms");
