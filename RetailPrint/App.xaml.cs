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
        var printWorkerRequestPath = GetArgumentValue(e.Args, "--print-worker");
        var smokeTest = HasArgument(e.Args, "--smoke-test");
        var startupProbe = HasArgument(e.Args, "--startup-probe");

        try
        {
            if (!string.IsNullOrWhiteSpace(printWorkerRequestPath))
            {
                base.OnStartup(e);
                var exitCode = WindowsPrintWorker.Run(printWorkerRequestPath);
                Shutdown(exitCode);
                return;
            }

            if (smokeTest)
            {
                base.OnStartup(e);
                RunSmokeTest();
                Shutdown(0);
                return;
            }

            StartApplication(e);

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
            var context = !string.IsNullOrWhiteSpace(printWorkerRequestPath)
                ? "Tiến trình in Windows"
                : smokeTest
                    ? "Smoke-test khởi động"
                    : startupProbe
                        ? "Kiểm tra đường khởi động thật"
                        : "Khởi động ứng dụng";
            CrashLogService.Write(error, context);
            if (string.IsNullOrWhiteSpace(printWorkerRequestPath) && !smokeTest && !startupProbe)
                ShowStartupFailure();
            Shutdown(-1);
        }
    }

    private void StartApplication(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            WindowsRuntimeNames.SingletonMutex,
            out var createdNew);

        if (!createdNew)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(WindowsRuntimeNames.ShowWindowEvent);
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
            WindowsRuntimeNames.ShowWindowEvent);

        _showWindowWait = ThreadPool.RegisterWaitForSingleObject(
            _showWindowEvent,
            (_, _) =>
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                    return;

                try
                {
                    _ = Dispatcher.BeginInvoke(ShowMainWindow);
                }
                catch (InvalidOperationException)
                {
                    // Ứng dụng đang đóng; bỏ tín hiệu gọi cửa sổ đến muộn.
                }
            },
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);

        _settingsService = new SettingsService();
        _startupService = new StartupService();
        _printerClient = new PrinterClient();
        _retailApiClient = new RetailApiClient();

        var identityService = new DeviceIdentityService();
        var pairingCacheService = new PairingCacheService();
        var journal = new PrintJobJournal();

        _agentService = new RetailAgentService(
            _settingsService,
            identityService,
            pairingCacheService,
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

    private static bool HasArgument(IEnumerable<string> args, string expected) => args.Any(
        arg => string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase));

    private static string? GetArgumentValue(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index += 1)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }

        return null;
    }

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
            if (!freshSettings.UsesWindowsPrinter
                || freshSettings.SettingsVersion != 2
                || !freshSettings.AutoCutPaper)
            {
                throw new InvalidOperationException("Cấu hình cài mới phải dùng máy in Windows và bật cắt giấy tự động.");
            }

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

            var pairingCache = new PairingCacheService(smokeDirectory);
            var pairing = new PairingResult
            {
                AgentId = "7422dfad-bd9d-4f58-a3b8-7551ddc9ab20",
                PairingCode = "ABCD2345",
                DeviceName = "Retail Print - Smoke"
            };
            pairingCache.Save(identity, pairing);
            if (pairingCache.Load(identity)?.PairingCode != pairing.PairingCode)
                throw new InvalidOperationException("Mã kết nối cục bộ không được lưu/đọc đúng.");
            if (pairingCache.Load(new DeviceIdentity
                {
                    DeviceId = "a41f4bce-83be-4e13-ae28-31bfe89cfa4a",
                    Credential = new string('b', 48)
                }) is not null)
            {
                throw new InvalidOperationException("Mã kết nối cục bộ không được dùng cho máy Windows khác.");
            }

            var receiptPayload = new RetailPrintPayload
            {
                DocumentType = "SALES_ORDER",
                Paper = "80mm",
                Copies = 1,
                FontSizePercent = 100,
                Heading = "HƯNG PHÁT",
                Title = "PHIẾU XUẤT KHO",
                Subtitle = "Đơn đang lập",
                Meta =
                [
                    new RetailPrintMeta { Label = "Khách hàng", Value = "Khách vãng lai" },
                    new RetailPrintMeta { Label = "Ngày", Value = "10:39 21/9/26" }
                ],
                Columns = ["STT", "Sản phẩm", "SL", "ĐVT", "Đơn giá", "Thành tiền"],
                Rows =
                [
                    ["1", "3Q BIBI ĐEN", "1", "Gói", "52.000 ₫", "52.000 ₫"],
                    ["2", "3Q BIBI ĐEN", "1", "Thùng", "280.000 ₫", "280.000 ₫"],
                    ["3", "3Q BIBI TRẮNG", "1", "Thùng", "270.000 ₫", "270.000 ₫"]
                ],
                Totals = [new RetailPrintTotal { Label = "Tổng cộng", Value = "602.000 ₫" }]
            };
            using (var receipt = ThermalReceiptRenderer.Render(receiptPayload, 80))
            {
                if (receipt.Width != 576 || receipt.Height < 300)
                    throw new InvalidOperationException("Bố cục phiếu nhiệt 80 mm không được tạo đúng kích thước.");
            }

            var startupService = new StartupService();
            startupService.Apply(false);
            var printerClient = new PrinterClient();
            using var retailApiClient = new RetailApiClient();
            using var agentService = new RetailAgentService(
                settingsService,
                new DeviceIdentityService(),
                pairingCache,
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

    private void ExitApplication()
    {
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

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Mutex đã được giải phóng.
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        Shutdown();
    }
}
