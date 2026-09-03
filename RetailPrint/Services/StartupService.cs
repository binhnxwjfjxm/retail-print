using System.IO;
using Microsoft.Win32;

namespace RetailPrint.Services;

public sealed class StartupService
{
    private const string ValueName = "RetailPrint";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(WindowsRuntimeNames.StartupRunKey, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(WindowsRuntimeNames.StartupRunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(WindowsRuntimeNames.StartupRunKey, writable: true);

        if (enabled)
        {
            key.SetValue(ValueName, BuildLaunchCommand(), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static string BuildLaunchCommand()
    {
        var processPath = Environment.ProcessPath
                          ?? throw new InvalidOperationException(
                              "Không xác định được đường dẫn Retail Print.");

        if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            var assemblyName = typeof(StartupService).Assembly.GetName().Name
                               ?? "RetailPrint";

            var assemblyPath = Path.Combine(
                AppContext.BaseDirectory,
                $"{assemblyName}.dll");

            return $"\"{processPath}\" \"{assemblyPath}\" --background";
        }

        return $"\"{processPath}\" --background";
    }
}
