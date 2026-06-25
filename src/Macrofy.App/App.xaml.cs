using System.Linq;
using System.Threading;
using System.Windows;

namespace Macrofy.App;

public partial class App : Application
{
    // Unique to Macrofy so we detect a second launch and surface the running instance instead.
    private const string MutexName = "Macrofy.SingleInstance.A8F3C2E1";
    private const string ShowEventName = "Macrofy.ShowWindow.A8F3C2E1";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // An elevated "Restart as administrator" hands off: the old instance is exiting, so
            // wait briefly for the lock instead of bowing out.
            bool relaunch = e.Args.Any(a => string.Equals(a, "--relaunch", StringComparison.OrdinalIgnoreCase));
            if (relaunch)
            {
                try { createdNew = _mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { createdNew = true; }
            }
            if (!createdNew)
            {
                // Already running - poke the live instance to come to the front, then bow out.
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* it'll be there next time */ }
                Shutdown();
                return;
            }
        }

        // Log unhandled exceptions and fail gracefully instead of vanishing.
        DispatcherUnhandledException += (_, args) =>
        {
            CrashLog.Write(args.Exception);
            MessageBox.Show(
                $"Macrofy hit an unexpected error and needs to close.\n\nDetails were saved to:\n{CrashLog.FilePath}",
                "Macrofy", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => CrashLog.Write(args.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);

        // Listen for a second launch asking us to surface the window.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() => _window?.BringToFront());
        })
        { IsBackground = true, Name = "Macrofy.SingleInstance" };
        listener.Start();

        // Tray app: come up hidden when launched with --minimized or when the user prefers it.
        bool minimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase))
                         || AppSettings.Load().StartMinimized;

        _window = new MainWindow();
        MainWindow = _window;
        if (!minimized)
            _window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { /* not owned */ }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
