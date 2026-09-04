using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using RetailPrint.Models;
using RetailPrint.Services;

namespace RetailPrint;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private RegisteredWaitHandle? _showWindowWait;
    private EventWaitHandle? _exitApplicationEvent;
    private RegisteredWaitHandle? _exitApplicationWait;
    private SettingsService? _settingsService;
    private StartupService? _startupService;
    private PrinterClient? _printerClient;
    private RetailApiClient? _retailApiClient;
    private RetailAgentService? _agentService;
    private bool _exceptionHandlersConfigured;
    private int _exitStarted;

    public MainWindow? MainWindowInstance { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        ConfigureExceptionHandling();
        var smokeTest = HasArgument(e.Args, "--smoke-test");
        var startupProbe = HasArgument(e.Args, "--startup-probe");
        var shutdownRequest = HasArgument(e.Args, "--shutdown");

        try
        {
            if (smokeTest)
            {
                base.OnStartup(e);
                RunSmokeTest();
                Shutdown(0);
                return;
            }

            StartApplication(e, shutdownRequest);
            if (shutdownRequest)
                return;

            if (startupProbe)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(8));
                    if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                        Dispatcher.Invoke(ExitApplication);
                });
            }
        }
        catch (Exception error)
        {
            var context = smokeTest
                ? "Smoke-test khởi động"
                : startupProbe
                    ? "Kiểm tra đường khởi động thật"
                    : shutdownRequest
                        ? "Dừng Retail Print"
                        : "Khởi động ứng dụng";
            CrashLogService.Write(error, context);
            if (!smokeTest && !startupProbe && !shutdownRequest)
                ShowStartupFailure();
            Shutdown(-1);
        }
    }

    private void StartApplication(StartupEventArgs e, bool shutdownRequest)
    {
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            WindowsRuntimeNames.SingletonMutex,
            out var createdNew);

        if (!createdNew)
        {
            if (shutdownRequest)
            {
                SignalExistingInstance(WindowsRuntimeNames.ExitApplicationEvent);
                WaitForOtherRetailPrintProcessesToExit();
            }
            else
            {
                SignalExistingInstance(WindowsRuntimeNames.ShowWindowEvent);
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        if (shutdownRequest)
        {
            ReleaseSingleInstanceMutex();
            Shutdown(0);
            return;
        }

        base.OnStartup(e);

        _showWindowEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            WindowsRuntimeNames.ShowWindowEvent);

        _showWindowWait = ThreadPool.RegisterWaitForSingleObject(
            _showWindowEvent,
            (_, _) => DispatchApplicationAction(ShowMainWindow),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);

        _exitApplicationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            WindowsRuntimeNames.ExitApplicationEvent);

        _exitApplicationWait = ThreadPool.RegisterWaitForSingleObject(
            _exitApplicationEvent,
            (_, _) => DispatchApplicationAction(ExitApplication),
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

        _agentService.Start();

        var startInBackground = HasArgument(e.Args, "--background");
        if (!startInBackground)
        {
            MainWindowInstance.Show();
            MainWindowInstance.Activate();
        }
    }

    private void DispatchApplicationAction(Action action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        try
        {
            _ = Dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Ứng dụng đang đóng; bỏ tín hiệu đến muộn.
        }
    }

    private static void SignalExistingInstance(string eventName)
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(eventName);
            signal.Set();
        }
        catch
        {
            // Bản cũ có thể chưa có event này; đường --shutdown có fallback theo process.
        }
    }

    private static void WaitForOtherRetailPrintProcessesToExit()
    {
        var currentProcessId = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName("RetailPrint"))
        {
            using (process)
            {
                if (process.Id == currentProcessId)
                    continue;

                try
                {
                    if (process.WaitForExit(5000))
                        continue;

                    try
                    {
                        process.CloseMainWindow();
                    }
                    catch
                    {
                        // Bản chạy nền có thể không có cửa sổ để đóng.
                    }

                    if (process.WaitForExit(2000))
                        continue;

                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch
                {
                    // Không để một process cũ lỗi làm treo cài/gỡ phiên bản mới.
                }
            }
        }
    }

    private static bool HasArgument(IEnumerable<string> args, string expected) => args.Any(
        arg => string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase));

    private static void RunSmokeTest()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("RETAIL_PRINT_SMOKE_FORCE_FAILURE"),
                "1",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Smoke-test thất bại có chủ ý để kiểm tra đường xử lý lỗi không tương tác.");
        }

        var smokeDirectory = Path.Combine(
            Path.GetTempPath(),
            $"RetailPrint-Smoke-{Guid.NewGuid():N}");

        try
        {
            var settingsService = new SettingsService(smokeDirectory);

            var freshSettings = settingsService.Load();
            if (!freshSettings.UsesWindowsPrinter || freshSettings.SettingsVersion != 2)
                throw new InvalidOperationException("Cấu hình cài mới không mặc định dùng máy in Windows.");

            Directory.CreateDirectory(smokeDirectory);
            File.WriteAllText(
                Path.Combine(smokeDirectory, "settings.json"),
                """
                {
                  "PrinterName": "Máy in quầy",
                  "IpAddress": "192.168.1.77",
                  "Port": 9100,
                  "PaperWidthMm": 80,
                  "StartWithWindows": false
                }
                """);

            var legacySettings = settingsService.Load();
            if (legacySettings.UsesWindowsPrinter
                || legacySettings.SettingsVersion != 2
                || legacySettings.IpAddress != "192.168.1.77"
                || legacySettings.Port != 9100)
            {
                throw new InvalidOperationException("Cấu hình IP cũ không được giữ nguyên khi nâng cấp.");
            }

            var identity = new DeviceIdentity
            {
                DeviceId = "9f7ad5b0-5641-4a19-9a8f-7b64b2a8ea03",
                Credential = new string('a', 48)
            };
            if (!RetailAgentService.ConnectionCodeMatchesDevice(identity.DeviceId, identity)
                || RetailAgentService.ConnectionCodeMatchesDevice("a41f4bce-83be-4e13-ae28-31bfe89cfa4a", identity))
            {
                throw new InvalidOperationException("Mã kết nối cũ phải bị bỏ khi deviceId của máy thay đổi.");
            }

            var startupService = new StartupService();
            startupService.Apply(false);
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
        finally
        {
            try
            {
                if (Directory.Exists(smokeDirectory))
                    Directory.Delete(smokeDirectory, recursive: true);
            }
            catch
            {
                // Thư mục kiểm thử tạm không được phép làm smoke-test thất bại.
            }
        }
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

    public void ExitApplication()
    {
        if (Interlocked.Exchange(ref _exitStarted, 1) != 0)
            return;

        _agentService?.Dispose();
        _agentService = null;

        _retailApiClient?.Dispose();
        _retailApiClient = null;

        if (MainWindowInstance is not null)
        {
            MainWindowInstance.AllowClose = true;
            MainWindowInstance.Close();
        }

        _showWindowWait?.Unregister(null);
        _showWindowWait = null;
        _showWindowEvent?.Dispose();
        _showWindowEvent = null;

        _exitApplicationWait?.Unregister(null);
        _exitApplicationWait = null;
        _exitApplicationEvent?.Dispose();
        _exitApplicationEvent = null;

        ReleaseSingleInstanceMutex();
        Shutdown();
    }

    private void ReleaseSingleInstanceMutex()
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Mutex đã được giải phóng hoặc phiên này không sở hữu mutex.
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }
}
