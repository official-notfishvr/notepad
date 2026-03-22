using FastNote.Core;
using System.Diagnostics;
using System.Threading.Tasks;

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

var openStopwatch = Stopwatch.StartNew();
using var document = await LargeFileDocument.OpenAsync(path);
openStopwatch.Stop();

Console.WriteLine($"Path: {path}");
Console.WriteLine($"Initial lines: {document.IndexedLineCount:N0}");
Console.WriteLine($"Estimated total after open: {document.EstimateTotalLineCount():N0}");
Console.WriteLine($"Initial open: {openStopwatch.Elapsed.TotalMilliseconds:N0} ms");

if (!document.IsIndexComplete)
{
    var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    document.ProgressChanged += OnProgressChanged;
    await completionSource.Task;
    document.ProgressChanged -= OnProgressChanged;

    void OnProgressChanged(object? sender, FileLoadProgress progress)
    {
        if (document.IsIndexComplete)
        {
            completionSource.TrySetResult();
        }
    }
}

Console.WriteLine($"Final lines: {document.LineCount:N0}");
Console.WriteLine($"Full index: {document.IndexDuration.TotalMilliseconds:N0} ms");
Console.WriteLine($"Encoding: {document.EncodingName}");
Console.WriteLine($"Size: {document.FileSizeBytes:N0} bytes");
