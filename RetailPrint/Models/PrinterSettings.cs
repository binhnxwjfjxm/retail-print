using System.Text.Json.Serialization;

namespace RetailPrint.Models;

public static class PrinterConnectionModes
{
    public const string Windows = "windows";
    public const string Network = "network";
}

public sealed class PrinterSettings
{
    public int SettingsVersion { get; set; } = 2;
    public string ConnectionMode { get; set; } = PrinterConnectionModes.Windows;
    public string PrinterName { get; set; } = "";
    public string WindowsPrinterName { get; set; } = "";
    public string IpAddress { get; set; } = "192.168.1.100";
    public int Port { get; set; } = 9100;
    public int PaperWidthMm { get; set; } = 80;
    public bool StartWithWindows { get; set; } = false;

    [JsonIgnore]
    public bool UsesWindowsPrinter => string.Equals(
        ConnectionMode,
        PrinterConnectionModes.Windows,
        StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string EffectivePrinterName => UsesWindowsPrinter
        ? WindowsPrinterName.Trim()
        : string.IsNullOrWhiteSpace(PrinterName)
            ? $"Máy in mạng {IpAddress.Trim()}"
            : PrinterName.Trim();
}
