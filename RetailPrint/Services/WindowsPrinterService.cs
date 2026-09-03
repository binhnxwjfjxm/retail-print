using System.Drawing;
using System.Drawing.Printing;
using DrawingPrinterSettings = System.Drawing.Printing.PrinterSettings;

namespace RetailPrint.Services;

public sealed class WindowsPrinterService
{
    public IReadOnlyList<string> ListInstalledPrinters()
    {
        var printers = new List<string>();
        foreach (string printerName in DrawingPrinterSettings.InstalledPrinters)
        {
            if (!string.IsNullOrWhiteSpace(printerName))
                printers.Add(printerName.Trim());
        }

        return printers
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public string? GetDefaultPrinterName()
    {
        try
        {
            var settings = new DrawingPrinterSettings();
            return settings.IsValid && !string.IsNullOrWhiteSpace(settings.PrinterName)
                ? settings.PrinterName.Trim()
                : null;
        }
        catch
        {
            return null;
        }
    }

    public bool IsInstalled(string printerName) => ListInstalledPrinters().Any(
        installed => string.Equals(installed, printerName, StringComparison.OrdinalIgnoreCase));

    public Task PrintTextAsync(
        string printerName,
        int paperWidthMm,
        string content,
        int copies,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            throw new InvalidOperationException("Chưa chọn máy in trên Windows.");
        if (paperWidthMm is not (58 or 80))
            throw new InvalidOperationException("Khổ giấy chỉ hỗ trợ 58 mm hoặc 80 mm.");
        if (copies is < 1 or > 5)
            throw new InvalidOperationException("Số bản in phải từ 1 đến 5.");

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsInstalled(printerName))
                throw new InvalidOperationException("Máy in đã chọn không còn trong danh sách máy in Windows.");

            var normalized = (content ?? "")
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            var lines = normalized.Split('\n', StringSplitOptions.None);

            for (var copy = 0; copy < copies; copy += 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PrintOneCopy(printerName, paperWidthMm, lines, cancellationToken);
            }
        }, cancellationToken);
    }

    private static void PrintOneCopy(
        string printerName,
        int paperWidthMm,
        string[] lines,
        CancellationToken cancellationToken)
    {
        using var document = new PrintDocument
        {
            DocumentName = "Retail Print",
            PrintController = new StandardPrintController()
        };

        document.PrinterSettings.PrinterName = printerName;
        document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

        if (!document.PrinterSettings.IsValid)
            throw new InvalidOperationException("Windows chưa thể sử dụng máy in đã chọn.");

        var lineIndex = 0;
        document.PrintPage += (_, args) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var graphics = args.Graphics
                           ?? throw new InvalidOperationException("Windows chưa tạo được vùng in cho máy in đã chọn.");

            var bounds = args.MarginBounds.Width > 0 && args.MarginBounds.Height > 0
                ? args.MarginBounds
                : args.PageBounds;
            var availableWidth = Math.Max(1, bounds.Width);
            var maxCharacters = paperWidthMm == 58 ? 32 : 48;

            using var font = CreateFittedFont(graphics, paperWidthMm, maxCharacters, availableWidth);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.None
            };

            var lineHeight = Math.Max(font.GetHeight(graphics) + 2f, 10f);
            var y = (float)bounds.Top;
            var bottom = (float)bounds.Bottom;

            while (lineIndex < lines.Length)
            {
                if (y + lineHeight > bottom && y > bounds.Top)
                {
                    args.HasMorePages = true;
                    return;
                }

                graphics.DrawString(
                    lines[lineIndex],
                    font,
                    Brushes.Black,
                    new PointF(bounds.Left, y),
                    format);

                y += lineHeight;
                lineIndex += 1;
            }

            args.HasMorePages = false;
        };

        try
        {
            document.Print();
        }
        catch (InvalidPrinterException)
        {
            throw new InvalidOperationException("Windows không tìm thấy máy in đã chọn.");
        }
    }

    private static Font CreateFittedFont(
        Graphics graphics,
        int paperWidthMm,
        int maxCharacters,
        float availableWidth)
    {
        var size = paperWidthMm == 58 ? 8f : 9f;
        while (size >= 6f)
        {
            var font = new Font("Consolas", size, FontStyle.Regular, GraphicsUnit.Point);
            var width = graphics.MeasureString(
                new string('M', maxCharacters),
                font,
                int.MaxValue,
                StringFormat.GenericTypographic).Width;

            if (width <= availableWidth)
                return font;

            font.Dispose();
            size -= 0.5f;
        }

        return new Font("Consolas", 6f, FontStyle.Regular, GraphicsUnit.Point);
    }
}
