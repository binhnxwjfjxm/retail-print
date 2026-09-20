using System.IO;
using System.Text.Json;
using RetailPrint.Models;

namespace RetailPrint.Services;

public sealed class PairingCacheService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _directory;

    public PairingCacheService(string? directory = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RetailPrint")
            : directory;
    }

    private string CachePath => Path.Combine(_directory, "pairing.json");

    public PairingResult? Load(DeviceIdentity identity)
    {
        try
        {
            if (!File.Exists(CachePath))
                return null;

            var record = JsonSerializer.Deserialize<PairingCacheRecord>(
                File.ReadAllText(CachePath),
                JsonOptions);
            if (record is null
                || !string.Equals(record.DeviceId, identity.DeviceId, StringComparison.OrdinalIgnoreCase)
                || !IsValidPairing(record.Pairing))
            {
                return null;
            }

            return record.Pairing;
        }
        catch
        {
            return null;
        }
    }

    public void Save(DeviceIdentity identity, PairingResult pairing)
    {
        if (!Guid.TryParse(identity.DeviceId, out _)
            || !IsValidPairing(pairing))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            var record = new PairingCacheRecord
            {
                DeviceId = identity.DeviceId,
                Pairing = pairing
            };
            var json = JsonSerializer.Serialize(record, JsonOptions);
            var tempPath = CachePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, CachePath, overwrite: true);
        }
        catch
        {
            // Cache chỉ để giữ mã khi mở lại ứng dụng; lỗi ghi file không được làm hỏng kết nối đang thành công.
        }
    }

    private static bool IsValidPairing(PairingResult? pairing)
    {
        if (pairing is null
            || !Guid.TryParse(pairing.AgentId, out _)
            || string.IsNullOrWhiteSpace(pairing.PairingCode)
            || pairing.PairingCode.Length != 8)
        {
            return false;
        }

        return pairing.PairingCode.All(character =>
            character is >= 'A' and <= 'Z'
            || character is >= '0' and <= '9');
    }

    private sealed class PairingCacheRecord
    {
        public string DeviceId { get; set; } = "";
        public PairingResult Pairing { get; set; } = new();
    }
}
