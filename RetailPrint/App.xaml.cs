using System.Threading;
using System.Windows;
using RetailPrint.Services;

namespace RetailPrint;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\RetailPrint.Singleton";
    private const string ShowWindowEventName = @"Local\RetailPrint.ShowWindow";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private RegisteredWaitHandle? _showWindowWait;
    private TrayService? _trayService;
    private SettingsService? _settingsService;
    private StartupService? _startupService;
    private PrinterClient? _printerClient;

    public MainWindow? MainWindowInstance { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowWindowEventName);
                signal.Set();
            }
            catch
            {
                // Náº¿u instance cÅ© Ä‘ang thoÃ¡t, chá»‰ káº¿t thÃºc instance má»›i Ä‘á»ƒ trÃ¡nh cháº¡y trÃ¹ng.
            }

            Shutdown();
            return;
        }

        base.OnStartup(e);

        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _showWindowWait = ThreadPool.RegisterWaitForSingleObject(
            _showWindowEvent,
            (_, _) => Dispatcher.Invoke(ShowMainWindow),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);

        _settingsService = new SettingsService();
        _startupService = new StartupService();
        _printerClient = new PrinterClient();

        MainWindowInstance = new MainWindow(_settingsService, _startupService, _printerClient);
        _trayService = new TrayService(
            showWindow: ShowMainWindow,
            testPrint: TestPrintFromTrayAsync,
            setStartWithWindows: SetStartWithWindowsFromTray,
            exit: ExitApplication,
            startupEnabled: _startupService.IsEnabled());

        MainWindowInstance.StartupPreferenceChanged += enabled =>
            _trayService?.SetStartupChecked(enabled);

        var startInBackground = e.Args.Any(arg =>
            string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));

        if (!startInBackground)
        {
            MainWindowInstance.Show();
            MainWindowInstance.Activate();
        }
    }

    public void ShowMainWindow()
    {
        if (MainWindowInstance is null)
            return;

        MainWindowInstance.Show();
        MainWindowInstance.WindowState = WindowState.Normal;
        MainWindowInstance.Activate();
    }

    private async Task TestPrintFromTrayAsync()
    {
        if (_settingsService is null || _printerClient is null)
            return;

        var settings = _settingsService.Load();
        try
        {
            await _printerClient.PrintTestAsync(settings);
            _trayService?.ShowStatus("Retail Print", "MÃ¡y in pháº£n há»“i tá»‘t. In thá»­ Ä‘Ã£ gá»­i.");
            MainWindowInstance?.SetConnectionStatus(true, "Sáºµn sÃ ng");
        }
        catch (Exception ex)
        {
            _trayService?.ShowStatus("Retail Print", $"KhÃ´ng in Ä‘Æ°á»£c: {ex.Message}");
            MainWindowInstance?.SetConnectionStatus(false, "KhÃ´ng káº¿t ná»‘i");
        }
    }

    private void SetStartWithWindowsFromTray(bool enabled)
    {
        if (_settingsService is null || _startupService is null)
            return;

        var settings = _settingsService.Load();
        settings.StartWithWindows = enabled;
        _startupService.Apply(enabled);
        _settingsService.Save(settings);
        MainWindowInstance?.SetStartWithWindows(enabled);
    }

    private void ExitApplication()
    {
        if (MainWindowInstance is not null)
        {
            MainWindowInstance.AllowClose = true;
            MainWindowInstance.Close();
        }

        _trayService?.Dispose();
        _trayService = null;
        _showWindowWait?.Unregister(null);
        _showWindowEvent?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Shutdown();
    }
}

