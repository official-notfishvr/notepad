using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using FastNote.App.Settings;

namespace FastNote.App;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private CancellationTokenSource? _singleInstanceListenerCancellation;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Directory.CreateDirectory(AppSettingsStore.SettingsDirectoryPath);
        ProfileOptimization.SetProfileRoot(AppSettingsStore.SettingsDirectoryPath);
        ProfileOptimization.StartProfile("startup.profile");

        var startupPaths = GetValidStartupPaths(e.Args);
        SingleInstanceLock();

        if (!_ownsSingleInstanceMutex && startupPaths.Count > 0 && TryForwardPathsToPrimaryInstance(startupPaths))
        {
            Shutdown();
            return;
        }

        var launchedWithFiles = startupPaths.Count > 0;
        var mainWindow = new MainWindow(restorePreviousSessionOnStartup: !launchedWithFiles, autoShowSetupOnFirstLaunch: !launchedWithFiles);
        MainWindow = mainWindow;
        mainWindow.Show();
        StartSingleInstanceListener();

        if (startupPaths.Count > 0)
        {
            _ = mainWindow.OpenStartupFilesAsync(startupPaths);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceListenerCancellation?.Cancel();
        _singleInstanceListenerCancellation?.Dispose();
        _singleInstanceListenerCancellation = null;

        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }

    private void SingleInstanceLock()
    {
        if (_singleInstanceMutex is not null)
        {
            return;
        }

        _singleInstanceMutex = new Mutex(initiallyOwned: true, GetSingleInstanceMutexName(), out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
    }

    private void StartSingleInstanceListener()
    {
        if (!_ownsSingleInstanceMutex || _singleInstanceListenerCancellation is not null)
        {
            return;
        }

        _singleInstanceListenerCancellation = new CancellationTokenSource();
        _ = ListenForSecondaryLaunchesAsync(_singleInstanceListenerCancellation.Token);
    }

    private async Task ListenForSecondaryLaunchesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipeServer = new NamedPipeServerStream(GetSingleInstancePipeName(), PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await pipeServer.WaitForConnectionAsync(cancellationToken);

                using var reader = new StreamReader(pipeServer, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: false);
                var payload = await reader.ReadToEndAsync(cancellationToken);
                var paths = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

                if (paths.Length == 0 || MainWindow is not FastNote.App.MainWindow mainWindow)
                {
                    continue;
                }

                var operation = Dispatcher.InvokeAsync(() => mainWindow.OpenFilesFromSecondaryLaunchAsync(paths));
                await operation.Task.Unwrap();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException) { }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private static IReadOnlyList<string> GetValidStartupPaths(IEnumerable<string> args)
    {
        return args.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool TryForwardPathsToPrimaryInstance(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return false;
        }

        try
        {
            using var client = new NamedPipeClientStream(".", GetSingleInstancePipeName(), PipeDirection.Out, PipeOptions.None);
            client.Connect(timeout: 1500);

            using var writer = new StreamWriter(client, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: false) { AutoFlush = true };

            writer.Write(string.Join(Environment.NewLine, paths));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static string GetSingleInstanceMutexName() => $@"Local\FastNote-{GetSingleInstanceKey()}";

    private static string GetSingleInstancePipeName() => $"FastNote-{GetSingleInstanceKey()}";

    private static string GetSingleInstanceKey()
    {
        var processPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "FastNote";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(processPath)));
    }
}
