using System.IO;
using System.Text;

namespace RetailPrint.Services;

public static class CrashLogService
{
    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RetailPrint",
        "Logs");

    public static string StartupLogPath => Path.Combine(LogDirectory, "startup.log");

    public static void Write(Exception error, string context)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var builder = new StringBuilder();
            builder.AppendLine("------------------------------------------------------------");
            builder.AppendLine($"Thời gian: {DateTimeOffset.Now:O}");
            builder.AppendLine($"Vị trí: {context}");
            builder.AppendLine($"Windows: {Environment.OSVersion}");
            builder.AppendLine($"Tiến trình: {Environment.ProcessPath ?? "không xác định"}");
            builder.AppendLine($"Kiểu lỗi: {error.GetType().FullName}");
            builder.AppendLine($"Thông báo: {error.Message}");
            builder.AppendLine(error.StackTrace ?? "Không có stack trace.");
            File.AppendAllText(StartupLogPath, builder.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Nhật ký không được phép làm ứng dụng lỗi thêm.
        }
    }
}
