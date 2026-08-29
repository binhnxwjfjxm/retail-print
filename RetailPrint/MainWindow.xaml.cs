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
        var settings = _settingsService.Load();
        PrinterNameTextBox.Text = settings.PrinterName;
        IpTextBox.Text = settings.IpAddress;
        PortTextBox.Text = settings.Port.ToString();
        Paper80Radio.IsChecked = settings.PaperWidthMm == 80;
        Paper58Radio.IsChecked = settings.PaperWidthMm == 58;
        StartupCheckBox.IsChecked = _startupService.IsEnabled() || settings.StartWithWindows;
    }

    private PrinterSettings ReadSettingsFromForm()
    {
        if (!int.TryParse(PortTextBox.Text.Trim(), out var port))
            throw new InvalidOperationException("Cổng máy in chưa đúng.");

        var settings = new PrinterSettings
        {
            PrinterName = string.IsNullOrWhiteSpace(PrinterNameTextBox.Text)
                ? "Máy in quầy"
                : PrinterNameTextBox.Text.Trim(),
            IpAddress = IpTextBox.Text.Trim(),
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

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TestButton.IsEnabled = false;
            PrinterStatusText.Text = "Đang kiểm tra máy in…";
            var settings = ReadSettingsFromForm();
            await _printerClient.PrintTestAsync(settings);
            PrinterStatusText.Text = "Máy in phản hồi tốt. Phiếu in thử đã được gửi.";
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
            PrinterStatusText.Text = "Đã lưu cấu hình.";
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
                    RetailStatusBadge.Background = new SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(236, 253, 245));
                    RetailStatusText.Foreground = new SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(4, 120, 87));
                    break;

                case false:
                    RetailStatusBadge.Background = new SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(254, 242, 242));
                    RetailStatusText.Foreground = new SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(185, 28, 28));
                    break;

                default:
                    RetailStatusBadge.Background = new SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(238, 242, 247));
                    RetailStatusText.Foreground = new SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(75, 85, 99));
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
