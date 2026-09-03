namespace RetailPrint.Services;

internal static class WindowsRuntimeNames
{
    internal const string SingletonMutex = @"Local\RetailPrint.Singleton";
    internal const string ShowWindowEvent = @"Local\RetailPrint.ShowWindow";
    internal const string StartupRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
}
