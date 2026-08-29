using System.Net;
using System.Net.Sockets;
using System.Text;
using RetailPrint.Models;

namespace RetailPrint.Services;

public sealed class PrinterClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    public async Task PrintTestAsync(PrinterSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);

        using var client = new TcpClient();
        await client.ConnectAsync(settings.IpAddress, settings.Port, cancellationToken)
            .AsTask()
            .WaitAsync(ConnectTimeout, cancellationToken);

        await using var stream = client.GetStream();
        var payload = BuildEscPosTest(settings);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static void Validate(PrinterSettings settings)
    {
        if (!IPAddress.TryParse(settings.IpAddress, out _))
            throw new InvalidOperationException("IP máy in chưa đúng.");

        if (settings.Port is < 1 or > 65535)
            throw new InvalidOperationException("Cổng máy in chưa đúng.");

        if (settings.PaperWidthMm is not (58 or 80))
            throw new InvalidOperationException("Khổ giấy chỉ hỗ trợ 58 mm hoặc 80 mm.");
    }

    private static byte[] BuildEscPosTest(PrinterSettings settings)
    {
        var width = settings.PaperWidthMm == 58 ? 32 : 48;
        var separator = new string('-', width);
        var now = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");

        var text = new StringBuilder()
            .AppendLine("RETAIL PRINT")
            .AppendLine("KET NOI MAY IN THANH CONG")
            .AppendLine(separator)
            .AppendLine($"May in: {ToAscii(settings.PrinterName)}")
            .AppendLine($"IP: {settings.IpAddress}:{settings.Port}")
            .AppendLine($"Kho giay: {settings.PaperWidthMm} mm")
            .AppendLine($"Luc: {now}")
            .AppendLine(separator)
            .AppendLine("San sang nhan lenh tu Retail.")
            .AppendLine()
            .AppendLine()
            .ToString();

        var bytes = new List<byte>();
        bytes.AddRange(new byte[] { 0x1B, 0x40 }); // Initialize
        bytes.AddRange(new byte[] { 0x1B, 0x61, 0x01 }); // Center
        bytes.AddRange(Encoding.ASCII.GetBytes(text));
        bytes.AddRange(new byte[] { 0x1B, 0x61, 0x00 }); // Left
        bytes.AddRange(new byte[] { 0x1D, 0x56, 0x42, 0x00 }); // Cut if supported
        return bytes.ToArray();
    }

    private static string ToAscii(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) !=
                System.Globalization.UnicodeCategory.NonSpacingMark && c <= 127)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
