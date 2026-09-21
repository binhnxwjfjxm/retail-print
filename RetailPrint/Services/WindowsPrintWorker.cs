using System.ComponentModel;
using System.Drawing;
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
    private const int RasterChunkRows = 192;
    private const int RawWriteChunkBytes = 64 * 1024;
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

            for (var copy = 0; copy < request.Copies; copy += 1)
            {
                PrintOneCopy(
                    request.PrinterName,
                    request.PaperWidthMm,
                    request.Payload,
                    request.AutoCutPaper);
            }

            WriteResult(
                resultPath,
                new WindowsPrintWorkerResult
                {
                    Success = true
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
        RetailPrintPayload payload,
        bool autoCutPaper)
    {
        using var receipt = ThermalReceiptRenderer.Render(payload, paperWidthMm);
        var document = BuildEscPosRasterDocument(receipt, autoCutPaper);
        SendRawDocument(printerName, document);
    }

    internal static byte[] BuildEscPosRasterDocument(Bitmap receipt, bool autoCutPaper)
    {
        if (receipt.Width <= 0 || receipt.Height <= 0)
            throw new InvalidOperationException("Bản in nhiệt chưa có nội dung.");

        var widthBytes = (receipt.Width + 7) / 8;
        var bytes = new List<byte>(
            16 + widthBytes * receipt.Height + (receipt.Height / RasterChunkRows + 1) * 8);

        bytes.AddRange([0x1B, 0x40]); // Khởi tạo máy in.
        bytes.AddRange([0x1B, 0x61, 0x01]); // Căn giữa ảnh trên khổ giấy.

        for (var top = 0; top < receipt.Height; top += RasterChunkRows)
        {
            var rows = Math.Min(RasterChunkRows, receipt.Height - top);
            bytes.AddRange([
                0x1D, 0x76, 0x30, 0x00,
                (byte)(widthBytes & 0xFF),
                (byte)((widthBytes >> 8) & 0xFF),
                (byte)(rows & 0xFF),
                (byte)((rows >> 8) & 0xFF)
            ]);

            for (var y = top; y < top + rows; y += 1)
            {
                for (var byteIndex = 0; byteIndex < widthBytes; byteIndex += 1)
                {
                    byte packed = 0;
                    for (var bit = 0; bit < 8; bit += 1)
                    {
                        var x = byteIndex * 8 + bit;
                        if (x >= receipt.Width) continue;

                        var pixel = receipt.GetPixel(x, y);
                        var luminance = (299 * pixel.R + 587 * pixel.G + 114 * pixel.B) / 1000;
                        if (pixel.A >= 128 && luminance < 210)
                            packed |= (byte)(0x80 >> bit);
                    }
                    bytes.Add(packed);
                }
            }
        }

        bytes.AddRange([0x0A, 0x0A, 0x0A]);
        if (autoCutPaper)
            bytes.AddRange([0x1D, 0x56, 0x42, 0x00]);

        return bytes.ToArray();
    }

    private static void SendRawDocument(string printerName, byte[] document)
    {
        if (!OpenPrinter(printerName, out var printerHandle, IntPtr.Zero))
            ThrowWin32("Windows không mở được hàng đợi máy in.");

        try
        {
            var documentInfo = new DocInfo1
            {
                DocumentName = "Retail Print",
                DataType = "RAW"
            };

            if (StartDocPrinter(printerHandle, 1, ref documentInfo) == 0)
                ThrowWin32("Windows không tạo được lệnh in.");

            var pageStarted = false;
            try
            {
                if (!StartPagePrinter(printerHandle))
                    ThrowWin32("Windows không bắt đầu được lệnh in.");
                pageStarted = true;

                WriteAll(printerHandle, document);
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

    private static void WriteAll(IntPtr printerHandle, byte[] bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = Math.Min(RawWriteChunkBytes, bytes.Length - offset);
            var chunk = new byte[count];
            Buffer.BlockCopy(bytes, offset, chunk, 0, count);

            if (!WritePrinter(printerHandle, chunk, chunk.Length, out var written) || written <= 0)
                ThrowWin32("Windows không gửi được nội dung tới máy in.");

            offset += written;
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
