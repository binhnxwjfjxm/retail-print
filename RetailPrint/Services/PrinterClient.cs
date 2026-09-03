using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RetailPrint.Models;

namespace RetailPrint.Services;

public sealed class PrinterClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private readonly WindowsPrinterService _windowsPrinterService = new();

    public IReadOnlyList<string> ListWindowsPrinters() => _windowsPrinterService.ListInstalledPrinters();

    public string? GetDefaultWindowsPrinterName() => _windowsPrinterService.GetDefaultPrinterName();

    public Task PrintTestAsync(PrinterSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        var meta = new List<RetailPrintMeta>
        {
            new() { Label = "Máy in", Value = settings.EffectivePrinterName },
            new() { Label = "Cách kết nối", Value = settings.UsesWindowsPrinter ? "Máy in Windows" : "Mạng trực tiếp" },
            new() { Label = "Khổ giấy", Value = $"{settings.PaperWidthMm} mm" }
        };

        if (!settings.UsesWindowsPrinter)
            meta.Insert(2, new RetailPrintMeta { Label = "Địa chỉ", Value = $"{settings.IpAddress}:{settings.Port}" });

        var payload = new RetailPrintPayload
        {
            DocumentType = "PRINTER_TEST",
            Paper = settings.PaperWidthMm == 58 ? "58mm" : "80mm",
            Copies = 1,
            Heading = "BÁN TẠI QUẦY",
            Title = "PHIẾU IN THỬ",
            Subtitle = "Kiểm tra kết nối máy in",
            Meta = meta,
            Footer = ["Nếu đọc rõ phiếu này, máy in đã sẵn sàng."]
        };
        return PrintAsync(settings, payload, cancellationToken);
    }

    public async Task PrintAsync(PrinterSettings settings, RetailPrintPayload payload, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        ValidatePayload(settings, payload);

        if (settings.UsesWindowsPrinter)
        {
            var document = BuildTextDocument(payload, asciiOnly: false);
            await _windowsPrinterService.PrintTextAsync(
                settings.WindowsPrinterName,
                settings.PaperWidthMm,
                document,
                payload.Copies,
                cancellationToken);
            return;
        }

        await PrintNetworkAsync(settings, payload, cancellationToken);
    }

    public static void Validate(PrinterSettings settings)
    {
        if (settings.PaperWidthMm is not (58 or 80))
            throw new InvalidOperationException("Khổ giấy chỉ hỗ trợ 58 mm hoặc 80 mm.");

        if (settings.UsesWindowsPrinter)
        {
            if (string.IsNullOrWhiteSpace(settings.WindowsPrinterName))
                throw new InvalidOperationException("Chưa chọn máy in trên Windows.");
            return;
        }

        if (!string.Equals(settings.ConnectionMode, PrinterConnectionModes.Network, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cách kết nối máy in chưa hợp lệ.");
        if (!IPAddress.TryParse(settings.IpAddress, out _))
            throw new InvalidOperationException("Địa chỉ máy in chưa đúng.");
        if (settings.Port is < 1 or > 65535)
            throw new InvalidOperationException("Cổng máy in chưa đúng.");
    }

    private static async Task PrintNetworkAsync(
        PrinterSettings settings,
        RetailPrintPayload payload,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(ConnectTimeout);
        try
        {
            await client.ConnectAsync(settings.IpAddress, settings.Port, connectCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Máy in không phản hồi trong thời gian cho phép.");
        }
        catch (SocketException)
        {
            throw new InvalidOperationException("Không kết nối được tới máy in. Kiểm tra địa chỉ, cổng và mạng nội bộ.");
        }

        await using var stream = client.GetStream();
        var document = BuildEscPosDocument(payload);
        for (var copy = 0; copy < payload.Copies; copy += 1)
        {
            await stream.WriteAsync(document, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }

    private static void ValidatePayload(PrinterSettings settings, RetailPrintPayload payload)
    {
        var payloadWidth = payload.Paper switch { "58mm" => 58, "80mm" => 80, _ => 0 };
        if (payloadWidth == 0)
            throw new InvalidOperationException("Khổ giấy từ Retail không hợp lệ.");
        if (payloadWidth != settings.PaperWidthMm)
            throw new InvalidOperationException($"Khổ giấy trên Retail là {payloadWidth} mm nhưng máy này đang đặt {settings.PaperWidthMm} mm.");
        if (payload.Copies is < 1 or > 5)
            throw new InvalidOperationException("Số bản in phải từ 1 đến 5.");
        if (string.IsNullOrWhiteSpace(payload.Title))
            throw new InvalidOperationException("Nội dung in chưa có tiêu đề.");
    }

    private static byte[] BuildEscPosDocument(RetailPrintPayload payload)
    {
        var text = BuildTextDocument(payload, asciiOnly: true);
        var bytes = new List<byte>();
        bytes.AddRange([0x1B, 0x40]);
        bytes.AddRange([0x1B, 0x61, 0x00]);
        bytes.AddRange(Encoding.ASCII.GetBytes(text));
        bytes.AddRange([0x1D, 0x56, 0x42, 0x00]);
        return bytes.ToArray();
    }

    private static string BuildTextDocument(RetailPrintPayload payload, bool asciiOnly)
    {
        var width = payload.Paper == "58mm" ? 32 : 48;
        var separator = new string('-', width);
        var body = new StringBuilder();
        AppendCentered(body, payload.Heading, width, asciiOnly);
        AppendCentered(body, payload.Title, width, asciiOnly);
        AppendCentered(body, payload.Subtitle, width, asciiOnly);
        AppendCentered(body, payload.DocumentNumber, width, asciiOnly);
        if (body.Length > 0) body.AppendLine(separator);

        foreach (var meta in payload.Meta ?? [])
            AppendWrapped(body, $"{meta.Label}: {meta.Value}", width, asciiOnly);
        if ((payload.Meta?.Count ?? 0) > 0) body.AppendLine(separator);

        AppendRows(body, payload, width, separator, asciiOnly);

        foreach (var total in payload.Totals ?? [])
        {
            var label = NormalizeText(total.Label, asciiOnly);
            var value = NormalizeText(total.Value, asciiOnly);
            if (label.Length + value.Length + 1 <= width)
            {
                body.Append(label);
                body.Append(' ', Math.Max(1, width - label.Length - value.Length));
                body.AppendLine(value);
            }
            else
            {
                AppendWrapped(body, $"{label}: {value}", width, asciiOnly: false);
            }
        }
        if ((payload.Totals?.Count ?? 0) > 0) body.AppendLine(separator);

        foreach (var footer in payload.Footer ?? [])
            AppendCentered(body, footer, width, asciiOnly);
        body.AppendLine();
        body.AppendLine();
        return body.ToString();
    }

    private static void AppendRows(
        StringBuilder body,
        RetailPrintPayload payload,
        int width,
        string separator,
        bool asciiOnly)
    {
        var columns = payload.Columns ?? [];
        var rows = payload.Rows ?? [];
        if (rows.Count == 0) return;

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex += 1)
        {
            var row = rows[rowIndex] ?? [];
            for (var columnIndex = 0; columnIndex < row.Count; columnIndex += 1)
            {
                var value = row[columnIndex] ?? "";
                var label = columnIndex < columns.Count ? columns[columnIndex] : $"Cột {columnIndex + 1}";
                var parts = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n', StringSplitOptions.None);

                if (columnIndex == 0 && label.Equals("STT", StringComparison.OrdinalIgnoreCase))
                {
                    AppendWrapped(body, $"#{parts[0]}", width, asciiOnly);
                    continue;
                }

                if (columnIndex == 0 || label.Equals("Sản phẩm", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var part in parts)
                        AppendWrapped(body, part, width, asciiOnly);
                    continue;
                }

                AppendWrapped(body, $"{label}: {string.Join(" / ", parts)}", width, asciiOnly);
            }

            if (rowIndex < rows.Count - 1)
                body.AppendLine(new string('-', Math.Min(width, 18)));
        }

        body.AppendLine(separator);
    }

    private static void AppendCentered(StringBuilder body, string? value, int width, bool asciiOnly)
    {
        var text = NormalizeText(value ?? "", asciiOnly);
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (var line in Wrap(text, width))
        {
            var padding = Math.Max(0, (width - line.Length) / 2);
            body.Append(' ', padding).AppendLine(line);
        }
    }

    private static void AppendWrapped(StringBuilder body, string value, int width, bool asciiOnly)
    {
        foreach (var line in Wrap(NormalizeText(value, asciiOnly), width))
            body.AppendLine(line);
    }

    private static IEnumerable<string> Wrap(string value, int width)
    {
        var text = value.Trim();
        if (text.Length == 0)
        {
            yield return "";
            yield break;
        }

        while (text.Length > width)
        {
            var cut = text.LastIndexOf(' ', width);
            if (cut <= 0) cut = width;
            yield return text[..cut].TrimEnd();
            text = text[cut..].TrimStart();
        }

        if (text.Length > 0) yield return text;
    }

    private static string NormalizeText(string value, bool asciiOnly) => asciiOnly
        ? ToAscii(value)
        : value.Normalize(NormalizationForm.FormC);

    private static string ToAscii(string value)
    {
        var source = value.Replace('Đ', 'D').Replace('đ', 'd');
        var normalized = source.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            if (character <= 127)
                builder.Append(character);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
