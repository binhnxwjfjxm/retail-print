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
    private RetailApiClient? _retailApiClient;
    private RetailAgentService? _agentService;

    public MainWindow? MainWindowInstance { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            MutexName,
            out var createdNew);

        if (!createdNew)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowWindowEventName);
                signal.Set();
            }
            catch
            {
                // Nếu phiên cũ đang thoát, chỉ kết thúc phiên mới để tránh chạy trùng.
            }

            Shutdown();
            return;
        }

        base.OnStartup(e);

        _showWindowEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            ShowWindowEventName);

        _showWindowWait = ThreadPool.RegisterWaitForSingleObject(
            _showWindowEvent,
            (_, _) => Dispatcher.Invoke(ShowMainWindow),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);

        _settingsService = new SettingsService();
        _startupService = new StartupService();
        _printerClient = new PrinterClient();
        _retailApiClient = new RetailApiClient();

        var identityService = new DeviceIdentityService();
        var journal = new PrintJobJournal();

        _agentService = new RetailAgentService(
            _settingsService,
            identityService,
            _retailApiClient,
            _printerClient,
            journal);

        MainWindowInstance = new MainWindow(
            _settingsService,
            _startupService,
            _printerClient,
            _agentService);

        _trayService = new TrayService(
            showWindow: ShowMainWindow,
            testPrint: TestPrintFromTrayAsync,
            setStartWithWindows: SetStartWithWindowsFromTray,
            exit: ExitApplication,
            startupEnabled: _startupService.IsEnabled());

        MainWindowInstance.StartupPreferenceChanged += enabled =>
            _trayService?.SetStartupChecked(enabled);

        _agentService.Start();

        var startInBackground = e.Args.Any(arg =>
            string.Equals(
                arg,
                "--background",
                StringComparison.OrdinalIgnoreCase));

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
            _trayService?.ShowStatus(
                "Retail Print",
                "Máy in phản hồi tốt. Phiếu in thử đã được gửi.");
        }
        catch (Exception error)
        {
            _trayService?.ShowStatus(
                "Retail Print",
                $"Không in được: {error.Message}");
        }
    }

    private void SetStartWithWindowsFromTray(bool enabled)
    {
        if (_settingsService is null || _startupService is null)
            return;

        var settings = _settingsService.Load();
        settings.StartWithWindows = enabled;

        try
        {
            _startupService.Apply(enabled);
            _settingsService.Save(settings);
            MainWindowInstance?.SetStartWithWindows(enabled);
        }
        catch (Exception error)
        {
            _trayService?.ShowStatus(
                "Retail Print",
                $"Chưa thể cập nhật khởi động cùng Windows: {error.Message}");
        }
    }

    private void ExitApplication()
    {
        if (MainWindowInstance is not null)
        {
            MainWindowInstance.AllowClose = true;
            MainWindowInstance.Close();
        }

        _agentService?.Dispose();
        _retailApiClient?.Dispose();

        _trayService?.Dispose();
        _trayService = null;

        _showWindowWait?.Unregister(null);
        _showWindowEvent?.Dispose();

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Mutex đã được giải phóng.
        }

        _singleInstanceMutex?.Dispose();
        Shutdown();
    }
}
