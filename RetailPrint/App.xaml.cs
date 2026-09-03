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
    private bool _exceptionHandlersConfigured;

    public MainWindow? MainWindowInstance { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        ConfigureExceptionHandling();

        try
        {
            if (HasArgument(e.Args, "--smoke-test"))
            {
                base.OnStartup(e);
                RunSmokeTest();
                Shutdown(0);
                return;
            }

            StartApplication(e);
        }
        catch (Exception error)
        {
            CrashLogService.Write(error, "Khởi động ứng dụng");
            ShowStartupFailure();
            Shutdown(-1);
        }
    }

    private void StartApplication(StartupEventArgs e)
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

        var startInBackground = HasArgument(e.Args, "--background");
        if (!startInBackground)
        {
            MainWindowInstance.Show();
            MainWindowInstance.Activate();
        }
    }

    private static bool HasArgument(IEnumerable<string> args, string expected) => args.Any(
        arg => string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase));

    private static void RunSmokeTest()
    {
        var settingsService = new SettingsService();
        var startupService = new StartupService();
        var printerClient = new PrinterClient();
        using var retailApiClient = new RetailApiClient();
        using var agentService = new RetailAgentService(
            settingsService,
            new DeviceIdentityService(),
            retailApiClient,
            printerClient,
            new PrintJobJournal());

        var window = new MainWindow(
            settingsService,
            startupService,
            printerClient,
            agentService)
        {
            AllowClose = true
        };
        window.Close();
    }

    private void ConfigureExceptionHandling()
    {
        if (_exceptionHandlersConfigured) return;
        _exceptionHandlersConfigured = true;

        DispatcherUnhandledException += (_, args) =>
        {
            CrashLogService.Write(args.Exception, "Lỗi giao diện chưa được xử lý");
            args.Handled = true;
            try
            {
                System.Windows.MessageBox.Show(
                    $"Retail Print gặp lỗi và cần đóng. Nhật ký lỗi được lưu tại:\n{CrashLogService.StartupLogPath}",
                    "Retail Print",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { }
            Shutdown(-1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error)
                CrashLogService.Write(error, "Lỗi tiến trình chưa được xử lý");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLogService.Write(args.Exception, "Lỗi tác vụ nền chưa được quan sát");
            args.SetObserved();
        };
    }

    private static void ShowStartupFailure()
    {
        try
        {
            System.Windows.MessageBox.Show(
                $"Retail Print không thể khởi động. Nhật ký lỗi được lưu tại:\n{CrashLogService.StartupLogPath}",
                "Retail Print",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Không để lỗi hiển thị che mất lỗi khởi động ban đầu.
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
                "Phiếu in thử đã được gửi tới máy in đã thiết lập.");
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
