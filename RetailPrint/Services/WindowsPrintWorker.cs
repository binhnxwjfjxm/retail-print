using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.IO;
using RetailPrint.Models;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RetailPrint.Services;

internal sealed class WindowsPrintWorkerRequest
{
    public string PrinterName { get; set; } = "";
    public int PaperWidthMm { get; set; }
    public RetailPrintPayload Payload { get; set; } = new();
    public int Copies { get; set; }
    public bool AutoCutPaper { get; set; }
}

internal sealed class WindowsPrintWorkerResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Warning { get; set; }
}

internal static class WindowsPrintWorker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ResultPathFor(string requestPath) => requestPath + ".result";

    public static int Run(string requestPath)
    {
        var resultPath = ResultPathFor(requestPath);
        try
        {
            var request = JsonSerializer.Deserialize<WindowsPrintWorkerRequest>(
                              File.ReadAllText(requestPath),
                              JsonOptions)
                          ?? throw new InvalidOperationException("Lệnh in Windows không hợp lệ.");

            Validate(request);

            string? warning = null;

            for (var copy = 0; copy < request.Copies; copy += 1)
            {
                PrintOneCopy(
                    request.PrinterName,
                    request.PaperWidthMm,
                    request.Payload);

                if (request.AutoCutPaper)
                {
                    try
                    {
                        SendCutCommand(request.PrinterName);
                    }
                    catch (Exception error)
                    {
                        warning ??= "Đã gửi nội dung in nhưng máy chưa thực hiện cắt giấy: " + SafeMessage(error.Message);
                        CrashLogService.Write(error, "Cắt giấy máy in Windows");
                    }
                }
            }

            WriteResult(
                resultPath,
                new WindowsPrintWorkerResult
                {
                    Success = true,
                    Warning = warning
                });
            return 0;
        }
        catch (Exception error)
        {
            CrashLogService.Write(error, "Tiến trình in Windows");
            WriteResult(
                resultPath,
                new WindowsPrintWorkerResult
                {
                    Success = false,
                    ErrorMessage = SafeMessage(error.Message)
                });
            return 1;
        }
    }

    private static void Validate(WindowsPrintWorkerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PrinterName))
            throw new InvalidOperationException("Chưa chọn máy in trên Windows.");
        if (request.PaperWidthMm is not (58 or 80))
            throw new InvalidOperationException("Khổ giấy chỉ hỗ trợ 58 mm hoặc 80 mm.");
        if (request.Copies is < 1 or > 5)
            throw new InvalidOperationException("Số bản in phải từ 1 đến 5.");
        if (request.Payload is null || string.IsNullOrWhiteSpace(request.Payload.Title))
            throw new InvalidOperationException("Nội dung in chưa có tiêu đề.");
    }

    private static void PrintOneCopy(
        string printerName,
        int paperWidthMm,
        RetailPrintPayload payload)
    {
        using var receipt = ThermalReceiptRenderer.Render(payload, paperWidthMm);
        var paperWidth = MillimetersToHundredthsInch(paperWidthMm);
        var horizontalMargin = paperWidthMm == 80 ? 16f : 10f;
        var printableWidth = Math.Max(1f, paperWidth - horizontalMargin * 2f);
        var receiptHeight = printableWidth * receipt.Height / receipt.Width;
        var paperHeight = Math.Max(100, (int)Math.Ceiling(receiptHeight + 18f));

        using var document = new PrintDocument
        {
            DocumentName = "Retail Print",
            PrintController = new StandardPrintController(),
            OriginAtMargins = false
        };

        document.PrinterSettings.PrinterName = printerName;
        document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        document.DefaultPageSettings.PaperSize = new PaperSize(
            $"Retail {paperWidthMm} mm",
            paperWidth,
            paperHeight);

        if (!document.PrinterSettings.IsValid)
            throw new InvalidOperationException("Windows chưa thể sử dụng máy in đã chọn.");

        document.PrintPage += (_, args) =>
        {
            var graphics = args.Graphics
                           ?? throw new InvalidOperationException("Windows chưa tạo được vùng in cho máy in đã chọn.");

            graphics.TranslateTransform(
                -args.PageSettings.HardMarginX,
                -args.PageSettings.HardMarginY);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(
                receipt,
                new RectangleF(horizontalMargin, 0f, printableWidth, receiptHeight),
                new RectangleF(0f, 0f, receipt.Width, receipt.Height),
                GraphicsUnit.Pixel);

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

    private static int MillimetersToHundredthsInch(int millimeters) =>
        Math.Max(1, (int)Math.Round(millimeters / 25.4d * 100d));

    private static void SendCutCommand(string printerName)
    {
        if (!OpenPrinter(printerName, out var printerHandle, IntPtr.Zero))
            ThrowWin32("Windows không mở được hàng đợi máy in để cắt giấy.");

        try
        {
            var documentInfo = new DocInfo1
            {
                DocumentName = "Retail Print - Cắt giấy",
                DataType = "RAW"
            };

            if (StartDocPrinter(printerHandle, 1, ref documentInfo) == 0)
                ThrowWin32("Windows không tạo được lệnh cắt giấy.");

            var pageStarted = false;
            try
            {
                if (!StartPagePrinter(printerHandle))
                    ThrowWin32("Windows không bắt đầu được lệnh cắt giấy.");
                pageStarted = true;

                byte[] command = [0x1B, 0x64, 0x03, 0x1D, 0x56, 0x42, 0x00];
                if (!WritePrinter(
                        printerHandle,
                        command,
                        command.Length,
                        out var written)
                    || written != command.Length)
                {
                    ThrowWin32("Windows không gửi đủ lệnh cắt giấy tới máy in.");
                }
            }
            finally
            {
                if (pageStarted)
                    EndPagePrinter(printerHandle);
                EndDocPrinter(printerHandle);
            }
        }
        finally
        {
            ClosePrinter(printerHandle);
        }
    }

    private static void ThrowWin32(string message)
    {
        var code = Marshal.GetLastWin32Error();
        throw new Win32Exception(code, message);
    }

    private static void WriteResult(string path, WindowsPrintWorkerResult result)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(result, JsonOptions));
        }
        catch
        {
            // Tiến trình cha sẽ dùng mã thoát nếu file kết quả không ghi được.
        }
    }

    private static string SafeMessage(string? message)
    {
        var value = string.IsNullOrWhiteSpace(message)
            ? "Windows chưa thể gửi chứng từ tới máy in đã chọn."
            : message.Trim();
        return value.Length <= 180 ? value : value[..180];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string DocumentName;

        public IntPtr OutputFile;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string DataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenPrinter(
        string printerName,
        out IntPtr printerHandle,
        IntPtr printerDefaults);

    [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClosePrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int StartDocPrinter(
        IntPtr printerHandle,
        int level,
        ref DocInfo1 documentInfo);

    [DllImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndDocPrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartPagePrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPagePrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WritePrinter(
        IntPtr printerHandle,
        byte[] bytes,
        int byteCount,
        out int bytesWritten);
}
