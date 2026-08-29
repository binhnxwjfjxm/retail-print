using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RetailPrint.Models;

namespace RetailPrint.Services;

public sealed class PrintJobJournal
{
    private const int MaxRecords = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();

    private static string JournalDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RetailPrint");

    private static string JournalPath => Path.Combine(JournalDirectory, "print-journal.json");

    public LocalPrintJobState? GetState(string jobId)
    {
        lock (_gate)
        {
            var records = LoadUnsafe();
            return records.FirstOrDefault(row =>
                string.Equals(row.JobId, jobId, StringComparison.OrdinalIgnoreCase))?.State;
        }
    }

    public void Mark(string jobId, LocalPrintJobState state)
    {
        lock (_gate)
        {
            var records = LoadUnsafe();
            var existing = records.FirstOrDefault(row =>
                string.Equals(row.JobId, jobId, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                records.Add(new LocalPrintJobRecord
                {
                    JobId = jobId,
                    State = state,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
            else
            {
                existing.State = state;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }

            var compact = records
                .OrderByDescending(row => row.UpdatedAt)
                .Take(MaxRecords)
                .ToList();

            SaveUnsafe(compact);
        }
    }

    private static List<LocalPrintJobRecord> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(JournalPath))
                return [];

            return JsonSerializer.Deserialize<List<LocalPrintJobRecord>>(
                       File.ReadAllText(JournalPath),
                       JsonOptions)
                   ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void SaveUnsafe(List<LocalPrintJobRecord> records)
    {
        Directory.CreateDirectory(JournalDirectory);
        var tempPath = JournalPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(records, JsonOptions));
        File.Move(tempPath, JournalPath, overwrite: true);
    }
}
