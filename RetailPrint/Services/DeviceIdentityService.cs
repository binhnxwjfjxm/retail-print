using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetailPrint.Models;

namespace RetailPrint.Services;

public sealed class DeviceIdentityService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string IdentityDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RetailPrint");

    private static string IdentityPath => Path.Combine(IdentityDirectory, "identity.json");

    public DeviceIdentity LoadOrCreate()
    {
        try
        {
            if (File.Exists(IdentityPath))
            {
                var identity = JsonSerializer.Deserialize<DeviceIdentity>(
                    File.ReadAllText(IdentityPath),
                    JsonOptions);

                if (identity is not null
                    && Guid.TryParse(identity.DeviceId, out _)
                    && IsValidCredential(identity.Credential))
                {
                    return identity;
                }
            }
        }
        catch
        {
            // Nếu tệp nhận diện không đọc được, tạo nhận diện mới.
        }

        var created = new DeviceIdentity
        {
            DeviceId = Guid.NewGuid().ToString(),
            Credential = Base64Url(RandomNumberGenerator.GetBytes(48))
        };

        Directory.CreateDirectory(IdentityDirectory);
        var json = JsonSerializer.Serialize(created, JsonOptions);
        var tempPath = IdentityPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, IdentityPath, overwrite: true);
        return created;
    }

    public static string CredentialHash(DeviceIdentity identity) => Sha256Hex(identity.Credential);

    public static string CreatePairingProofHash()
    {
        var proof = Base64Url(RandomNumberGenerator.GetBytes(32));
        return Sha256Hex(proof);
    }

    private static bool IsValidCredential(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 32 or > 160)
            return false;

        return value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '-');
    }

    private static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
