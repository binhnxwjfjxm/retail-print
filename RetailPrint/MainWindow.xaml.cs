using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using RetailPrint.Models;
using RetailPrint.Services;

namespace RetailPrint;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly StartupService _startupService;
    private readonly PrinterClient _printerClient;
    private readonly RetailAgentService _agentService;
    private bool _loadingSettings;
    private string? _preferredWindowsPrinterName;
    private int _printerDiscoveryVersion;

    public bool AllowClose { get; set; }

    public MainWindow(
        SettingsService settingsService,
        StartupService startupService,
        PrinterClient printerClient,
        RetailAgentService agentService)
    {
        InitializeComponent();

        _settingsService = settingsService;
        _startupService = startupService;
        _printerClient = printerClient;
        _agentService = agentService;

        _agentService.StatusChanged += SetRetailStatus;
        _agentService.PairingChanged += SetPairing;

        LoadSettings();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            var settings = _settingsService.Load();
            IpTextBox.Text = settings.IpAddress;
            PortTextBox.Text = settings.Port.ToString();
            Paper80Radio.IsChecked = settings.PaperWidthMm == 80;
            Paper58Radio.IsChecked = settings.PaperWidthMm == 58;
            StartupCheckBox.IsChecked = _startupService.IsEnabled() || settings.StartWithWindows;

            WindowsPrinterRadio.IsChecked = settings.UsesWindowsPrinter;
            NetworkPrinterRadio.IsChecked = !settings.UsesWindowsPrinter;
            _preferredWindowsPrinterName = settings.WindowsPrinterName;
            UpdateConnectionPanels();
            PrinterStatusText.Text = settings.UsesWindowsPrinter
                ? "Đang tải danh sách máy in Windows…"
                : "Nhập địa chỉ máy in mạng rồi bấm In thử.";
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        if (WindowsPrinterRadio.IsChecked == true)
            await RefreshWindowsPrintersAsync(_preferredWindowsPrinterName, quiet: true);
    }

    private PrinterSettings ReadSettingsFromForm()
    {
        var useWindowsPrinter = WindowsPrinterRadio.IsChecked == true;
        var port = 9100;
        if (!useWindowsPrinter && !int.TryParse(PortTextBox.Text.Trim(), out port))
            throw new InvalidOperationException("Cổng máy in chưa đúng.");
        if (useWindowsPrinter && int.TryParse(PortTextBox.Text.Trim(), out var savedPort))
            port = savedPort;

        var windowsPrinterName = WindowsPrinterComboBox.SelectedItem as string ?? "";
        var ipAddress = IpTextBox.Text.Trim();
        var settings = new PrinterSettings
        {
            SettingsVersion = 2,
            ConnectionMode = useWindowsPrinter
                ? PrinterConnectionModes.Windows
                : PrinterConnectionModes.Network,
            WindowsPrinterName = windowsPrinterName,
            PrinterName = useWindowsPrinter
                ? windowsPrinterName
                : string.IsNullOrWhiteSpace(ipAddress)
                    ? "Máy in mạng"
                    : $"Máy in mạng {ipAddress}",
            IpAddress = ipAddress,
            Port = port,
            PaperWidthMm = Paper58Radio.IsChecked == true ? 58 : 80,
            StartWithWindows = StartupCheckBox.IsChecked == true
        };

        PrinterClient.Validate(settings);
        return settings;
    }

    private void SaveCurrentSettings()
    {
        var settings = ReadSettingsFromForm();
        _settingsService.Save(settings);
        _startupService.Apply(settings.StartWithWindows);
    }

    private async Task RefreshWindowsPrintersAsync(string? preferred = null, bool quiet = false)
    {
        var discoveryVersion = ++_printerDiscoveryVersion;
        try
        {
            if (!quiet)
                PrinterStatusText.Text = "Đang tải danh sách máy in Windows…";

            var discoveryTask = Task.Run(() =>
            {
                var printers = _printerClient.ListWindowsPrinters();
                var defaultPrinter = _printerClient.GetDefaultWindowsPrinterName();
                return (Printers: printers, DefaultPrinter: defaultPrinter);
            });

            var completed = await Task.WhenAny(
                discoveryTask,
                Task.Delay(TimeSpan.FromSeconds(5)));

            if (completed != discoveryTask
                && discoveryVersion == _printerDiscoveryVersion
                && WindowsPrinterRadio.IsChecked == true)
            {
                PrinterStatusText.Text =
                    "Windows đang phản hồi chậm khi đọc máy in. Retail Print vẫn hoạt động và sẽ tự cập nhật khi danh sách sẵn sàng.";
            }

            var result = await discoveryTask;
            if (discoveryVersion != _printerDiscoveryVersion || WindowsPrinterRadio.IsChecked != true)
                return;

            var printers = result.Printers;
            WindowsPrinterComboBox.ItemsSource = printers;

            var desired = FindPrinter(printers, preferred)
                          ?? FindPrinter(printers, result.DefaultPrinter)
                          ?? printers.FirstOrDefault();
            WindowsPrinterComboBox.SelectedItem = desired;

            PrinterStatusText.Text = printers.Count == 0
                ? "Windows chưa có máy in nào. Hãy cài máy in trong Windows rồi bấm Làm mới."
                : $"Đã tìm thấy {printers.Count} máy in trên Windows.";
        }
        catch (Exception error)
        {
            if (discoveryVersion != _printerDiscoveryVersion)
                return;

            WindowsPrinterComboBox.ItemsSource = Array.Empty<string>();
            WindowsPrinterComboBox.SelectedItem = null;
            PrinterStatusText.Text = $"Chưa thể đọc danh sách máy in Windows: {error.Message}";
            CrashLogService.Write(error, "Đọc danh sách máy in Windows");
        }
    }

    private static string? FindPrinter(IReadOnlyList<string> printers, string? desired)
    {
        if (string.IsNullOrWhiteSpace(desired)) return null;
        return printers.FirstOrDefault(name =>
            string.Equals(name, desired.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateConnectionPanels()
    {
        if (WindowsPrinterPanel is null || NetworkPrinterPanel is null) return;
        var useWindowsPrinter = WindowsPrinterRadio.IsChecked == true;
        WindowsPrinterPanel.Visibility = useWindowsPrinter ? Visibility.Visible : Visibility.Collapsed;
        NetworkPrinterPanel.Visibility = useWindowsPrinter ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ConnectionMode_Checked(object sender, RoutedEventArgs e)
    {
        UpdateConnectionPanels();
        if (_loadingSettings) return;

        if (WindowsPrinterRadio.IsChecked == true && WindowsPrinterComboBox.Items.Count == 0)
        {
            await RefreshWindowsPrintersAsync();
        }
        else if (WindowsPrinterRadio.IsChecked == true)
        {
            PrinterStatusText.Text = "Chọn máy in Windows rồi bấm In thử.";
        }
        else
        {
            _printerDiscoveryVersion += 1;
            PrinterStatusText.Text = "Nhập địa chỉ máy in mạng rồi bấm In thử.";
        }
    }

    private async void RefreshPrintersButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshWindowsPrintersAsync(WindowsPrinterComboBox.SelectedItem as string);
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TestButton.IsEnabled = false;
            PrinterStatusText.Text = "Đang gửi phiếu in thử…";
            var settings = ReadSettingsFromForm();
            await _printerClient.PrintTestAsync(settings);
            PrinterStatusText.Text = "Phiếu in thử đã được gửi tới máy in đã chọn.";
        }
        catch (Exception error)
        {
            PrinterStatusText.Text = error.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveCurrentSettings();
            PrinterStatusText.Text = "Đã lưu máy in cho Retail Print.";
        }
        catch (Exception error)
        {
            PrinterStatusText.Text = error.Message;
        }
    }

    private void DispatchToUi(Action action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        try
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                    return;

                try
                {
                    action();
                }
                catch (Exception error)
                {
                    CrashLogService.Write(error, "Cập nhật giao diện từ tác vụ nền");
                }
            });
        }
        catch (InvalidOperationException)
        {
            // Ứng dụng đang đóng; bỏ cập nhật trạng thái muộn từ luồng nền.
        }
    }

    public void SetRetailStatus(bool? connected, string text)
    {
        DispatchToUi(() =>
        {
            RetailStatusText.Text = text;

            switch (connected)
            {
                case true:
                    RetailStatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(236, 253, 245));
                    RetailStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(4, 120, 87));
                    break;
                case false:
                    RetailStatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(254, 242, 242));
                    RetailStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(185, 28, 28));
                    break;
                default:
                    RetailStatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(238, 242, 247));
                    RetailStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(75, 85, 99));
                    break;
            }
        });
    }

    public void SetPairing(PairingResult? pairing)
    {
        DispatchToUi(() =>
        {
            if (pairing is null || string.IsNullOrWhiteSpace(pairing.PairingCode))
            {
                PairingCodeText.Text = "Đang tải mã…";
                PairingHintText.Text = "Mã cố định của máy này sẽ tự hiện khi kết nối được Công Ty.";
                return;
            }

            PairingCodeText.Text = pairing.PairingCode;
            PairingHintText.Text = "Mã cố định. Dùng cùng mã này trên mọi điện thoại Retail.";
        });
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (AllowClose)
            return;

        e.Cancel = true;
        if (System.Windows.Application.Current is App app)
            _ = Dispatcher.BeginInvoke(new Action(app.ExitApplication));
    }
}
