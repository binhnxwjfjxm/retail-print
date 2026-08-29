namespace RetailPrint.Models;

public sealed class PrinterSettings
{
    public string PrinterName { get; set; } = "Máy in quầy";
    public string IpAddress { get; set; } = "192.168.1.100";
    public int Port { get; set; } = 9100;
    public int PaperWidthMm { get; set; } = 80;
    public bool StartWithWindows { get; set; } = false;
}
