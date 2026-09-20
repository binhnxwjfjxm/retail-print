using System.Diagnostics;
using System.Drawing.Printing;
using System.IO;
using System.Text.Json;
using DrawingPrinterSettings = System.Drawing.Printing.PrinterSettings;

namespace RetailPrint.Services;

public sealed class WindowsPrinterService
{
    private static readonly TimeSpan PrintTimeout = TimeSpan.FromSeconds(25);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _printGate = new(1, 1);

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

    public async Task PrintTextAsync(
        string printerName,
        int paperWidthMm,
        string content,
        int copies,
        bool autoCutPaper,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            throw new InvalidOperationException("Chưa chọn máy in trên Windows.");
        if (paperWidthMm is not (58 or 80))
            throw new InvalidOperationException("Khổ giấy chỉ hỗ trợ 58 mm hoặc 80 mm.");
        if (copies is < 1 or > 5)
            throw new InvalidOperationException("Số bản in phải từ 1 đến 5.");

        await _printGate.WaitAsync(cancellationToken);
        try
        {
            await RunWorkerAsync(
                printerName.Trim(),
                paperWidthMm,
                content ?? "",
                copies,
                autoCutPaper,
                cancellationToken);
        }
        finally
        {
            _printGate.Release();
        }
    }

    private static async Task RunWorkerAsync(
        string printerName,
        int paperWidthMm,
        string content,
        int copies,
        bool autoCutPaper,
        CancellationToken cancellationToken)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            throw new InvalidOperationException("Retail Print chưa xác định được tiến trình in Windows.");

        var workDirectory = Path.Combine(
            Path.GetTempPath(),
            "RetailPrint",
            "PrintJobs");
        Directory.CreateDirectory(workDirectory);

        var requestPath = Path.Combine(workDirectory, $"{Guid.NewGuid():N}.json");
        var resultPath = WindowsPrintWorker.ResultPathFor(requestPath);
        var request = new WindowsPrintWorkerRequest
        {
            PrinterName = printerName,
            PaperWidthMm = paperWidthMm,
            Content = content,
            Copies = copies,
            AutoCutPaper = autoCutPaper
        };

        await File.WriteAllTextAsync(
            requestPath,
            JsonSerializer.Serialize(request, JsonOptions),
            cancellationToken);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(processPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppContext.BaseDirectory
                }
            };
            process.StartInfo.ArgumentList.Add("--print-worker");
            process.StartInfo.ArgumentList.Add(requestPath);

            if (!process.Start())
                throw new InvalidOperationException("Windows chưa thể khởi động tiến trình in.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PrintTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new InvalidOperationException(
                    "Máy in hoặc Windows đang không phản hồi. Retail Print đã dừng lệnh bị treo để tiếp tục nhận lệnh mới.");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            WindowsPrintWorkerResult? result = null;
            try
            {
                if (File.Exists(resultPath))
                {
                    var json = await File.ReadAllTextAsync(resultPath, cancellationToken);
                    result = JsonSerializer.Deserialize<WindowsPrintWorkerResult>(json, JsonOptions);
                }
            }
            catch (JsonException)
            {
                // Xử lý bên dưới bằng thông báo ổn định.
            }

            if (process.ExitCode != 0 || result?.Success != true)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result?.ErrorMessage)
                        ? "Windows chưa thể gửi chứng từ tới máy in đã chọn."
                        : result.ErrorMessage);
            }

            if (!string.IsNullOrWhiteSpace(result.Warning))
            {
                CrashLogService.Write(
                    new InvalidOperationException(result.Warning),
                    "Cắt giấy máy in Windows");
            }
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(resultPath);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Tiến trình đã thoát hoặc Windows đang thu hồi tài nguyên.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // File tạm không được phép làm lệnh in thất bại.
        }
    }
}
