using System.IO;
using System.Text.Json;
using RetailPrint.Models;

namespace RetailPrint.Services;

public sealed class SettingsService
{
    private const int CurrentSettingsVersion = 2;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };
    private readonly string _settingsDirectory;

    public SettingsService(string? settingsDirectory = null)
    {
        _settingsDirectory = string.IsNullOrWhiteSpace(settingsDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RetailPrint")
            : settingsDirectory;
    }

    private string SettingsPath => Path.Combine(_settingsDirectory, "settings.json");

    public PrinterSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return Normalize(new PrinterSettings { SettingsVersion = CurrentSettingsVersion });

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<PrinterSettings>(json, _jsonOptions)
                           ?? new PrinterSettings();

            // Cấu hình trước phiên bản 2 chỉ có IP/cổng. Giữ nguyên đường in mạng
            // để bản cập nhật không tự đổi máy in đang vận hành.
            if (settings.SettingsVersion < CurrentSettingsVersion)
            {
                settings.SettingsVersion = CurrentSettingsVersion;
                settings.ConnectionMode = PrinterConnectionModes.Network;
            }

            return Normalize(settings);
        }
        catch
        {
            return Normalize(new PrinterSettings { SettingsVersion = CurrentSettingsVersion });
        }
    }

    public void Save(PrinterSettings settings)
    {
        settings.SettingsVersion = CurrentSettingsVersion;
        Normalize(settings);
        Directory.CreateDirectory(_settingsDirectory);
        var json = JsonSerializer.Serialize(settings, _jsonOptions);
        File.WriteAllText(SettingsPath, json);
    }

    private static PrinterSettings Normalize(PrinterSettings settings)
    {
        if (!string.Equals(settings.ConnectionMode, PrinterConnectionModes.Windows, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(settings.ConnectionMode, PrinterConnectionModes.Network, StringComparison.OrdinalIgnoreCase))
        {
            settings.ConnectionMode = PrinterConnectionModes.Windows;
        }

        settings.SettingsVersion = CurrentSettingsVersion;
        settings.WindowsPrinterName = (settings.WindowsPrinterName ?? "").Trim();
        settings.PrinterName = (settings.PrinterName ?? "").Trim();
        settings.IpAddress = string.IsNullOrWhiteSpace(settings.IpAddress)
            ? "192.168.1.100"
            : settings.IpAddress.Trim();
        settings.Port = settings.Port is >= 1 and <= 65535 ? settings.Port : 9100;
        settings.PaperWidthMm = settings.PaperWidthMm == 58 ? 58 : 80;
        return settings;
    }
}
