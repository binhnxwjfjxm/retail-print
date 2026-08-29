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

    public bool AllowClose { get; set; }
    public event Action<bool>? StartupPreferenceChanged;

    public MainWindow(
        SettingsService settingsService,
        StartupService startupService,
        PrinterClient printerClient)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _startupService = startupService;
        _printerClient = printerClient;

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

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TestButton.IsEnabled = false;
            SetConnectionStatus(null, "Đang kiểm tra...");
            var settings = ReadSettingsFromForm();
            await _printerClient.PrintTestAsync(settings);
            SetConnectionStatus(true, "Sẵn sàng");
        }
        catch (Exception ex)
        {
            SetConnectionStatus(false, ex.Message);
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
            var settings = ReadSettingsFromForm();
            _settingsService.Save(settings);
            _startupService.Apply(settings.StartWithWindows);
            StartupPreferenceChanged?.Invoke(settings.StartWithWindows);
            SetConnectionStatus(null, "Đã lưu");
        }
        catch (Exception ex)
        {
            SetConnectionStatus(false, ex.Message);
        }
    }

    public void SetConnectionStatus(bool? connected, string text)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = text;
            switch (connected)
            {
                case true:
                    StatusBadge.Background = new SolidColorBrush(Color.FromRgb(236, 253, 245));
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(4, 120, 87));
                    break;
                case false:
                    StatusBadge.Background = new SolidColorBrush(Color.FromRgb(254, 242, 242));
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                    break;
                default:
                    StatusBadge.Background = new SolidColorBrush(Color.FromRgb(238, 242, 247));
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99));
                    break;
            }
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
