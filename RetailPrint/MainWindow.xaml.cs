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

    public bool AllowClose { get; set; }
    public event Action<bool>? StartupPreferenceChanged;

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
            RefreshWindowsPrinters(settings.WindowsPrinterName, quiet: true);
            UpdateConnectionPanels();
        }
        finally
        {
            _loadingSettings = false;
        }
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
        StartupPreferenceChanged?.Invoke(settings.StartWithWindows);
    }

    private void RefreshWindowsPrinters(string? preferred = null, bool quiet = false)
    {
        try
        {
            var printers = _printerClient.ListWindowsPrinters();
            WindowsPrinterComboBox.ItemsSource = printers;

            var desired = FindPrinter(printers, preferred)
                          ?? FindPrinter(printers, _printerClient.GetDefaultWindowsPrinterName())
                          ?? printers.FirstOrDefault();
            WindowsPrinterComboBox.SelectedItem = desired;

            if (!quiet)
            {
                PrinterStatusText.Text = printers.Count == 0
                    ? "Windows chưa có máy in nào. Hãy cài máy in trong Windows rồi bấm Làm mới."
                    : $"Đã tìm thấy {printers.Count} máy in trên Windows.";
            }
        }
        catch (Exception error)
        {
            WindowsPrinterComboBox.ItemsSource = Array.Empty<string>();
            WindowsPrinterComboBox.SelectedItem = null;
            if (!quiet)
                PrinterStatusText.Text = $"Chưa thể đọc danh sách máy in Windows: {error.Message}";
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

    private void ConnectionMode_Checked(object sender, RoutedEventArgs e)
    {
        UpdateConnectionPanels();
        if (_loadingSettings) return;

        if (WindowsPrinterRadio.IsChecked == true && WindowsPrinterComboBox.Items.Count == 0)
            RefreshWindowsPrinters();
        else
            PrinterStatusText.Text = WindowsPrinterRadio.IsChecked == true
                ? "Chọn máy in Windows rồi bấm In thử."
                : "Nhập địa chỉ máy in mạng rồi bấm In thử.";
    }

    private void RefreshPrintersButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshWindowsPrinters(WindowsPrinterComboBox.SelectedItem as string);
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

    private async void PairButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PairButton.IsEnabled = false;
            SaveCurrentSettings();
            PairingCodeText.Text = "Đang lấy…";
            PairingHintText.Text = "Đang tạo mã kết nối với Retail.";

            var pairing = await _agentService.CreatePairingCodeAsync();
            SetPairing(pairing);
        }
        catch (RetailApiException error)
        {
            PairingCodeText.Text = "Chưa có mã";
            PairingHintText.Text = error.Message;
        }
        catch (Exception error)
        {
            PairingCodeText.Text = "Chưa có mã";
            PairingHintText.Text = error.Message;
        }
        finally
        {
            PairButton.IsEnabled = true;
        }
    }

    public void SetRetailStatus(bool? connected, string text)
    {
        Dispatcher.Invoke(() =>
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
        Dispatcher.Invoke(() =>
        {
            if (pairing is null)
            {
                PairingCodeText.Text = "Đã kết nối";
                PairingHintText.Text = "Retail Print đang nhận lệnh in từ Công Ty.";
                return;
            }

            PairingCodeText.Text = pairing.PairingCode;
            PairingHintText.Text = "Nhập mã này trên Retail. Mã dùng một lần, hiệu lực 10 phút.";
        });
    }

    public void SetStartWithWindows(bool enabled)
    {
        Dispatcher.Invoke(() => StartupCheckBox.IsChecked = enabled);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (AllowClose)
            return;

        e.Cancel = true;
        Hide();
    }
}
