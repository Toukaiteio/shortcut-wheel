using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ShortcutWheel;

public partial class App : Application
{
    private const string MutexName = "ShortcutWheel-Singleton-Mutex";
    private Mutex? _mutex;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Handle UI thread exceptions
        DispatcherUnhandledException += (_, args) =>
        {
            LogError($"Dispatcher exception: {args.Exception}");
            args.Handled = true;
        };

        // Handle non-UI thread exceptions
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            LogError($"Unhandled: {ex}");
        };

        // Single instance check
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            _mutex = null;
            MessageBox.Show("快捷转盘已经在运行。\nShortcutWheel is already running.", "ShortcutWheel",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            _mainWindow = new MainWindow();

            // Force HWND creation WITHOUT calling Show(). This avoids the
            // bottom-left title-bar artefact that appears when a hidden,
            // non-taskbar WPF window is forced into the Minimized state.
            new WindowInteropHelper(_mainWindow).EnsureHandle();

            _mainWindow.InitializeServices();

            // Defer balloon to first idle so the tray icon is fully alive.
            Dispatcher.BeginInvoke(new Action(() => _mainWindow?.NotifyStartup()),
                DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex)
        {
            LogError($"Startup failed: {ex}");
            MessageBox.Show($"Failed to start ShortcutWheel:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mainWindow?.DisposeResources(); } catch { }
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    // Logs may be appended from the UI thread, the mouse-hook thread and pool
    // threads, so serialize access. Cap the file so it cannot grow unbounded.
    private static readonly object LogLock = new();
    private const long MaxLogSize = 2 * 1024 * 1024;

    internal static void LogInfo(string message) => WriteLog("shortcutwheel.log", message);

    internal static void LogError(string message) => WriteLog("error.log", message);

    private static void WriteLog(string fileName, string message)
    {
        try
        {
            lock (LogLock)
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                if (new FileInfo(logPath).Exists && new FileInfo(logPath).Length > MaxLogSize)
                    File.WriteAllText(logPath, "");
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
            }
        }
        catch { }
    }
}
