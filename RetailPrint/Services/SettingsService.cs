using System.IO;
using System.Text.Json;
using RetailPrint.Models;

namespace RetailPrint.Services;

public sealed class SettingsService
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    private string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RetailPrint");

    private string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public PrinterSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new PrinterSettings();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<PrinterSettings>(json, _jsonOptions) ?? new PrinterSettings();
        }
        catch
        {
            return new PrinterSettings();
        }
    }

    public void Save(PrinterSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(settings, _jsonOptions);
        File.WriteAllText(SettingsPath, json);
    }
}

