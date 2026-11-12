using System.Threading;
using System.Windows;

namespace PhoneCam;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\PhoneCamSingleInstanceMutex";
    private const string ShowEventName = @"Local\PhoneCamShowWindowEvent";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set();
            }
            catch
            {
            }
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        bool startMinimized = e.Args.Any(a =>
            string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

        var mainWindow = new MainWindow(startMinimized);
        MainWindow = mainWindow;

        ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) => Dispatcher.BeginInvoke(() => mainWindow.RestoreWindowFromExternal()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        if (!startMinimized)
        {
            mainWindow.Show();
        }
        else
        {
            // Force Loaded event/services to start while keeping the window hidden in tray.
            mainWindow.Show();
            mainWindow.Hide();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showEvent?.Dispose();
        if (_instanceMutex != null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch { }
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }
}
